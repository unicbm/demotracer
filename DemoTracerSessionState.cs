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

        public long NextReplayMusicKitRepairToken { get; set; }
        public bool SafeC4Aligned { get; set; }
        public int InitialSpawnAssignmentToken { get; set; }
        public bool InitialSpawnAssignmentComplete { get; set; }
        public bool InitialSpawnAssignmentScheduled { get; set; }
        public ulong LastReplayPovMask { get; set; } = ulong.MaxValue;

        public bool Armed { get; set; }
        public bool ArmedLoop { get; set; }
        public string ArmedLabel { get; set; } = string.Empty;
        public string ArmedManifestPath { get; set; } = string.Empty;
        public int ArmedSourceRound { get; set; } = -1;
        public bool ArmedPrepared { get; set; }
        public int ArmedPreparePollToken { get; set; }
        public int FreezePrerollToken { get; set; }
        public bool FreezePrerollStarted { get; set; }

        public bool SequenceActive { get; set; }
        public string SequenceManifestPath { get; set; } = string.Empty;
        public int[] SequenceRounds { get; set; } = [];
        public int SequenceIndex { get; set; }
        public bool SequencePrepared { get; set; }
        public int SequencePreparedRound { get; set; } = -1;
        public int SequencePreparePollToken { get; set; }
        public ReplayRoundScoreboard? LoadedRoundScoreboard { get; set; }

        public long NextReplayIdentityGeneration { get; set; }
        public long NextReplayMutationGeneration { get; set; }

        public bool PlayoffPreparePending { get; set; }
        public bool PlayoffPendingCanLoad { get; set; }
        public int PlayoffPrepareToken { get; set; }
        public int PlayoffPendingTRound { get; set; } = -1;
        public int PlayoffPendingCtRound { get; set; } = -1;
        public string PlayoffPendingReason { get; set; } = string.Empty;
        public string PlayoffPendingPrepareReason { get; set; } = string.Empty;
        public bool PlayoffPrepared { get; set; }
        public int PlayoffPreparedTRound { get; set; } = -1;
        public int PlayoffPreparedCtRound { get; set; } = -1;
        public string PlayoffPreparedLabel { get; set; } = string.Empty;
        public int PlayoffRoundIndex { get; set; }
    }
}
