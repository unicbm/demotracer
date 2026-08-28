/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private readonly ReplaySessionState _session = new();

    private sealed class ReplaySessionState
    {
        public ReplaySlotRegistry ReplaySlots { get; } = new();
        public IReadOnlyList<int> LoadedSlots => ReplaySlots.LoadedSlots;
        public HashSet<int> WarmReplayBufferSlots { get; } = [];
        public HashSet<int> FreezePrerollSlots { get; } = [];
        public HashSet<int> ResumedFreezePrerollSlots { get; } = [];
        public Dictionary<int, LoadedReplay> LoadedReplays { get; } = [];
        public Dictionary<CsTeam, LoadedTeamAvatarOverride> TeamAvatarOverrides { get; } = [];
        public Dictionary<int, AppliedHumanTeamAvatarOverride> HumanTeamAvatarOverrides { get; } = [];
        public Dictionary<int, int> LastEnsuredWeaponDef { get; } = [];
        public Dictionary<int, int> LastReplayWeaponDef { get; } = [];
        public Dictionary<int, int> LastLockedWeaponTarget { get; } = [];
        public Dictionary<(int PlayerSlot, ReplayWeaponSlot WeaponSlot), PendingWeaponSlotReplacement>
            PendingWeaponSlotReplacements { get; } = [];
        public Dictionary<int, int> ProjectileAlignNextBySlot { get; } = [];
        public Dictionary<int, int> ReplayHifiEventNextBySlot { get; } = [];
        public Dictionary<int, long> ReplayIdentityGenerationBySlot { get; } = [];
        public Queue<string> ProjectileAlignLog { get; } = [];
        public HashSet<int> RebuiltInventorySlots { get; } = [];
        public HashSet<int> WeaponLoadoutSyncedSlots { get; } = [];
        public ReplayPawnEquipmentSyncTracker PawnEquipmentSync { get; } = new();
        public HashSet<int> BalanceSyncedSlots { get; } = [];
        public Dictionary<int, float> ReplayStartedAt { get; } = [];
        public Dictionary<int, uint> ReplayPerceptionBaselineSerial { get; } = [];
        public Dictionary<int, PendingBulletHit> PendingBulletHits { get; } = [];
        public Dictionary<int, PendingBulletDamage> PendingBulletDamages { get; } = [];
        public Dictionary<int, PendingThreat360> PendingThreat360 { get; } = [];
        public HashSet<int> CosmeticSyncedSlots { get; } = [];
        public HashSet<int> ScoreboardSyncedSlots { get; } = [];
        public Dictionary<int, ReplayViewmodel> ReplayOriginalViewmodels { get; } = [];
        public Dictionary<int, ReplayViewmodel> ReplayAppliedViewmodels { get; } = [];
        public HashSet<int> ReplayFailedViewmodelSlots { get; } = [];
        public ReplayPlanState Plan { get; } = new();

        public bool SafeC4Aligned { get; set; }
        public int InitialSpawnAssignmentToken { get; set; }
        public bool InitialSpawnAssignmentComplete { get; set; }
        public bool InitialSpawnAssignmentScheduled { get; set; }
        public ulong LastReplayPovMask { get; set; } = ulong.MaxValue;

        public int FreezePrerollToken { get; set; }
        public bool FreezePrerollStarted { get; set; }
        public ReplayRoundScoreboard? LoadedRoundScoreboard { get; set; }

        public long NextReplayIdentityGeneration { get; set; }
    }
}
