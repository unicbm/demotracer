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
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PresentationFailureLogInterval = TimeSpan.FromSeconds(30);

    private readonly NativePresentationClient _client;
    private readonly object _sync = new();
    private readonly string _providerEpoch = Guid.NewGuid().ToString("N");
    private readonly bool[] _observedManaged = new bool[MaxSlots];
    private readonly int[] _observedUserIds = Enumerable.Repeat(int.MinValue, MaxSlots).ToArray();
    private readonly ulong[] _slotIncarnations = new ulong[MaxSlots];
    private readonly AppliedPresentation?[] _applied = new AppliedPresentation?[MaxSlots];
    private readonly uint[] _appliedControllerHandles = new uint[MaxSlots];
    private readonly bool[] _crosshairPublicationPending = new bool[MaxSlots];
    private readonly bool[] _scoreboardFlairManaged = new bool[MaxSlots];
    private readonly bool[] _scoreboardFlairRepublishPending = new bool[MaxSlots];
    private readonly DateTime[] _nextPresentationFailureLogUtc = new DateTime[MaxSlots];
    private readonly int[] _suppressedPresentationFailures = new int[MaxSlots];
    private readonly Dictionary<string, PresentationLease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _leaseBySlot = new();
    private ulong _nextIncarnation;
    private ulong _nativeSession;
    private readonly ulong[] _nativeIncarnations = new ulong[MaxSlots];
    private ulong _mapEpoch = 1;
    private bool _draining;
    private bool _disposed;
    private int _revokedLeases;
    private int _expiredLeases;
    private int _publishedWrites;
    private int _controllerRepairs;

    public BotHiderPresentationService(NativePresentationClient client)
    {
        _client = client;
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
            return TryReadManagedSlot(slot, out _);
    }

    public bool TryGetManagedSlot(int slot, out BotHiderManagedSlot state)
    {
        lock (_sync)
            return TryReadManagedSlot(slot, out state);
    }

    public BotHiderPresentationLeaseResult AcquirePresentationLease(
        string owner,
        BotHiderPresentationOverride[] overrides)
    {
        lock (_sync)
        {
            ObserveNativeSession();
            if (_draining || _disposed)
                return Fail("provider_draining");

            owner = owner?.Trim() ?? string.Empty;
            if (owner.Length == 0 || owner.Length > MaxOwnerLength)
                return Fail("invalid_owner");

            if (!TryNormalizeOverrides(overrides, allowedLeaseToken: null, out var normalized, out var reason))
                return Fail(reason);

            var token = $"{_providerEpoch}:{Guid.NewGuid():N}";
            var lease = new PresentationLease(token, owner, normalized, DateTime.UtcNow);
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
                Overrides = normalized,
                LastHeartbeatUtc = DateTime.UtcNow
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

    public bool HeartbeatPresentationLease(string leaseToken)
    {
        lock (_sync)
        {
            ObserveNativeSession();
            if (_draining || _disposed ||
                string.IsNullOrWhiteSpace(leaseToken) ||
                !_leases.TryGetValue(leaseToken, out var lease))
            {
                return false;
            }

            _leases[leaseToken] = lease with { LastHeartbeatUtc = DateTime.UtcNow };
            return true;
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
                if (TryReadManagedSlot(slot, out _))
                    managed++;
            }

            return new BotHiderDiagnostics
            {
                Connected = !_disposed && _client.IsConnected(),
                ManagedSlots = managed,
                ActiveLeases = _leases.Count,
                LeasedSlots = _leaseBySlot.Count,
                RevokedLeases = _revokedLeases,
                ExpiredLeases = _expiredLeases,
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
            Array.Fill(_observedManaged, false);
            Array.Fill(_observedUserIds, int.MinValue);
            Array.Fill(_slotIncarnations, 0UL);
            Array.Fill(_applied, null);
            Array.Clear(_appliedControllerHandles);
            Array.Clear(_crosshairPublicationPending);
            Array.Fill(_scoreboardFlairManaged, false);
            Array.Fill(_scoreboardFlairRepublishPending, false);
        }
    }

    public void InvalidateSlot(int slot)
    {
        if (slot is < 0 or >= MaxSlots)
            return;
        lock (_sync)
            _applied[slot] = null;
    }

    public void HandleClientDisconnect(int slot)
    {
        lock (_sync)
            ObserveUnmanaged(slot);
    }

    public void InvalidateAll()
    {
        lock (_sync)
            Array.Fill(_applied, null);
    }

    public void PublishManagedSlots()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            SweepExpiredLeases();
            for (var slot = 0; slot < MaxSlots; slot++)
            {
                if (!TryReadManagedSlot(slot, out var state))
                {
                    _applied[slot] = null;
                    continue;
                }

                PublishSlot(state);
            }
        }
    }

    private void ObserveNativeSession()
    {
        var session = _client.Session;
        if (session == _nativeSession) return;
        foreach (var token in _leases.Keys.ToArray()) RemoveLease(token, countRevocation: true);
        Array.Fill(_observedManaged, false);
        Array.Fill(_applied, null);
        Array.Clear(_appliedControllerHandles);
        Array.Clear(_crosshairPublicationPending);
        Array.Fill(_nativeIncarnations, 0UL);
        _nativeSession = session;
    }

    private bool TryReadManagedSlot(int slot, out BotHiderManagedSlot state)
    {
        state = new BotHiderManagedSlot { Slot = slot };
        ObserveNativeSession();
        if (_disposed || slot is < 0 or >= MaxSlots || !_client.TryGetSlot(slot, out var native) || native.Managed == 0)
        {
            ObserveUnmanaged(slot);
            return false;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true, UserId: int userId })
        {
            ObserveUnmanaged(slot);
            return false;
        }

        if (!_observedManaged[slot] || _observedUserIds[slot] != userId ||
            _nativeIncarnations[slot] != native.Incarnation)
        {
            RemoveSlotPresentation(slot);
            _observedManaged[slot] = true;
            _observedUserIds[slot] = userId;
            _slotIncarnations[slot] = ++_nextIncarnation;
            _nativeIncarnations[slot] = native.Incarnation;
            _applied[slot] = null;
            _appliedControllerHandles[slot] = 0;
            _crosshairPublicationPending[slot] = false;
            _scoreboardFlairManaged[slot] = false;
            _scoreboardFlairRepublishPending[slot] = false;
        }

        state = new BotHiderManagedSlot
        {
            Slot = slot,
            Incarnation = _slotIncarnations[slot],
            BaseSteamId = native.BaseSteamId,
            BasePlayerName = native.ReadBaseName(),
            BasePing = native.Ping,
            BaseCrosshairCode = native.ReadCrosshair(),
            BaseScoreboardFlair = native.ScoreboardFlair
        };
        return true;
    }

    private void ObserveUnmanaged(int slot)
    {
        if (slot is < 0 or >= MaxSlots)
            return;
        RemoveSlotPresentation(slot);
        _observedManaged[slot] = false;
        _observedUserIds[slot] = int.MinValue;
        _slotIncarnations[slot] = 0;
        _applied[slot] = null;
        _appliedControllerHandles[slot] = 0;
        _crosshairPublicationPending[slot] = false;
        _scoreboardFlairManaged[slot] = false;
        _scoreboardFlairRepublishPending[slot] = false;
        ClearPresentationFailure(slot);
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
            if (_client.IsManagedBot(player.Slot))
            {
                var nativeSteamId = _client.GetPublishedSteamId(player.Slot);
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
                    var replacementSteamId = moving.SteamId ?? _client.GetBaseSteamId(player.Slot);
                    if (replacementSteamId != observedSteamId)
                        continue;
                }
                else if (!string.IsNullOrWhiteSpace(replacedLeaseToken) &&
                         _leases.TryGetValue(replacedLeaseToken, out var replacedLease) &&
                         replacedLease.Overrides.ContainsKey(player.Slot) &&
                         _client.GetBaseSteamId(player.Slot) != observedSteamId)
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
                   lease.Overrides.TryGetValue(slot, out var candidate) &&
                   candidate.Incarnation == incarnation
                ? candidate
                : null;
        }
    }

    private void PublishSlot(BotHiderManagedSlot state)
    {
        var presentationOverride = GetPresentationOverride(state.Slot, state.Incarnation);

        var effective = new AppliedPresentation(
            state.Incarnation,
            presentationOverride?.PlayerName ?? state.BasePlayerName,
            presentationOverride?.SteamId ?? state.BaseSteamId,
            presentationOverride?.CrosshairCode ?? state.BaseCrosshairCode,
            presentationOverride?.ScoreboardFlair ?? state.BaseScoreboardFlair);
        var effectiveScoreboardFlairManaged = presentationOverride?.ScoreboardFlair.HasValue == true;

        var player = Utilities.GetPlayerFromSlot(state.Slot);
        if (player is not { IsValid: true })
            return;

        var previous = _applied[state.Slot];
        var forceCrosshairPublication = RequiresCrosshairPublication(
            previous.HasValue,
            previous?.Incarnation ?? 0,
            effective.Incarnation) || _crosshairPublicationPending[state.Slot] ||
            _appliedControllerHandles[state.Slot] != player.EntityHandle.Raw;
        try
        {
            // Native userinfo and controller fields must both confirm the
            // effective identity before the lease can report success.
            var nativeNameMismatch = !_client.GetPublishedPersonaName(state.Slot).Equals(effective.PlayerName, StringComparison.Ordinal);
            var nativeSidMismatch = _client.GetPublishedSteamId(state.Slot) != effective.SteamId;
            if (!_client.PublishIdentity(state.Slot, _nativeSession, _nativeIncarnations[state.Slot],
                    effective.SteamId, effective.PlayerName))
                throw new InvalidOperationException("native identity publication rejected");
            if (nativeNameMismatch || nativeSidMismatch) _publishedWrites++;

            if (!player.PlayerName.Equals(effective.PlayerName, StringComparison.Ordinal))
            {
                player.PlayerName = effective.PlayerName;
                Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName");
                if (!player.PlayerName.Equals(effective.PlayerName, StringComparison.Ordinal))
                    throw new InvalidOperationException("controller name write was not retained");
                _publishedWrites++; _controllerRepairs++;
            }
            if (player.SteamID != effective.SteamId)
            {
                Schema.SetSchemaValue(player.Handle, "CBasePlayerController", "m_steamID", effective.SteamId);
                Utilities.SetStateChanged(player, "CBasePlayerController", "m_steamID");
                if (player.SteamID != effective.SteamId)
                    throw new InvalidOperationException("controller SteamID write was not retained");
                _publishedWrites++; _controllerRepairs++;
            }

            var expectedPing = checked((uint)Math.Max(state.BasePing, 0));
            if (player.Ping != expectedPing &&
                TryWriteEngineOwnedPing(player, state.BasePing))
            {
                // Ping is display-only engine state. Keep it outside identity
                // repair accounting and never publish it through SetStateChanged:
                // m_iPing is present in schema, but it is not networked.
                _publishedWrites++;
            }

            var crosshairSynchronized = TryWriteNetworkedCrosshair(
                    effective.CrosshairCode,
                    forceCrosshairPublication,
                    () => player.CrosshairCodes,
                    value => player.CrosshairCodes = value,
                    () => _client.PublishCrosshair(state.Slot, _nativeSession,
                        _nativeIncarnations[state.Slot], player.EntityHandle.Raw),
                    out var crosshairChanged,
                    out var crosshairPublished);
            _crosshairPublicationPending[state.Slot] = !crosshairSynchronized;
            if (crosshairChanged || crosshairPublished) _publishedWrites++;
            if (crosshairChanged) _controllerRepairs++;
            if (!crosshairSynchronized)
                ReportPresentationFailure(state.Slot, "crosshair notification pending");

            // A requested crosshair requires native notification submission as
            // well as readback. This is not a remote client's rendering ACK.
            var scoreboardFlairNeedsWrite = effectiveScoreboardFlairManaged ||
                                             _scoreboardFlairManaged[state.Slot];
            if (scoreboardFlairNeedsWrite)
            {
                var scoreboardFlairSynchronized = ScoreboardFlairMatches(
                    player,
                    effective.ScoreboardFlair);
                var scoreboardFlairNeedsPublish = ShouldPublishScoreboardFlair(
                    scoreboardFlairSynchronized,
                    _scoreboardFlairRepublishPending[state.Slot]);
                if (scoreboardFlairNeedsPublish &&
                    ApplyScoreboardFlair(player, effective.ScoreboardFlair))
                {
                    scoreboardFlairSynchronized = ScoreboardFlairMatches(
                        player,
                        effective.ScoreboardFlair);
                    _publishedWrites++;
                    _controllerRepairs++;
                }

                if (scoreboardFlairSynchronized)
                {
                    _scoreboardFlairManaged[state.Slot] = effectiveScoreboardFlairManaged;
                    _scoreboardFlairRepublishPending[state.Slot] = false;
                }
                else
                    throw new InvalidOperationException("controller scoreboard flair write was not retained");
            }

            _applied[state.Slot] = effective;
            _appliedControllerHandles[state.Slot] = player.EntityHandle.Raw;
            if (crosshairSynchronized) _suppressedPresentationFailures[state.Slot] = 0;
        }
        catch (Exception ex)
        {
            _applied[state.Slot] = null;
            ReportPresentationFailure(state.Slot, ex.Message);
        }
    }

    private void ReportPresentationFailure(int slot, string reason)
    {
        var now = DateTime.UtcNow;
        if (now >= _nextPresentationFailureLogUtc[slot])
        {
            var suppressed = _suppressedPresentationFailures[slot];
            var suffix = suppressed > 0 ? $" (suppressed={suppressed})" : string.Empty;
            Server.PrintToConsole(
                $"[DemoTracer BotHider] presentation write failed slot={slot}: {reason}{suffix}");
            _nextPresentationFailureLogUtc[slot] = now + PresentationFailureLogInterval;
            _suppressedPresentationFailures[slot] = 0;
            return;
        }

        _suppressedPresentationFailures[slot]++;
    }

    private void ClearPresentationFailure(int slot)
    {
        _nextPresentationFailureLogUtc[slot] = default;
        _suppressedPresentationFailures[slot] = 0;
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

    private static bool TryWriteEngineOwnedPing(CCSPlayerController player, int ping)
    {
        try
        {
            Schema.SetSchemaValue(
                player.Handle,
                "CCSPlayerController",
                "m_iPing",
                Math.Max(ping, 0));
            return true;
        }
        catch
        {
            // Cosmetic ping must never fail or roll back an identity lease.
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

    private void SweepExpiredLeases()
    {
        var now = DateTime.UtcNow;
        foreach (var lease in _leases.Values.ToArray())
        {
            if (now - lease.LastHeartbeatUtc <= LeaseTimeout)
                continue;
            if (RemoveLease(lease.Token, countRevocation: true))
                _expiredLeases++;
        }
    }

    internal void AddLease(PresentationLease lease)
    {
        _leases.Add(lease.Token, lease);
        AddLeaseMappings(lease);
        InvalidateSlots(lease.Overrides.Keys);
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
                _applied[slot] = null;
        }
    }

    internal static bool RequiresCrosshairPublication(
        bool hasAppliedPresentation,
        ulong appliedIncarnation,
        ulong effectiveIncarnation)
        => !hasAppliedPresentation || appliedIncarnation != effectiveIncarnation;

    private bool IsLeaseAppliedSynchronously(PresentationLease lease, out string reason)
    {
        foreach (var requested in lease.Overrides.Values)
        {
            if (!_leaseBySlot.TryGetValue(requested.Slot, out var token) ||
                !token.Equals(lease.Token, StringComparison.Ordinal) ||
                !_applied[requested.Slot].HasValue ||
                _applied[requested.Slot]!.Value.Incarnation != requested.Incarnation)
            {
                reason = $"controller_presentation_not_applied:{requested.Slot}";
                return false;
            }

            var player = Utilities.GetPlayerFromSlot(requested.Slot);
            if (player is not { IsValid: true })
            {
                reason = $"controller_presentation_not_applied:{requested.Slot}";
                return false;
            }

            var playerNameMatches = requested.PlayerName == null ||
                                    (player.PlayerName.Equals(requested.PlayerName, StringComparison.Ordinal) &&
                                     _client.GetPublishedPersonaName(requested.Slot).Equals(requested.PlayerName, StringComparison.Ordinal));
            var steamIdMatches = !requested.SteamId.HasValue ||
                                 (player.SteamID == requested.SteamId.Value &&
                                  _client.GetPublishedSteamId(requested.Slot) == requested.SteamId.Value);
            var scoreboardFlairMatches = !requested.ScoreboardFlair.HasValue ||
                                         ScoreboardFlairMatches(player, requested.ScoreboardFlair.Value);
            var crosshairMatches = RequestedCrosshairMatches(
                requested.CrosshairCode,
                player.CrosshairCodes,
                _crosshairPublicationPending[requested.Slot]);
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
                        _scoreboardFlairRepublishPending[slot] = true;
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
            Array.Fill(_applied, null);
            Array.Fill(_scoreboardFlairManaged, false);
            Array.Fill(_scoreboardFlairRepublishPending, false);
            _disposed = true;
        }
    }

    internal sealed record PresentationLease(
        string Token,
        string Owner,
        Dictionary<int, BotHiderPresentationOverride> Overrides,
        DateTime LastHeartbeatUtc);

    private readonly record struct AppliedPresentation(
        ulong Incarnation,
        string PlayerName,
        ulong SteamId,
        string CrosshairCode,
        uint ScoreboardFlair);
}
