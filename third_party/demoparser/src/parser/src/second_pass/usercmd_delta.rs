//! Direct codegen-delta decoding. Ordinary fields retain prost's wire validation;
//! nested messages and wire-type 7 resets are handled without rewriting protobufs.

use super::{read_delta_varint, DeltaBaseUserCmdPb, DeltaCsgoUserCmdPb, DeltaMessageSchema};
use csgoproto::{CInButtonStatePb, CMsgQAngle, CMsgVector, CSubtickMoveStep, CsgoInputHistoryEntryPb, CsgoInterpolationInfoPb, CsgoInterpolationInfoPbCl};
use prost::encoding::{DecodeContext, WireType};
use prost::Message;

pub(super) enum ChildValue<'a> {
    Message(&'a [u8]),
    Reset,
}

pub(super) trait DeltaMessage: Message + Default {
    const SCHEMA: DeltaMessageSchema;

    fn merge_child(&mut self, field: u64, value: ChildValue<'_>) -> Option<()>;
}

fn merge_child<M: DeltaMessage>(child: &mut Option<M>, value: ChildValue<'_>) -> Option<()> {
    let child = child.get_or_insert_with(M::default);
    match value {
        ChildValue::Message(bytes) => merge_message(child, bytes),
        ChildValue::Reset => {
            // A nested reset merges explicit defaults, preserving fields outside
            // the schema's reset set just like protobuf message merging does.
            for &(field, _) in M::SCHEMA.default_fields() {
                reset_field(child, field)?;
            }
            Some(())
        }
    }
}

macro_rules! delta_message {
    ($message:ty, $schema:ident, {$($tag:literal => $field:ident),* $(,)?}) => {
        impl DeltaMessage for $message {
            const SCHEMA: DeltaMessageSchema = DeltaMessageSchema::$schema;

            fn merge_child(&mut self, field: u64, _value: ChildValue<'_>) -> Option<()> {
                match field {
                    $($tag => merge_child(&mut self.$field, _value),)*
                    _ => None,
                }
            }
        }
    };
}

delta_message!(DeltaCsgoUserCmdPb, CsgoUserCmd, {1 => base});
delta_message!(DeltaBaseUserCmdPb, BaseUserCmd, {3 => buttons_pb, 4 => viewangles});
delta_message!(CInButtonStatePb, Buttons, {});
delta_message!(CMsgQAngle, QAngle, {});
delta_message!(CMsgVector, Vector, {});
delta_message!(CSubtickMoveStep, SubtickMove, {});
delta_message!(CsgoInterpolationInfoPb, Interpolation, {});
delta_message!(CsgoInterpolationInfoPbCl, InterpolationCl, {});
delta_message!(CsgoInputHistoryEntryPb, InputHistory, {
    2 => view_angles,
    12 => cl_interp,
    13 => sv_interp0,
    14 => sv_interp1,
    15 => player_interp,
    66 => shoot_position,
    67 => target_head_pos_check,
    68 => target_abs_pos_check,
    69 => target_abs_ang_check,
});

fn reset_field<M: DeltaMessage>(message: &mut M, field: u64) -> Option<()> {
    let wire_type = M::SCHEMA.field_wire_type(field)?;
    if M::SCHEMA.child(field).is_some() {
        return message.merge_child(field, ChildValue::Reset);
    }
    // Zero is the canonical varint, fixed32, fixed64 and empty-bytes value.
    // prost still handles field presence and declared protobuf scalar types.
    let mut zero = &[0_u8; 8][..];
    message
        .merge_field(
            u32::try_from(field).ok()?,
            WireType::try_from(u64::from(wire_type)).ok()?,
            &mut zero,
            DecodeContext::default(),
        )
        .ok()
}

fn merge_message<M: DeltaMessage>(message: &mut M, mut bytes: &[u8]) -> Option<()> {
    while !bytes.is_empty() {
        let key = read_delta_varint(&mut bytes)?;
        let field = key >> 3;
        if field == 0 || field > 0x1fff_ffff {
            return None;
        }
        let wire_type = (key & 7) as u8;
        if wire_type == 7 {
            reset_field(message, field)?;
        } else if M::SCHEMA.child(field).is_some() {
            if wire_type != 2 {
                return None;
            }
            let length = usize::try_from(read_delta_varint(&mut bytes)?).ok()?;
            let (child, rest) = bytes.split_at_checked(length)?;
            message.merge_child(field, ChildValue::Message(child))?;
            bytes = rest;
        } else {
            // The previous sanitizer rejected protobuf groups too.
            if !matches!(wire_type, 0 | 1 | 2 | 5) {
                return None;
            }
            message
                .merge_field(
                    field as u32,
                    WireType::try_from(u64::from(wire_type)).ok()?,
                    &mut bytes,
                    DecodeContext::default(),
                )
                .ok()?;
        }
    }
    Some(())
}

pub(super) fn decode_command(bytes: &[u8]) -> Option<DeltaCsgoUserCmdPb> {
    let mut command = DeltaCsgoUserCmdPb::default();
    merge_message(&mut command, bytes)?;
    Some(command)
}

/// Stage only changed elements before committing a repeated-field delta. An
/// invalid payload leaves the baseline untouched, including its original length.
/// Returned indices retain first-update order for subtick output; input history
/// callers instead materialize the complete resulting baseline.
#[cfg(test)]
pub(super) fn apply_repeated<M: DeltaMessage + Clone>(payloads: &[prost::bytes::Bytes], baseline: &mut Vec<M>) -> Option<Vec<usize>> {
    let mut indices = Vec::new();
    apply_repeated_into(payloads, baseline, &mut Vec::new(), |index, _| indices.push(index))?;
    Some(indices)
}

/// Reuse staging storage across commands. Callbacks run only after the entire
/// delta validates, preserving atomic failure and first-update ordering.
pub(super) fn apply_repeated_into<M: DeltaMessage + Clone>(
    payloads: &[prost::bytes::Bytes], baseline: &mut Vec<M>,
    updates: &mut Vec<(usize, M)>, mut on_update: impl FnMut(usize, &M),
) -> Option<bool> {
    updates.clear();
    let mut count = baseline.len();
    let mut declared_count = None;
    for payload in payloads {
        let mut bytes = payload.as_ref();
        if !bytes.is_empty() {
            let mut after_marker = bytes;
            let marker = read_delta_varint(&mut after_marker)?;
            if marker & 7 == 7 {
                if declared_count.is_some() {
                    return None;
                }
                count = usize::try_from(marker >> 3).ok()?;
                if updates.iter().any(|(index, _)| *index >= count) {
                    return None;
                }
                declared_count = Some(count);
                bytes = after_marker;
            }
        }
        while !bytes.is_empty() {
            let key = read_delta_varint(&mut bytes)?;
            if key & 7 != 2 {
                return None;
            }
            let index = usize::try_from(key >> 3).ok()?;
            if index >= count {
                return None;
            }
            let length = usize::try_from(read_delta_varint(&mut bytes)?).ok()?;
            let (message, rest) = bytes.split_at_checked(length)?;
            if let Some((_, value)) = updates.iter_mut().find(|(updated, _)| *updated == index) {
                merge_message(value, message)?;
            } else {
                let mut value = baseline.get(index).cloned().unwrap_or_default();
                merge_message(&mut value, message)?;
                updates.push((index, value));
            }
            bytes = rest;
        }
    }
    if count > baseline.len() {
        baseline.try_reserve(count - baseline.len()).ok()?;
    }
    let changed = count != baseline.len() || !updates.is_empty();
    baseline.resize_with(count, M::default);
    for (index, value) in updates.drain(..) {
        baseline[index] = value;
        on_update(index, &baseline[index]);
    }
    Some(changed)
}

#[cfg(test)]
mod tests {
    use super::super::{decode_codegen_delta_repeated, sanitize_codegen_delta_message, write_delta_varint};
    use super::*;

