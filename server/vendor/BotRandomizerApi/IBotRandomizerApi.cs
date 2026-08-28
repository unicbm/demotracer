namespace BotRandomizerApi;

public static class BotRandomizerContract
{
    public const int ApiVersion = 2;
    public const string Capability = "botrandomizer:replay-cosmetic-plan:v2";
    public const string DemoTracerOwner = "demotracer";
    public const int LeaseTimeoutMilliseconds = 4_000;
}

public static class BotRandomizerReplayTeamPolicy
{
    public const byte Terrorist = 2;
    public const byte CounterTerrorist = 3;

    public static byte ResolveUpcomingTeam(byte currentTeam, bool switchingTeamsAtRoundReset)
    {
        if (!switchingTeamsAtRoundReset)
            return currentTeam;
        return currentTeam switch
        {
            Terrorist => CounterTerrorist,
            CounterTerrorist => Terrorist,
            _ => currentTeam
        };
    }

    public static bool CanTargetSpawnTeam(
        byte currentTeam,
        byte spawnTeam,
        bool switchingTeamsAtRoundReset)
    {
        if (spawnTeam is not (Terrorist or CounterTerrorist) ||
            currentTeam is not (Terrorist or CounterTerrorist))
        {
            return false;
        }

        // During the reset boundary this accepts both observations: the plan
        // can be validated immediately before the engine flips the controller,
        // or just after the controller already reports its upcoming team.
        return spawnTeam == currentTeam ||
               switchingTeamsAtRoundReset &&
               spawnTeam == ResolveUpcomingTeam(currentTeam, switchingTeamsAtRoundReset: true);
    }
}

/// <summary>
/// The provider is the sole cosmetic entity writer. Consumers submit immutable,
/// demo-backed desired state; the provider validates it and applies it only from
/// its normal spawn and GiveNamedItem lifecycle.
/// </summary>
public interface IBotRandomizerApi
{
    int ApiVersion { get; }
    BotRandomizerProviderInfo GetProviderInfo();
    bool TryGetManagedBot(int slot, out BotRandomizerManagedBot state);
    BotRandomizerReplayPlanResult AcquireReplayPlan(string owner, BotRandomizerReplayCosmeticPlan[] plans);
    BotRandomizerReplayPlanResult ReplaceReplayPlan(string planToken, BotRandomizerReplayCosmeticPlan[] plans);
    bool HeartbeatReplayPlan(string planToken);
    bool ReleaseReplayPlan(string planToken);
    int ReleaseReplayPlansByOwner(string owner);
    BotRandomizerDiagnostics GetDiagnostics();
}

public enum BotRandomizerAgentPlanMode
{
    Randomized = 0,
    PreserveEngineDefault = 1,
    ReplayModel = 2
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
    public bool ReplayPlanPrebuildAvailable { get; set; }
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
    public bool HasReplayPlan { get; set; }
    public string ReplayPlanOwner { get; set; } = string.Empty;
}

public sealed class BotRandomizerReplayCosmeticPlan
{
    public int Slot { get; set; }
    public ulong Incarnation { get; set; }
    public ulong SubjectSteamId { get; set; }
    /// <summary>
    /// Team on which this plan may be written at spawn. Zero preserves the
    /// pre-1.6.2 behavior and resolves to the controller's current team.
    /// </summary>
    public byte SpawnTeam { get; set; }
    public BotRandomizerAgentPlan Agent { get; set; } = new();
    public BotRandomizerReplayItem? Knife { get; set; }
    public BotRandomizerReplayItem? Gloves { get; set; }
    public int? MusicKit { get; set; }
    public BotRandomizerReplayWeapon[] Weapons { get; set; } = [];
}

public sealed class BotRandomizerAgentPlan
{
    public BotRandomizerAgentPlanMode Mode { get; set; }
    public uint? ItemDefinitionIndex { get; set; }
    public string? ModelPath { get; set; }
}

public class BotRandomizerReplayItem
{
    public int ItemDefinitionIndex { get; set; }
    public uint PaintKit { get; set; }
    public uint PaintSeed { get; set; }
    public float PaintWear { get; set; }
    public int? Quality { get; set; }
    public int? StattrakCounter { get; set; }
    public ulong? OriginalOwnerSteamId { get; set; }
    public uint? ItemAccountId { get; set; }
    public ulong? ItemId { get; set; }
    public string? CustomName { get; set; }
}

public sealed class BotRandomizerReplayWeapon : BotRandomizerReplayItem
{
    public bool? PaintUsesLegacyModel { get; set; }
    public BotRandomizerReplaySticker[] Stickers { get; set; } = [];
    public BotRandomizerReplayKeychain[] Keychains { get; set; } = [];
}

public sealed class BotRandomizerReplaySticker
{
    public int Slot { get; set; }
    public uint StickerId { get; set; }
    public uint Schema { get; set; }
    public float Wear { get; set; }
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public float? Scale { get; set; }
    public float? Rotation { get; set; }
}

public sealed class BotRandomizerReplayKeychain
{
    public int Slot { get; set; }
    public uint KeychainId { get; set; }
    public int Seed { get; set; }
    public uint? StickerId { get; set; }
    public uint? Highlight { get; set; }
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public float OffsetZ { get; set; }
}

public sealed class BotRandomizerReplayPlanResult
{
    public bool Ok { get; set; }
    public string PlanToken { get; set; } = string.Empty;
    public string ProviderEpoch { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int[] Slots { get; set; } = [];
    public bool AppliesOnNextSpawn { get; set; }
}

public sealed class BotRandomizerDiagnostics
{
    public bool Ready { get; set; }
    public int ActivePlans { get; set; }
    public int PlannedSlots { get; set; }
    public int AcquiredPlans { get; set; }
    public int ReplacedPlans { get; set; }
    public int ReleasedPlans { get; set; }
    public int RevokedPlans { get; set; }
    public int ExpiredPlans { get; set; }
    public int RejectedRequests { get; set; }
}
