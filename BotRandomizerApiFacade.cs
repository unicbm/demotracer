using BotRandomizerApi;
using CounterStrikeSharp.API;

namespace BotRandomizer;

public sealed partial class BotRandomizerPlugin
{
    private sealed class BotRandomizerApiFacade(BotRandomizerPlugin plugin) : IBotRandomizerApi
    {
        private BotRandomizerPlugin Provider
        {
            get
            {
                if (Environment.CurrentManagedThreadId != plugin._serverThreadId)
                    throw new InvalidOperationException("BotRandomizer API calls must run on the server thread.");
                return plugin;
            }
        }

        public int ApiVersion => BotRandomizerContract.ApiVersion;
        public BotRandomizerProviderInfo GetProviderInfo() => Provider.GetProviderInfoForApi();
        public bool TryGetManagedBot(int slot, out BotRandomizerManagedBot state)
            => Provider.TryGetManagedBotForApi(slot, out state);
        public BotRandomizerReplayPlanResult AcquireReplayPlan(
            string owner,
            BotRandomizerReplayCosmeticPlan[] plans,
            CancellationToken ownerLifetime)
            => Provider.AcquireReplayPlanForApi(owner, plans, ownerLifetime);
        public BotRandomizerReplayPlanResult ReplaceReplayPlan(
            string planToken,
            BotRandomizerReplayCosmeticPlan[] plans)
            => Provider.ReplaceReplayPlanForApi(planToken, plans);
        public bool ReleaseReplayPlan(string planToken) => Provider.ReleaseReplayPlanForApi(planToken);
        public int ReleaseReplayPlansByOwner(string owner) => Provider.ReleaseReplayPlansByOwnerForApi(owner);
        public BotRandomizerDiagnostics GetDiagnostics() => Provider.GetDiagnosticsForApi();
    }

    private BotRandomizerProviderInfo GetProviderInfoForApi()
        => new()
        {
            ApiVersion = BotRandomizerContract.ApiVersion,
            ProviderEpoch = _providerEpoch,
            MapEpoch = _mapEpoch,
            Ready = !_draining && _catalog is not null && _replayEconIndex is not null && _roller is not null,
            Draining = _draining,
            EconAttributeWriterAvailable = _applicator?.NativeAvailable == true,
            WeaponPrebuildAvailable = _weaponItemViews?.NativeAvailable == true,
            ReplayPlanPrebuildAvailable = _weaponItemViews?.NativeAvailable == true,
            CatalogRepository = _catalog?.SourceRepository ?? string.Empty,
            CatalogCommit = _catalog?.SourceCommit ?? string.Empty
        };

    private bool TryGetManagedBotForApi(int slot, out BotRandomizerManagedBot result)
    {
        result = new BotRandomizerManagedBot { Slot = slot };
        if (_draining || slot is < 0 or >= 64)
            return false;

        var player = Utilities.GetPlayerFromSlot(slot);
        var state = GetOrCreateState(player);
        if (player is not { IsValid: true, IsBot: true, IsHLTV: false } || state is null)
            return false;

        var hasPlan = _writeLeases.TryGetPolicy(slot, state.Incarnation, out _, out var owner);
        var pawn = player.PlayerPawn?.Value;
        result = new BotRandomizerManagedBot
        {
            Slot = slot,
            UserId = state.UserId,
            Incarnation = state.Incarnation,
            SteamId = player.SteamID,
            PawnEntityIndex = pawn is { IsValid: true } ? (int)pawn.Index : -1,
            Team = state.Loadout.Team,
            HasReplayPlan = hasPlan,
            ReplayPlanOwner = owner
        };
        return true;
    }

    private BotRandomizerReplayPlanResult AcquireReplayPlanForApi(
        string owner,
        BotRandomizerReplayCosmeticPlan[] plans,
        CancellationToken ownerLifetime)
    {
        if (_draining)
            return FailReplayPlan("provider_draining");
        if (!TryNormalizeReplayPlans(plans, out var normalized, out var reason))
        {
            _writeLeases.RecordRejectedRequest();
            return FailReplayPlan(reason);
        }
        if (!_writeLeases.TryAcquire(owner ?? string.Empty, normalized, ownerLifetime, out var lease, out reason))
            return FailReplayPlan(reason);

        InvalidateLeasePolicySlots(lease.Claims.Keys);
        return SuccessReplayPlan(lease);
    }

