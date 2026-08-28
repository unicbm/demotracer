/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Globalization;
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
        if (!root.TryGetProperty("replay_equipment", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("econ index is missing replay_equipment");
        }

        var byClassName = new Dictionary<string, ReplayEquipmentDefinition>(
            StringComparer.OrdinalIgnoreCase);
        var byDefIndex = new Dictionary<int, ReplayEquipmentDefinition>();
        foreach (var value in values.EnumerateArray())
        {
            if (!TryReadIntProperty(value, "weapon_defidx", out var weaponDefIndex) ||
                !value.TryGetProperty("class_name", out var classNameValue) ||
                classNameValue.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("replay_slot", out var replaySlotValue) ||
                replaySlotValue.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("econ index contains invalid replay equipment");
            }

            var className = classNameValue.GetString();
            var replaySlot = replaySlotValue.GetString() switch
            {
                "primary" => ReplayWeaponSlot.Primary,
                "secondary" => ReplayWeaponSlot.Secondary,
                "utility" => ReplayWeaponSlot.Utility,
                "c4" => ReplayWeaponSlot.C4,
                "taser" => ReplayWeaponSlot.Taser,
                "knife" => ReplayWeaponSlot.Knife,
                _ => ReplayWeaponSlot.Other
            };
            var definition = new ReplayEquipmentDefinition(
                weaponDefIndex,
                className ?? string.Empty,
                replaySlot);
            if (string.IsNullOrWhiteSpace(className) ||
                replaySlot == ReplayWeaponSlot.Other ||
                !byClassName.TryAdd(className, definition) ||
                !byDefIndex.TryAdd(weaponDefIndex, definition))
            {
                throw new InvalidDataException(
                    "econ index contains duplicate or unsupported replay equipment");
            }
        }

        return new ReplayEquipmentCatalog(byClassName, byDefIndex);
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

    private static bool TryReadIntProperty(JsonElement value, string propertyName, out int parsed)
    {
        parsed = 0;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out parsed),
            JsonValueKind.String => int.TryParse(
                property.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parsed),
            _ => false
        };
    }
}