    fn field(tag: u64, value: &[u8], out: &mut Vec<u8>) {
        write_delta_varint((tag << 3) | 2, out);
        write_delta_varint(value.len() as u64, out);
        out.extend_from_slice(value);
    }

    fn reset(tag: u64, out: &mut Vec<u8>) {
        write_delta_varint((tag << 3) | 7, out);
    }

    fn reference_command(bytes: &[u8]) -> Option<DeltaCsgoUserCmdPb> {
        let sanitized = sanitize_codegen_delta_message(bytes, DeltaMessageSchema::CsgoUserCmd)?;
        DeltaCsgoUserCmdPb::decode(sanitized.as_slice()).ok()
    }

    #[test]
    fn direct_command_matches_legacy_for_nested_and_scalar_resets() {
        let initial = DeltaCsgoUserCmdPb {
            base: Some(DeltaBaseUserCmdPb {
                client_tick: Some(7284),
                buttons_pb: Some(CInButtonStatePb {
                    buttonstate1: Some(19),
                    buttonstate2: Some(3),
                    buttonstate3: Some(1),
                }),
                viewangles: Some(CMsgQAngle {
                    x: Some(4.0),
                    y: Some(5.0),
                    z: Some(6.0),
                }),
                forwardmove: Some(1.0),
                leftmove: Some(-1.0),
                ..Default::default()
            }),
            left_hand_desired: Some(true),
            ..Default::default()
        };
        for reset_base in [false, true] {
            let mut bytes = initial.encode_to_vec();
            if reset_base {
                reset(1, &mut bytes);
            } else {
                let mut base = Vec::new();
                reset(3, &mut base);
                reset(4, &mut base);
                reset(5, &mut base);
                reset(6, &mut base);
                reset(18, &mut base);
                field(1, &base, &mut bytes);
            }
            reset(9, &mut bytes);
            assert_eq!(decode_command(&bytes), reference_command(&bytes));
            let command = decode_command(&bytes).unwrap();
            let base = command.base.unwrap();
            assert_eq!(base.client_tick, Some(7284));
            assert_eq!(base.buttons_pb.unwrap().buttonstate1, Some(0));
            assert_eq!(base.viewangles.unwrap().y, Some(0.0));
            assert_eq!(base.forwardmove, Some(0.0));
            assert_eq!(command.left_hand_desired, Some(false));
        }
    }

