//! Deferred scalar columns. Protocol state stays on the CPU;
//! only the repeated lookup and expansion of unchanged output values is deferred.

use super::collect_data::PropType;
use super::decoder::Decoder;
use super::other_netmessages::Class;
use super::variants::{PropColumn, VarVec, Variant};
use crate::first_pass::prop_controller::{PropController, USERCMD_ATTACK_START_HISTORY_INDEX_1,
    USERCMD_ATTACK_START_HISTORY_INDEX_2, USERCMD_CLIENT_TICK, USERCMD_CONSUMED_SERVER_ANGLE_CHANGES,
    USERCMD_FORWARDMOVE, USERCMD_IMPULSE, USERCMD_LEFTMOVE, USERCMD_MOUSE_DX, USERCMD_MOUSE_DY,
    USERCMD_SUBTICK_LEFT_HAND_DESIRED, USERCMD_UPMOVE, USERCMD_VIEWANGLE_X, USERCMD_VIEWANGLE_Y,
    USERCMD_VIEWANGLE_Z, USERCMD_WEAPON_SELECT};
use crate::first_pass::sendtables::Field;
use ahash::AHashMap;

pub(crate) const ABSENT_GENERATION: u32 = u32::MAX;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum ScalarKind {
    Bool,
    I32,
    U32,
    F32,
}

impl ScalarKind {
    fn from_decoder(decoder: Decoder) -> Option<Self> {
        use Decoder::*;
        match decoder {
            BooleanDecoder | ComponentDecoder | GameModeRulesDecoder => Some(Self::Bool),
            SignedDecoder => Some(Self::I32),
            UnsignedDecoder | CentityHandleDecoder | BaseDecoder | AmmoDecoder => Some(Self::U32),
            NoscaleDecoder | FloatSimulationTimeDecoder | QuantalizedFloatDecoder(_)
            | FloatCoordDecoder => Some(Self::F32),
            _ => None,
        }
    }

    fn bits(self, value: &Variant) -> Option<u32> {
        match (self, value) {
            (Self::Bool, Variant::Bool(value)) => Some(u32::from(*value)),
            (Self::I32, Variant::I32(value)) => Some(*value as u32),
            (Self::U32, Variant::U32(value)) => Some(*value),
            (Self::F32, Variant::F32(value)) => Some(value.to_bits()),
            _ => None,
        }
    }
}

/// Synthetic properties have no sendtable declarations. Their types are
/// established by both authoritative usercmd writers in parser.rs, whose
/// actual insertions also feed these streams. Lists and 64-bit buttons stay
/// on the existing collector.
fn usercmd_scalar_kind(property_id: u32) -> Option<ScalarKind> {
    match property_id {
        USERCMD_VIEWANGLE_X | USERCMD_VIEWANGLE_Y | USERCMD_VIEWANGLE_Z
        | USERCMD_FORWARDMOVE | USERCMD_LEFTMOVE | USERCMD_UPMOVE => Some(ScalarKind::F32),
        USERCMD_IMPULSE | USERCMD_MOUSE_DX | USERCMD_MOUSE_DY | USERCMD_WEAPON_SELECT
        | USERCMD_CLIENT_TICK | USERCMD_ATTACK_START_HISTORY_INDEX_1
        | USERCMD_ATTACK_START_HISTORY_INDEX_2 => Some(ScalarKind::I32),
        USERCMD_SUBTICK_LEFT_HAND_DESIRED => Some(ScalarKind::Bool),
        USERCMD_CONSUMED_SERVER_ANGLE_CHANGES => Some(ScalarKind::U32),
        _ => None,
    }
}

/// Updates use output-row sequence, not demo tick. Multiple collections can
/// occur during the same tick, and updates between those collections differ.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(C)]
pub(crate) struct ScalarUpdate {
    pub row: u32,
    pub bits: u32,
}

#[derive(Debug)]
struct SparseColumn {
    output_slot: usize,
    kind: ScalarKind,
    source: usize,
}

fn property_source(prop_type: PropType) -> Option<usize> {
    match prop_type {
        PropType::Player => Some(0), PropType::Controller => Some(1),
        PropType::Rules => Some(2), PropType::Team => Some(3),
        PropType::Weapon => Some(4), _ => None,
    }
}

