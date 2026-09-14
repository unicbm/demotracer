/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

//! Demo-backed state for discontinuous playback boundaries. Values retain
//! presence independently of zero; this is not a per-command correction lane.
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum SourceKind {
    F32,
    I32,
    U32,
    Bool,
}
pub struct SourceField {
    pub name: &'static str,
    pub prop: &'static str,
    pub kind: SourceKind,
    pub component: Option<usize>,
}
pub const SOURCE_FIELDS: &[SourceField] = &[
    SourceField { name: "ServerTick", prop: "server_tick", kind: SourceKind::U32, component: None },
    SourceField { name: "PlayerTick", prop: "CCSPlayerController.m_nTickBase", kind: SourceKind::U32, component: None },
    SourceField { name: "DuckRoot", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flDuckRootOffset", kind: SourceKind::F32, component: None },
    SourceField { name: "DuckView", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flDuckViewOffset", kind: SourceKind::F32, component: None },
    SourceField { name: "LastDuckTime", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flLastDuckTime", kind: SourceKind::F32, component: None },
    SourceField { name: "DuckOverride", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_bDuckOverride", kind: SourceKind::Bool, component: None },
    SourceField { name: "Stamina", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flStamina", kind: SourceKind::F32, component: None },
    SourceField { name: "DuckAmount", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flDuckAmount", kind: SourceKind::F32, component: None },
    SourceField { name: "DuckSpeed", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flDuckSpeed", kind: SourceKind::F32, component: None },
    SourceField { name: "Ducked", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_bDucked", kind: SourceKind::Bool, component: None },
    SourceField { name: "Ducking", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_bDucking", kind: SourceKind::Bool, component: None },
    SourceField { name: "DesiresDuck", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_bDesiresDuck", kind: SourceKind::Bool, component: None },
    SourceField { name: "LastJumpTick", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_nLastJumpTick", kind: SourceKind::I32, component: None },
    SourceField { name: "LastJumpFrac", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flLastJumpFrac", kind: SourceKind::F32, component: None },
    SourceField { name: "LastJumpVelocityZ", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flLastJumpVelocityZ", kind: SourceKind::F32, component: None },
    SourceField { name: "GroundTopology", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_bUsingGroundTopologyOffset", kind: SourceKind::Bool, component: None },
    SourceField { name: "GroundTopologySmoothing", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flUsingGroundTopologyOffsetTransitionSmoothing", kind: SourceKind::F32, component: None },
    SourceField { name: "FrictionStashedSpeed", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flFrictionStashedSpeed", kind: SourceKind::F32, component: None },
    SourceField { name: "UseFrictionStashedSpeed", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_bUseFrictionStashedSpeed", kind: SourceKind::Bool, component: None },
    SourceField { name: "FrictionStashedUntilFrac", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flUseFrictionStashedSpeedUntilFrac", kind: SourceKind::F32, component: None },
    SourceField { name: "LadderSurface", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_nLadderSurfacePropIndex", kind: SourceKind::I32, component: None },
    SourceField { name: "FallVelocity", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flFallVelocity", kind: SourceKind::F32, component: None },
    SourceField { name: "LastActualJumpPressTick", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_nLastActualJumpPressTick", kind: SourceKind::I32, component: None },
    SourceField { name: "LastActualJumpPressFrac", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flLastActualJumpPressFrac", kind: SourceKind::F32, component: None },
    SourceField { name: "LastUsableJumpPressTick", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_nLastUsableJumpPressTick", kind: SourceKind::I32, component: None },
    SourceField { name: "LastUsableJumpPressFrac", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flLastUsableJumpPressFrac", kind: SourceKind::F32, component: None },
    SourceField { name: "LastLandedTick", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_nLastLandedTick", kind: SourceKind::I32, component: None },
    SourceField { name: "LastLandedFrac", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flLastLandedFrac", kind: SourceKind::F32, component: None },
    SourceField { name: "LastLandedVelocityX", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flLastLandedVelocityX", kind: SourceKind::F32, component: None },
    SourceField { name: "LastLandedVelocityY", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flLastLandedVelocityY", kind: SourceKind::F32, component: None },
    SourceField { name: "LastLandedVelocityZ", prop: "CCSPlayerPawn.CCSPlayer_MovementServices.m_flLastLandedVelocityZ", kind: SourceKind::F32, component: None },
    SourceField { name: "VelocityModifier", prop: "CCSPlayerPawn.m_flVelocityModifier", kind: SourceKind::F32, component: None },
    SourceField { name: "Friction", prop: "CCSPlayerPawn.m_flFriction", kind: SourceKind::F32, component: None },
    SourceField { name: "GravityScale", prop: "CCSPlayerPawn.m_flGravityScale", kind: SourceKind::F32, component: None },
    SourceField { name: "GravityDisabled", prop: "CCSPlayerPawn.m_bGravityDisabled", kind: SourceKind::Bool, component: None },
    SourceField { name: "ShotsFired", prop: "CCSPlayerPawn.m_iShotsFired", kind: SourceKind::I32, component: None },
    SourceField { name: "Scoped", prop: "CCSPlayerPawn.m_bIsScoped", kind: SourceKind::Bool, component: None },
    SourceField { name: "BaseVelocityX", prop: "CCSPlayerPawn.m_vecBaseVelocity", kind: SourceKind::F32, component: Some(0) },
    SourceField { name: "BaseVelocityY", prop: "CCSPlayerPawn.m_vecBaseVelocity", kind: SourceKind::F32, component: Some(1) },
    SourceField { name: "BaseVelocityZ", prop: "CCSPlayerPawn.m_vecBaseVelocity", kind: SourceKind::F32, component: Some(2) },
    SourceField { name: "PredictableAngleX", prop: "CCSPlayerPawn.CCSPlayer_AimPunchServices.m_predictableBaseAngle", kind: SourceKind::F32, component: Some(0) },
    SourceField { name: "PredictableAngleY", prop: "CCSPlayerPawn.CCSPlayer_AimPunchServices.m_predictableBaseAngle", kind: SourceKind::F32, component: Some(1) },
    SourceField { name: "PredictableAngleZ", prop: "CCSPlayerPawn.CCSPlayer_AimPunchServices.m_predictableBaseAngle", kind: SourceKind::F32, component: Some(2) },
    SourceField { name: "PredictableAngleVelX", prop: "CCSPlayerPawn.CCSPlayer_AimPunchServices.m_predictableBaseAngleVel", kind: SourceKind::F32, component: Some(0) },
    SourceField { name: "PredictableAngleVelY", prop: "CCSPlayerPawn.CCSPlayer_AimPunchServices.m_predictableBaseAngleVel", kind: SourceKind::F32, component: Some(1) },
    SourceField { name: "PredictableAngleVelZ", prop: "CCSPlayerPawn.CCSPlayer_AimPunchServices.m_predictableBaseAngleVel", kind: SourceKind::F32, component: Some(2) },
    SourceField { name: "UnpredictableAngleX", prop: "CCSPlayerPawn.CCSPlayer_AimPunchServices.m_unpredictableBaseAngle", kind: SourceKind::F32, component: Some(0) },
    SourceField { name: "UnpredictableAngleY", prop: "CCSPlayerPawn.CCSPlayer_AimPunchServices.m_unpredictableBaseAngle", kind: SourceKind::F32, component: Some(1) },
    SourceField { name: "UnpredictableAngleZ", prop: "CCSPlayerPawn.CCSPlayer_AimPunchServices.m_unpredictableBaseAngle", kind: SourceKind::F32, component: Some(2) },
    SourceField { name: "PredictableTick", prop: "CCSPlayerPawn.CCSPlayer_AimPunchServices.m_predictableBaseTick", kind: SourceKind::I32, component: None },
    SourceField { name: "PredictableTickFrac", prop: "CCSPlayerPawn.CCSPlayer_AimPunchServices.m_predictableBaseTickInterpAmount", kind: SourceKind::F32, component: None },
    SourceField { name: "UnpredictableTick", prop: "CCSPlayerPawn.CCSPlayer_AimPunchServices.m_unpredictableBaseTick", kind: SourceKind::I32, component: None },
    SourceField { name: "Clip1", prop: "Weapon.m_iClip1", kind: SourceKind::I32, component: None },
    SourceField { name: "Clip2", prop: "Weapon.m_iClip2", kind: SourceKind::I32, component: None },
    SourceField { name: "InReload", prop: "Weapon.m_bInReload", kind: SourceKind::Bool, component: None },
    SourceField { name: "NextPrimaryTick", prop: "Weapon.m_nNextPrimaryAttackTick", kind: SourceKind::I32, component: None },
    SourceField { name: "NextPrimaryFrac", prop: "Weapon.m_flNextPrimaryAttackTickRatio", kind: SourceKind::F32, component: None },
    SourceField { name: "NextSecondaryTick", prop: "Weapon.m_nNextSecondaryAttackTick", kind: SourceKind::I32, component: None },
    SourceField { name: "NextSecondaryFrac", prop: "Weapon.m_flNextSecondaryAttackTickRatio", kind: SourceKind::F32, component: None },
    SourceField { name: "RecoilIndex", prop: "Weapon.m_flRecoilIndex", kind: SourceKind::F32, component: None },
    SourceField { name: "AccuracyPenalty", prop: "Weapon.m_fAccuracyPenalty", kind: SourceKind::F32, component: None },
    SourceField { name: "LastShotTime", prop: "Weapon.m_fLastShotTime", kind: SourceKind::F32, component: None },
    SourceField { name: "BurstShotsRemaining", prop: "Weapon.m_iBurstShotsRemaining", kind: SourceKind::I32, component: None },
    SourceField { name: "NextAttack", prop: "CCSPlayerPawn.CCSPlayer_WeaponServices.m_flNextAttack", kind: SourceKind::F32, component: None },
    SourceField { name: "ActiveWeaponHandle", prop: "CCSPlayerPawn.CCSPlayer_WeaponServices.m_hActiveWeapon", kind: SourceKind::U32, component: None },
    SourceField { name: "ReserveAmmoPrimary", prop: "Weapon.m_pReserveAmmo", kind: SourceKind::I32, component: None },
    SourceField { name: "ReserveAmmoSecondary", prop: "weapon_reserve_ammo_secondary", kind: SourceKind::I32, component: None },
 ];
#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
pub struct SourceState {
    values: [[u32; 32]; 3],
    present: [u32; 3],
}
impl SourceState {
    pub fn get(&self, id: usize) -> Option<u32> {
        (id < SOURCE_FIELDS.len() && self.present[id / 32] & (1 << (id % 32)) != 0)
            .then(|| self.values[id / 32][id % 32])
    }
    pub fn set(&mut self, id: usize, value: u32) {
        assert!(id < SOURCE_FIELDS.len());
        self.values[id / 32][id % 32] = value;
        self.present[id / 32] |= 1 << (id % 32);
    }
}
#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
pub struct SourceStateChange {
    pub tick_index: u32,
    pub field_id: u32,
    pub value_bits: u32,
    pub present: u32,
}
impl SourceStateChange {
    pub fn valid(&self, tick_count: usize) -> bool {
        let Some(field) = SOURCE_FIELDS.get(self.field_id as usize) else {
            return false;
        };
        (self.tick_index as usize) < tick_count
            && self.present <= 1
            && if self.present == 0 {
                self.value_bits == 0
            } else {
                match field.kind {
                    SourceKind::F32 => f32::from_bits(self.value_bits).is_finite(),
                    SourceKind::Bool => self.value_bits <= 1,
                    _ => true,
                }
            }
    }
}
pub fn changes_between(
    previous: &SourceState,
    next: &SourceState,
    tick_index: u32,
    out: &mut Vec<SourceStateChange>,
) {
    for id in 0..SOURCE_FIELDS.len() {
        let value = next.get(id);
        if previous.get(id) != value {
            out.push(SourceStateChange {
                tick_index,
                field_id: id as u32,
                value_bits: value.unwrap_or(0),
                present: u32::from(value.is_some()),
            });
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn absent_zero_and_disappearing_state_are_distinct() {
        let absent = SourceState::default();
        let mut zero = absent.clone();
        zero.set(2, 0);
        let mut changes = Vec::new();
        changes_between(&absent, &zero, 0, &mut changes);
        changes_between(&zero, &zero, 1, &mut changes);
        changes_between(&zero, &absent, 2, &mut changes);
        assert_eq!(changes.len(), 2);
        assert_eq!(changes[0].present, 1);
        assert_eq!(changes[1].present, 0);
        assert!(changes.iter().all(|c| c.valid(3)));
    }
    #[test]
    fn field_registry_matches_shared_contract() {
        let value: serde_json::Value = serde_json::from_str(include_str!(
            "../../../../shared/contracts/replay-source-fields.v1.json"
        ))
        .unwrap();
        let fields = value["fields"].as_array().unwrap();
        assert_eq!(fields.len(), SOURCE_FIELDS.len());
        for (id, field) in fields.iter().enumerate() {
            assert_eq!(field["id"], id);
            assert_eq!(field["name"], SOURCE_FIELDS[id].name);
            assert_eq!(field["prop"], SOURCE_FIELDS[id].prop);
            assert_eq!(field["kind"], format!("{:?}", SOURCE_FIELDS[id].kind));
            assert_eq!(
                field["component"],
                serde_json::json!(SOURCE_FIELDS[id].component)
            );
        }
    }
}