    #[test]
    fn every_declared_reset_matches_legacy_decoder() {
        fn compare<M: DeltaMessage + PartialEq + std::fmt::Debug>() {
            for tag in 1..=75 {
                if M::SCHEMA.field_wire_type(tag).is_none() {
                    continue;
                }
                let mut bytes = Vec::new();
                reset(tag, &mut bytes);
                let sanitized = sanitize_codegen_delta_message(&bytes, M::SCHEMA).unwrap();
                let reference = M::decode(sanitized.as_slice()).unwrap();
                let mut actual = M::default();
                merge_message(&mut actual, &bytes).unwrap();
                assert_eq!(actual, reference, "reset field {tag}");
            }
        }
        compare::<DeltaCsgoUserCmdPb>();
        compare::<DeltaBaseUserCmdPb>();
        compare::<CInButtonStatePb>();
        compare::<CMsgQAngle>();
        compare::<CMsgVector>();
        compare::<CSubtickMoveStep>();
        compare::<CsgoInputHistoryEntryPb>();
        compare::<CsgoInterpolationInfoPb>();
        compare::<CsgoInterpolationInfoPbCl>();
    }

    #[test]
    fn history_merges_nested_fields_and_reset_preserves_unmentioned_values() {
        let previous = vec![CsgoInputHistoryEntryPb {
            view_angles: Some(CMsgQAngle {
                x: Some(1.0),
                y: Some(2.0),
                z: Some(3.0),
            }),
            render_tick_count: Some(100),
            sv_interp0: Some(CsgoInterpolationInfoPb {
                src_tick: Some(80),
                dst_tick: Some(90),
                frac: Some(0.5),
            }),
            shoot_position: Some(CMsgVector {
                x: Some(10.0),
                y: Some(20.0),
                z: Some(30.0),
                ..Default::default()
            }),
            ..Default::default()
        }];
        let mut update = Vec::new();
        reset(2, &mut update);
        field(13, &[0x17], &mut update); // Explicitly clear only interpolation dst_tick.
        field(
            66,
            &CMsgVector {
                y: Some(50.0),
                ..Default::default()
            }
            .encode_to_vec(),
            &mut update,
        );
        let mut payload = Vec::new();
        field(0, &update, &mut payload);
        let payloads = vec![prost::bytes::Bytes::from(payload)];
        let expected = decode_codegen_delta_repeated(&payloads, DeltaMessageSchema::InputHistory, &previous).unwrap();
        let mut actual = previous;
        let indices = apply_repeated(&payloads, &mut actual).unwrap();
        assert_eq!(actual, expected.state);
        assert_eq!(indices, vec![0]);
        assert_eq!(actual[0].view_angles.unwrap().x, Some(0.0));
        assert_eq!(actual[0].render_tick_count, Some(100));
        assert_eq!(actual[0].sv_interp0.unwrap().dst_tick, Some(0));
        assert_eq!(actual[0].sv_interp0.unwrap().src_tick, Some(80));
        assert_eq!(actual[0].shoot_position.unwrap().x, Some(10.0));
        assert_eq!(actual[0].shoot_position.unwrap().y, Some(50.0));
    }

