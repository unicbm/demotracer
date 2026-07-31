namespace DemoTracer;

internal static class ReplayUtilityGrantPolicy
{
    public static bool ShouldQueue(
        ReplayHifiEvent replayEvent,
        ulong replaySteamId,
        ReplayEquipmentCatalog equipment)
    {
        var kind = replayEvent.Kind.Trim().ToLowerInvariant();
        return kind is "item_pickup" or "item_transfer" &&
               BelongsToSlot(replayEvent.TargetSteamId, replaySteamId) &&
               TryResolveWeaponDefIndex(replayEvent, equipment, out var weaponDefIndex) &&
               equipment.ByDefIndex.TryGetValue(weaponDefIndex, out var definition) &&
               definition.Slot == ReplayWeaponSlot.Utility;
    }

    private static bool BelongsToSlot(ulong? eventSteamId, ulong replaySteamId)
        => !eventSteamId.HasValue || replaySteamId == 0 || eventSteamId.Value == replaySteamId;

    private static bool TryResolveWeaponDefIndex(
        ReplayHifiEvent replayEvent,
        ReplayEquipmentCatalog equipment,
        out int weaponDefIndex)
    {
        if (replayEvent.WeaponDefIndex.HasValue)
        {
            weaponDefIndex = replayEvent.WeaponDefIndex.Value;
            return equipment.ByDefIndex.ContainsKey(weaponDefIndex);
        }

        weaponDefIndex = -1;
        if (string.IsNullOrWhiteSpace(replayEvent.ItemName))
            return false;

        var normalized = replayEvent.ItemName.Trim().ToLowerInvariant() switch
        {
            "decoy_grenade" or "weapon_decoy_grenade" => "weapon_decoy",
            "c4" or "weapon_c4_explosive" => "weapon_c4",
            var value => value
        };
        if (!normalized.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase))
            normalized = $"weapon_{normalized}";

        if (!equipment.ByClassName.TryGetValue(normalized, out var definition))
            return false;

        weaponDefIndex = definition.WeaponDefIndex;
        return true;
    }
}
