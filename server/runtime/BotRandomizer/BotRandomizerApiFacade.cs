using BotRandomizerApi;
using CounterStrikeSharp.API;

namespace BotRandomizer;

public sealed partial class BotRandomizerPlugin
{
    private sealed class BotRandomizerApiFacade(BotRandomizerPlugin plugin) : IBotRandomizerApi
    {
        public int ApiVersion => BotRandomizerContract.ApiVersion;
        public BotRandomizerProviderInfo GetProviderInfo() => plugin.GetProviderInfoForApi();
        public bool TryGetManagedBot(int slot, out BotRandomizerManagedBot state)
            => plugin.TryGetManagedBotForApi(slot, out state);
        public BotRandomizerReplayPlanResult AcquireReplayPlan(
            string owner,
            BotRandomizerReplayCosmeticPlan[] plans)
            => plugin.AcquireReplayPlanForApi(owner, plans);
        public BotRandomizerReplayPlanResult ReplaceReplayPlan(
            string planToken,
            BotRandomizerReplayCosmeticPlan[] plans)
            => plugin.ReplaceReplayPlanForApi(planToken, plans);
        public bool HeartbeatReplayPlan(string planToken) => plugin._writeLeases.Heartbeat(planToken);
        public bool ReleaseReplayPlan(string planToken) => plugin.ReleaseReplayPlanForApi(planToken);
        public int ReleaseReplayPlansByOwner(string owner) => plugin.ReleaseReplayPlansByOwnerForApi(owner);
        public BotRandomizerDiagnostics GetDiagnostics() => plugin.GetDiagnosticsForApi();
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
            CatalogCommit = _catalog?.SourceCommit ?? string.Empty,
            LeaseTimeoutMilliseconds = BotRandomizerContract.LeaseTimeoutMilliseconds
        };