    #[test]
    fn sparse_state_sequences_match_legacy_with_growth_shrink_and_duplicate_indices() {
        let mut baseline = Vec::<CSubtickMoveStep>::new();
        let mut seed = 0x6d2b_79f5_u32;
        let mut next = || {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            seed
        };
        for iteration in 0..2048 {
            let mut payload = Vec::new();
            let count = if iteration % 3 != 0 {
                let count = (next() % 9) as usize;
                reset(count as u64, &mut payload);
                count
            } else {
                baseline.len()
            };
            if count > 0 {
                for _ in 0..(next() % 12) {
                    let index = (next() as usize) % count;
                    let update = match next() % 4 {
                        0 => CSubtickMoveStep {
                            button: Some(u64::from(next())),
                            ..Default::default()
                        }
                        .encode_to_vec(),
                        1 => CSubtickMoveStep {
                            when: Some((next() % 100) as f32 / 100.0),
                            ..Default::default()
                        }
                        .encode_to_vec(),
                        2 => CSubtickMoveStep {
                            pressed: Some(next() & 1 != 0),
                            ..Default::default()
                        }
                        .encode_to_vec(),
                        _ => vec![0x0f, 0x17, 0x1f],
                    };
                    field(index as u64, &update, &mut payload);
                }
            }
            let payloads = vec![prost::bytes::Bytes::from(payload)];
            let expected = decode_codegen_delta_repeated(&payloads, DeltaMessageSchema::SubtickMove, &baseline).unwrap();
            let indices = apply_repeated(&payloads, &mut baseline).unwrap();
            assert_eq!(baseline, expected.state, "state after command {iteration}");
            let updates: Vec<_> = indices.iter().map(|index| baseline[*index]).collect();
            assert_eq!(updates, expected.updates, "updates after command {iteration}");
        }
    }

    #[test]
    fn malformed_repeated_updates_leave_baseline_untouched() {
        let previous = vec![CSubtickMoveStep {
            button: Some(42),
            pressed: Some(true),
            ..Default::default()
        }];
        for invalid in [
            vec![0x17, 0x02, 0x02, 0x08, 0x03, 0x0a, 0x02, 0x08], // Truncated second element after a valid first update.
            vec![0x07, 0x02, 0x00],                               // Shrink to zero, then invalid element index.
            vec![0x0f, 0x0a, 0x00],                               // Index outside declared length.
            vec![0x0f, 0x02, 0x01, 0x03],                         // Unsupported nested wire type.
            vec![0x0f, 0x02, 0x01, 0x3f],                         // Unknown explicit-clear field.
        ] {
            let mut actual = previous.clone();
            assert!(apply_repeated(&[prost::bytes::Bytes::from(invalid)], &mut actual).is_none());
            assert_eq!(actual, previous);
        }
        let mut actual = previous.clone();
        assert!(apply_repeated(
            &[prost::bytes::Bytes::from_static(&[0x17]), prost::bytes::Bytes::from_static(&[0x0f])],
            &mut actual
        )
        .is_none());
        assert_eq!(actual, previous);
    }

