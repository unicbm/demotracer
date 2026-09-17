using BotRandomizerApi;

namespace BotRandomizer;

internal sealed class CosmeticWritePolicy
{
    internal CosmeticWritePolicy(
        byte spawnTeam,
        BotRandomizerAgentPlanMode agentMode,
        string? agentModel,
        ReplayItemSelection? knife,
        ReplayItemSelection? gloves,
        int? musicKit,
        IReadOnlyDictionary<ushort, ReplayWeaponSelection> weapons)
    {
        SpawnTeam = spawnTeam;
        AgentMode = agentMode;
        AgentModel = agentModel;
        Knife = knife;
        Gloves = gloves;
        MusicKit = musicKit;
        Weapons = weapons;
    }

    internal byte SpawnTeam { get; }
    internal BotRandomizerAgentPlanMode AgentMode { get; }
    internal string? AgentModel { get; }
    internal ushort AgentItemDefinitionIndex { get; init; }
    internal ReplayItemSelection? Knife { get; }
    internal ReplayItemSelection? Gloves { get; }
    internal int? MusicKit { get; }
    internal IReadOnlyDictionary<ushort, ReplayWeaponSelection> Weapons { get; }
    internal bool ClaimsAnything =>
        AgentMode != BotRandomizerAgentPlanMode.Randomized ||
        Knife is not null || Gloves is not null || MusicKit is not null || Weapons.Count > 0;

    internal bool TryGetWeapon(ushort defIndex, out ReplayWeaponSelection policy)
        => Weapons.TryGetValue(defIndex, out policy!);
}

internal sealed record LeasedCosmeticWriteClaim(
    ulong Incarnation,
    ulong? SubjectSteamId,
    CosmeticWritePolicy Policy);

internal sealed record CosmeticWriteLease(
    string Token,
    string Owner,
    IReadOnlyDictionary<int, LeasedCosmeticWriteClaim> Claims,
    CancellationToken OwnerLifetime)
{
    internal CancellationTokenRegistration OwnerRegistration { get; set; }
}

internal readonly record struct CosmeticWriteLeaseCounters(
    int ActiveLeases,
    int LeasedSlots,
    int AcquiredLeases,
    int ReplacedLeases,
    int ReleasedLeases,
    int RevokedLeases,
    int RejectedRequests);

internal sealed class CosmeticWriteLeaseStore
{
    private const int MaximumOwnerLength = 64;
    private readonly object _sync = new();
    private readonly Action<int[]> _ownerReleased;
    private readonly string _providerEpoch;
    private readonly Dictionary<string, CosmeticWriteLease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _leaseBySlot = [];

    private int _acquiredLeases;
    private int _replacedLeases;
    private int _releasedLeases;
    private int _revokedLeases;
    private int _rejectedRequests;

    internal CosmeticWriteLeaseStore(string providerEpoch, Action<int[]>? ownerReleased = null)
    {
        _providerEpoch = providerEpoch;
        _ownerReleased = ownerReleased ?? (_ => { });
    }

    internal bool TryAcquire(
        string owner,
        IReadOnlyDictionary<int, LeasedCosmeticWriteClaim> claims,
        CancellationToken ownerLifetime,
        out CosmeticWriteLease lease,
        out string reason)
    {
        lease = null!;
        reason = string.Empty;
        owner = owner.Trim();
        lock (_sync)
        {
            if (!ownerLifetime.CanBeCanceled || ownerLifetime.IsCancellationRequested)
                return Reject("owner_lifetime_inactive", out reason);
            if (owner.Length is 0 or > MaximumOwnerLength)
                return Reject("invalid_owner", out reason);
            if (claims.Count == 0)
                return Reject("no_positive_claims", out reason);
            foreach (var slot in claims.Keys)
            {
                if (_leaseBySlot.ContainsKey(slot))
                    return Reject($"slot_leased:{slot}", out reason);
            }

            var token = $"{_providerEpoch}:{Guid.NewGuid():N}";
            var storedClaims = claims.ToDictionary(pair => pair.Key, pair => pair.Value);
            lease = new CosmeticWriteLease(token, owner, storedClaims, ownerLifetime);
            _leases.Add(token, lease);
            AddMappings(lease);
            lease.OwnerRegistration = ownerLifetime.Register(() =>
            {
                if (TryRelease(token, out var slots)) _ownerReleased(slots);
            });
            if (ownerLifetime.IsCancellationRequested)
                return Reject("owner_lifetime_inactive", out reason);
            _acquiredLeases++;
            return true;
        }
    }

    internal bool TryReplace(
        string leaseToken,
        IReadOnlyDictionary<int, LeasedCosmeticWriteClaim> claims,
        out CosmeticWriteLease lease,
        out int[] affectedSlots,
        out string reason)
    {
        lease = null!;
        affectedSlots = [];
        reason = string.Empty;
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(leaseToken)
                || !_leases.TryGetValue(leaseToken, out var existing))
            {
                return Reject("lease_not_found", out reason);
            }
            if (claims.Count == 0)
                return Reject("no_positive_claims", out reason);
            foreach (var slot in claims.Keys)
            {
                if (_leaseBySlot.TryGetValue(slot, out var occupiedBy)
                    && !occupiedBy.Equals(leaseToken, StringComparison.Ordinal))
                {
                    return Reject($"slot_leased:{slot}", out reason);
                }
            }