    private BotRandomizerReplayPlanResult ReplaceReplayPlanForApi(
        string planToken,
        BotRandomizerReplayCosmeticPlan[] plans)
    {
        if (_draining)
            return FailReplayPlan("provider_draining");
        if (!TryNormalizeReplayPlans(plans, out var normalized, out var reason))
        {
            _writeLeases.RecordRejectedRequest();
            return FailReplayPlan(reason);
        }
        if (!_writeLeases.TryReplace(
                planToken ?? string.Empty,
                normalized,
                out var lease,
                out var affectedSlots,
                out reason))
        {
            return FailReplayPlan(reason);
        }

        InvalidateLeasePolicySlots(affectedSlots);
        return SuccessReplayPlan(lease);
    }

    private bool ReleaseReplayPlanForApi(string planToken)
    {
        if (!_writeLeases.TryRelease(planToken ?? string.Empty, out var affectedSlots))
            return false;
        InvalidateLeasePolicySlots(affectedSlots);
        return true;
    }

    private int ReleaseReplayPlansByOwnerForApi(string owner)
    {
        var released = _writeLeases.ReleaseOwner(owner ?? string.Empty, out var affectedSlots);
        if (released > 0)
            InvalidateLeasePolicySlots(affectedSlots);
        return released;
    }

    private BotRandomizerDiagnostics GetDiagnosticsForApi()
    {
        var counters = _writeLeases.GetCounters();
        return new BotRandomizerDiagnostics
        {
            Ready = !_draining && _catalog is not null && _replayEconIndex is not null && _roller is not null,
            ActivePlans = counters.ActiveLeases,
            PlannedSlots = counters.LeasedSlots,
            AcquiredPlans = counters.AcquiredLeases,
            ReplacedPlans = counters.ReplacedLeases,
            ReleasedPlans = counters.ReleasedLeases,
            RevokedPlans = counters.RevokedLeases,
            RejectedRequests = counters.RejectedRequests
        };
    }

    private bool TryNormalizeReplayPlans(
        BotRandomizerReplayCosmeticPlan[]? requestedPlans,
        out IReadOnlyDictionary<int, LeasedCosmeticWriteClaim> normalized,
        out string reason)
    {
        if (_catalog is null || _replayEconIndex is null || _roller is null)
        {
            normalized = new Dictionary<int, LeasedCosmeticWriteClaim>();
            reason = "provider_not_ready";
            return false;
        }
        return new ReplayPlanValidator(_catalog, _replayEconIndex).TryNormalize(
            requestedPlans, ResolveReplayPlanBot,
            requestedPlans is { Length: > 0 and <= 64 } && IsSwitchingTeamsAtRoundReset(),
            out normalized, out reason);
    }

    private ReplayPlanBot? ResolveReplayPlanBot(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        var state = GetOrCreateState(player);
        return player is { IsValid: true, IsBot: true, IsHLTV: false } && state is not null
            ? new ReplayPlanBot(state.Incarnation, state.Loadout.Team)
            : null;
    }

    private void InvalidateLeasePolicySlots(IEnumerable<int> slots)
    {
        foreach (var slot in slots.Distinct())
        {
            // Plan transitions never mutate the current pawn. They only cancel
            // callbacks captured under the old generation. The provider consumes
            // the new desired state at the next natural spawn/item construction.
            _states.BumpGeneration(slot);
            _applicator?.ClearSlot(slot);
        }
    }

    private BotRandomizerReplayPlanResult SuccessReplayPlan(CosmeticWriteLease lease)
        => new()
        {
            Ok = true,
            PlanToken = lease.Token,
            ProviderEpoch = _providerEpoch,
            Reason = "accepted_for_next_spawn",
            Slots = lease.Claims.Keys.Order().ToArray(),
            AppliesOnNextSpawn = true
        };

    private BotRandomizerReplayPlanResult FailReplayPlan(string reason)
        => new() { Ok = false, ProviderEpoch = _providerEpoch, Reason = reason };
}