    #[test]
    fn repeated_updates_span_payloads_with_late_length_markers() {
        let previous = vec![CSubtickMoveStep {
            button: Some(2),
            when: Some(0.5),
            ..Default::default()
        }];
        let payloads = vec![
            prost::bytes::Bytes::from_static(&[0x02, 0x02, 0x08, 0x04]),
            prost::bytes::Bytes::from_static(&[0x17, 0x02, 0x02, 0x10, 0x01, 0x0a, 0x02, 0x08, 0x08]),
        ];
        let expected = decode_codegen_delta_repeated(&payloads, DeltaMessageSchema::SubtickMove, &previous).unwrap();
        let mut actual = previous;
        let indices = apply_repeated(&payloads, &mut actual).unwrap();
        assert_eq!(actual, expected.state);
        assert_eq!(indices, vec![0, 1]);
        assert_eq!(indices.iter().map(|index| actual[*index]).collect::<Vec<_>>(), expected.updates);

        // Previously this malformed sequence could index an element truncated by
        // a later payload's marker. Reject it without changing either element.
        let previous = actual.clone();
        let malformed = [
            prost::bytes::Bytes::from_static(&[0x0a, 0x02, 0x08, 0x10]),
            prost::bytes::Bytes::from_static(&[0x0f]),
        ];
        assert!(apply_repeated(&malformed, &mut actual).is_none());
        assert_eq!(actual, previous);
    }

    #[test]
    fn overflowing_scalar_varint_is_rejected() {
        let mut bytes = vec![0x30];
        bytes.extend_from_slice(&[0xff; 9]);
        bytes.push(0x02); // The tenth byte cannot carry more than one value bit.
        assert!(decode_command(&bytes).is_none());
    }

    #[test]
    fn command_unknown_fields_and_malformed_fields_match_reference() {
        for bytes in [
            vec![0xa0, 0x06, 0x05],             // Unknown normal varint field is skipped.
            vec![0xa2, 0x06, 0x02, 0x00, 0x01], // Unknown bytes field is skipped.
            vec![0xa7, 0x06],                   // Unknown reset is rejected.
            vec![0x0a, 0x02, 0x28],             // Truncated base.
            vec![0x0a, 0x02, 0x0d, 0x00],       // Wrong wire type in a known field.
            vec![0x00],                         // Invalid tag zero.
            vec![0x0b, 0x0c],                   // Groups remain unsupported.
        ] {
            assert_eq!(decode_command(&bytes), reference_command(&bytes), "bytes: {bytes:x?}");
        }
    }

