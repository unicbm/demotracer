/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Globalization;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private readonly HashSet<(int WeaponDefIndex, uint PaintKit)> _legacyCosmeticPaints = new();
    private readonly ReplayCosmeticAlignmentTracker _cosmeticAlignmentTracker = new();

    private ReplayCosmetics NormalizeReplayCosmetics(ReplayCosmetics? cosmetics)
    {
        var normalized = new ReplayCosmetics();
        if (cosmetics == null)
            return normalized;

        foreach (var group in (cosmetics.Weapons ?? [])
                     .Where(IsValidWeaponCosmetic)
                     .GroupBy(weapon => NormalizeWeaponDefIndex(weapon.WeaponDefIndex)))
        {
            if (!IsWeaponCosmeticDefIndex(group.Key) || group.Count() != 1)
                continue;
            var weapon = group.First();
            if (!IsKnownWeaponCosmeticPaint(group.Key, weapon.PaintKit))
                continue;
            normalized.Weapons.Add(new ReplayWeaponCosmetic
            {
                WeaponDefIndex = group.Key,
                PaintKit = weapon.PaintKit,
                Seed = weapon.Seed,
                Wear = weapon.Wear,
                Quality = NormalizeStattrakQuality(weapon.Quality),
                StattrakCounter = NormalizeStattrakCounter(weapon.StattrakCounter),
                OriginalOwnerSteamId = NormalizeOptionalULong(weapon.OriginalOwnerSteamId),
                ItemAccountId = NormalizeOptionalUInt(weapon.ItemAccountId),
                ItemId = NormalizeOptionalULong(weapon.ItemId),
                CustomName = NormalizeCosmeticCustomName(weapon.CustomName),
                Stickers = NormalizeWeaponStickers(weapon.Stickers),
                Charms = NormalizeWeaponCharms(weapon.Charms)
            });
        }

        if (cosmetics.Knife is { } knife &&
            IsValidItemCosmetic(knife) &&
            HasCosmeticSeedEvidence(knife.SeedKnown) &&
            knife.ItemDefIndex is { } knifeDef &&
            IsExactKnifeCosmeticDefIndex(knifeDef) &&
            IsKnownKnifeCosmeticItemDefIndex(knifeDef))
        {
            normalized.Knife = CloneItemCosmetic(knife);
        }

        if (cosmetics.Glove is { } glove &&
            IsValidItemCosmetic(glove) &&
            (glove.ItemDefIndex == null ||
             IsKnownGloveCosmeticItemDefIndex(glove.ItemDefIndex.Value)))
        {
            normalized.Glove = CloneItemCosmetic(glove);
        }

        if (NormalizeAgentCosmetic(cosmetics.Agent) is { } agent)
        {
            normalized.Agent = agent;
        }

        normalized.Weapons = normalized.Weapons
            .OrderBy(weapon => weapon.WeaponDefIndex)
            .ToList();
        return normalized;
    }

    private static ReplayItemCosmetic CloneItemCosmetic(ReplayItemCosmetic source)
        => new()
        {
            ItemDefIndex = source.ItemDefIndex,
            PaintKit = source.PaintKit,
            Seed = source.Seed,
            SeedKnown = source.SeedKnown,
            Wear = source.Wear,
            OriginalOwnerSteamId = NormalizeOptionalULong(source.OriginalOwnerSteamId),
            ItemAccountId = NormalizeOptionalUInt(source.ItemAccountId),
            ItemId = NormalizeOptionalULong(source.ItemId),
            CustomName = NormalizeCosmeticCustomName(source.CustomName)
        };

    private ReplayAgentCosmetic? NormalizeAgentCosmetic(ReplayAgentCosmetic? source)
    {
        if (source == null || source.ItemDefIndex == 0 || !IsKnownAgentCosmeticItemDefIndex(source.ItemDefIndex))
            return null;
        var modelPath = NormalizeAgentModelPath(source.ModelPath);
        if (modelPath == null)
            return null;
        return new ReplayAgentCosmetic
        {
            ItemDefIndex = source.ItemDefIndex,
            ModelPath = modelPath,
            Name = NormalizeAgentName(source.Name)
        };
    }

    private static bool HasCosmeticEvidence(ReplayCosmetics? cosmetics)
        => cosmetics != null &&
           ((cosmetics.Weapons?.Count ?? 0) > 0 ||
            cosmetics.Knife != null ||
            cosmetics.Glove != null ||
            cosmetics.Agent != null);

    private bool IsValidWeaponCosmetic(ReplayWeaponCosmetic cosmetic)
        => cosmetic.PaintKit > 0 &&
           IsKnownPaintKit(cosmetic.PaintKit) &&
           cosmetic.Wear is >= 0.0f and <= 1.0f &&
           float.IsFinite(cosmetic.Wear);

    private bool IsValidItemCosmetic(ReplayItemCosmetic? cosmetic)
        => cosmetic != null &&
           cosmetic.PaintKit > 0 &&
           IsKnownPaintKit(cosmetic.PaintKit) &&
           cosmetic.Wear is >= 0.0f and <= 1.0f &&
           float.IsFinite(cosmetic.Wear);

    internal static bool HasCosmeticSeedEvidence(bool? seedKnown)
        => seedKnown is not false;

    private static int? NormalizeStattrakQuality(int? quality)
        => quality == 9 ? 9 : null;

    private static int? NormalizeStattrakCounter(int? counter)
        => counter is >= 0 ? counter : null;

    private List<ReplayWeaponSticker> NormalizeWeaponStickers(IEnumerable<ReplayWeaponSticker>? stickers)
    {
        if (stickers == null)
            return [];

        var normalized = new List<ReplayWeaponSticker>();
        var slots = new HashSet<int>();
        foreach (var sticker in stickers)
        {
            if (sticker.Slot is < 0 or > 4 ||
                sticker.StickerId == 0 ||
                !IsKnownStickerId(sticker.StickerId) ||
                sticker.Wear is < 0.0f or > 1.0f ||
                !float.IsFinite(sticker.Wear) ||
                !float.IsFinite(sticker.OffsetX) ||
                !float.IsFinite(sticker.OffsetY) ||
                (sticker.Scale.HasValue && !float.IsFinite(sticker.Scale.Value)) ||
                (sticker.Rotation.HasValue && !float.IsFinite(sticker.Rotation.Value)) ||
                !slots.Add(sticker.Slot))
            {
                return [];
            }

            normalized.Add(new ReplayWeaponSticker
            {
                Slot = sticker.Slot,
                StickerId = sticker.StickerId,
                Wear = sticker.Wear,
                OffsetX = sticker.OffsetX,
                OffsetY = sticker.OffsetY,
                Scale = sticker.Scale,
                Rotation = sticker.Rotation
            });
        }

        return normalized
            .OrderBy(sticker => sticker.Slot)
            .ToList();
    }

    private List<ReplayWeaponCharm> NormalizeWeaponCharms(IEnumerable<ReplayWeaponCharm>? charms)
    {
        if (charms == null)
            return [];

        var normalized = new List<ReplayWeaponCharm>();
        var slots = new HashSet<int>();
        foreach (var charm in charms)
        {
            if (charm.Slot != 0 ||
                charm.CharmId == 0 ||
                !IsKnownKeychainId(charm.CharmId) ||
                !float.IsFinite(charm.OffsetX) ||
                !float.IsFinite(charm.OffsetY) ||
                !float.IsFinite(charm.OffsetZ) ||
                !slots.Add(charm.Slot))
            {
                return [];
            }

            normalized.Add(new ReplayWeaponCharm
            {
                Slot = charm.Slot,
                CharmId = charm.CharmId,
                OffsetX = charm.OffsetX,
                OffsetY = charm.OffsetY,
                OffsetZ = charm.OffsetZ,
                Seed = NormalizeOptionalUInt(charm.Seed),
                Highlight = NormalizeOptionalUInt(charm.Highlight),
                StickerId = NormalizeKnownStickerId(charm.StickerId)
            });
        }

        return normalized
            .OrderBy(charm => charm.Slot)
            .ToList();
    }

    private static uint? NormalizeOptionalUInt(uint? value)
        => value is > 0 ? value : null;

    private uint? NormalizeKnownStickerId(uint? value)
    {
        var normalized = NormalizeOptionalUInt(value);
        return normalized.HasValue && IsKnownStickerId(normalized.Value) ? normalized : null;
    }

    private static ulong? NormalizeOptionalULong(ulong? value)
        => value is > 0 ? value : null;

    private static string? NormalizeCosmeticCustomName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var cleaned = new string(value
            .Trim()
            .Where(ch => !char.IsControl(ch) || ch == '\t')
            .Take(128)
            .ToArray())
            .Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? NormalizeAgentName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var cleaned = value.Trim().ToLowerInvariant();
        if (cleaned.Length is 0 or > 128 ||
            cleaned.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_'))
        {
            return null;
        }
        return cleaned;
    }

    private static string? NormalizeAgentModelPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var path = value.Trim().Replace('/', '\\').ToLowerInvariant();
        if (path.Length is < 24 or > 160 ||
            !path.StartsWith("agents\\models\\", StringComparison.Ordinal) ||
            !path.EndsWith(".vmdl", StringComparison.Ordinal) ||
            path.Contains("..", StringComparison.Ordinal) ||
            path.Contains(':', StringComparison.Ordinal) ||
            path.Contains('\0'))
        {
            return null;
        }

        foreach (var ch in path)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '\\' or '.' or '-'))
                return null;
        }
        return path;
    }

    private bool IsWeaponCosmeticDefIndex(int weaponDefIndex)
        => IsWeaponCosmeticCategory(weaponDefIndex);

    private bool IsWeaponCosmeticCategory(int weaponDefIndex)
        => _replayEquipment.IsWeaponCosmeticCategory(NormalizeWeaponDefIndex(weaponDefIndex));

    private bool IsExactKnifeCosmeticDefIndex(int weaponDefIndex)
        => IsKnownKnifeCosmeticItemDefIndex(weaponDefIndex);

    private bool IsLegacyCosmeticPaint(int weaponDefIndex, int paintKit)
        => paintKit > 0 &&
           _legacyCosmeticPaints.Contains((NormalizeWeaponDefIndex(weaponDefIndex), (uint)paintKit));

    private void ResetCosmeticAlignState(bool resetCounters = false)
    {
        _session.CosmeticSyncedSlots.Clear();
        _cosmeticAlignmentTracker.Clear();
        if (resetCounters)
        {
            _cosmeticAppliedCount = 0;
            _cosmeticSkippedCount = 0;
        }
    }

    private void ResetStickerAlignState(bool resetCounters = false)
    {
        if (resetCounters)
        {
            _stickerAppliedCount = 0;
            _stickerSkippedCount = 0;
        }
    }

    private void ResetCharmAlignState(bool resetCounters = false)
    {
        if (resetCounters)
        {
            _charmAppliedCount = 0;
            _charmSkippedCount = 0;
        }
    }

    private string FormatCosmeticStatusCounts()
    {
        var counts = CountLoadedCosmeticEvidence();
        return
            $"cosmetics_evidence={counts.Files} cosmetic_weapons={counts.Weapons} cosmetic_knives={counts.Knives} cosmetic_gloves={counts.Gloves} cosmetic_agents={counts.Agents} sticker_evidence={counts.Stickers} charm_evidence={counts.Charms} applied={_cosmeticAppliedCount} skipped={_cosmeticSkippedCount} sticker_applied={_stickerAppliedCount} sticker_skipped={_stickerSkippedCount} charm_applied={_charmAppliedCount} charm_skipped={_charmSkippedCount} {FormatBotRandomizerLeaseStatus()}";
    }

    private (int Files, int Weapons, int Knives, int Gloves, int Agents, int Stickers, int Charms) CountLoadedCosmeticEvidence()
    {
        var files = 0;
        var weapons = 0;
        var knives = 0;
        var gloves = 0;
        var agents = 0;
        var stickers = 0;
        var charms = 0;

        foreach (var replay in _session.LoadedReplays.Values)
        {
            if (!HasCosmeticEvidence(replay.Cosmetics))
                continue;

            files++;
            weapons += replay.Cosmetics.Weapons.Count;
            stickers += replay.Cosmetics.Weapons.Sum(weapon => weapon.Stickers.Count);
            charms += replay.Cosmetics.Weapons.Sum(weapon => weapon.Charms.Count);
            if (replay.Cosmetics.Knife != null)
                knives++;
            if (replay.Cosmetics.Glove != null)
                gloves++;
            if (replay.Cosmetics.Agent != null)
                agents++;
        }

        return (files, weapons, knives, gloves, agents, stickers, charms);
    }

}
