/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotRandomizer;
using BotRandomizerApi;

internal static class ReplayPlanValidationTests
{
    internal static void Run(CosmeticCatalog catalog, ReplayEconIndex econ)
    {
        var validator = new ReplayPlanValidator(catalog, econ);
        var bots = new Dictionary<int, ReplayPlanBot> { [1] = new(11, 3), [2] = new(22, 2) };
        var resolutions = 0;
        ReplayPlanBot? Resolve(int slot)
        {
            resolutions++;
            return bots.TryGetValue(slot, out var bot) ? bot : null;
        }
        IReadOnlyDictionary<int, LeasedCosmeticWriteClaim> Accept(
            BotRandomizerReplayCosmeticPlan[] plans, bool switching = false)
        {
            Require(validator.TryNormalize(plans, Resolve, switching, out var result, out var reason),
                $"valid plan rejected: {reason}");
            Require(reason.Length == 0 && result.Count == plans.Length, "complete normalized batch");
            return result;
        }
        void Reject(BotRandomizerReplayCosmeticPlan[]? plans, string expected, bool switching = false)
        {
            Require(!validator.TryNormalize(plans, Resolve, switching, out var result, out var reason),
                $"invalid plan accepted: {expected}");
            Require(reason == expected, $"expected {expected}, received {reason}");
            Require(result.Count == 0, "rejected batch must not expose partially normalized claims");
        }

        Reject(null, "no_replay_plans");
        Reject([], "no_replay_plans");
        Reject(new BotRandomizerReplayCosmeticPlan[65], "too_many_slots");
        Require(resolutions == 0, "invalid batch sizes never resolve live slots");
        Reject([Plan(slot: -1)], "slot_not_managed:-1");
        Reject([Plan(slot: 64)], "slot_not_managed:64");
        Require(resolutions == 0, "out-of-range slots never reach the host resolver");
        Reject([null!], "null_plan");
        Reject([Plan(), Plan()], "duplicate_slot:1");
        Reject([Plan(), Plan(slot: 63)], "slot_not_managed:63");
        var stale = Plan();
        stale.Incarnation++;
        Reject([stale], "stale_incarnation:1");
        var missingSubject = Plan();
        missingSubject.SubjectSteamId = 0;
        Reject([missingSubject], "invalid_subject:1");
        var empty = Plan();
        empty.MusicKit = null;
        Reject([empty], "empty_plan:1");

        var first = Accept([Plan(), Plan(slot: 2)])[1];
        Require(first.Incarnation == 11 && first.SubjectSteamId == 76_561_198_012_345_678UL,
            "claim keeps the bot incarnation and demo subject identity");
        Require(first.Policy.SpawnTeam == 3 && first.Policy.MusicKit == 70,
            "unspecified spawn team resolves to the current bot team");
        var swapped = Plan();
        swapped.SpawnTeam = 2;
        Reject([swapped], "invalid_spawn_team:1:3:2");
        Require(Accept([swapped], switching: true)[1].Policy.SpawnTeam == 2,
            "round reset accepts the upcoming team");
        bots[1] = new(11, 2);
        Accept([swapped], switching: true);
        bots[1] = new(11, 1);
        Reject([swapped], "invalid_spawn_team:1:1:2", switching: true);
        bots[1] = new(11, 3);

        var complete = FullPlan();
        var policy = Accept([complete])[1].Policy;
        Require(policy.AgentModel == "agents\\models\\ctm_sas\\ctm_sas_variantf.vmdl" &&
                policy.AgentItemDefinitionIndex == 5601 &&
                policy.Knife!.DefIndex == 507 && policy.Gloves!.DefIndex == 5030,
            "all appearance families pass the production normalizer");
        var weapon = policy.Weapons[4];
        Require(!weapon.Legacy && weapon.Identity.CustomName == "Replay" &&
                weapon.Identity.Quality == 9 && weapon.Identity.StattrakCounter == 88 &&
                weapon.Identity.OriginalOwnerSteamId == 76_561_198_012_345_679UL &&
                weapon.Identity.ItemAccountId == 123 && weapon.Identity.ItemId == 456,
            "normalization preserves evidence and resolves legacy model metadata");
        complete.Slot = 2;
        complete.Agent.ModelPath = "changed";
        complete.Knife!.PaintKit = 1;
        complete.Gloves!.PaintWear = 0.9f;
        complete.Weapons[0].PaintKit = 1;
        complete.Weapons[0].CustomName = "changed";
        complete.Weapons[0].Stickers[0].Wear = 0.9f;
        complete.Weapons[0].Stickers[0] = new();
        complete.Weapons[0].Keychains[0].Seed = 999;
        complete.Weapons[0].Keychains = [];
        complete.Weapons = [];
        Require(policy.Knife!.PaintKit == 38 && policy.Gloves!.Wear == 0.1f &&
                policy.AgentItemDefinitionIndex == 5601 && weapon.PaintKit == 799 &&
                weapon.Identity.CustomName == "Replay" && weapon.Stickers[0].DefIndex == 7887 &&
                weapon.Stickers[0].Wear == 0.2f && weapon.Keychains[0].Seed == 42,
            "caller mutation cannot change copied items, nested arrays, identities or agent state");

        var zeroIdentity = FullPlan();
        var zeroWeapon = zeroIdentity.Weapons[0];
        zeroWeapon.OriginalOwnerSteamId = 0;
        zeroWeapon.ItemAccountId = 0;
        zeroWeapon.ItemId = 0;
        zeroWeapon.CustomName = " \t ";
        var identity = Accept([zeroIdentity])[1].Policy.Weapons[4].Identity;
        Require(identity.OriginalOwnerSteamId is null && identity.ItemAccountId is null &&
                identity.ItemId is null && identity.CustomName is null,
            "absent identity values normalize to null");
        var preserve = Plan();
        preserve.MusicKit = null;
        preserve.Agent.Mode = BotRandomizerAgentPlanMode.PreserveEngineDefault;
        Require(Accept([preserve])[1].Policy.ClaimsAnything, "preserve-engine-default is a real claim");

        void RejectChange(Action<BotRandomizerReplayCosmeticPlan> change, string reason)
        {
            var request = FullPlan();
            change(request);
            Reject([Plan(slot: 2), request], reason);
        }
        RejectChange(p => p.Agent.Mode = (BotRandomizerAgentPlanMode)99, "invalid_agent_mode:1");
        RejectChange(p => p.Agent.ItemDefinitionIndex = 5602, "unknown_agent_model:1");
        RejectChange(p => p.SpawnTeam = 2, "invalid_spawn_team:1:3:2");
        var wrongTeamAgent = FullPlan();
        wrongTeamAgent.SpawnTeam = 2;
        Reject([wrongTeamAgent], "unknown_agent_model:1", switching: true);
        RejectChange(p => p.Knife!.ItemDefinitionIndex = 999, "unknown_knife:1:999:38");
        RejectChange(p => p.Gloves!.ItemDefinitionIndex = 999, "unknown_gloves:1:999:10038");
        RejectChange(p => p.Knife!.PaintKit = 10038, "unknown_knife:1:507:10038");
        RejectChange(p => p.Gloves!.PaintKit = 38, "unknown_gloves:1:5030:38");
        Require(catalog.TryGetKnifePaints(500, out var bayonetPaints) &&
                bayonetPaints.Any(paint => paint.PaintKit == 558) &&
                catalog.Gloves.Any(glove => glove.DefIndex == 4725 && glove.PaintKit == 10085),
            "mismatched paints are valid for another item in the same family");
        RejectChange(p => p.Knife!.PaintKit = 558, "unknown_knife:1:507:558");
        RejectChange(p => p.Gloves!.PaintKit = 10085, "unknown_gloves:1:5030:10085");
        foreach (var definition in new ushort[] { 506, 526 })
        {
            Require(RandomizerAssets.Knives.All(knife => knife.DefIndex != definition),
                "replay-only knife regression covers a type excluded from random weights");
            var replayOnlyKnife = Plan();
            replayOnlyKnife.Knife = new()
                { ItemDefinitionIndex = definition, PaintKit = 38, PaintWear = 0.1f };
            Require(Accept([replayOnlyKnife])[1].Policy.Knife!.DefIndex == definition,
                "a valid replay knife pair does not require a random type weight");
        }
        RejectChange(p => p.MusicKit = 2, "unknown_music_kit:1:2");
        RejectChange(p => p.Weapons[0].PaintKit = 1, "unknown_weapon_paint:1:4:1");
        RejectChange(p => p.Weapons[0].PaintUsesLegacyModel = true, "unknown_weapon_paint:1:4:799");
        RejectChange(p => p.Weapons = [p.Weapons[0], p.Weapons[0]], "duplicate_weapon:1:4");
        RejectChange(p => p.Weapons[0].PaintWear = float.NaN, "invalid_weapon:1");
        RejectChange(p => p.Weapons[0].PaintWear = 1.1f, "invalid_weapon:1");
        RejectChange(p => p.Weapons[0].PaintSeed = uint.MaxValue, "invalid_weapon:1");
        RejectChange(p => p.Weapons[0].CustomName = new string('x', 129), "invalid_weapon:1");
        RejectChange(p => p.Weapons[0].Stickers = [p.Weapons[0].Stickers[0], p.Weapons[0].Stickers[0]],
            "invalid_sticker:1:4");
        RejectChange(p => p.Weapons[0].Stickers[0].StickerId = uint.MaxValue, "invalid_sticker:1:4");
        RejectChange(p => p.Weapons[0].Stickers[0].Schema = 5, "invalid_sticker:1:4");
        RejectChange(p => p.Weapons[0].Stickers[0].OffsetX = float.PositiveInfinity, "invalid_sticker:1:4");
        RejectChange(p => p.Weapons[0].Keychains[0].Slot = 1, "invalid_keychain:1:4");
        RejectChange(p => p.Weapons[0].Keychains[0].KeychainId = uint.MaxValue, "invalid_keychain:1:4");
        RejectChange(p => p.Weapons[0].Keychains[0].OffsetZ = float.NaN, "invalid_keychain:1:4");
        Require(bots.Count == 2 && bots[1] == new ReplayPlanBot(11, 3),
            "normalizing and rejecting plans does not mutate resolved bot identities");
    }

