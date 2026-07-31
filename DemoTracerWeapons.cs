
namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    internal enum WeaponSlotReplacementAction
    {
        TargetReady,
        WaitForClear,
        GrantTarget,
        PreserveExisting
    }

    internal static WeaponSlotReplacementAction DecideWeaponSlotReplacement(
        bool targetPresent,
        bool anySlotWeapon,
        int clearWaitFramesRemaining)
    {
        if (targetPresent)
            return WeaponSlotReplacementAction.TargetReady;
        if (!anySlotWeapon)
            return WeaponSlotReplacementAction.GrantTarget;
        return clearWaitFramesRemaining > 0
            ? WeaponSlotReplacementAction.WaitForClear
            : WeaponSlotReplacementAction.PreserveExisting;
    }

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

    internal static bool CanDropAndKillReplayWeapon(string className)
        => !WeaponClassMatches(className, "weapon_knife") &&
           GetReplayWeaponSlot(className) != ReplayWeaponSlot.Knife;

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
        return ReplayEquipment.ByClassName.TryGetValue(className, out var definition)
            ? definition.Slot
            : ReplayWeaponSlot.Other;
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
        if (ReplayEquipment.ByDefIndex.TryGetValue(weaponDefIndex, out var definition) &&
            definition.Slot == ReplayWeaponSlot.Knife)
            return WeaponDefIndex("weapon_knife");
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
        return ReplayEquipment.ByClassName.TryGetValue(
            NormalizeWeaponClassName(className),
            out var definition)
            ? definition.WeaponDefIndex
            : -1;
    }

    private static bool TryGetWeaponClassByDefIndex(int weaponDefIndex, out string className)
    {
        if (ReplayEquipment.ByDefIndex.TryGetValue(
                NormalizeWeaponDefIndex(weaponDefIndex),
                out var definition))
        {
            className = definition.ClassName;
            return true;
        }
        className = string.Empty;
        return false;
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
