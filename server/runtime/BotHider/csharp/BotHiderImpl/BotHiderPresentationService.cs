using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using DemoTracerBotHiderApi;

namespace BotHiderImpl;

internal sealed class BotHiderPresentationService : IBotHiderApi, IDisposable
{
    private const int MaxSlots = 64;
    private const int MaxOwnerLength = 64;
    private static readonly TimeSpan PresentationFailureLogInterval = TimeSpan.FromSeconds(30);

    private readonly NativePresentationClient _client;
    private readonly Action _ownerReleased;
    private readonly object _sync = new();
    private readonly string _providerEpoch = Guid.NewGuid().ToString("N");
    private readonly SlotState[] _slots = new SlotState[MaxSlots];
    private readonly Dictionary<string, PresentationLease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _leaseBySlot = new();
    private ulong _nextIncarnation;
    private ulong _nativeSession;
    private ulong _mapEpoch = 1;
    private bool _draining;
    private bool _disposed;
    private int _revokedLeases;
    private int _publishedWrites;
    private int _controllerRepairs;

    public BotHiderPresentationService(NativePresentationClient client, Action? ownerReleased = null)
    {
        _client = client;
        _ownerReleased = ownerReleased ?? PublishManagedSlots;
    }

    public int ApiVersion => DemoTracerBotHiderContract.ApiVersion;

    public BotHiderProviderInfo GetProviderInfo()
    {
        lock (_sync)
        {
            ObserveNativeSession();
            return new BotHiderProviderInfo
            {
                ApiVersion = ApiVersion,
                ProviderEpoch = $"{_providerEpoch}:{_nativeSession:x}",
                MapEpoch = _mapEpoch,
                Connected = !_disposed && _client.IsConnected(),
                Draining = _draining || _disposed
            };
        }
    }

    public bool IsManagedBot(int slot)
    {
        lock (_sync)
            return TryReadManagedNativeSlot(slot, out _, out _);
    }

    public bool TryGetManagedSlot(int slot, out BotHiderManagedSlot state)
    {
        lock (_sync)
            return TryReadManagedSlot(slot, out state);
    }

    public BotHiderPresentationLeaseResult AcquirePresentationLease(
        string owner,
        BotHiderPresentationOverride[] overrides,
        CancellationToken ownerLifetime)
    {
        lock (_sync)
        {
            ObserveNativeSession();
            if (_draining || _disposed)
                return Fail("provider_draining");
            if (!ownerLifetime.CanBeCanceled || ownerLifetime.IsCancellationRequested)
                return Fail("owner_lifetime_inactive");

            owner = owner?.Trim() ?? string.Empty;
            if (owner.Length == 0 || owner.Length > MaxOwnerLength)
                return Fail("invalid_owner");

            if (!TryNormalizeOverrides(overrides, allowedLeaseToken: null, out var normalized, out var reason))
                return Fail(reason);

            var token = $"{_providerEpoch}:{Guid.NewGuid():N}";
            var lease = new PresentationLease(token, owner, normalized, ownerLifetime);
            AddLease(lease);
            PublishManagedSlots();
            if (IsLeaseAppliedSynchronously(lease, out var applyReason))
                return Success(lease);

            RemoveLease(lease.Token, countRevocation: false);
            PublishManagedSlots();
            return Fail(applyReason);
        }
    }

    public BotHiderPresentationLeaseResult ReplacePresentationLease(
        string leaseToken,
        BotHiderPresentationOverride[] overrides)
    {
        lock (_sync)
        {
            ObserveNativeSession();
            if (_draining || _disposed)
                return Fail("provider_draining");
            if (string.IsNullOrWhiteSpace(leaseToken) ||
                !_leases.TryGetValue(leaseToken, out var existing))
            {
                return Fail("lease_not_found");
            }

            if (!TryNormalizeOverrides(overrides, leaseToken, out var normalized, out var reason))
                return Fail(reason);

            RemoveLeaseMappings(existing);
            var replacement = existing with
            {
                Overrides = normalized
            };
            _leases[leaseToken] = replacement;
            AddLeaseMappings(replacement);
            InvalidateSlots(existing.Overrides.Keys.Concat(normalized.Keys));
            PublishManagedSlots();
            if (IsLeaseAppliedSynchronously(replacement, out var applyReason))
                return Success(replacement);

            RemoveLeaseMappings(replacement);
            _leases[leaseToken] = existing;
            AddLeaseMappings(existing);
            InvalidateSlots(existing.Overrides.Keys.Concat(normalized.Keys));
            PublishManagedSlots();
            return Fail(applyReason);
        }
    }