#[derive(Debug)]
pub(crate) struct SparseScalarColumns {
    columns: Vec<SparseColumn>,
    property_slots: AHashMap<u32, usize>,
    direct_slots: Vec<usize>,
    usercmd_slots: [usize; 1024],
    deferred_output_slots: Vec<bool>,
    entity_generations: Vec<u32>,
    /// Indexed by generation * column_count + column.
    pub(crate) streams: Vec<Vec<ScalarUpdate>>,
    pub(crate) row_generations: Vec<u32>,
    linked_generations: [Vec<u32>; 4],
    used_sources: [bool; 5],
}

fn inspect_field(
    field: &Field,
    dynamic_path: bool,
    candidates: &AHashMap<u32, usize>,
    kinds: &mut AHashMap<u32, Option<ScalarKind>>,
) {
    match field {
        Field::Value(value) if candidates.contains_key(&value.prop_id) => {
            let kind = (!dynamic_path && value.should_parse)
                .then(|| ScalarKind::from_decoder(value.decoder))
                .flatten();
            kinds
                .entry(value.prop_id)
                .and_modify(|existing| {
                    if *existing != kind {
                        *existing = None;
                    }
                })
                .or_insert(kind);
        }
        Field::Serializer(value) => {
            for field in &value.serializer.fields {
                inspect_field(field, dynamic_path, candidates, kinds);
            }
        }
        Field::Pointer(value) => {
            for field in &value.serializer.fields {
                inspect_field(field, dynamic_path, candidates, kinds);
            }
        }
        Field::Array(value) => inspect_field(&value.field_enum, true, candidates, kinds),
        Field::Vector(value) => inspect_field(&value.field_enum, true, candidates, kinds),
        _ => {}
    }
}

impl SparseScalarColumns {
    /// Called only when the existing dense collector is eligible. A schema
    /// declaration on any dynamic path, or with inconsistent types, excludes
    /// that property before any rows are suppressed.
    pub(crate) fn new(props: &PropController, classes: &[Class]) -> Option<Self> {
        if std::env::var("DEMOTRACER_SPARSE_COLUMNS").as_deref() == Ok("0") {
            return None;
        }
        Self::from_schema(props, classes)
    }

    pub(crate) fn from_schema(props: &PropController, classes: &[Class]) -> Option<Self> {
        let candidates: AHashMap<_, _> = props
            .prop_infos
            .iter()
            .enumerate()
            .filter(|(_, prop)| property_source(prop.prop_type).is_some())
            .map(|(slot, prop)| (prop.id, slot))
            .collect();
        let mut kinds = AHashMap::default();
        for class in classes {
            for field in &class.serializer.fields {
                inspect_field(field, false, &candidates, &mut kinds);
            }
        }
        let selected: Vec<_> = props
            .prop_infos
            .iter()
            .enumerate()
            .filter_map(|(slot, prop)| {
                let source = property_source(prop.prop_type)?;
                let kind = match (usercmd_scalar_kind(prop.id), kinds.get(&prop.id)) {
                    (Some(kind), None) => Some(kind),
                    (Some(expected), Some(Some(actual))) if expected == *actual => Some(expected),
                    (Some(_), _) => None,
                    (None, kind) => kind.copied().flatten(),
                };
                kind.map(|kind| (prop.id, slot, kind, source))
            })
            .collect();
        if selected.is_empty() {
            return None;
        }
        Some(Self::with_sources(props.prop_infos.len(), selected))
    }

    #[cfg(test)]
    fn with_columns(output_count: usize, selected: Vec<(u32, usize, ScalarKind)>) -> Self {
        Self::with_sources(output_count, selected.into_iter().map(|(id, slot, kind)| (id, slot, kind, 0)).collect())
    }

    fn with_sources(output_count: usize, selected: Vec<(u32, usize, ScalarKind, usize)>) -> Self {
        let mut deferred_output_slots = vec![false; output_count];
        let mut property_slots = AHashMap::default();
        let direct_len = selected.iter().map(|(id, _, _, _)| *id).filter(|id| *id < 100_000).max().map_or(0, |id| id as usize + 1);
        let mut direct_slots = vec![usize::MAX; direct_len];
        let mut usercmd_slots = [usize::MAX; 1024];
        let mut used_sources = [false; 5];
        let columns = selected
            .into_iter()
            .enumerate()
            .map(|(index, (property_id, output_slot, kind, source))| {
                deferred_output_slots[output_slot] = true;
                if (property_id as usize) < direct_slots.len() {
                    direct_slots[property_id as usize] = index;
                } else if let Some(slot) = usercmd_slots.get_mut(property_id.wrapping_sub(USERCMD_VIEWANGLE_X) as usize) {
                    *slot = index;
                } else { property_slots.insert(property_id, index); }
                used_sources[source] = true;
                SparseColumn { output_slot, kind, source }
            })
            .collect();
        Self {
            columns,
            property_slots,
            direct_slots,
            usercmd_slots,
            deferred_output_slots,
            entity_generations: Vec::new(),
            streams: Vec::new(),
            row_generations: Vec::new(),
            linked_generations: Default::default(),
            used_sources,
        }
    }

