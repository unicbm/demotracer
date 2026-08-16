namespace BotRandomizerApi;

public static class BotRandomizerContract
{
    public const int ApiVersion = 1;
    public const string Capability = "botrandomizer:cosmetic-writer:v1";
    public const string DemoTracerOwner = "demotracer";
    public const int LeaseTimeoutMilliseconds = 4_000;
}

public interface IBotRandomizerApi
{
    int ApiVersion { get; }

    BotRandomizerProviderInfo GetProviderInfo();

    bool TryGetManagedBot(int slot, out BotRandomizerManagedBot state);

    BotRandomizerWriteLeaseResult AcquireWriteLease(
        string owner,
        BotRandomizerCosmeticWriteClaim[] claims);

    BotRandomizerWriteLeaseResult ReplaceWriteLease(
        string leaseToken,
        BotRandomizerCosmeticWriteClaim[] claims);

    bool HeartbeatWriteLease(string leaseToken);

    bool ReleaseWriteLease(string leaseToken);

    int ReleaseWriteLeasesByOwner(string owner);

    BotRandomizerDiagnostics GetDiagnostics();
}

public sealed class BotRandomizerProviderInfo
{
    public int ApiVersion { get; set; }

    public string ProviderEpoch { get; set; } = string.Empty;

    public ulong MapEpoch { get; set; }

    public bool Ready { get; set; }

    public bool Draining { get; set; }

    public bool EconAttributeWriterAvailable { get; set; }

    public bool WeaponPrebuildAvailable { get; set; }

    // True only when externally owned paint can be supplied to the
    // GiveNamedItem CEconItemView before the weapon entity is constructed.
    public bool AuthoritativePaintPrebuildAvailable { get; set; }

    public string CatalogRepository { get; set; } = string.Empty;

    public string CatalogCommit { get; set; } = string.Empty;

    public int LeaseTimeoutMilliseconds { get; set; }
}

public sealed class BotRandomizerManagedBot
{
    public int Slot { get; set; }

    public int UserId { get; set; }

    public ulong Incarnation { get; set; }

    public ulong SteamId { get; set; }

    public int PawnEntityIndex { get; set; } = -1;

    public byte Team { get; set; }

    public bool HasWriteLease { get; set; }

    public string LeaseOwner { get; set; } = string.Empty;
}

public sealed class BotRandomizerCosmeticWriteClaim
{
    public int Slot { get; set; }

    public ulong Incarnation { get; set; }

    public ulong? SubjectSteamId { get; set; }

    public bool Agent { get; set; }

    public bool Knife { get; set; }

    public bool Gloves { get; set; }

    public bool MusicKit { get; set; }

    public BotRandomizerWeaponWriteClaim[] Weapons { get; set; } = [];
}

public sealed class BotRandomizerWeaponWriteClaim
{
    public int WeaponDefinitionIndex { get; set; }

    public bool Paint { get; set; }

    public bool Stickers { get; set; }

    public bool Keychain { get; set; }

    // Paint ownership requires the complete authoritative paint tuple. The
    // provider validates and prebuilds these values before GiveNamedItem so
    // model-sensitive weapons never start life with an unpainted item view.
    public uint? PaintKit { get; set; }

    public uint? PaintSeed { get; set; }

    public float? PaintWear { get; set; }

    // When external paint evidence is authoritative but stickers remain random,
    // this optional hint selects the matching legacy/current sticker schema.
    // null makes BotRandomizer use the safe intersection of both schemas.
    public bool? PaintUsesLegacyModel { get; set; }
}

public sealed class BotRandomizerWriteLeaseResult
{
    public bool Ok { get; set; }

    public string LeaseToken { get; set; } = string.Empty;

    public string ProviderEpoch { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public int[] Slots { get; set; } = [];
}

public sealed class BotRandomizerDiagnostics
{
    public bool Ready { get; set; }

    public int ActiveLeases { get; set; }

    public int LeasedSlots { get; set; }

    public int AcquiredLeases { get; set; }

    public int ReplacedLeases { get; set; }

    public int ReleasedLeases { get; set; }

    public int RevokedLeases { get; set; }

    public int ExpiredLeases { get; set; }

    public int RejectedRequests { get; set; }
}