    private bool TryGetManagedBotForApi(int slot, out BotRandomizerManagedBot result)
    {
        result = new BotRandomizerManagedBot { Slot = slot };
        if (_draining)
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
        BotRandomizerReplayCosmeticPlan[] plans)
    {
        if (_draining)
            return FailReplayPlan("provider_draining");
        SweepExpiredWriteLeases();
        if (!TryNormalizeReplayPlans(plans, out var normalized, out var reason))
        {
            _writeLeases.RecordRejectedRequest();
            return FailReplayPlan(reason);
        }
        if (!_writeLeases.TryAcquire(owner ?? string.Empty, normalized, out var lease, out reason))
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
        SweepExpiredWriteLeases();
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
            ExpiredPlans = counters.ExpiredLeases,
            RejectedRequests = counters.RejectedRequests
        };
    }

    private bool TryNormalizeReplayPlans(
        BotRandomizerReplayCosmeticPlan[]? requestedPlans,
        out IReadOnlyDictionary<int, LeasedCosmeticWriteClaim> normalized,
        out string reason)
    {
        normalized = new Dictionary<int, LeasedCosmeticWriteClaim>();
        reason = string.Empty;
        if (_catalog is null || _replayEconIndex is null || _roller is null)
            return Fail("provider_not_ready", out reason);
        if (requestedPlans is null || requestedPlans.Length == 0)
            return Fail("no_replay_plans", out reason);
        if (requestedPlans.Length > 64)
            return Fail("too_many_slots", out reason);

        var switchingTeamsAtRoundReset = IsSwitchingTeamsAtRoundReset();
        var plansBySlot = new Dictionary<int, LeasedCosmeticWriteClaim>();
        foreach (var requested in requestedPlans)
        {
            if (requested is null)
                return Fail("null_plan", out reason);
            if (plansBySlot.ContainsKey(requested.Slot))
                return Fail($"duplicate_slot:{requested.Slot}", out reason);

            var player = Utilities.GetPlayerFromSlot(requested.Slot);
            var state = GetOrCreateState(player);
            if (player is not { IsValid: true, IsBot: true, IsHLTV: false } || state is null)
                return Fail($"slot_not_managed:{requested.Slot}", out reason);
            if (state.Incarnation != requested.Incarnation)
                return Fail($"stale_incarnation:{requested.Slot}", out reason);
            if (requested.SubjectSteamId == 0)
                return Fail($"invalid_subject:{requested.Slot}", out reason);

            var spawnTeam = requested.SpawnTeam == 0
                ? state.Loadout.Team
                : requested.SpawnTeam;
            if (!BotRandomizerReplayTeamPolicy.CanTargetSpawnTeam(
                    state.Loadout.Team,
                    spawnTeam,
                    switchingTeamsAtRoundReset))
            {
                return Fail(
                    $"invalid_spawn_team:{requested.Slot}:{state.Loadout.Team}:{spawnTeam}",
                    out reason);
            }

            if (!TryNormalizeAgent(
                    requested.Slot,
                    spawnTeam,
                    requested.Agent,
                    out var agentMode,
                    out var agentModel,
                    out reason) ||
                !TryNormalizeKnife(requested.Slot, requested.Knife, out var knife, out reason) ||
                !TryNormalizeGloves(requested.Slot, requested.Gloves, out var gloves, out reason) ||
                !TryNormalizeMusicKit(requested.Slot, requested.MusicKit, out var musicKit, out reason) ||
                !TryNormalizeWeapons(requested.Slot, requested.Weapons, out var weapons, out reason))
            {
                return false;
            }

            var policy = new CosmeticWritePolicy(
                spawnTeam,
                agentMode,
                agentModel,
                knife,
                gloves,
                musicKit,
                weapons);
            if (!policy.ClaimsAnything)
                return Fail($"empty_plan:{requested.Slot}", out reason);
            plansBySlot.Add(
                requested.Slot,
                new LeasedCosmeticWriteClaim(requested.Incarnation, requested.SubjectSteamId, policy));
        }

        normalized = plansBySlot;
        return true;
    }

    private bool TryNormalizeAgent(
        int slot,
        byte team,
        BotRandomizerAgentPlan? requested,
        out BotRandomizerAgentPlanMode mode,
        out string? model,
        out string reason)
    {
        mode = requested?.Mode ?? BotRandomizerAgentPlanMode.Randomized;
        model = null;
        reason = string.Empty;
        if (!Enum.IsDefined(mode))
            return Fail($"invalid_agent_mode:{slot}", out reason);
        if (mode != BotRandomizerAgentPlanMode.ReplayModel)
            return true;

        if (requested?.ItemDefinitionIndex is not { } itemDefinitionIndex ||
            !_replayEconIndex!.IsAgentDefinition(itemDefinitionIndex) ||
            !RandomizerAssets.TryNormalizeAgentModel(team, requested.ModelPath, out model))
        {
            return Fail($"unknown_agent_model:{slot}", out reason);
        }
        return true;
    }

    private bool TryNormalizeKnife(
        int slot,
        BotRandomizerReplayItem? requested,
        out ReplayItemSelection? result,
        out string reason)
    {
        result = null;
        reason = string.Empty;
        if (requested is null)
            return true;
        if (requested.ItemDefinitionIndex is <= 0 or > ushort.MaxValue ||
            !_replayEconIndex!.IsKnifeDefinition((ushort)requested.ItemDefinitionIndex) ||
            !RandomizerAssets.KnifeDefIndexByName.Values.Contains((ushort)requested.ItemDefinitionIndex) ||
            !_replayEconIndex.IsPaintKit(requested.PaintKit))
        {
            return Fail($"unknown_knife:{slot}:{requested.ItemDefinitionIndex}:{requested.PaintKit}", out reason);
        }
        return TryNormalizeItem(slot, "knife", requested, out result, out reason);
    }

    private bool TryNormalizeGloves(
        int slot,
        BotRandomizerReplayItem? requested,
        out ReplayItemSelection? result,
        out string reason)
    {
        result = null;
        reason = string.Empty;
        if (requested is null)
            return true;
        if (requested.ItemDefinitionIndex is <= 0 or > ushort.MaxValue ||
            !_replayEconIndex!.IsGloveDefinition((ushort)requested.ItemDefinitionIndex) ||
            !_replayEconIndex.IsPaintKit(requested.PaintKit))
        {
            return Fail($"unknown_gloves:{slot}:{requested.ItemDefinitionIndex}:{requested.PaintKit}", out reason);
        }
        return TryNormalizeItem(slot, "gloves", requested, out result, out reason);
    }

    private static bool TryNormalizeItem(
        int slot,
        string family,
        BotRandomizerReplayItem requested,
        out ReplayItemSelection? result,
        out string reason)
    {
        result = null;
        reason = string.Empty;
        if (requested.PaintKit is 0 or > int.MaxValue ||
            requested.PaintSeed > int.MaxValue ||
            !float.IsFinite(requested.PaintWear) || requested.PaintWear is < 0.0f or > 1.0f ||
            requested.CustomName?.Length > 128)
        {
            return Fail($"invalid_{family}:{slot}", out reason);
        }
        result = new ReplayItemSelection(
            (ushort)requested.ItemDefinitionIndex,
            (int)requested.PaintKit,
            (int)requested.PaintSeed,
            requested.PaintWear,
            NormalizeIdentity(requested));
        return true;
    }

    private bool TryNormalizeMusicKit(int slot, int? requested, out int? result, out string reason)
    {
        result = requested;
        reason = string.Empty;
        return requested is null || _replayEconIndex!.IsMusicKit(requested.Value)
            ? true
            : Fail($"unknown_music_kit:{slot}:{requested}", out reason);
    }

    private bool TryNormalizeWeapons(
        int slot,
        BotRandomizerReplayWeapon[]? requestedWeapons,
        out IReadOnlyDictionary<ushort, ReplayWeaponSelection> result,
        out string reason)
    {
        var weapons = new Dictionary<ushort, ReplayWeaponSelection>();
        result = weapons;
        reason = string.Empty;
        foreach (var requested in requestedWeapons ?? [])
        {
            if (requested is null || requested.ItemDefinitionIndex is <= 0 or > ushort.MaxValue ||
                !_catalog!.TryGetWeapon((ushort)requested.ItemDefinitionIndex, out _))
                return Fail($"unknown_weapon:{slot}:{requested?.ItemDefinitionIndex}", out reason);
            if (!TryNormalizeItem(slot, "weapon", requested, out var item, out reason))
                return false;
            if (!_replayEconIndex!.TryGetWeaponPaint(
                    (ushort)requested.ItemDefinitionIndex,
                    requested.PaintKit,
                    out var replayPaintUsesLegacyModel) ||
                requested.PaintUsesLegacyModel is { } legacy && legacy != replayPaintUsesLegacyModel)
                return Fail($"unknown_weapon_paint:{slot}:{requested.ItemDefinitionIndex}:{requested.PaintKit}", out reason);

            var stickers = new List<StickerSelection>();
            var stickerSlots = new HashSet<int>();
            foreach (var sticker in requested.Stickers ?? [])
            {
                if (sticker is null || sticker.Slot is < 0 or > 4 || sticker.Schema > 4 ||
                    !stickerSlots.Add(sticker.Slot) ||
                    !_replayEconIndex.IsSticker(sticker.StickerId) ||
                    !AreFinite(sticker.Wear, sticker.OffsetX, sticker.OffsetY) ||
                    sticker.Scale is { } scale && !float.IsFinite(scale) ||
                    sticker.Rotation is { } rotation && !float.IsFinite(rotation))
                {
                    return Fail($"invalid_sticker:{slot}:{requested.ItemDefinitionIndex}", out reason);
                }
                stickers.Add(new StickerSelection(
                    sticker.StickerId,
                    sticker.Slot,
                    sticker.Schema,
                    sticker.Wear,
                    sticker.Rotation,
                    sticker.OffsetX,
                    sticker.OffsetY,
                    sticker.Scale));
            }

            var keychains = new List<KeychainSelection>();
            var keychainSlots = new HashSet<int>();
            foreach (var keychain in requested.Keychains ?? [])
            {
                if (keychain is null || keychain.Slot != 0 ||
                    !keychainSlots.Add(keychain.Slot) ||
                    !_replayEconIndex.IsKeychain(keychain.KeychainId) ||
                    keychain.StickerId is { } stickerId && !_replayEconIndex.IsSticker(stickerId) ||
                    !AreFinite(keychain.OffsetX, keychain.OffsetY, keychain.OffsetZ))
                {
                    return Fail($"invalid_keychain:{slot}:{requested.ItemDefinitionIndex}", out reason);
                }
                keychains.Add(new KeychainSelection(
                    keychain.KeychainId,
                    keychain.Seed,
                    keychain.Slot,
                    keychain.StickerId,
                    keychain.OffsetX,
                    keychain.OffsetY,
                    keychain.OffsetZ,
                    keychain.Highlight));
            }

            var normalized = new ReplayWeaponSelection(
                item!.DefIndex,
                item.PaintKit,
                item.Seed,
                item.Wear,
                replayPaintUsesLegacyModel,
                stickers,
                keychains,
                item.Identity);
            if (!weapons.TryAdd(normalized.DefIndex, normalized))
                return Fail($"duplicate_weapon:{slot}:{normalized.DefIndex}", out reason);
        }
        return true;
    }

    private static ReplayEconIdentity NormalizeIdentity(BotRandomizerReplayItem requested)
        => new(
            requested.Quality,
            requested.StattrakCounter,
            requested.OriginalOwnerSteamId is > 0 ? requested.OriginalOwnerSteamId : null,
            requested.ItemAccountId is > 0 ? requested.ItemAccountId : null,
            requested.ItemId is > 0 ? requested.ItemId : null,
            string.IsNullOrWhiteSpace(requested.CustomName) ? null : requested.CustomName.Trim());

    private static bool AreFinite(params float[] values) => values.All(float.IsFinite);

    private void SweepExpiredWriteLeases()
    {
        var affectedSlots = _writeLeases.SweepExpired();
        if (affectedSlots.Length > 0)
            InvalidateLeasePolicySlots(affectedSlots);
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

    private static bool Fail(string value, out string reason)
    {
        reason = value;
        return false;
    }
}