    private static BotRandomizerReplayCosmeticPlan Plan(int slot = 1) => new()
    {
        Slot = slot,
        Incarnation = slot == 2 ? 22UL : 11UL,
        SubjectSteamId = 76_561_198_012_345_678UL,
        MusicKit = 70
    };

    private static BotRandomizerReplayCosmeticPlan FullPlan()
    {
        var plan = Plan();
        plan.Agent = new()
        {
            Mode = BotRandomizerAgentPlanMode.ReplayModel, ItemDefinitionIndex = 5601,
            ModelPath = " AGENTS/MODELS/CTM_SAS/CTM_SAS_VARIANTF.VMDL "
        };
        plan.Knife = new() { ItemDefinitionIndex = 507, PaintKit = 38, PaintWear = 0.1f };
        plan.Gloves = new() { ItemDefinitionIndex = 5030, PaintKit = 10038, PaintWear = 0.1f };
        plan.Weapons = [new()
        {
            ItemDefinitionIndex = 4, PaintKit = 799, PaintSeed = 42, PaintWear = 0.12f,
            Quality = 9, StattrakCounter = 88, CustomName = "  Replay  ",
            OriginalOwnerSteamId = 76_561_198_012_345_679UL, ItemAccountId = 123, ItemId = 456,
            Stickers = [new() { Slot = 0, Schema = 0, StickerId = 7887, Wear = 0.2f }],
            Keychains = [new() { Slot = 0, KeychainId = 37, Seed = 42, StickerId = 7887 }]
        }];
        return plan;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Replay plan validation failed: {message}");
    }
}
