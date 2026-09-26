/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using BotRandomizerApi;

namespace BotRandomizer;

// The host resolves only managed bots; normalization never touches engine objects.
internal readonly record struct ReplayPlanBot(ulong Incarnation, byte Team);

internal sealed class ReplayPlanValidator(CosmeticCatalog catalog, ReplayEconIndex econ)
{
    internal bool TryNormalize(
        BotRandomizerReplayCosmeticPlan[]? requestedPlans,
        Func<int, ReplayPlanBot?> resolveBot,
        bool switchingTeamsAtRoundReset,
        out IReadOnlyDictionary<int, LeasedCosmeticWriteClaim> normalized,
        out string reason)
    {
        normalized = new Dictionary<int, LeasedCosmeticWriteClaim>();
        reason = string.Empty;
        if (requestedPlans is null || requestedPlans.Length == 0)
            return Fail("no_replay_plans", out reason);
        if (requestedPlans.Length > 64)
            return Fail("too_many_slots", out reason);

        var plansBySlot = new Dictionary<int, LeasedCosmeticWriteClaim>();
        foreach (var requested in requestedPlans)
        {
            if (requested is null)
                return Fail("null_plan", out reason);
            if (plansBySlot.ContainsKey(requested.Slot))
                return Fail($"duplicate_slot:{requested.Slot}", out reason);

            if (requested.Slot is < 0 or >= 64 || resolveBot(requested.Slot) is not { } state)
                return Fail($"slot_not_managed:{requested.Slot}", out reason);
            if (state.Incarnation != requested.Incarnation)
                return Fail($"stale_incarnation:{requested.Slot}", out reason);
            if (requested.SubjectSteamId == 0)
                return Fail($"invalid_subject:{requested.Slot}", out reason);

            var spawnTeam = requested.SpawnTeam == 0
                ? state.Team
                : requested.SpawnTeam;
            if (!BotRandomizerReplayTeamPolicy.CanTargetSpawnTeam(
                    state.Team,
                    spawnTeam,
                    switchingTeamsAtRoundReset))
            {
                return Fail(
                    $"invalid_spawn_team:{requested.Slot}:{state.Team}:{spawnTeam}",
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
                weapons)
            {
                AgentItemDefinitionIndex = agentMode == BotRandomizerAgentPlanMode.ReplayModel
                    ? checked((ushort)requested.Agent!.ItemDefinitionIndex!.Value)
                    : (ushort)0
            };
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
            !econ.IsAgentDefinition(itemDefinitionIndex) ||
            !RandomizerAssets.TryNormalizeAgentModel(team, requested.ModelPath, out model, itemDefinitionIndex))
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
        // Catalog pairs include every pinned variant, independently of random type weights.
        if (requested.ItemDefinitionIndex is <= 0 or > ushort.MaxValue ||
            !econ.IsKnifeDefinition((ushort)requested.ItemDefinitionIndex) ||
            !RandomizerAssets.KnifeDefIndexByName.Values.Contains((ushort)requested.ItemDefinitionIndex) ||
            !catalog.TryGetKnifePaints((ushort)requested.ItemDefinitionIndex, out var paints) ||
            !paints.Any(paint => paint.PaintKit == requested.PaintKit))
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
            !econ.IsGloveDefinition((ushort)requested.ItemDefinitionIndex) ||
            !catalog.Gloves.Any(glove => glove.DefIndex == requested.ItemDefinitionIndex &&
                                        glove.PaintKit == requested.PaintKit))
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
        return requested is null || econ.IsMusicKit(requested.Value)
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
                !catalog.TryGetWeapon((ushort)requested.ItemDefinitionIndex, out _))
                return Fail($"unknown_weapon:{slot}:{requested?.ItemDefinitionIndex}", out reason);
            if (!TryNormalizeItem(slot, "weapon", requested, out var item, out reason))
                return false;
            if (!econ.TryGetWeaponPaint(
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
                    !econ.IsSticker(sticker.StickerId) ||
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
                    !econ.IsKeychain(keychain.KeychainId) ||
                    keychain.StickerId is { } stickerId && !econ.IsSticker(stickerId) ||
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

    private static bool Fail(string value, out string reason)
    {
        reason = value;
        return false;
    }
}