    public bool ReleasePresentationLease(string leaseToken)
    {
        bool released;
        lock (_sync)
        {
            ObserveNativeSession();
            released = RemoveLease(leaseToken, countRevocation: false);
        }

        if (released)
            PublishManagedSlots();
        return released;
    }

    public int ReleasePresentationLeasesByOwner(string owner)
    {
        string[] tokens;
        lock (_sync)
        {
            ObserveNativeSession();
            tokens = _leases.Values
                .Where(lease => lease.Owner.Equals(owner, StringComparison.Ordinal))
                .Select(lease => lease.Token)
                .ToArray();
            foreach (var token in tokens)
                RemoveLease(token, countRevocation: false);
        }

        if (tokens.Length > 0)
            PublishManagedSlots();
        return tokens.Length;
    }

    public BotHiderDiagnostics GetDiagnostics()
    {
        lock (_sync)
        {
            var managed = 0;
            for (var slot = 0; slot < MaxSlots; slot++)
            {
                if (TryReadManagedNativeSlot(slot, out _, out _))
                    managed++;
            }

            return new BotHiderDiagnostics
            {
                Connected = !_disposed && _client.IsConnected(),
                ManagedSlots = managed,
                ActiveLeases = _leases.Count,
                LeasedSlots = _leaseBySlot.Count,
                RevokedLeases = _revokedLeases,
                PublishedWrites = _publishedWrites,
                ControllerRepairs = _controllerRepairs,
                Signatures = _client.GetSignatures()
                    .Select(signature => $"{signature.Name}=0x{signature.Addr:X}")
                    .ToArray()
            };
        }
    }

    public void ResetForMapBoundary()
    {
        lock (_sync)
        {
            _mapEpoch++;
            foreach (var token in _leases.Keys.ToArray())
                RemoveLease(token, countRevocation: true);
            Array.Clear(_slots);
        }
    }

    public void HandleClientDisconnect(int slot)
    {
        lock (_sync)
            ObserveUnmanaged(slot);
    }

