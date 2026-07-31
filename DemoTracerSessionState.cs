using CounterStrikeSharp.API.Core;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private readonly ReplaySessionState _session = new();

    private sealed class ReplaySessionState
    {
        public List<int> LoadedSlots { get; } = [];
        public HashSet<int> WarmReplayBufferSlots { get; } = [];
        public HashSet<int> DemoTracerOwnedSlots { get; } = [];
        public HashSet<int> FreezePrerollSlots { get; } = [];
        public HashSet<int> ResumedFreezePrerollSlots { get; } = [];
        public Dictionary<int, LoadedReplay> LoadedReplays { get; } = [];
        public Dictionary<int, int> LastEnsuredWeaponDef { get; } = [];
        public Dictionary<int, int> LastReplayWeaponDef { get; } = [];
        public Dictionary<int, int> LastLockedWeaponTarget { get; } = [];
        public Dictionary<(int PlayerSlot, ReplayWeaponSlot WeaponSlot), PendingWeaponSlotReplacement>
            PendingWeaponSlotReplacements { get; } = [];
        public Dictionary<int, int> ProjectileAlignNextBySlot { get; } = [];
        public Dictionary<int, int> ReplayHifiEventNextBySlot { get; } = [];
        public Dictionary<int, long> ReplayIdentityGenerationBySlot { get; } = [];
        public Dictionary<int, long> ReplayMutationGenerationBySlot { get; } = [];
        public Dictionary<uint, PendingProjectileAlign> PendingProjectileAlign { get; } = [];
        public List<KeyValuePair<uint, PendingProjectileAlign>> PendingProjectileAlignTickScratch { get; } = [];
        public Queue<string> ProjectileAlignLog { get; } = [];
        public HashSet<int> RebuiltInventorySlots { get; } = [];
        public HashSet<int> LoadoutSyncedSlots { get; } = [];
        public HashSet<int> BalanceSyncedSlots { get; } = [];
        public HashSet<int> LastPlayingSlots { get; } = [];
        public Dictionary<int, float> ReplayStartedAt { get; } = [];
        public Dictionary<int, uint> ReplayPerceptionBaselineSerial { get; } = [];
        public Dictionary<int, PendingBulletHit> PendingBulletHits { get; } = [];
        public Dictionary<int, PendingBulletDamage> PendingBulletDamages { get; } = [];
        public Dictionary<int, PendingThreat360> PendingThreat360 { get; } = [];
        public Dictionary<int, ReplayMusicKitBaseline> ReplayMusicKitBaselines { get; } = [];
        public Dictionary<int, long> ReplayMusicKitRepairTokens { get; } = [];
        public HashSet<int> CosmeticSyncedSlots { get; } = [];
        public Dictionary<int, AppliedActiveWeaponCosmetic> ActiveWeaponCosmetics { get; } = [];
        public HashSet<int> ScoreboardSyncedSlots { get; } = [];
        public Dictionary<int, ReplayViewmodel> ReplayOriginalViewmodels { get; } = [];
        public Dictionary<int, ReplayViewmodel> ReplayAppliedViewmodels { get; } = [];
        public HashSet<int> ReplayFailedViewmodelSlots { get; } = [];
        public ReplayPlanState Plan { get; } = new();

        public long NextReplayMusicKitRepairToken { get; set; }
        public bool SafeC4Aligned { get; set; }
        public int InitialSpawnAssignmentToken { get; set; }
        public bool InitialSpawnAssignmentComplete { get; set; }
        public bool InitialSpawnAssignmentScheduled { get; set; }
        public ulong LastReplayPovMask { get; set; } = ulong.MaxValue;

        public int FreezePrerollToken { get; set; }
        public bool FreezePrerollStarted { get; set; }
        public ReplayRoundScoreboard? LoadedRoundScoreboard { get; set; }

        public long NextReplayIdentityGeneration { get; set; }
        public long NextReplayMutationGeneration { get; set; }

    }
}
