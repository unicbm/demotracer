//! Optional output-driven projection of entity state. Protocol bytes are still
//! consumed; only properties with no output or parser dependency are omitted.
use super::collect_data::PropType;
use crate::first_pass::prop_controller::*;

pub(crate) struct EntityProjection {
    ordinary: Vec<bool>,
    pub(crate) direct_rows_only: bool,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::second_pass::variants::Variant;
    use ahash::AHashMap;

    fn prop(id: u32, prop_type: PropType) -> PropInfo {
        PropInfo {
            id,
            prop_type,
            prop_name: format!("p{id}"),
            prop_friendly_name: format!("p{id}"),
            is_player_prop: true,
        }
    }

    #[test]
    fn projection_retains_output_filters_metadata_and_dynamic_namespaces() {
        let mut props = PropController::new(vec![], vec![], AHashMap::default(), AHashMap::default(), false, &[], false);
        props.prop_infos = vec![prop(101, PropType::Player)];
        props.wanted_prop_state_infos.push(WantedPropStateInfo {
            base: prop(102, PropType::Player),
            wanted_prop_state: Variant::U32(0),
        });
        props.special_ids.player_pawn = Some(103);
        props.special_ids.active_weapon = Some(104);
        let projection = EntityProjection::new(&props, true);
        assert!(!projection.direct_rows_only);
        for id in [
            101,
            102,
            103,
            104,
            MY_WEAPONS_OFFSET + 250_001,
            WEAPON_SKIN_ID + 63,
            GLOVE_ATTRIBUTE_DEF_INDEX_ID + 63,
            ITEM_PURCHASE_COST + 7,
            u32::MAX,
        ] {
            assert!(projection.keeps(id), "{id}");
        }
        assert!(!projection.keeps(100));
        assert!(!projection.keeps(105));
    }

    #[test]
    fn direct_rows_plan_excludes_unused_links_only_when_no_getter_needs_them() {
        let mut props = PropController::new(vec![], vec![], AHashMap::default(), AHashMap::default(), false, &[], false);
        props.special_ids.player_pawn = Some(103);
        props.special_ids.active_weapon = Some(104);
        props.prop_infos = vec![
            prop(101, PropType::Player),
            prop(ENTITY_ID_ID, PropType::Custom),
            prop(USERCMD_INPUT_HISTORY_BASEID, PropType::Custom),
        ];
        let direct = EntityProjection::new(&props, true);
        assert!(direct.direct_rows_only);
        assert!(direct.keeps(101) && direct.keeps(103));
        assert!(!direct.keeps(104));
        for extra in [
            prop(PLAYER_X_ID, PropType::Custom),
            prop(INVENTORY_ID, PropType::Custom),
            prop(105, PropType::Weapon),
            prop(106, PropType::Team),
            prop(BUTTONS_BASEID, PropType::Button),
        ] {
            props.prop_infos.push(extra);
            let full = EntityProjection::new(&props, true);
            assert!(!full.direct_rows_only);
            assert!(full.keeps(104));
            props.prop_infos.pop();
        }
        assert!(!EntityProjection::new(&props, false).direct_rows_only);
    }
}

impl EntityProjection {
    pub(crate) fn new(props: &PropController, rows_only: bool) -> Self {
        // This restricted plan has no inventory, weapon, coordinate, angle,
        // button-derived or other custom getters. Their dependencies are not
        // needed just to emit direct state and usercmd rows.
        let direct_rows_only = rows_only
            && props.wanted_prop_state_infos.is_empty()
            && props.prop_infos.iter().all(|prop| match prop.prop_type {
                PropType::Player | PropType::Controller | PropType::Rules | PropType::Name | PropType::Steamid | PropType::Tick | PropType::GameTime => true,
                PropType::Custom => matches!(
                    prop.id,
                    ENTITY_ID_ID
                        | SERVER_TICK_ID
                        | USERCMD_INPUT_HISTORY_BASEID
                        | USERCMD_SUBTICK_MOVES_BASEID
                        | USERCMD_CLIENT_TICK
                        | USERCMD_ATTACK_START_HISTORY_INDEX_1
                        | USERCMD_ATTACK_START_HISTORY_INDEX_2
                ),
                _ => false,
            });
        let ids = &props.special_ids;
        let dependencies: Vec<_> = if direct_rows_only {
            [ids.teamnum, ids.player_name, ids.steamid, ids.player_pawn, ids.team_team_num]
                .into_iter()
                .flatten()
                .collect()
        } else {
            ids.iter().collect()
        };
        let mut ordinary = Vec::new();
        for id in props
            .prop_infos
            .iter()
            .map(|prop| prop.id)
            .chain(props.wanted_prop_state_infos.iter().map(|prop| prop.base.id))
            .chain(dependencies)
        {
            if id < BUTTONS_BASEID {
                if ordinary.len() <= id as usize {
                    ordinary.resize(id as usize + 1, false);
                }
                ordinary[id as usize] = true;
            }
        }
        Self { ordinary, direct_rows_only }
    }

    #[inline]
    pub(crate) fn keeps(&self, id: u32) -> bool {
        // Preserve all flattened/dynamic namespaces, including inventory,
        // ammunition, econ attributes, purchase events and synthetic inputs.
        id >= BUTTONS_BASEID || self.ordinary.get(id as usize).copied().unwrap_or(false)
    }
}