    public void PublishManagedSlots()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            for (var slot = 0; slot < MaxSlots; slot++)
            {
                if (!TryReadManagedNativeSlot(slot, out var native, out var player))
                    continue;
                PublishSlot(slot, native, player);
            }
        }
    }

    public void PublishPing(int slot)
    {
        lock (_sync)
        {
            if (TryReadManagedNativeSlot(slot, out var native, out var player))
                PublishPing(slot, native, player);
        }
    }

    private void PublishPing(int slot, NativePresentationClient.Slot native, CCSPlayerController player)
    {
        if (player.Ping != checked((uint)Math.Max(native.Ping, 0)) &&
            _client.PublishPing(slot, _nativeSession, native.Incarnation, player.EntityHandle.Raw))
            _publishedWrites++;
    }

    private void ObserveNativeSession()
    {
        var session = _client.Session;
        if (session == _nativeSession) return;
        foreach (var token in _leases.Keys.ToArray()) RemoveLease(token, countRevocation: true);
        Array.Clear(_slots);
        _nativeSession = session;
    }

    private bool TryReadManagedSlot(int slot, out BotHiderManagedSlot state)
    {
        state = new BotHiderManagedSlot { Slot = slot };
        if (!TryReadManagedNativeSlot(slot, out var native, out _))
            return false;
        state.Incarnation = _slots[slot].Incarnation;
        state.BaseSteamId = native.BaseSteamId;
        state.PublishedSteamId = native.SteamId;
        state.BasePlayerName = native.ReadBaseName();
        state.BasePing = native.Ping;
        state.BaseCrosshairCode = native.ReadCrosshair();
        state.BaseScoreboardFlair = native.ScoreboardFlair;
        return true;
    }

    // IsBot is queried by DTR and other plugins on their hot paths. Observe
    // ownership here without allocating presentation DTOs or decoding strings.
    private bool TryReadManagedNativeSlot(int slot, out NativePresentationClient.Slot native,
        out CCSPlayerController player)
    {
        native = default;
        player = null!;
        ObserveNativeSession();
        if (_disposed || slot is < 0 or >= MaxSlots || !_client.TryGetSlot(slot, out native) || native.Managed == 0)
        {
            ObserveUnmanaged(slot);
            return false;
        }

        player = Utilities.GetPlayerFromSlot(slot)!;
        if (player is not { IsValid: true, UserId: int userId })
        {
            ObserveUnmanaged(slot);
            return false;
        }

        ObserveSlot(slot, userId, player.EntityHandle.Raw, native.Incarnation);
        return true;
    }

    internal void ObserveSlot(int slot, int userId, uint controller, ulong nativeIncarnation)
    {
        ref var state = ref _slots[slot];
        if (state.Incarnation != 0 && state.UserId == userId &&
            state.Controller == controller && state.NativeIncarnation == nativeIncarnation)
            return;
        RemoveSlotPresentation(slot);
        state = new SlotState
        {
            UserId = userId,
            Controller = controller,
            Incarnation = ++_nextIncarnation,
            NativeIncarnation = nativeIncarnation
        };
    }

    private void ObserveUnmanaged(int slot)
    {
        if (slot is < 0 or >= MaxSlots)
            return;
        RemoveSlotPresentation(slot);
        _slots[slot] = default;
    }

    private bool TryNormalizeOverrides(
        BotHiderPresentationOverride[]? overrides,
        string? allowedLeaseToken,
        out Dictionary<int, BotHiderPresentationOverride> normalized,
        out string reason)
    {
        normalized = new Dictionary<int, BotHiderPresentationOverride>();
        reason = string.Empty;
        if (overrides == null || overrides.Length == 0)
        {
            reason = "empty_batch";
            return false;
        }

        foreach (var requested in overrides)
        {
            if (requested == null || requested.Slot is < 0 or >= MaxSlots)
            {
                reason = "invalid_slot";
                return false;
            }
            if (normalized.ContainsKey(requested.Slot))
            {
                reason = $"duplicate_slot:{requested.Slot}";
                return false;
            }
            if (_leaseBySlot.TryGetValue(requested.Slot, out var occupiedBy) &&
                !occupiedBy.Equals(allowedLeaseToken, StringComparison.Ordinal))
            {
                reason = $"slot_leased:{requested.Slot}";
                return false;
            }
            if (!TryReadManagedSlot(requested.Slot, out var state))
            {
                reason = $"slot_not_managed:{requested.Slot}";
                return false;
            }
            if (requested.Incarnation == 0 || requested.Incarnation != state.Incarnation)
            {
                reason = $"slot_incarnation_changed:{requested.Slot}";
                return false;
            }

            var playerName = requested.PlayerName?.Trim();
            if (playerName != null &&
                (playerName.Length == 0 || playerName.Contains('\0') ||
                 Encoding.UTF8.GetByteCount(playerName) > DemoTracerBotHiderContract.MaxPlayerNameUtf8Bytes))
            {
                reason = $"invalid_name:{requested.Slot}";
                return false;
            }
            if (requested.SteamId.HasValue && requested.SteamId.Value == 0)
            {
                reason = $"invalid_steam_id:{requested.Slot}";
                return false;
            }
            if (requested.ScoreboardFlair > ushort.MaxValue)
            {
                reason = $"invalid_scoreboard_flair:{requested.Slot}";
                return false;
            }

            if (!DemoTracerBotHiderContract.TryNormalizeCrosshairCode(
                    requested.CrosshairCode,
                    out var crosshair))
            {
                reason = $"invalid_crosshair:{requested.Slot}";
                return false;
            }
            if (playerName == null &&
                !requested.SteamId.HasValue &&
                !requested.ScoreboardFlair.HasValue &&
                crosshair == null)
            {
                reason = $"empty_override:{requested.Slot}";
                return false;
            }

            normalized[requested.Slot] = new BotHiderPresentationOverride
            {
                Slot = requested.Slot,
                Incarnation = requested.Incarnation,
                PlayerName = playerName,
                SteamId = requested.SteamId,
                ScoreboardFlair = requested.ScoreboardFlair,
                CrosshairCode = crosshair
            };
        }

        return ValidateRequestedSteamIds(normalized, allowedLeaseToken, out reason);
    }

    private bool ValidateRequestedSteamIds(
        IReadOnlyDictionary<int, BotHiderPresentationOverride> normalized,
        string? replacedLeaseToken,
        out string reason)
    {
        reason = string.Empty;
        var requestedBySteamId = new Dictionary<ulong, int>();
        foreach (var request in normalized.Values)
        {
            if (!request.SteamId.HasValue)
                continue;
            if (!requestedBySteamId.TryAdd(request.SteamId.Value, request.Slot))
            {
                reason = $"duplicate_steam_id:{request.SteamId.Value}";
                return false;
            }
        }

        if (requestedBySteamId.Count == 0)
            return true;

        foreach (var player in Utilities.GetPlayers())
        {
            if (player is not { IsValid: true })
                continue;

            var observedSteamIds = new HashSet<ulong>();
            if (player.SteamID != 0)
                observedSteamIds.Add(player.SteamID);
            if (_client.TryGetSlot(player.Slot, out var native) && native.Managed != 0)
            {
                var nativeSteamId = native.SteamId;
                if (nativeSteamId != 0)
                    observedSteamIds.Add(nativeSteamId);
            }

            foreach (var observedSteamId in observedSteamIds)
            {
                if (!requestedBySteamId.TryGetValue(observedSteamId, out var targetSlot) ||
                    player.Slot == targetSlot)
                {
                    continue;
                }

                // A batch may permute identities between managed slots. It is
                // safe when the current owner moves away in the same batch.
                if (normalized.TryGetValue(player.Slot, out var moving))
                {
                    var replacementSteamId = moving.SteamId ?? native.BaseSteamId;
                    if (replacementSteamId != observedSteamId)
                        continue;
                }
                else if (!string.IsNullOrWhiteSpace(replacedLeaseToken) &&
                         _leases.TryGetValue(replacedLeaseToken, out var replacedLease) &&
                         replacedLease.Overrides.ContainsKey(player.Slot) &&
                         native.BaseSteamId != observedSteamId)
                {
                    continue;
                }

                reason = $"steam_id_in_use:{observedSteamId}:slot{player.Slot}";
                return false;
            }
        }

        return true;
    }

    internal BotHiderPresentationOverride? GetPresentationOverride(int slot, ulong incarnation)
    {
        lock (_sync)
        {
            return _leaseBySlot.TryGetValue(slot, out var token) &&
                   _leases.TryGetValue(token, out var lease) &&
                   !lease.OwnerLifetime.IsCancellationRequested &&
                   lease.Overrides.TryGetValue(slot, out var candidate) &&
                   candidate.Incarnation == incarnation
                ? candidate
                : null;
        }
    }

    private void PublishSlot(int slot, NativePresentationClient.Slot native, CCSPlayerController player)
    {
        var presentationOverride = GetPresentationOverride(slot, _slots[slot].Incarnation);
        var playerName = presentationOverride?.PlayerName ?? native.ReadBaseName();
        var steamId = presentationOverride?.SteamId ?? native.BaseSteamId;
        var crosshair = presentationOverride?.CrosshairCode ?? native.ReadCrosshair();
        var flair = presentationOverride?.ScoreboardFlair ?? native.ScoreboardFlair;
        var effectiveScoreboardFlairManaged = presentationOverride?.ScoreboardFlair.HasValue == true;
        var forceCrosshairPublication = _slots[slot].NeedsCrosshairPublication(player.EntityHandle.Raw);
        try
        {
            // Native userinfo and controller fields must both confirm the
            // effective identity before the lease can report success.
            var nativeNameMismatch = !native.ReadName().Equals(playerName, StringComparison.Ordinal);
            var nativeSidMismatch = native.SteamId != steamId;
            if (!_client.PublishIdentity(slot, _nativeSession, _slots[slot].NativeIncarnation,
                    steamId, playerName))
                throw new InvalidOperationException("native identity publication rejected");
            if (nativeNameMismatch || nativeSidMismatch) _publishedWrites++;

            if (!player.PlayerName.Equals(playerName, StringComparison.Ordinal))
            {
                player.PlayerName = playerName;
                Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName");
                if (!player.PlayerName.Equals(playerName, StringComparison.Ordinal))
                    throw new InvalidOperationException("controller name write was not retained");
                _publishedWrites++; _controllerRepairs++;
            }
            if (player.SteamID != steamId)
            {
                Schema.SetSchemaValue(player.Handle, "CBasePlayerController", "m_steamID", steamId);
                Utilities.SetStateChanged(player, "CBasePlayerController", "m_steamID");
                if (player.SteamID != steamId)
                    throw new InvalidOperationException("controller SteamID write was not retained");
                _publishedWrites++; _controllerRepairs++;
            }

            PublishPing(slot, native, player);

            var crosshairSynchronized = TryWriteNetworkedCrosshair(
                    crosshair,
                    forceCrosshairPublication,
                    () => player.CrosshairCodes,
                    value => player.CrosshairCodes = value,
                    () => _client.PublishCrosshair(slot, _nativeSession,
                        _slots[slot].NativeIncarnation, player.EntityHandle.Raw),
                    out var crosshairChanged,
                    out var crosshairPublished);
            _slots[slot].CrosshairPending = !crosshairSynchronized;
            if (crosshairChanged || crosshairPublished) _publishedWrites++;
            if (crosshairChanged) _controllerRepairs++;
            if (!crosshairSynchronized)
                ReportPresentationFailure(slot, "crosshair notification pending");

            // A requested crosshair requires native notification submission as
            // well as readback. This is not a remote client's rendering ACK.
            var scoreboardFlairNeedsWrite = effectiveScoreboardFlairManaged ||
                                             _slots[slot].FlairManaged;
            if (scoreboardFlairNeedsWrite)
            {
                var scoreboardFlairSynchronized = ScoreboardFlairMatches(
                    player,
                    flair);
                var scoreboardFlairNeedsPublish = ShouldPublishScoreboardFlair(
                    scoreboardFlairSynchronized,
                    _slots[slot].FlairPending);
                if (scoreboardFlairNeedsPublish &&
                    ApplyScoreboardFlair(player, flair))
                {
                    scoreboardFlairSynchronized = ScoreboardFlairMatches(
                        player,
                        flair);
                    _publishedWrites++;
                    _controllerRepairs++;
                }

                if (scoreboardFlairSynchronized)
                {
                    _slots[slot].FlairManaged = effectiveScoreboardFlairManaged;
                    _slots[slot].FlairPending = false;
                }
                else
                    throw new InvalidOperationException("controller scoreboard flair write was not retained");
            }

            _slots[slot].PublishedController = player.EntityHandle.Raw;
            if (crosshairSynchronized) _slots[slot].SuppressedFailures = 0;
        }
        catch (Exception ex)
        {
            _slots[slot].PublishedController = 0;
            ReportPresentationFailure(slot, ex.Message);
        }
    }

    private void ReportPresentationFailure(int slot, string reason)
    {
        var now = DateTime.UtcNow;
        if (now >= _slots[slot].NextFailureLogUtc)
        {
            var suppressed = _slots[slot].SuppressedFailures;
            var suffix = suppressed > 0 ? $" (suppressed={suppressed})" : string.Empty;
            Server.PrintToConsole(
                $"[DemoTracer BotHider] presentation write failed slot={slot}: {reason}{suffix}");
            _slots[slot].NextFailureLogUtc = now + PresentationFailureLogInterval;
            _slots[slot].SuppressedFailures = 0;
            return;
        }

        _slots[slot].SuppressedFailures++;
    }

    private static bool ScoreboardFlairMatches(CCSPlayerController player, uint itemDefIndex)
    {
        var inventory = player.InventoryServices;
        if (inventory == null)
            return false;
        var ranks = inventory.Rank;
        if (ranks.Length == 0) return false;
        for (int i = 0; i < ranks.Length; i++) if ((uint)ranks[i] != itemDefIndex) return false;
        return true;
    }

    private static bool ApplyScoreboardFlair(CCSPlayerController player, uint itemDefIndex)
    {
        var inventory = player.InventoryServices;
        if (inventory == null)
            return false;
        var ranks = inventory.Rank;
        if (ranks.Length == 0)
            return false;
        for (var index = 0; index < ranks.Length; index++)
        {
            ranks[index] = (MedalRank_t)itemDefIndex;
            TrySetStateChanged(
                player,
                "CCSPlayerController_InventoryServices",
                "m_rank",
                index * sizeof(uint));
        }
        return true;
    }

    internal static bool ShouldPublishScoreboardFlair(
        bool scoreboardFlairMatches,
        bool nextFrameRepublishPending)
        => !scoreboardFlairMatches || nextFrameRepublishPending;

    private static bool IsNetworkedSchemaField(string className, string fieldName)
    {
        try
        {
            return Schema.IsSchemaFieldNetworked(className, fieldName);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryWriteNetworkedCrosshair(
        string expected,
        bool forcePublication,
        Func<string?> read,
        Action<string> write,
        Func<bool> publish,
        out bool changed,
        out bool published)
    {
        changed = false;
        published = false;
        try
        {
            if (!string.Equals(read() ?? string.Empty, expected, StringComparison.Ordinal))
            {
                write(expected);
                if (!string.Equals(read() ?? string.Empty, expected, StringComparison.Ordinal))
                    return false;

                changed = true;
            }

            if (!changed && !forcePublication)
                return true;

            published = publish();
            if (!published)
                return false;
            return true;
        }
        catch
        {
            // Keep notification failure visible to the retry state and to
            // leases that explicitly requested a crosshair override.
            return false;
        }
    }

    private static void TrySetStateChanged(
        CBaseEntity entity,
        string className,
        string fieldName,
        int extraOffset = 0)
    {
        if (!IsNetworkedSchemaField(className, fieldName))
            return;
        try
        {
            Utilities.SetStateChanged(entity, className, fieldName, extraOffset);
        }
        catch
        {
        }
    }

    internal void AddLease(PresentationLease lease)
    {
        _leases.Add(lease.Token, lease);
        AddLeaseMappings(lease);
        InvalidateSlots(lease.Overrides.Keys);
        lease.OwnerRegistration = lease.OwnerLifetime.Register(() =>
        {
            bool removed;
            lock (_sync) removed = RemoveLease(lease.Token, countRevocation: true);
            if (removed) _ownerReleased();
        });
    }

    private void AddLeaseMappings(PresentationLease lease)
    {
        foreach (var slot in lease.Overrides.Keys)
            _leaseBySlot[slot] = lease.Token;
    }

    private bool RemoveLease(string leaseToken, bool countRevocation)
    {
        if (string.IsNullOrWhiteSpace(leaseToken) || !_leases.Remove(leaseToken, out var lease))
            return false;
        lease.OwnerRegistration.Unregister();
        RemoveLeaseMappings(lease);
        InvalidateSlots(lease.Overrides.Keys);
        if (countRevocation)
            _revokedLeases++;
        return true;
    }

    private void RemoveSlotPresentation(int slot)
    {
        if (!_leaseBySlot.Remove(slot, out var token) || !_leases.TryGetValue(token, out var lease))
            return;

        // Batch acquisition/replacement is atomic, but losing one participant
        // must not restore the other participants to their base personas.
        if (lease.Overrides.Count == 1)
            RemoveLease(token, countRevocation: true);
        else
            _leases[token] = lease with
            {
                Overrides = lease.Overrides.Where(pair => pair.Key != slot)
                    .ToDictionary(pair => pair.Key, pair => pair.Value)
            };
    }

    private void RemoveLeaseMappings(PresentationLease lease)
    {
        foreach (var slot in lease.Overrides.Keys)
        {
            if (_leaseBySlot.TryGetValue(slot, out var token) &&
                token.Equals(lease.Token, StringComparison.Ordinal))
            {
                _leaseBySlot.Remove(slot);
            }
        }
    }

    private void InvalidateSlots(IEnumerable<int> slots)
    {
        foreach (var slot in slots)
        {
            if (slot is >= 0 and < MaxSlots)
                _slots[slot].PublishedController = 0;
        }
    }

    private bool IsLeaseAppliedSynchronously(PresentationLease lease, out string reason)
    {
        foreach (var requested in lease.Overrides.Values)
        {
            if (!_leaseBySlot.TryGetValue(requested.Slot, out var token) ||
                !token.Equals(lease.Token, StringComparison.Ordinal) ||
                _slots[requested.Slot].PublishedController == 0 ||
                _slots[requested.Slot].Incarnation != requested.Incarnation)
            {
                reason = $"controller_presentation_not_applied:{requested.Slot}";
                return false;
            }

            var player = Utilities.GetPlayerFromSlot(requested.Slot);
            if (player is not { IsValid: true } ||
                _slots[requested.Slot].PublishedController != player.EntityHandle.Raw ||
                !_client.TryGetSlot(requested.Slot, out var native))
            {
                reason = $"controller_presentation_not_applied:{requested.Slot}";
                return false;
            }

            var playerNameMatches = requested.PlayerName == null ||
                                    (player.PlayerName.Equals(requested.PlayerName, StringComparison.Ordinal) &&
                                     native.ReadName().Equals(requested.PlayerName, StringComparison.Ordinal));
            var steamIdMatches = !requested.SteamId.HasValue ||
                                 (player.SteamID == requested.SteamId.Value &&
                                  native.SteamId == requested.SteamId.Value);
            var scoreboardFlairMatches = !requested.ScoreboardFlair.HasValue ||
                                         ScoreboardFlairMatches(player, requested.ScoreboardFlair.Value);
            var crosshairMatches = RequestedCrosshairMatches(
                requested.CrosshairCode,
                player.CrosshairCodes,
                _slots[requested.Slot].CrosshairPending);
            if (!CanCommitSynchronousPresentationLease(
                    playerNameMatches,
                    steamIdMatches,
                    scoreboardFlairMatches,
                    crosshairMatches))
            {
                reason = $"controller_presentation_not_applied:{requested.Slot}";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    internal static bool RequestedCrosshairMatches(string? requested, string? actual, bool publicationPending = false)
        => requested == null ||
           (!publicationPending && string.Equals(actual ?? string.Empty, requested, StringComparison.Ordinal));

    internal static bool CanCommitSynchronousPresentationLease(
        bool playerNameMatches,
        bool steamIdMatches,
        bool scoreboardFlairMatches,
        bool crosshairMatches)
    {
        return playerNameMatches &&
               steamIdMatches &&
               scoreboardFlairMatches && crosshairMatches;
    }

    private BotHiderPresentationLeaseResult Success(PresentationLease lease)
    {
        ScheduleScoreboardFlairRepublish(lease);
        return new BotHiderPresentationLeaseResult
        {
            Ok = true,
            LeaseToken = lease.Token,
            ProviderEpoch = $"{_providerEpoch}:{_nativeSession:x}",
            Reason = "ok",
            Slots = lease.Overrides.Keys.Order().ToArray()
        };
    }

    private void ScheduleScoreboardFlairRepublish(PresentationLease lease)
    {
        var slots = lease.Overrides.Values
            .Where(requested => requested.ScoreboardFlair.HasValue)
            .Select(requested => requested.Slot)
            .ToArray();
        if (slots.Length == 0)
            return;

        var leaseToken = lease.Token;
        Server.NextFrame(() =>
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                foreach (var slot in slots)
                {
                    if (_leaseBySlot.TryGetValue(slot, out var currentToken) &&
                        currentToken.Equals(leaseToken, StringComparison.Ordinal))
                    {
                        _slots[slot].FlairPending = true;
                    }
                }
            }
            PublishManagedSlots();
        });
    }

    private BotHiderPresentationLeaseResult Fail(string reason)
    {
        return new BotHiderPresentationLeaseResult
        {
            Ok = false,
            ProviderEpoch = $"{_providerEpoch}:{_nativeSession:x}",
            Reason = reason
        };
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _draining = true;
            foreach (var token in _leases.Keys.ToArray())
                RemoveLease(token, countRevocation: true);
        }

        PublishManagedSlots();
        lock (_sync)
        {
            Array.Clear(_slots);
            _disposed = true;
        }
    }

    internal sealed record PresentationLease(
        string Token,
        string Owner,
        Dictionary<int, BotHiderPresentationOverride> Overrides,
        CancellationToken OwnerLifetime)
    {
        public CancellationTokenRegistration OwnerRegistration { get; set; }
    }

    internal struct SlotState
    {
        public int UserId;
        public ulong Incarnation, NativeIncarnation;
        public uint Controller, PublishedController;
        public bool CrosshairPending, FlairManaged, FlairPending;
        public DateTime NextFailureLogUtc;
        public int SuppressedFailures;

        public readonly bool NeedsCrosshairPublication(uint controller)
            => PublishedController == 0 || PublishedController != controller || CrosshairPending;
    }
}
