using System.Text;

namespace DemoTracerBotHiderApi;

public static class DemoTracerBotHiderContract
{
    public const int ApiVersion = 2;
    public const string Capability = "demotracer:bot-hider:v2";
    public const string DemoTracerOwner = "demotracer";
    public const int MaxPlayerNameUtf8Bytes = 31;
    public const int MaxCrosshairCodeUtf8Bytes = 63;

    // Shared contract assembly: notifications survive provider hot reload.
    // Subscribers must unsubscribe on unload and defer engine work to a frame.
    public static event Action? ProviderChanged;
    public static void NotifyProviderChanged() => ProviderChanged?.Invoke();

    public static bool TryNormalizeCrosshairCode(string? source, out string? normalized)
    {
        if (source == null)
        {
            normalized = null;
            return true;
        }

        normalized = source.Trim();
        if (!normalized.Contains('\0') && Encoding.UTF8.GetByteCount(normalized) <= MaxCrosshairCodeUtf8Bytes)
            return true;

        normalized = null;
        return false;
    }
}

public interface IBotHiderApi
{
    int ApiVersion { get; }

    BotHiderProviderInfo GetProviderInfo();

    bool IsManagedBot(int slot);

    bool TryGetManagedSlot(int slot, out BotHiderManagedSlot state);

    // Acquisition/replacement commits lease ownership only after native identity
    // and requested controller fields confirm success; this is not a client ACK.
    // All operations, including ownerLifetime cancellation, are main-thread only.
    // The owner must cancel on unload; no heartbeat or expiration is required.
    // A disconnected or reused slot leaves
    // the lease without revoking surviving slots. An empty lease is revoked.
    BotHiderPresentationLeaseResult AcquirePresentationLease(
        string owner,
        BotHiderPresentationOverride[] overrides,
        CancellationToken ownerLifetime);

    BotHiderPresentationLeaseResult ReplacePresentationLease(
        string leaseToken,
        BotHiderPresentationOverride[] overrides);

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
    public ulong PublishedSteamId { get; set; }

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

    public int PublishedWrites { get; set; }

    public int ControllerRepairs { get; set; }

    public string[] Signatures { get; set; } = [];
}
