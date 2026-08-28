using System.Globalization;
using System.Text.Json;

namespace BotRandomizer;

internal sealed class CharmPlacementCatalog
{
    private readonly Dictionary<ushort, IReadOnlyList<CharmPlacement>> _placements;

    private CharmPlacementCatalog(Dictionary<ushort, IReadOnlyList<CharmPlacement>> placements)
    {
        _placements = placements;
    }

    internal int WeaponCount => _placements.Count;
    internal int PlacementCount => _placements.Values.Sum(placements => placements.Count);

    internal static CharmPlacementCatalog Load(string path, CosmeticCatalog cosmeticCatalog)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return new CharmPlacementCatalog(ParseAndValidate(document.RootElement, cosmeticCatalog));
    }

    internal bool TryGetPlacements(ushort weaponDefIndex, out IReadOnlyList<CharmPlacement> placements)
        => _placements.TryGetValue(weaponDefIndex, out placements!);

    private static Dictionary<ushort, IReadOnlyList<CharmPlacement>> ParseAndValidate(
        JsonElement root,
        CosmeticCatalog cosmeticCatalog)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Charm placement catalog root must be an object.");

        var placementsByWeapon = new Dictionary<ushort, IReadOnlyList<CharmPlacement>>();
        foreach (var weaponProperty in root.EnumerateObject())
        {
            if (!ushort.TryParse(
                    weaponProperty.Name,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var defIndex))
            {
                throw new InvalidDataException(
                    $"Charm placement catalog contains invalid weapon definition '{weaponProperty.Name}'.");
            }

            if (!cosmeticCatalog.TryGetWeapon(defIndex, out _))
                throw new InvalidDataException($"Charm placement catalog contains unknown weapon definition {defIndex}.");

            if (weaponProperty.Value.ValueKind != JsonValueKind.Array
                || weaponProperty.Value.GetArrayLength() == 0)
            {
                throw new InvalidDataException($"Charm placement weapon {defIndex} has no positions.");
            }

            var placements = new List<CharmPlacement>(weaponProperty.Value.GetArrayLength());
            var uniquePlacements = new HashSet<CharmPlacement>();
            foreach (var position in weaponProperty.Value.EnumerateArray())
            {
                if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() != 3)
                {
                    throw new InvalidDataException(
                        $"Charm placement weapon {defIndex} contains a position that is not an [x, y, z] array.");
                }

                var coordinates = position.EnumerateArray().ToArray();
                if (!TryReadFiniteSingle(coordinates[0], out var x)
                    || !TryReadFiniteSingle(coordinates[1], out var y)
                    || !TryReadFiniteSingle(coordinates[2], out var z))
                {
                    throw new InvalidDataException(
                        $"Charm placement weapon {defIndex} contains an invalid position.");
                }

                var placement = new CharmPlacement(x, y, z);
                if (!uniquePlacements.Add(placement))
                    throw new InvalidDataException($"Charm placement weapon {defIndex} contains a duplicate position.");

                placements.Add(placement);
            }

            if (!placementsByWeapon.TryAdd(defIndex, placements))
                throw new InvalidDataException($"Charm placement catalog contains duplicate weapon definition {defIndex}.");
        }

        if (placementsByWeapon.Count == 0)
            throw new InvalidDataException("Charm placement catalog contains no weapons.");

        return placementsByWeapon;
    }

    private static bool TryReadFiniteSingle(JsonElement value, out float coordinate)
    {
        coordinate = default;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetSingle(out coordinate)
            && float.IsFinite(coordinate);
    }
}
