namespace BotRandomizer;

[Flags]
internal enum CosmeticScope
{
    None = 0,
    Weapons = 1 << 0,
    Knife = 1 << 1,
    Gloves = 1 << 2,
    Agent = 1 << 3,
    MusicKit = 1 << 4,
    All = Weapons | Knife | Gloves | Agent | MusicKit
}

internal enum CosmeticRarity
{
    Consumer,
    Industrial,
    MilSpec,
    Restricted,
    Classified,
    Covert,
    Contraband
}

internal enum StickerFinish
{
    Paper,
    Glitter,
    Holo,
    Foil,
    Gold,
    Lenticular
}

internal sealed record PaintCatalogEntry(
    int PaintKit,
    CosmeticRarity Rarity,
    bool Legacy,
    float WearMin,
    float WearMax);

internal sealed record KnifePaintCatalogEntry(
    int PaintKit,
    string Finish,
    float WearMin,
    float WearMax);

internal sealed record WeaponCatalogEntry(
    string DesignerName,
    ushort DefIndex,
    int StickerSchemaCount,
    int LegacyStickerSchemaCount,
    IReadOnlyList<PaintCatalogEntry> Paints);

internal sealed record GloveCatalogEntry(
    ushort DefIndex,
    int PaintKit,
    float WearMin,
    float WearMax);

internal sealed record StickerCatalogEntry(
    uint DefIndex,
    StickerFinish Finish,
    int Category);

internal readonly record struct KnifeDefinition(
    ushort DefIndex,
    int Weight);

internal sealed record StickerSelection(
    uint DefIndex,
    int Slot,
    uint Schema,
    float Wear = 0.0f,
    float? Rotation = null,
    float? X = null,
    float? Y = null,
    float? Scale = null);

internal readonly record struct CharmPlacement(float X, float Y, float Z);

internal sealed record KeychainSelection(
    uint DefIndex,
    int Seed,
    int Slot = 0,
    uint? Sticker = null,
    float? X = null,
    float? Y = null,
    float? Z = null,
    uint? Highlight = null);

internal sealed record WeaponCosmeticSelection(
    int PaintKit,
    int Seed,
    float Wear,
    bool Legacy,
    IReadOnlyList<StickerSelection> Stickers,
    KeychainSelection? Keychain);

internal sealed record ReplayEconIdentity(
    int? Quality,
    int? StattrakCounter,
    ulong? OriginalOwnerSteamId,
    uint? ItemAccountId,
    ulong? ItemId,
    string? CustomName);

internal static class ReplayOriginalOwner
{
    private const ulong SteamId64AccountBase = 76_561_197_960_265_728UL;

    internal static ulong ResolveSteamId(ReplayEconIdentity identity, ulong fallbackSteamId)
    {
        if (identity.OriginalOwnerSteamId is > 0)
            return identity.OriginalOwnerSteamId.Value;
        if (identity.ItemAccountId is > 0)
            return SteamId64AccountBase + identity.ItemAccountId.Value;
        return fallbackSteamId;
    }

    internal static (uint Low, uint High) SplitSteamId(ulong steamId)
        => ((uint)(steamId & uint.MaxValue), (uint)(steamId >> 32));
}

internal sealed record ReplayItemSelection(
    ushort DefIndex,
    int PaintKit,
    int Seed,
    float Wear,
    ReplayEconIdentity Identity);

internal sealed record ReplayWeaponSelection(
    ushort DefIndex,
    int PaintKit,
    int Seed,
    float Wear,
    bool Legacy,
    IReadOnlyList<StickerSelection> Stickers,
    IReadOnlyList<KeychainSelection> Keychains,
    ReplayEconIdentity Identity);

internal sealed record KnifeSelection(
    ushort DefIndex,
    int PaintKit,
    float Wear,
    int Seed = 0,
    ReplayEconIdentity? Identity = null);

internal sealed record GloveSelection(
    ushort DefIndex,
    int PaintKit,
    float Wear,
    int Seed = 0,
    ReplayEconIdentity? Identity = null);

internal sealed class BotCosmeticLoadout
{
    public required byte Team { get; init; }
    public required string AgentModel { get; init; }
    public required int MusicKit { get; init; }
    public required KnifeSelection Knife { get; init; }
    public required GloveSelection Glove { get; init; }
    public Dictionary<ushort, WeaponCosmeticSelection> Weapons { get; } = new();
}

internal sealed class RandomizerOptions
{
    public bool Enabled { get; set; } = true;
    public bool Weapons { get; set; } = true;
    public bool Knives { get; set; } = true;
    public bool Gloves { get; set; } = true;
    public bool Agents { get; set; } = true;
    public bool Music { get; set; } = true;
    public bool Stickers { get; set; } = true;
    public bool Charms { get; set; } = true;
}
