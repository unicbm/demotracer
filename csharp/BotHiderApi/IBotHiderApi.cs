namespace DemoTracerBotHiderApi;

public static class DemoTracerBotHiderContract
{
    public const int ApiVersion = 1;
    public const string Capability = "demotracer:bot-hider:v1";
    public const string DemoTracerOwner = "demotracer";
    public const int MaxPlayerNameUtf8Bytes = 31;
}

public interface IBotHiderApi
{
    int ApiVersion { get; }

    BotHiderProviderInfo GetProviderInfo();

    bool IsManagedBot(int slot);

    bool TryGetManagedSlot(int slot, out BotHiderManagedSlot state);

    BotHiderPresentationLeaseResult AcquirePresentationLease(
        string owner,
        BotHiderPresentationOverride[] overrides);

    BotHiderPresentationLeaseResult ReplacePresentationLease(
        string leaseToken,
        BotHiderPresentationOverride[] overrides);

    bool HeartbeatPresentationLease(string leaseToken);

    bool ReleasePresentationLease(string leaseToken);

    int ReleasePresentationLeasesByOwner(string owner);

    BotHiderDiagnostics GetDiagnostics();
}

public sealed class BotHiderProviderInfo
{
    public int ApiVersion { get; set; }

    public string ProviderEpoch { get; set; } = string.Empty;

    public ulong MapEpoch { get; set; }

    public bool Connected { get; set; }

    public bool Draining { get; set; }
}

public sealed class BotHiderManagedSlot
{
    public int Slot { get; set; }

    public ulong Incarnation { get; set; }

    public ulong BaseSteamId { get; set; }

    public string BasePlayerName { get; set; } = string.Empty;

    public int BasePing { get; set; }

    public string BaseCrosshairCode { get; set; } = string.Empty;

    public uint BaseScoreboardFlair { get; set; }
}

public sealed class BotHiderPresentationOverride
{
    public int Slot { get; set; }

    public ulong Incarnation { get; set; }

    public string? PlayerName { get; set; }

    public ulong? SteamId { get; set; }

    public uint? ScoreboardFlair { get; set; }

    // null keeps the current persona base; empty explicitly clears it.
    public string? CrosshairCode { get; set; }
}

public sealed class BotHiderPresentationLeaseResult
{
    public bool Ok { get; set; }

    public string LeaseToken { get; set; } = string.Empty;

    public string ProviderEpoch { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public int[] Slots { get; set; } = [];
}

public sealed class BotHiderDiagnostics
{
    public bool Connected { get; set; }

    public int ManagedSlots { get; set; }

    public int ActiveLeases { get; set; }

    public int LeasedSlots { get; set; }

    public int RevokedLeases { get; set; }

    public int ExpiredLeases { get; set; }

    public int PublishedWrites { get; set; }

    public int ControllerRepairs { get; set; }

    public string[] Signatures { get; set; } = [];
}