    #[test]
    fn malformed_lists_preserve_independent_history_and_subtick_transactions() {
        use crate::first_pass::parser_settings::{FirstPassParser, ParserInputs};
        use crate::first_pass::prop_controller::{USERCMD_INPUT_HISTORY_BASEID, USERCMD_SUBTICK_MOVES_BASEID};
        use crate::parse_demo::DecodePlan;
        use crate::second_pass::entities::{Entity, EntityType};
        use crate::second_pass::parser_settings::SecondPassParser;
        use crate::second_pass::variants::Variant;
        use ahash::AHashMap;
        use std::sync::Arc;

        let huffman = Vec::new();
        let settings = ParserInputs {
            real_name_to_og_name: AHashMap::default(),
            wanted_players: vec![],
            wanted_player_props: vec![],
            wanted_other_props: vec![],
            wanted_prop_states: AHashMap::default(),
            wanted_ticks: vec![],
            wanted_events: vec![],
            parse_ents: true,
            parse_projectiles: false,
            collect_projectile_records: false,
            parse_grenades: false,
            only_header: false,
            only_convars: false,
            huffman_lookup_table: &huffman,
            order_by_steamid: false,
            list_props: false,
            fallback_bytes: None,
            cancelled: None,
        };
        let mut first_pass = FirstPassParser::new(&settings);
        first_pass.cls_by_id = Some(Arc::new(vec![]));
        for invalid_history in [false, true] {
            let mut parser = SecondPassParser::new(first_pass.create_first_pass_output().unwrap(), 0, true, None, DecodePlan::FULL).unwrap();
            parser.entities[1] = Some(Entity {
                cls_id: 0,
                entity_id: 1,
                serial: 0,
                props: AHashMap::default(),
                entity_type: EntityType::Normal,
                cosmetic_revision: 0,
            });
            parser.parse_usercmd = true;
            let full_command = csgoproto::CsgoUserCmdPb {
                input_history: vec![CsgoInputHistoryEntryPb {
                    render_tick_count: Some(7),
                    ..Default::default()
                }],
                base: Some(csgoproto::CBaseUserCmdPb {
                    pawn_entity_handle: Some(1),
                    subtick_moves: vec![CSubtickMoveStep {
                        button: Some(2),
                        ..Default::default()
                    }],
                    ..Default::default()
                }),
                ..Default::default()
            };
            let network_message = csgoproto::CsvcMsgUserCommands {
                commands: vec![csgoproto::CMsgServerUserCmd {
                    data: Some(full_command.encode_to_vec().into()),
                    player_slot: Some(0),
                    ..Default::default()
                }],
            };
            parser.parse_user_cmd(&network_message.encode_to_vec()).unwrap();
            assert_eq!(parser.usercmd_input_history_baselines[&0], full_command.input_history);
            assert_eq!(parser.usercmd_subtick_baselines[&0], full_command.base.unwrap().subtick_moves);
            let full_props = &parser.entities[1].as_ref().unwrap().props;
            let Variant::InputHistory(history) = &full_props[&USERCMD_INPUT_HISTORY_BASEID] else {
                panic!("expected full-command history")
            };
            assert_eq!(history[0].render_tick_count, Some(7));
            let Variant::UserCmdSubtickMoves(subticks) = &full_props[&USERCMD_SUBTICK_MOVES_BASEID] else {
                panic!("expected full-command subticks")
            };
            assert_eq!(subticks.len(), 1);
            assert_eq!(subticks[0].button, 2);
            let history = if invalid_history {
                &[0x0f, 0x02, 0x02, 0x20][..]
            } else {
                &[0x0f, 0x02, 0x02, 0x20, 0x11][..]
            };
            let subticks = if invalid_history {
                &[0x0f, 0x02, 0x02, 0x08, 0x04][..]
            } else {
                &[0x0f, 0x02, 0x02, 0x08][..]
            };
            parser.apply_delta_user_cmd(
                DeltaCsgoUserCmdPb {
                    input_history_delta: vec![prost::bytes::Bytes::copy_from_slice(history)],
                    base: Some(DeltaBaseUserCmdPb {
                        pawn_entity_handle: Some(1),
                        subtick_moves_delta: vec![prost::bytes::Bytes::copy_from_slice(subticks)],
                        ..Default::default()
                    }),
                    ..Default::default()
                },
                0,
            );
            let expected_tick = if invalid_history { 7 } else { 17 };
            let expected_button = if invalid_history { 4 } else { 2 };
            assert_eq!(parser.usercmd_input_history_baselines[&0][0].render_tick_count, Some(expected_tick));
            assert_eq!(parser.usercmd_subtick_baselines[&0][0].button, Some(expected_button));
            let props = &parser.entities[1].as_ref().unwrap().props;
            let Variant::InputHistory(history) = &props[&USERCMD_INPUT_HISTORY_BASEID] else {
                panic!("expected complete history")
            };
            assert_eq!(history.len(), 1);
            assert_eq!(history[0].render_tick_count, Some(expected_tick));
            let Variant::UserCmdSubtickMoves(subticks) = &props[&USERCMD_SUBTICK_MOVES_BASEID] else {
                panic!("expected subtick updates")
            };
            assert_eq!(subticks.len(), usize::from(invalid_history));

            let previous_props = props.clone();
            let no_base_message = csgoproto::CsvcMsgUserCommands {
                commands: vec![csgoproto::CMsgServerUserCmd {
                    data: Some(csgoproto::CsgoUserCmdPb::default().encode_to_vec().into()),
                    player_slot: Some(0),
                    ..Default::default()
                }],
            };
            parser.parse_user_cmd(&no_base_message.encode_to_vec()).unwrap();
            assert!(parser.usercmd_input_history_baselines[&0].is_empty());
            assert!(parser.usercmd_subtick_baselines[&0].is_empty());
            assert_eq!(parser.entities[1].as_ref().unwrap().props, previous_props);
        }
    }

}
