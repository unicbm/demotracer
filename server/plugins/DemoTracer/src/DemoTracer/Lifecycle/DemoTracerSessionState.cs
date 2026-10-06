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
        public Dictionary<int, ReplayInventoryTimeline> ReplayInventoryBySlot { get; } = [];
        public Dictionary<int, long> ReplayIdentityGenerationBySlot { get; } = [];
        public Queue<string> ProjectileAlignLog { get; } = [];
        public PendingProjectileBirths ProjectileBirths { get; } = new();
        public HashSet<int> RebuiltInventorySlots { get; } = [];
        public HashSet<int> WeaponLoadoutSyncedSlots { get; } = [];
        public ReplayPawnEquipmentSyncTracker PawnEquipmentSync { get; } = new();
        public HashSet<int> BalanceSyncedSlots { get; } = [];
        public Dictionary<int, uint> ReplayPerceptionBaselineSerial { get; } = [];
        public Dictionary<int, PendingBulletHit> PendingBulletHits { get; } = [];
        public Dictionary<int, PendingBulletDamage> PendingBulletDamages { get; } = [];
        public HashSet<int> CosmeticSyncedSlots { get; } = [];
        public Dictionary<int, ReplayPawnViewState> ReplayViewmodels { get; } = [];
        public ReplayPlanState Plan { get; } = new();

        public bool SafeC4Aligned { get; set; }
        public bool RoundSpawnsPending { get; set; }
        public int InitialSpawnAssignmentToken { get; set; }
        public bool InitialSpawnAssignmentComplete { get; set; }
        public bool InitialSpawnAssignmentScheduled { get; set; }

        public int FreezePrerollToken { get; set; }
        public bool FreezePrerollStarted { get; set; }

        public long NextReplayIdentityGeneration { get; set; }

        public void ClearWeaponSelection(int slot)
        {
            LastEnsuredWeaponDef.Remove(slot);
            LastReplayWeaponDef.Remove(slot);
            LastLockedWeaponTarget.Remove(slot);
        }

        public void InvalidateEquipment(int slot)
        {
            RebuiltInventorySlots.Remove(slot);
            WeaponLoadoutSyncedSlots.Remove(slot);
            PawnEquipmentSync.Invalidate(slot);
            BalanceSyncedSlots.Remove(slot);
        }

        // Execution ends independently of loaded buffers and presentation.
        // Handoff keeps the prepared inventory; unload invalidates it.
        public void ClearSlotExecution(int slot)
        {
            ClearWeaponSelection(slot);
            FreezePrerollSlots.Remove(slot);
            ResumedFreezePrerollSlots.Remove(slot);
            ProjectileAlignNextBySlot.Remove(slot);
            ReplayInventoryBySlot.Remove(slot);
            ReplayPerceptionBaselineSerial.Remove(slot);
            PendingBulletHits.Remove(slot);
            PendingBulletDamages.Remove(slot);
        }

        public void ClearExecution()
        {
            LastEnsuredWeaponDef.Clear();
            LastReplayWeaponDef.Clear();
            LastLockedWeaponTarget.Clear();
            FreezePrerollSlots.Clear();
            ResumedFreezePrerollSlots.Clear();
            ProjectileAlignNextBySlot.Clear();
            ReplayInventoryBySlot.Clear();
            RebuiltInventorySlots.Clear();
            ReplayPerceptionBaselineSerial.Clear();
            PendingBulletHits.Clear();
            PendingBulletDamages.Clear();
            SafeC4Aligned = false;
        }

        // Native execution must be released first; presentation retains its
        // own restoration state until the plugin restores live entities.
        // Warm buffers and scheduling plans have longer, separate lifetimes.
        public void ClearLoaded()
        {
            ReplaySlots.Clear();
            LoadedReplays.Clear();
            ReplayIdentityGenerationBySlot.Clear();
            ClearExecution();
            WeaponLoadoutSyncedSlots.Clear();
            PawnEquipmentSync.Clear();
            BalanceSyncedSlots.Clear();
            CosmeticSyncedSlots.Clear();
        }
    }
}