            RemoveMappings(existing);
            var storedClaims = claims.ToDictionary(pair => pair.Key, pair => pair.Value);
            lease = existing with
            {
                Claims = storedClaims
            };
            _leases[leaseToken] = lease;
            AddMappings(lease);
            affectedSlots = existing.Claims.Keys.Concat(claims.Keys).Distinct().Order().ToArray();
            _replacedLeases++;
            return true;
        }
    }

    internal bool TryRelease(string leaseToken, out int[] affectedSlots)
    {
        lock (_sync)
        {
            if (!RemoveLease(leaseToken, out var lease))
            {
                affectedSlots = [];
                return false;
            }

            affectedSlots = lease.Claims.Keys.Order().ToArray();
            _releasedLeases++;
            return true;
        }
    }

    internal int ReleaseOwner(string owner, out int[] affectedSlots)
    {
        owner = owner.Trim();
        lock (_sync)
        {
            var tokens = _leases.Values
                .Where(lease => lease.Owner.Equals(owner, StringComparison.Ordinal))
                .Select(lease => lease.Token)
                .ToArray();
            var slots = new HashSet<int>();
            foreach (var token in tokens)
            {
                if (!RemoveLease(token, out var lease))
                    continue;
                foreach (var slot in lease.Claims.Keys)
                    slots.Add(slot);
                _releasedLeases++;
            }

            affectedSlots = slots.Order().ToArray();
            return tokens.Length;
        }
    }

    internal bool RevokeSlot(int slot)
    {
        lock (_sync)
        {
            if (!_leaseBySlot.Remove(slot, out var token)
                || !_leases.TryGetValue(token, out var lease))
                return false;

            // A disconnected participant leaves the batch without invalidating
            // the remaining bots' plans or their pending spawn callbacks.
            if (lease.Claims.Count == 1)
            {
                RemoveLease(token, out _);
                _revokedLeases++;
            }
            else
            {
                _leases[token] = lease with
                {
                    Claims = lease.Claims.Where(pair => pair.Key != slot)
                        .ToDictionary(pair => pair.Key, pair => pair.Value)
                };
            }
            return true;
        }
    }

    internal int[] Reset(bool countRevocation)
    {
        lock (_sync)
        {
            var slots = _leaseBySlot.Keys.Order().ToArray();
            if (countRevocation)
                _revokedLeases += _leases.Count;
            foreach (var lease in _leases.Values) lease.OwnerRegistration.Unregister();
            _leases.Clear();
            _leaseBySlot.Clear();
            return slots;
        }
    }

    internal bool TryGetPolicy(
        int slot,
        ulong incarnation,
        out CosmeticWritePolicy policy,
        out string owner)
    {
        lock (_sync)
        {
            if (_leaseBySlot.TryGetValue(slot, out var token)
                && _leases.TryGetValue(token, out var lease)
                && !lease.OwnerLifetime.IsCancellationRequested
                && lease.Claims.TryGetValue(slot, out var claim)
                && claim.Incarnation == incarnation)
            {
                policy = claim.Policy;
                owner = lease.Owner;
                return true;
            }
        }

        policy = null!;
        owner = string.Empty;
        return false;
    }

    internal CosmeticWriteLeaseCounters GetCounters()
    {
        lock (_sync)
        {
            return new CosmeticWriteLeaseCounters(
                _leases.Count,
                _leaseBySlot.Count,
                _acquiredLeases,
                _replacedLeases,
                _releasedLeases,
                _revokedLeases,
                _rejectedRequests);
        }
    }

    internal void RecordRejectedRequest()
    {
        lock (_sync)
            _rejectedRequests++;
    }

    private bool Reject(string value, out string reason)
    {
        _rejectedRequests++;
        reason = value;
        return false;
    }

    private void AddMappings(CosmeticWriteLease lease)
    {
        foreach (var slot in lease.Claims.Keys)
            _leaseBySlot[slot] = lease.Token;
    }

    private void RemoveMappings(CosmeticWriteLease lease)
    {
        foreach (var slot in lease.Claims.Keys)
        {
            if (_leaseBySlot.TryGetValue(slot, out var token)
                && token.Equals(lease.Token, StringComparison.Ordinal))
            {
                _leaseBySlot.Remove(slot);
            }
        }
    }

    private bool RemoveLease(string leaseToken, out CosmeticWriteLease lease)
    {
        lease = null!;
        if (string.IsNullOrWhiteSpace(leaseToken)
            || !_leases.Remove(leaseToken, out lease!))
        {
            return false;
        }

        lease.OwnerRegistration.Unregister();
        RemoveMappings(lease);
        return true;
    }
}