    pub(crate) fn column_count(&self) -> usize {
        self.columns.len()
    }

    pub(crate) fn is_deferred(&self, output_slot: usize) -> bool {
        self.deferred_output_slots[output_slot]
    }

    /// Both deletion and replacement invalidate the old stream. Stale player
    /// metadata can still cause rows to be collected after entity deletion.
    pub(crate) fn reset_entity(&mut self, entity_id: i32) {
        if let Some(generation) = self.entity_generations.get_mut(entity_id as usize) {
            *generation = ABSENT_GENERATION;
        }
    }

    fn ensure_generation(&mut self, entity_id: i32) -> u32 {
        let index = entity_id as usize;
        if self.entity_generations.len() <= index {
            self.entity_generations.resize(index + 1, ABSENT_GENERATION);
        }
        if self.entity_generations[index] == ABSENT_GENERATION {
            let generation = u32::try_from(self.streams.len() / self.column_count())
                .expect("entity generation count exceeds addressable row storage");
            for _ in 0..self.column_count() {
                self.streams.push(Vec::new());
            }
            self.entity_generations[index] = generation;
        }
        self.entity_generations[index]
    }

    pub(crate) fn record(&mut self, entity_id: i32, property_id: u32, value: &Variant) {
        let column = if let Some(&column) = self.direct_slots.get(property_id as usize) { column }
            else if let Some(&column) = self.usercmd_slots.get(property_id.wrapping_sub(USERCMD_VIEWANGLE_X) as usize) { column }
            else { self.property_slots.get(&property_id).copied().unwrap_or(usize::MAX) };
        if column == usize::MAX { return; }
        // Eligibility is proven from every schema decoder for this property.
        let bits = self.columns[column].kind.bits(value)
            .expect("eligible scalar decoder changed its output type");
        let generation = self.ensure_generation(entity_id);
        let stream_index = generation as usize * self.column_count() + column;
        let stream = &mut self.streams[stream_index];
        let row = u32::try_from(self.row_generations.len())
            .expect("player output exceeds addressable row storage");
        if let Some(last) = stream.last_mut() {
            if last.row == row {
                // No output row observed the intermediate value.
                last.bits = bits;
                return;
            }
            if last.bits == bits {
                return;
            }
        }
        stream.push(ScalarUpdate { row, bits });
    }

    #[cfg(test)]
    pub(crate) fn record_row(&mut self, entity_id: i32, entity_exists: bool) {
        self.record_linked_row([entity_exists.then_some(entity_id), None, None, None, None]);
    }

    pub(crate) fn record_linked_row(&mut self, entities: [Option<i32>; 5]) {
        for (source, entity) in entities.into_iter().enumerate() {
            if source != 0 && !self.used_sources[source] { continue; }
            let generation = entity.map_or(ABSENT_GENERATION, |entity| self.ensure_generation(entity));
            if source == 0 { self.row_generations.push(generation); }
            else { self.linked_generations[source - 1].push(generation); }
        }
    }

    fn column_generations(&self, column: usize) -> &[u32] {
        match self.columns[column].source {
            0 => &self.row_generations,
            source => &self.linked_generations[source - 1],
        }
    }

    /// Output is column-major, each cell [raw_value_bits, presence]. Keeping
    /// validity separate preserves absent values, explicit zero, NaNs and -0.
    #[cfg(test)]
    pub(crate) fn expand_cpu(&self, start: usize, end: usize) -> Vec<[u32; 2]> {
        let count = end - start;
        let mut values = vec![[0, 0]; count * self.column_count()];
        // Each source stream is monotonic. Seek once per tile, then advance
        // cursors linearly instead of binary-searching every output cell.
        let mut cursors: Vec<_> = self.streams.iter()
            .map(|stream| stream.partition_point(|update| update.row < start as u32))
            .collect();
        for column in 0..self.column_count() {
            for (local_row, &generation) in self.column_generations(column)[start..end].iter().enumerate() {
                if generation == ABSENT_GENERATION {
                    continue;
                }
                let stream_index = generation as usize * self.column_count() + column;
                let stream = &self.streams[stream_index];
                let row = (start + local_row) as u32;
                let cursor = &mut cursors[stream_index];
                while *cursor < stream.len() && stream[*cursor].row <= row {
                    *cursor += 1;
                }
                if *cursor != 0 {
                    values[column * count + local_row] = [stream[*cursor - 1].bits, 1];
                }
            }
        }
        values
    }

