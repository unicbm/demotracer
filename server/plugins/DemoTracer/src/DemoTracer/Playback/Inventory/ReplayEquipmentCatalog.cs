/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text.Json;

namespace DemoTracer;

internal enum ReplayWeaponSlot
{
    Other,
    Primary,
    Secondary,
    Utility,
    C4,
    Taser,
    Knife
}

internal readonly record struct ReplayEquipmentDefinition(
    int WeaponDefIndex,
    string ClassName,
    ReplayWeaponSlot Slot);

internal sealed class ReplayEquipmentCatalog
{
    private ReplayEquipmentCatalog(
        IReadOnlyDictionary<string, ReplayEquipmentDefinition> byClassName,
        IReadOnlyDictionary<int, ReplayEquipmentDefinition> byDefIndex)
    {
        ByClassName = byClassName;
        ByDefIndex = byDefIndex;
    }

    public static ReplayEquipmentCatalog Empty { get; } = new(
        new Dictionary<string, ReplayEquipmentDefinition>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<int, ReplayEquipmentDefinition>());

    public IReadOnlyDictionary<string, ReplayEquipmentDefinition> ByClassName { get; }

    public IReadOnlyDictionary<int, ReplayEquipmentDefinition> ByDefIndex { get; }

    public static ReplayEquipmentCatalog Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return Parse(document.RootElement);
    }

    public static ReplayEquipmentCatalog Parse(JsonElement root)
    {
        var definitions = root.GetProperty("replay_equipment").EnumerateArray().Select(value =>
            new ReplayEquipmentDefinition(
                value.GetProperty("weapon_defidx").GetInt32(),
                value.GetProperty("class_name").GetString()!,
                value.GetProperty("replay_slot").GetString() switch
                {
                    "primary" => ReplayWeaponSlot.Primary,
                    "secondary" => ReplayWeaponSlot.Secondary,
                    "utility" => ReplayWeaponSlot.Utility,
                    "c4" => ReplayWeaponSlot.C4,
                    "taser" => ReplayWeaponSlot.Taser,
                    "knife" => ReplayWeaponSlot.Knife,
                    _ => throw new InvalidDataException("econ index contains an unsupported replay slot")
                })).ToArray();
        return new ReplayEquipmentCatalog(
            definitions.ToDictionary(value => value.ClassName, StringComparer.OrdinalIgnoreCase),
            definitions.ToDictionary(value => value.WeaponDefIndex));
    }

    public bool IsWeaponCosmeticCategory(int weaponDefIndex)
        => ByDefIndex.TryGetValue(weaponDefIndex, out var definition) &&
           definition.Slot is ReplayWeaponSlot.Primary
               or ReplayWeaponSlot.Secondary
               or ReplayWeaponSlot.Taser;

    public int NormalizeWeaponDefIndex(int weaponDefIndex)
    {
        if (ByDefIndex.TryGetValue(weaponDefIndex, out var definition) &&
            definition.Slot == ReplayWeaponSlot.Knife &&
            ByClassName.TryGetValue("weapon_knife", out var genericKnife))
        {
            return genericKnife.WeaponDefIndex;
        }

        return weaponDefIndex;
    }

    public string ResolveObservedClassName(string designerName, int itemDefinitionIndex)
        => itemDefinitionIndex > 0 && ByDefIndex.TryGetValue(itemDefinitionIndex, out var definition)
            ? definition.ClassName
            : designerName;
}
