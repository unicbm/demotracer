
namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private static ReplayLoadoutSnapshot NormalizeReplayLoadout(ReplayLoadoutSnapshot loadout)
    {
        return new ReplayLoadoutSnapshot
        {
            WeaponDefIndices = loadout.WeaponDefIndices?
                .Select(NormalizeWeaponDefIndex)
                .Where(IsLoadoutWeaponDefIndex)
                .ToArray() ?? Array.Empty<int>(),
            ArmorValue = Math.Min(loadout.ArmorValue, 100),
            HasHelmet = loadout.HasHelmet,
            HasDefuser = loadout.HasDefuser
        };
    }

    private static Dictionary<string, int> BuildLoadoutItemCounts(ReplayLoadoutSnapshot loadout)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in loadout.WeaponDefIndices ?? Array.Empty<int>())
        {
            if (!TryGetWeaponClassByDefIndex(def, out var className))
                continue;
            if (GetReplayWeaponSlot(className) is ReplayWeaponSlot.Knife or ReplayWeaponSlot.C4)
                continue;
            counts[className] = counts.GetValueOrDefault(className) + 1;
        }
        return counts;
    }

    private static string? BestTargetSlotItem(
        Dictionary<string, int> targetItems,
        Func<string, bool> predicate)
    {
        return targetItems.Keys
            .Where(predicate)
            .OrderByDescending(WeaponClassValue)
            .ThenBy(itemName => itemName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool WeaponClassMatches(string actual, string expected)
    {
        actual = NormalizeWeaponClassName(actual);
        expected = NormalizeWeaponClassName(expected);
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            return true;
        if (expected == "weapon_knife")
        {
            return actual.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase)
                   || actual.Equals("weapon_bayonet", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string NormalizeWeaponClassName(string className)
    {
        return className switch
        {
            "weapon_decoy_grenade" => "weapon_decoy",
            "weapon_c4_explosive" => "weapon_c4",
            _ => className
        };
    }

    private static ReplayWeaponSlot GetReplayWeaponSlot(string className)
    {
        className = NormalizeWeaponClassName(className);
        return className switch
        {
            "weapon_ak47" or "weapon_aug" or "weapon_awp" or "weapon_famas" or
            "weapon_g3sg1" or "weapon_galilar" or "weapon_m249" or "weapon_m4a1" or
            "weapon_m4a1_silencer" or "weapon_mac10" or "weapon_p90" or
            "weapon_mp5sd" or "weapon_mp7" or "weapon_mp9" or "weapon_ump45" or
            "weapon_xm1014" or "weapon_bizon" or "weapon_mag7" or "weapon_negev" or
            "weapon_sawedoff" or "weapon_nova" or "weapon_scar20" or "weapon_sg556" or
            "weapon_ssg08" => ReplayWeaponSlot.Primary,

            "weapon_deagle" or "weapon_elite" or "weapon_fiveseven" or "weapon_glock" or
            "weapon_hkp2000" or "weapon_p250" or "weapon_tec9" or "weapon_usp_silencer" or
            "weapon_cz75a" or "weapon_revolver" => ReplayWeaponSlot.Secondary,

            "weapon_flashbang" or "weapon_hegrenade" or "weapon_smokegrenade" or
            "weapon_molotov" or "weapon_decoy" or "weapon_incgrenade" => ReplayWeaponSlot.Utility,

            "weapon_c4" => ReplayWeaponSlot.C4,
            "weapon_taser" => ReplayWeaponSlot.Taser,
            "weapon_knife" => ReplayWeaponSlot.Knife,
            _ => ReplayWeaponSlot.Other
        };
    }

    private static int GetReplayLockTarget(int weaponDefIndex)
    {
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
            return 0;
        return GetReplayWeaponSlot(className) switch
        {
            ReplayWeaponSlot.Primary => 1,
            ReplayWeaponSlot.Secondary => 2,
            ReplayWeaponSlot.Knife or ReplayWeaponSlot.Taser => 3,
            ReplayWeaponSlot.C4 => 5,
            _ => 0
        };
    }

    private static bool IsSlotReplaceableWeaponDef(int weaponDefIndex)
    {
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
            return false;
        return GetReplayWeaponSlot(className) is ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary;
    }

    private static int NormalizeWeaponDefIndex(int weaponDefIndex)
    {
        if (weaponDefIndex is 41 or 42 or 59 ||
            weaponDefIndex is >= 500 and < 600)
            return 42;
        return weaponDefIndex;
    }

    private static int[] NormalizePreloadWeaponDefs(IEnumerable<int> weaponDefIndices)
    {
        var seen = new HashSet<int>();
        var outDefs = new List<int>();
        foreach (var rawDef in weaponDefIndices)
        {
            var def = NormalizeWeaponDefIndex(rawDef);
            if (IsPreloadWeaponDefIndex(def) && seen.Add(def))
                outDefs.Add(def);
        }
        return outDefs.ToArray();
    }

    private static int[] BuildReplayPreloadWeaponDefs(
        IReadOnlyList<int>? manifestPreloadWeaponDefIndices,
        int[] scannedPreloadWeaponDefIndices,
        ReplayLoadoutSnapshot normalizedLoadout,
        bool hasManifestLoadout)
    {
        var preloadDefs = NormalizePreloadWeaponDefs(
            manifestPreloadWeaponDefIndices is { Count: > 0 }
                ? manifestPreloadWeaponDefIndices
                : scannedPreloadWeaponDefIndices);
        if (!hasManifestLoadout)
            return preloadDefs;

        var loadoutDefs = new HashSet<int>(
            (normalizedLoadout.WeaponDefIndices ?? Array.Empty<int>())
            .Select(NormalizeWeaponDefIndex)
            .Where(IsPreloadWeaponDefIndex));
        if (loadoutDefs.Count == 0)
            return [];

        return preloadDefs
            .Where(loadoutDefs.Contains)
            .ToArray();
    }

    private static bool IsKnownWeaponDefIndex(int weaponDefIndex)
        => TryGetWeaponClassByDefIndex(weaponDefIndex, out _);

    private static bool IsPreloadWeaponDefIndex(int weaponDefIndex)
    {
        if (!IsKnownWeaponDefIndex(weaponDefIndex))
            return false;
        var slot = GetReplayWeaponSlot(TryGetWeaponClassByDefIndex(weaponDefIndex, out var className)
            ? className
            : string.Empty);
        return slot is not ReplayWeaponSlot.Other
            and not ReplayWeaponSlot.Knife
            and not ReplayWeaponSlot.C4
            and not ReplayWeaponSlot.Taser;
    }

    private static bool IsLoadoutWeaponDefIndex(int weaponDefIndex)
    {
        if (!IsKnownWeaponDefIndex(weaponDefIndex))
            return false;
        var slot = GetReplayWeaponSlot(TryGetWeaponClassByDefIndex(weaponDefIndex, out var className)
            ? className
            : string.Empty);
        return slot is not ReplayWeaponSlot.Other
            and not ReplayWeaponSlot.Knife
            and not ReplayWeaponSlot.C4;
    }

    private static bool IsUtilityWeaponDefIndex(int weaponDefIndex)
    {
        if (!TryGetWeaponClassByDefIndex(weaponDefIndex, out var className))
            return false;
        return GetReplayWeaponSlot(className) == ReplayWeaponSlot.Utility;
    }


    private static int WeaponDefIndex(string className)
    {
        return NormalizeWeaponClassName(className).ToLowerInvariant() switch
        {
            "weapon_deagle" => 1,
            "weapon_elite" => 2,
            "weapon_fiveseven" => 3,
            "weapon_glock" => 4,
            "weapon_ak47" => 7,
            "weapon_aug" => 8,
            "weapon_awp" => 9,
            "weapon_famas" => 10,
            "weapon_g3sg1" => 11,
            "weapon_galilar" => 13,
            "weapon_m249" => 14,
            "weapon_m4a1" => 16,
            "weapon_mac10" => 17,
            "weapon_p90" => 19,
            "weapon_mp5sd" => 23,
            "weapon_ump45" => 24,
            "weapon_xm1014" => 25,
            "weapon_bizon" => 26,
            "weapon_mag7" => 27,
            "weapon_negev" => 28,
            "weapon_sawedoff" => 29,
            "weapon_tec9" => 30,
            "weapon_taser" => 31,
            "weapon_hkp2000" => 32,
            "weapon_mp7" => 33,
            "weapon_mp9" => 34,
            "weapon_nova" => 35,
            "weapon_p250" => 36,
            "weapon_scar20" => 38,
            "weapon_sg556" => 39,
            "weapon_ssg08" => 40,
            "weapon_knifegg" => 41,
            "weapon_knife" => 42,
            "weapon_flashbang" => 43,
            "weapon_hegrenade" => 44,
            "weapon_smokegrenade" => 45,
            "weapon_molotov" => 46,
            "weapon_decoy" => 47,
            "weapon_incgrenade" => 48,
            "weapon_c4" => 49,
            "weapon_knife_t" => 59,
            "weapon_m4a1_silencer" => 60,
            "weapon_usp_silencer" => 61,
            "weapon_cz75a" => 63,
            "weapon_revolver" => 64,
            "weapon_bayonet" => 500,
            "weapon_knife_css" => 503,
            "weapon_knife_flip" => 505,
            "weapon_knife_gut" => 506,
            "weapon_knife_karambit" => 507,
            "weapon_knife_m9_bayonet" => 508,
            "weapon_knife_tactical" => 509,
            "weapon_knife_falchion" => 512,
            "weapon_knife_survival_bowie" => 514,
            "weapon_knife_butterfly" => 515,
            "weapon_knife_push" => 516,
            "weapon_knife_cord" => 517,
            "weapon_knife_canis" => 518,
            "weapon_knife_ursus" => 519,
            "weapon_knife_gypsy_jackknife" => 520,
            "weapon_knife_outdoor" => 521,
            "weapon_knife_stiletto" => 522,
            "weapon_knife_widowmaker" => 523,
            "weapon_knife_skeleton" => 525,
            "weapon_knife_kukri" => 526,
            _ => -1
        };
    }

    private static bool TryGetWeaponClassByDefIndex(int weaponDefIndex, out string className)
    {
        className = NormalizeWeaponDefIndex(weaponDefIndex) switch
        {
            1 => "weapon_deagle",
            2 => "weapon_elite",
            3 => "weapon_fiveseven",
            4 => "weapon_glock",
            7 => "weapon_ak47",
            8 => "weapon_aug",
            9 => "weapon_awp",
            10 => "weapon_famas",
            11 => "weapon_g3sg1",
            13 => "weapon_galilar",
            14 => "weapon_m249",
            16 => "weapon_m4a1",
            17 => "weapon_mac10",
            19 => "weapon_p90",
            23 => "weapon_mp5sd",
            24 => "weapon_ump45",
            25 => "weapon_xm1014",
            26 => "weapon_bizon",
            27 => "weapon_mag7",
            28 => "weapon_negev",
            29 => "weapon_sawedoff",
            30 => "weapon_tec9",
            31 => "weapon_taser",
            32 => "weapon_hkp2000",
            33 => "weapon_mp7",
            34 => "weapon_mp9",
            35 => "weapon_nova",
            36 => "weapon_p250",
            38 => "weapon_scar20",
            39 => "weapon_sg556",
            40 => "weapon_ssg08",
            42 => "weapon_knife",
            43 => "weapon_flashbang",
            44 => "weapon_hegrenade",
            45 => "weapon_smokegrenade",
            46 => "weapon_molotov",
            47 => "weapon_decoy",
            48 => "weapon_incgrenade",
            49 => "weapon_c4",
            60 => "weapon_m4a1_silencer",
            61 => "weapon_usp_silencer",
            63 => "weapon_cz75a",
            64 => "weapon_revolver",
            _ => string.Empty
        };
        return className.Length > 0;
    }

    private static uint WeaponClassValue(string className)
    {
        return className.ToLowerInvariant() switch
        {
            "weapon_deagle" => 700,
            "weapon_elite" => 300,
            "weapon_fiveseven" => 500,
            "weapon_glock" => 200,
            "weapon_ak47" => 2700,
            "weapon_aug" => 3300,
            "weapon_awp" => 4750,
            "weapon_famas" => 2050,
            "weapon_g3sg1" => 5000,
            "weapon_galilar" => 1800,
            "weapon_m249" => 5200,
            "weapon_m4a1" => 3100,
            "weapon_mac10" => 1050,
            "weapon_p90" => 2350,
            "weapon_mp5sd" => 1500,
            "weapon_ump45" => 1200,
            "weapon_xm1014" => 2000,
            "weapon_bizon" => 1400,
            "weapon_mag7" => 1300,
            "weapon_negev" => 1700,
            "weapon_sawedoff" => 1100,
            "weapon_tec9" => 500,
            "weapon_taser" => 200,
            "weapon_hkp2000" => 200,
            "weapon_mp7" => 1500,
            "weapon_mp9" => 1250,
            "weapon_nova" => 1050,
            "weapon_p250" => 300,
            "weapon_scar20" => 5000,
            "weapon_sg556" => 3000,
            "weapon_ssg08" => 1700,
            "weapon_flashbang" => 200,
            "weapon_hegrenade" => 300,
            "weapon_smokegrenade" => 300,
            "weapon_molotov" => 400,
            "weapon_decoy" => 50,
            "weapon_incgrenade" => 600,
            "weapon_m4a1_silencer" => 2900,
            "weapon_usp_silencer" => 200,
            "weapon_cz75a" => 500,
            "weapon_revolver" => 600,
            _ => 0
        };
    }

    private static bool TryBuildWeaponPlan(
        IReadOnlyList<int> weaponDefIndices,
        out int firstWeaponDefIndex,
        out int[] preloadWeaponDefIndices)
    {
        firstWeaponDefIndex = -1;
        preloadWeaponDefIndices = [];

        if (weaponDefIndices.Count == 0)
            return false;

        var preload = new List<int>();
        foreach (var value in weaponDefIndices)
        {
            var def = NormalizeWeaponDefIndex(value);
            if (IsKnownWeaponDefIndex(def) && firstWeaponDefIndex < 0)
                firstWeaponDefIndex = def;
            if (IsPreloadWeaponDefIndex(def))
                preload.Add(def);
        }
        preloadWeaponDefIndices = NormalizePreloadWeaponDefs(preload);
        return true;
    }
}
