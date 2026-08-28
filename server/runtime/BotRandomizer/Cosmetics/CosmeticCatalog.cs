using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotRandomizer;

internal sealed class CosmeticCatalog
{
    private readonly Dictionary<ushort, WeaponCatalogEntry> _weapons;
    private readonly Dictionary<string, WeaponCatalogEntry> _weaponsByDesignerName;
    private readonly Dictionary<ushort, IReadOnlyList<KnifePaintCatalogEntry>> _knives;
    private readonly Dictionary<string, int> _knifeFinishWeights;
    private readonly IReadOnlyList<IReadOnlyList<StickerCatalogEntry>> _stickerCategoryPools;

    private CosmeticCatalog(CatalogDocument document)
    {
        SourceRepository = document.Source.Repository;
        SourceCommit = document.Source.Commit;
        _weapons = document.Weapons.ToDictionary(entry => entry.DefIndex);
        _weaponsByDesignerName = document.Weapons.ToDictionary(
            entry => entry.DesignerName,
            StringComparer.Ordinal);
        _knives = document.Knives.ToDictionary(
            entry => entry.DefIndex,
            entry => (IReadOnlyList<KnifePaintCatalogEntry>)entry.Paints);
        _knifeFinishWeights = document.KnifeFinishPreferences.ToDictionary(
            entry => entry.Finish,
            entry => entry.Weight,
            StringComparer.Ordinal);
        Gloves = document.Gloves;
        StickerCategories = document.StickerCategories;
        StickerKits = document.StickerKits;
        var stickerGroups = document.StickerKits
            .GroupBy(entry => entry.Category)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<StickerCatalogEntry>)group.ToArray());
        _stickerCategoryPools = document.StickerCategories
            .Select((_, index) => stickerGroups[index])
            .ToArray();
        KeychainDefinitions = document.KeychainDefinitions;
        MusicKits = document.MusicKits;
        SourceLogicalMaps = document.Source.ProDemo.LogicalMaps;
        SourceKnifeObservations = document.Source.ProDemo.KnifeObservations;
        MatchedKnifeObservations = document.Source.ProDemo.MatchedKnifeObservations;
        SourceProConverterSha256 = document.Source.ProDemo.ConverterSha256;
        SourceProCorpusDigest = document.Source.ProDemo.CorpusDigest;
    }

    internal string SourceRepository { get; }
    internal string SourceCommit { get; }
    internal int SourceLogicalMaps { get; }
    internal int SourceKnifeObservations { get; }
    internal int MatchedKnifeObservations { get; }
    internal string SourceProConverterSha256 { get; }
    internal string SourceProCorpusDigest { get; }
    internal IReadOnlyList<GloveCatalogEntry> Gloves { get; }
    internal IReadOnlyList<string> StickerCategories { get; }
    internal IReadOnlyList<StickerCatalogEntry> StickerKits { get; }
    internal IReadOnlyList<IReadOnlyList<StickerCatalogEntry>> StickerCategoryPools
        => _stickerCategoryPools;
    internal IReadOnlyList<uint> KeychainDefinitions { get; }
    internal IReadOnlyList<int> MusicKits { get; }
    internal IReadOnlyCollection<WeaponCatalogEntry> Weapons => _weapons.Values;
    internal int WeaponCount => _weapons.Count;
    internal int WeaponPaintCount => _weapons.Values.Sum(entry => entry.Paints.Count);
    internal int KnifePaintCount => _knives.Values.Sum(entry => entry.Count);

    internal static CosmeticCatalog Load(string path)
    {
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<CatalogDocument>(stream, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        })
            ?? throw new InvalidDataException("Cosmetic catalog is empty.");
        Validate(document);
        return new CosmeticCatalog(document);
    }

    internal bool TryGetWeapon(ushort defIndex, out WeaponCatalogEntry entry)
        => _weapons.TryGetValue(defIndex, out entry!);

    internal bool TryGetWeapon(string designerName, out WeaponCatalogEntry entry)
        => _weaponsByDesignerName.TryGetValue(designerName, out entry!);

    internal bool TryGetKnifePaints(
        ushort defIndex,
        out IReadOnlyList<KnifePaintCatalogEntry> paints)
        => _knives.TryGetValue(defIndex, out paints!);

    internal int GetKnifeFinishWeight(string finish)
        => _knifeFinishWeights.GetValueOrDefault(finish, 1);

    private static void Validate(CatalogDocument document)
    {
        if (document.Source is null
            || document.Source.Repository != "ianlucas/cs2-lib"
            || document.Source.Commit.Length != 40
            || document.Source.Commit.Any(ch => !Uri.IsHexDigit(ch))
            || document.Source.ProDemo.LogicalMaps <= 0
            || document.Source.ProDemo.KnifeObservations <= 0
            || document.Source.ProDemo.MatchedKnifeObservations <= 0
            || document.Source.ProDemo.MatchedKnifeObservations
                != document.Source.ProDemo.KnifeObservations
            || document.Source.ProDemo.ConverterSha256.Length != 64
            || document.Source.ProDemo.ConverterSha256.Any(ch => !Uri.IsHexDigit(ch))
            || document.Source.ProDemo.CorpusDigest.Length != 64
            || document.Source.ProDemo.CorpusDigest.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException("Catalog source metadata is invalid.");
        }

        if (document.Weapons.Count == 0
            || document.Knives.Count == 0
            || document.KnifeFinishPreferences.Count == 0
            || document.Gloves.Count == 0
            || document.StickerCategories.Count == 0
            || document.StickerKits.Count == 0
            || document.KeychainDefinitions.Count == 0
            || document.MusicKits.Count == 0)
        {
            throw new InvalidDataException("Catalog is missing a required cosmetic family.");
        }

        EnsureUnique(document.Weapons.Select(entry => entry.DefIndex), "weapon definition");
        EnsureUnique(document.Weapons.Select(entry => entry.DesignerName), "weapon designer name");
        EnsureUnique(document.Knives.Select(entry => entry.DefIndex), "knife definition");
        EnsureUnique(document.KnifeFinishPreferences.Select(entry => entry.Finish),
            "knife finish preference");
        EnsureUnique(document.Gloves.Select(entry => (entry.DefIndex, entry.PaintKit)), "glove variant");
        EnsureUnique(document.StickerCategories, "sticker category");
        EnsureUnique(document.StickerKits.Select(entry => entry.DefIndex), "sticker kit");
        EnsureUnique(document.KeychainDefinitions, "keychain definition");
        EnsureUnique(document.MusicKits, "music kit");

        foreach (var weapon in document.Weapons)
        {
            if (weapon.DefIndex == 0
                || !weapon.DesignerName.StartsWith("weapon_", StringComparison.Ordinal)
                || weapon.Paints.Count == 0)
            {
                throw new InvalidDataException($"Weapon {weapon.DefIndex} has no valid paints.");
            }
            if (weapon.StickerSchemaCount <= 0 || weapon.LegacyStickerSchemaCount <= 0)
                throw new InvalidDataException($"Weapon {weapon.DefIndex} has invalid sticker schemas.");
            ValidateWeaponPaints(weapon.Paints, $"weapon {weapon.DefIndex}");
        }

        foreach (var knife in document.Knives)
        {
            if (knife.DefIndex == 0 || knife.Paints.Count == 0)
                throw new InvalidDataException($"Knife {knife.DefIndex} has no valid paints.");
            ValidateKnifePaints(knife.Paints, $"knife {knife.DefIndex}");
        }

        foreach (var preference in document.KnifeFinishPreferences)
        {
            if (string.IsNullOrWhiteSpace(preference.Finish)
                || preference.Observations <= 0
                || preference.Weight <= 0)
            {
                throw new InvalidDataException("Catalog contains an invalid knife preference.");
            }
        }

        foreach (var glove in document.Gloves)
        {
            if (glove.DefIndex == 0 || glove.PaintKit <= 0)
                throw new InvalidDataException("Catalog contains an invalid glove variant.");
            ValidateWear(glove.WearMin, glove.WearMax, $"glove {glove.DefIndex}/{glove.PaintKit}");
        }

        if (document.StickerKits.Any(entry =>
                entry.DefIndex == 0
                || !Enum.IsDefined(entry.Finish)
                || entry.Category < 0
                || entry.Category >= document.StickerCategories.Count)
            || document.KeychainDefinitions.Any(value => value == 0)
            || !document.KeychainDefinitions.Contains(37)
            || document.MusicKits.Any(value => value <= 0))
        {
            throw new InvalidDataException("Catalog contains invalid cosmetic indexes.");
        }

        var populatedStickerCategories = document.StickerKits
            .Select(entry => entry.Category)
            .Distinct()
            .Count();
        if (populatedStickerCategories != document.StickerCategories.Count)
            throw new InvalidDataException("Catalog contains an empty sticker category.");
    }

    private static void ValidateWeaponPaints(
        IReadOnlyList<PaintCatalogEntry> paints,
        string owner)
    {
        EnsureUnique(paints.Select(entry => entry.PaintKit), $"{owner} paint");
        foreach (var paint in paints)
        {
            if (paint.PaintKit <= 0 || !Enum.IsDefined(paint.Rarity))
                throw new InvalidDataException($"{owner} contains an invalid paint kit.");
            ValidateWear(paint.WearMin, paint.WearMax, $"{owner}/{paint.PaintKit}");
        }
    }

    private static void ValidateKnifePaints(
        IReadOnlyList<KnifePaintCatalogEntry> paints,
        string owner)
    {
        EnsureUnique(paints.Select(entry => entry.PaintKit), $"{owner} paint");
        foreach (var paint in paints)
        {
            if (paint.PaintKit <= 0 || string.IsNullOrWhiteSpace(paint.Finish))
                throw new InvalidDataException($"{owner} contains an invalid paint kit.");
            ValidateWear(paint.WearMin, paint.WearMax, $"{owner}/{paint.PaintKit}");
        }
    }

    private static void ValidateWear(float minimum, float maximum, string owner)
    {
        if (!float.IsFinite(minimum)
            || !float.IsFinite(maximum)
            || minimum < 0.0f
            || maximum > 1.0f
            || minimum > maximum)
        {
            throw new InvalidDataException($"{owner} has an invalid wear range.");
        }

        var firstRepresentableTick = (int)Math.Ceiling(minimum * 1000.0f);
        var lastRepresentableTick = (int)Math.Floor(maximum * 1000.0f);
        if (firstRepresentableTick > lastRepresentableTick)
            throw new InvalidDataException($"{owner} has no representable wear tick.");
    }

    private static void EnsureUnique<T>(IEnumerable<T> values, string label)
        where T : notnull
    {
        var set = new HashSet<T>();
        foreach (var value in values)
        {
            if (!set.Add(value))
                throw new InvalidDataException($"Catalog contains duplicate {label}: {value}.");
        }
    }

    private sealed class CatalogDocument
    {
        [JsonPropertyName("source")]
        public CatalogSource Source { get; init; } = new();

        [JsonPropertyName("weapons")]
        public List<WeaponCatalogEntry> Weapons { get; init; } = [];

        [JsonPropertyName("knives")]
        public List<KnifeCatalogDocument> Knives { get; init; } = [];

        [JsonPropertyName("knifeFinishPreferences")]
        public List<KnifeFinishPreferenceDocument> KnifeFinishPreferences { get; init; } = [];

        [JsonPropertyName("gloves")]
        public List<GloveCatalogEntry> Gloves { get; init; } = [];

        [JsonPropertyName("stickerCategories")]
        public List<string> StickerCategories { get; init; } = [];

        [JsonPropertyName("stickerKits")]
        public List<StickerCatalogEntry> StickerKits { get; init; } = [];

        [JsonPropertyName("keychainDefinitions")]
        public List<uint> KeychainDefinitions { get; init; } = [];

        [JsonPropertyName("musicKits")]
        public List<int> MusicKits { get; init; } = [];
    }

    private sealed class CatalogSource
    {
        [JsonPropertyName("repository")]
        public string Repository { get; init; } = string.Empty;

        [JsonPropertyName("commit")]
        public string Commit { get; init; } = string.Empty;

        [JsonPropertyName("proDemo")]
        public ProDemoSource ProDemo { get; init; } = new();
    }

    private sealed class ProDemoSource
    {
        [JsonPropertyName("logicalMaps")]
        public int LogicalMaps { get; init; }

        [JsonPropertyName("knifeObservations")]
        public int KnifeObservations { get; init; }

        [JsonPropertyName("matchedKnifeObservations")]
        public int MatchedKnifeObservations { get; init; }

        [JsonPropertyName("converterSha256")]
        public string ConverterSha256 { get; init; } = string.Empty;

        [JsonPropertyName("corpusDigest")]
        public string CorpusDigest { get; init; } = string.Empty;
    }

    private sealed class KnifeCatalogDocument
    {
        [JsonPropertyName("defIndex")]
        public ushort DefIndex { get; init; }

        [JsonPropertyName("paints")]
        public List<KnifePaintCatalogEntry> Paints { get; init; } = [];
    }

    private sealed class KnifeFinishPreferenceDocument
    {
        [JsonPropertyName("finish")]
        public string Finish { get; init; } = string.Empty;

        [JsonPropertyName("observations")]
        public int Observations { get; init; }

        [JsonPropertyName("weight")]
        public int Weight { get; init; }
    }
}