    fn expand_cpu_column<T>(&self, column: usize, convert: impl Fn(u32) -> T) -> Option<Vec<Option<T>>> {
        let columns = self.column_count();
        // Never allocate dense storage for a property absent in every entity.
        if self.streams.iter().skip(column).step_by(columns).all(Vec::is_empty) {
            return None;
        }
        let mut cursors = vec![0; self.streams.len() / columns];
        let mut present = false;
        let values = self.column_generations(column).iter().enumerate().map(|(row, &generation)| {
            if generation == ABSENT_GENERATION {
                return None;
            }
            let generation = generation as usize;
            let stream = &self.streams[generation * columns + column];
            let cursor = &mut cursors[generation];
            while *cursor < stream.len() && stream[*cursor].row <= row as u32 {
                *cursor += 1;
            }
            if *cursor == 0 {
                None
            } else {
                present = true;
                Some(convert(stream[*cursor - 1].bits))
            }
        }).collect();
        present.then_some(values)
    }

    /// Linear stream cursors write final typed vectors
    /// directly, with no intermediate cell buffer or per-cell Variant dispatch.
    fn finish_cpu(&self, output: &mut [PropColumn]) {
        for (column, descriptor) in self.columns.iter().enumerate() {
            let data = match descriptor.kind {
                ScalarKind::Bool => self.expand_cpu_column(column, |bits| bits != 0).map(VarVec::Bool),
                ScalarKind::I32 => self.expand_cpu_column(column, |bits| bits as i32).map(VarVec::I32),
                ScalarKind::U32 => self.expand_cpu_column(column, |bits| bits).map(VarVec::U32),
                ScalarKind::F32 => self.expand_cpu_column(column, f32::from_bits).map(VarVec::F32),
            };
            output[descriptor.output_slot] = PropColumn {
                num_nones: if data.is_none() { self.row_generations.len() } else { 0 },
                data,
            };
        }
    }

