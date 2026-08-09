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
    private static readonly Version MaxVerifiedManagedSchemaPatch = new(1, 41, 7, 3);
    private static readonly Lazy<(bool Allowed, string Patch)> ManagedSchemaRuntime =
        new(DetectManagedSchemaRuntime);
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PresentationFailureLogInterval = TimeSpan.FromSeconds(30);

    private readonly SharedMemoryClient _client;
    private readonly object _sync = new();
    private readonly string _providerEpoch = Guid.NewGuid().ToString("N");
    private readonly bool[] _observedManaged = new bool[MaxSlots];
    private readonly int[] _observedUserIds = Enumerable.Repeat(int.MinValue, MaxSlots).ToArray();
    private readonly ulong[] _slotIncarnations = new ulong[MaxSlots];
    private readonly AppliedPresentation?[] _applied = new AppliedPresentation?[MaxSlots];
    private readonly bool[] _scoreboardFlairManaged = new bool[MaxSlots];
    private readonly bool[] _scoreboardFlairRepublishPending = new bool[MaxSlots];
    private readonly DateTime[] _nextPresentationFailureLogUtc = new DateTime[MaxSlots];
    private readonly int[] _suppressedPresentationFailures = new int[MaxSlots];
    private readonly Dictionary<string, PresentationLease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _leaseBySlot = new();
    private ulong _nextIncarnation;
    private ulong _mapEpoch = 1;
    private bool _draining;
    private bool _disposed;
    private int _revokedLeases;
    private int _expiredLeases;
    private int _publishedWrites;
    private int _controllerRepairs;

    public BotHiderPresentationService(SharedMemoryClient client)
    {
        _client = client;
    }

    public int ApiVersion => DemoTracerBotHiderContract.ApiVersion;

    internal static bool ManagedSchemaWritesAllowed => ManagedSchemaRuntime.Value.Allowed;
    internal static string DetectedCs2Patch => ManagedSchemaRuntime.Value.Patch;

    public BotHiderProviderInfo GetProviderInfo()
    {
        lock (_sync)
        {
            return new BotHiderProviderInfo
            {
                ApiVersion = ApiVersion,
                ProviderEpoch = _providerEpoch,
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
            released = RemoveLease(leaseToken, countRevocation: false);

        if (released)
            PublishManagedSlots();
        return released;
    }

    public int ReleasePresentationLeasesByOwner(string owner)
    {
        string[] tokens;
        lock (_sync)
        {
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
        if (slot is < 0 or >= MaxSlots)
            return;

        var revoked = false;
        lock (_sync)
        {
            if (_leaseBySlot.TryGetValue(slot, out var token))
                revoked = RemoveLease(token, countRevocation: true);
            _observedManaged[slot] = false;
            _observedUserIds[slot] = int.MinValue;
            _slotIncarnations[slot] = 0;
            _applied[slot] = null;
            _scoreboardFlairManaged[slot] = false;
            _scoreboardFlairRepublishPending[slot] = false;
        }

        // A lease can cover several slots. Restore the remaining slots now
        // instead of waiting for the periodic publisher after one disconnects.
        if (revoked)
            PublishManagedSlots();
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

    private bool TryReadManagedSlot(int slot, out BotHiderManagedSlot state)
    {
        state = new BotHiderManagedSlot { Slot = slot };
        if (_disposed || slot is < 0 or >= MaxSlots || !_client.IsManagedBot(slot))
        {
            ObserveUnmanaged(slot);
            return false;
        }

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true })
        {
            ObserveUnmanaged(slot);
            return false;
        }

        var userId = player.UserId ?? -1;
        if (!_observedManaged[slot] || _observedUserIds[slot] != userId)
        {
            if (_leaseBySlot.TryGetValue(slot, out var staleToken))
                RemoveLease(staleToken, countRevocation: true);
            _observedManaged[slot] = true;
            _observedUserIds[slot] = userId;
            _slotIncarnations[slot] = ++_nextIncarnation;
            _applied[slot] = null;
            _scoreboardFlairManaged[slot] = false;
            _scoreboardFlairRepublishPending[slot] = false;
        }

        state = new BotHiderManagedSlot
        {
            Slot = slot,
            Incarnation = _slotIncarnations[slot],
            BaseSteamId = _client.GetBaseSteamId(slot),
            BasePlayerName = _client.GetBasePersonaName(slot),
            BasePing = _client.GetPing(slot),
            BaseCrosshairCode = _client.GetCrosshairCode(slot),
            BaseScoreboardFlair = _client.GetScoreboardFlair(slot)
        };
        return true;
    }

    private void ObserveUnmanaged(int slot)
    {
        if (slot is < 0 or >= MaxSlots)
            return;
        if (_leaseBySlot.TryGetValue(slot, out var token))
            RemoveLease(token, countRevocation: true);
        _observedManaged[slot] = false;
        _observedUserIds[slot] = int.MinValue;
        _slotIncarnations[slot] = 0;
        _applied[slot] = null;
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
                (playerName.Length == 0 ||
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

    private void PublishSlot(BotHiderManagedSlot state)
    {
        BotHiderPresentationOverride? presentationOverride = null;
        if (_leaseBySlot.TryGetValue(state.Slot, out var token) &&
            _leases.TryGetValue(token, out var lease) &&
            lease.Overrides.TryGetValue(state.Slot, out var candidate) &&
            candidate.Incarnation == state.Incarnation)
        {
            presentationOverride = candidate;
        }

        var effective = new AppliedPresentation(
            state.Incarnation,
            presentationOverride?.PlayerName ?? state.BasePlayerName,
            presentationOverride?.SteamId ?? state.BaseSteamId,
            state.BasePing,
            presentationOverride?.CrosshairCode ?? state.BaseCrosshairCode,
            presentationOverride?.ScoreboardFlair ?? state.BaseScoreboardFlair);
        var effectiveScoreboardFlairManaged = presentationOverride?.ScoreboardFlair.HasValue == true;

        var player = Utilities.GetPlayerFromSlot(state.Slot);
        if (player is not { IsValid: true })
            return;

        var previous = _applied[state.Slot];
        var forceNativeIdentityReassert = RequiresNativeIdentityReassert(
            previous.HasValue,
            previous?.Incarnation ?? 0,
            effective.Incarnation);
        try
        {
            // Controller identity is the synchronous presentation truth. The
            // native shared-memory command is only the userinfo companion; it
            // must never turn a queued/published cache value into a successful
            // lease while the visible controller still has the base identity.
            var controllerNameMismatch = !player.PlayerName.Equals(
                effective.PlayerName,
                StringComparison.Ordinal);
            var nativeNameMismatch = !_client.GetPublishedPersonaName(state.Slot).Equals(
                effective.PlayerName,
                StringComparison.Ordinal);
            var nativeNameQueued = true;
            if (forceNativeIdentityReassert || nativeNameMismatch || controllerNameMismatch)
            {
                nativeNameQueued = _client.SetPublishedPersonaName(state.Slot, effective.PlayerName);
                if (controllerNameMismatch)
                {
                    player.PlayerName = effective.PlayerName;
                    Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName");
                    if (!player.PlayerName.Equals(effective.PlayerName, StringComparison.Ordinal))
                        throw new InvalidOperationException("controller persona write was not retained");
                    _publishedWrites++;
                    _controllerRepairs++;
                }
                if (nativeNameQueued && (forceNativeIdentityReassert || nativeNameMismatch))
                    _publishedWrites++;
            }

            var controllerSteamIdMismatch = player.SteamID != effective.SteamId;
            var nativeSteamIdMismatch = _client.GetPublishedSteamId(state.Slot) != effective.SteamId;
            var nativeSteamIdQueued = true;
            if (forceNativeIdentityReassert || nativeSteamIdMismatch || controllerSteamIdMismatch)
            {
                nativeSteamIdQueued = _client.SetPublishedSteamId(state.Slot, effective.SteamId);
                if (controllerSteamIdMismatch)
                {
                    Schema.SetSchemaValue(
                        player.Handle,
                        "CBasePlayerController",
                        "m_steamID",
                        effective.SteamId);
                    Utilities.SetStateChanged(player, "CBasePlayerController", "m_steamID");
                    if (player.SteamID != effective.SteamId)
                        throw new InvalidOperationException("controller SteamID write was not retained");
                    _publishedWrites++;
                    _controllerRepairs++;
                }
                if (nativeSteamIdQueued && (forceNativeIdentityReassert || nativeSteamIdMismatch))
                    _publishedWrites++;
            }

            var expectedPing = checked((uint)Math.Max(effective.Ping, 0));
            if (player.Ping != expectedPing)
            {
                // Keep the exact schema lane used by the last known-good
                // identity build; the property is read back only to verify it.
                Schema.SetSchemaValue(
                    player.Handle,
                    "CCSPlayerController",
                    "m_iPing",
                    effective.Ping);
                Utilities.SetStateChanged(player, "CCSPlayerController", "m_iPing");
                if (player.Ping != expectedPing)
                    throw new InvalidOperationException("controller ping write was not retained");
                _publishedWrites++;
                _controllerRepairs++;
            }

            if (ManagedSchemaWritesAllowed &&
                IsNetworkedSchemaField("CCSPlayerController", "m_szCrosshairCodes"))
            {
                var currentCrosshair = player.CrosshairCodes ?? string.Empty;
                if (!currentCrosshair.Equals(effective.CrosshairCode, StringComparison.Ordinal))
                {
                    player.CrosshairCodes = effective.CrosshairCode;
                    Utilities.SetStateChanged(player, "CCSPlayerController", "m_szCrosshairCodes");
                    _publishedWrites++;
                    _controllerRepairs++;
                }
            }

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
            _suppressedPresentationFailures[state.Slot] = 0;
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
        return ranks.Length > 0 && ranks.ToArray().All(rank => (uint)rank == itemDefIndex);
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
        TrySetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
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

    private static (bool Allowed, string Patch) DetectManagedSchemaRuntime()
    {
        try
        {
            var steamInfPath = Path.Combine(Server.GameDirectory, "steam.inf");
            var patchLine = File.ReadLines(steamInfPath)
                .FirstOrDefault(line => line.StartsWith("PatchVersion=", StringComparison.OrdinalIgnoreCase));
            var patch = patchLine?["PatchVersion=".Length..].Trim() ?? "unknown";
            return Version.TryParse(patch, out var current) &&
                   current.CompareTo(MaxVerifiedManagedSchemaPatch) <= 0
                ? (true, patch)
                : (false, patch);
        }
        catch
        {
            return (false, "unknown");
        }
    }

    private static void TrySetStateChanged(
        CBaseEntity entity,
        string className,
        string fieldName,
        int extraOffset = 0)
    {
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

    private void AddLease(PresentationLease lease)
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

    internal static bool RequiresNativeIdentityReassert(
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
                                    player.PlayerName.Equals(requested.PlayerName, StringComparison.Ordinal);
            var steamIdMatches = !requested.SteamId.HasValue ||
                                 player.SteamID == requested.SteamId.Value;
            var pingMatches = player.Ping ==
                              checked((uint)Math.Max(_applied[requested.Slot]!.Value.Ping, 0));
            var scoreboardFlairMatches = !requested.ScoreboardFlair.HasValue ||
                                         ScoreboardFlairMatches(player, requested.ScoreboardFlair.Value);
            var crosshairMatches = RequestedCrosshairMatches(
                requested.CrosshairCode,
                player.CrosshairCodes);
            if (!CanCommitSynchronousPresentationLease(
                    playerNameMatches,
                    steamIdMatches,
                    pingMatches,
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

    internal static bool RequestedCrosshairMatches(string? requested, string? actual)
        => requested == null ||
           string.Equals(actual ?? string.Empty, requested, StringComparison.Ordinal);

    internal static bool CanCommitSynchronousPresentationLease(
        bool playerNameMatches,
        bool steamIdMatches,
        bool pingMatches,
        bool scoreboardFlairMatches,
        bool crosshairMatches)
    {
        // Name and SteamID are the core identity transaction. Crosshair is an
        // optional, compatibility-gated field that the periodic publisher can
        // keep repairing; it must never roll an otherwise valid identity lease
        // back to the bot's base persona.
        _ = crosshairMatches;
        return playerNameMatches &&
               steamIdMatches &&
               pingMatches &&
               scoreboardFlairMatches;
    }

    private BotHiderPresentationLeaseResult Success(PresentationLease lease)
    {
        ScheduleScoreboardFlairRepublish(lease);
        return new BotHiderPresentationLeaseResult
        {
            Ok = true,
            LeaseToken = lease.Token,
            ProviderEpoch = _providerEpoch,
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
            ProviderEpoch = _providerEpoch,
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

    private sealed record PresentationLease(
        string Token,
        string Owner,
        Dictionary<int, BotHiderPresentationOverride> Overrides,
        DateTime LastHeartbeatUtc);

    private readonly record struct AppliedPresentation(
        ulong Incarnation,
        string PlayerName,
        ulong SteamId,
        int Ping,
        string CrosshairCode,
        uint ScoreboardFlair);
}