    pub(crate) fn finish(self, output: &mut [PropColumn]) {
        let started = std::time::Instant::now();
        let column_count = self.column_count();
        let row_count = self.row_generations.len();
        let update_count: usize = self.streams.iter().map(Vec::len).sum();
        let stream_count = self.streams.len();
        self.finish_cpu(output);
        if std::env::var_os("DEMOTRACER_PROFILE").is_some() {
            eprintln!(
                "[demotracer-profile] sparse_columns={} sparse_rows={} sparse_streams={} sparse_updates={} sparse_update_bytes={} scalar_finish_ms={:.3}",
                column_count, row_count, stream_count, update_count, update_count * 8,
                started.elapsed().as_secs_f64() * 1000.0,
            );
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn linked_sources_preserve_missing_links_replacement_and_shared_rules() {
        let mut scalar = SparseScalarColumns::with_sources(5, (0..5).map(|source|
            (10 + source as u32, source, ScalarKind::U32, source)).collect());
        for source in 0..5 { scalar.record(source as i32 + 1, source as u32 + 10, &Variant::U32(source as u32 + 100)); }
        scalar.record_linked_row([Some(1), Some(2), Some(3), Some(4), Some(5)]);
        scalar.reset_entity(2);
        scalar.record(2, 11, &Variant::U32(201));
        scalar.record(3, 12, &Variant::U32(202));
        scalar.record_linked_row([None, Some(2), Some(3), None, None]);
        scalar.record_linked_row([Some(1), None, Some(3), Some(4), Some(5)]);
        let mut output: Vec<_> = (0..5).map(|_| PropColumn::new()).collect();
        scalar.finish_cpu(&mut output);
        let expected = [vec![Some(100), None, Some(100)], vec![Some(101), Some(201), None],
            vec![Some(102), Some(202), Some(202)], vec![Some(103), None, Some(103)], vec![Some(104), None, Some(104)]];
        for (column, expected) in output.iter().zip(expected) { assert_eq!(column.data, Some(VarVec::U32(expected))); }
    }

    fn collector() -> SparseScalarColumns {
        SparseScalarColumns::with_columns(2, vec![(10, 0, ScalarKind::F32), (20, 1, ScalarKind::U32)])
    }

    #[test]
    fn sequence_distinguishes_collections_with_the_same_demo_tick() {
        let mut scalar = collector();
        scalar.record(7, 20, &Variant::U32(11));
        scalar.record_row(7, true);
        scalar.record(7, 20, &Variant::U32(12));
        scalar.record(7, 20, &Variant::U32(0));
        scalar.record_row(7, true);
        assert_eq!(scalar.expand_cpu(0, 2), vec![[0, 0], [0, 0], [11, 1], [0, 1]]);
    }

    #[test]
    fn deleted_roster_rows_and_recreated_baselines_do_not_leak_values() {
        let mut scalar = collector();
        scalar.record(7, 20, &Variant::U32(11));
        scalar.record_row(7, true);
        scalar.reset_entity(7);
        scalar.record_row(7, false);
        scalar.reset_entity(7);
        scalar.record_row(7, true);
        scalar.record(7, 20, &Variant::U32(3));
        scalar.record(7, 20, &Variant::U32(4));
        scalar.record_row(7, true);
        assert_eq!(&scalar.expand_cpu(0, 4)[4..], &[[11, 1], [0, 0], [0, 0], [4, 1]]);
    }

    #[test]
    fn raw_float_bits_and_missing_values_are_exact() {
        let mut scalar = collector();
        scalar.record_row(7, true);
        scalar.record(7, 10, &Variant::F32(-0.0));
        for _ in 0..4096 {
            scalar.record_row(7, true);
        }
        let nan_bits = 0x7fc0_1234;
        scalar.record(7, 10, &Variant::F32(f32::from_bits(nan_bits)));
        scalar.record_row(7, true);
        let mut output = vec![PropColumn::new(), PropColumn::new()];
        scalar.finish_cpu(&mut output);
        let Some(VarVec::F32(values)) = &output[0].data else { panic!("float column missing") };
        assert!(values[0].is_none());
        assert_eq!(values[1].unwrap().to_bits(), (-0.0_f32).to_bits());
        assert_eq!(values.last().unwrap().unwrap().to_bits(), nan_bits);
        assert_eq!(output[1].data, None);
        assert_eq!(output[1].num_nones, 4098);
    }

    #[test]
    fn unchanged_updates_are_coalesced_but_other_entities_remain_independent() {
        let mut scalar = collector();
        scalar.record(7, 20, &Variant::U32(1));
        scalar.record_row(7, true);
        scalar.record(7, 20, &Variant::U32(1));
        scalar.record(8, 20, &Variant::U32(2));
        scalar.record_row(8, true);
        scalar.record_row(7, true);
        assert_eq!(scalar.streams[1].len(), 1);
        assert_eq!(&scalar.expand_cpu(0, 3)[3..], &[[1, 1], [2, 1], [1, 1]]);
    }

    #[test]
    fn schema_requires_direct_scalar_declaration_and_excludes_unknown_synthetic_props() {
        use crate::first_pass::prop_controller::PropInfo;
        use crate::first_pass::sendtables::{ArrayField, Serializer, ValueField};
        let mut props = PropController::new(
            vec![], vec![], AHashMap::default(), AHashMap::default(), false, &[], false,
        );
        props.prop_infos = (10..15).map(|id| PropInfo {
            id,
            prop_type: PropType::Player,
            prop_name: format!("test_{id}"),
            prop_friendly_name: format!("test_{id}"),
            is_player_prop: true,
        }).collect();
        let field = |id, decoder| Field::Value(ValueField {
            decoder, name: format!("p{id}"), full_name: format!("p{id}"),
            should_parse: true, prop_id: id,
        });
        let class = Class {
            class_id: 0,
            name: "CCSPlayerPawn".into(),
            serializer: Serializer {
                name: "CCSPlayerPawn".into(),
                fields: vec![
                    field(10, Decoder::NoscaleDecoder),
                    Field::Array(ArrayField::new(field(11, Decoder::UnsignedDecoder), 8)),
                    field(12, Decoder::StringDecoder),
                    field(13, Decoder::NoscaleDecoder),
                    field(13, Decoder::UnsignedDecoder),
                    // Unknown synthetic Player property with no declared writer.
                ],
            },
        };
        let scalar = SparseScalarColumns::from_schema(&props, &[class]).unwrap();
        assert_eq!(scalar.column_count(), 1);
        assert!(scalar.is_deferred(0));
        assert!((1..5).all(|slot| !scalar.is_deferred(slot)));
    }

    #[test]
    fn monotonic_cpu_expansion_matches_point_lookup_for_sliced_tiles() {
        let mut scalar = collector();
        let mut state = 0x1234_5678_u32;
        for row in 0..4096 {
            state = state.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
            let entity_id = (state % 7) as i32;
            if row % 29 == 0 {
                scalar.reset_entity(entity_id);
            }
            if row % 3 != 0 {
                scalar.record(entity_id, 20, &Variant::U32(state));
            }
            scalar.record_row(entity_id, row % 13 != 0);
        }
        for (start, end) in [(0, 4096), (17, 109), (1071, 4010)] {
            let values = scalar.expand_cpu(start, end);
            for column in 0..scalar.column_count() {
                for row in start..end {
                    let generation = scalar.row_generations[row];
                    let expected = if generation == ABSENT_GENERATION {
                        [0, 0]
                    } else {
                        scalar.streams[generation as usize * scalar.column_count() + column]
                            .iter().rev().find(|update| update.row <= row as u32)
                            .map_or([0, 0], |update| [update.bits, 1])
                    };
                    assert_eq!(values[column * (end - start) + row - start], expected);
                }
            }
        }
    }

    #[test]
    fn direct_typed_cpu_columns_match_independent_point_lookup() {
        let mut scalar = collector();
        for row in 0..35 {
            if row % 4 == 0 {
                let bits = if row == 4 { 0x8000_0000 } else if row == 12 { 0x7fc0_5678 } else { row };
                scalar.record(7, 10, &Variant::F32(f32::from_bits(bits)));
                scalar.record(7, 20, &Variant::U32(row));
            }
            scalar.record_row(7, row % 5 != 0);
        }
        let mut direct = vec![PropColumn::new(), PropColumn::new()];
        scalar.finish_cpu(&mut direct);
        for (column, actual) in direct.iter().enumerate() {
            let expected: Vec<_> = scalar.row_generations.iter().enumerate().map(|(row, &generation)| {
                if generation == ABSENT_GENERATION { return None; }
                scalar.streams[generation as usize * scalar.column_count() + column]
                    .iter().rev().find(|update| update.row <= row as u32).map(|update| update.bits)
            }).collect();
            match &actual.data {
                Some(VarVec::F32(actual)) => {
                    assert_eq!(actual.iter().map(|v| v.map(f32::to_bits)).collect::<Vec<_>>(), expected);
                }
                Some(VarVec::U32(actual)) => assert_eq!(*actual, expected),
                _ => panic!("missing typed column"),
            }
        }
    }

    #[test]
    fn synthetic_usercmd_types_are_explicit_and_lists_and_u64_remain_excluded() {
        use crate::first_pass::prop_controller::{USERCMD_BUTTONSTATE_1, USERCMD_BUTTONSTATE_2,
            USERCMD_BUTTONSTATE_3, USERCMD_INPUT_HISTORY_BASEID, USERCMD_SUBTICK_MOVES_BASEID};
        for id in [USERCMD_VIEWANGLE_X, USERCMD_VIEWANGLE_Y, USERCMD_VIEWANGLE_Z,
            USERCMD_LEFTMOVE, USERCMD_FORWARDMOVE, USERCMD_UPMOVE] {
            assert_eq!(usercmd_scalar_kind(id), Some(ScalarKind::F32));
        }
        for id in [USERCMD_IMPULSE, USERCMD_MOUSE_DX, USERCMD_MOUSE_DY, USERCMD_WEAPON_SELECT,
            USERCMD_CLIENT_TICK, USERCMD_ATTACK_START_HISTORY_INDEX_1, USERCMD_ATTACK_START_HISTORY_INDEX_2] {
            assert_eq!(usercmd_scalar_kind(id), Some(ScalarKind::I32));
        }
        assert_eq!(usercmd_scalar_kind(USERCMD_SUBTICK_LEFT_HAND_DESIRED), Some(ScalarKind::Bool));
        assert_eq!(usercmd_scalar_kind(USERCMD_CONSUMED_SERVER_ANGLE_CHANGES), Some(ScalarKind::U32));
        for id in [USERCMD_BUTTONSTATE_1, USERCMD_BUTTONSTATE_2, USERCMD_BUTTONSTATE_3,
            USERCMD_INPUT_HISTORY_BASEID, USERCMD_SUBTICK_MOVES_BASEID] {
            assert_eq!(usercmd_scalar_kind(id), None);
        }
    }
}
