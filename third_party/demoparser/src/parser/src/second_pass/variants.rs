use crate::first_pass::prop_controller::PropInfo;
use crate::second_pass::collect_data::ProjectileRecord;
use crate::second_pass::parser_settings::{EconItem, PlayerEndMetaData};
use ahash::{AHashMap, HashMap};
use itertools::Itertools;
use memmap2::Mmap;
use serde::ser::{SerializeMap, SerializeSeq, SerializeStruct};
use serde::Serialize;
use std::sync::Arc;

#[inline]
pub(crate) fn into_shared_slice<T>(values: Vec<T>) -> Arc<[T]> {
    if values.is_empty() {
        // Use the standard empty-slice default instead of allocating from a Vec.
        Arc::default()
    } else {
        values.into()
    }
}

#[derive(Debug, Clone, PartialEq)]
pub enum Variant {
    Bool(bool),
    U32(u32),
    I32(i32),
    F32(f32),
    U64(u64),
    String(String),
    VecXY([f32; 2]),
    VecXYZ([f32; 3]),
    // Todo change to Vec<T>
    StringVec(Vec<String>),
    U32Vec(Vec<u32>),
    U64Vec(Vec<u64>),
    Stickers(Arc<[Sticker]>),
    InventoryWeaponCosmetics(Arc<[InventoryWeaponCosmetic]>),
    InputHistory(Arc<[InputHistory]>),
    UserCmdSubtickMoves(Arc<[UserCmdSubtickMove]>),
}
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Sticker {
    pub slot: u32,
    pub name: String,
    pub wear: f32,
    pub id: u32,
    pub x: f32,
    pub y: f32,
    pub scale: Option<f32>,
    pub rotation: Option<f32>,
}
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct InventoryWeaponCosmetic {
    pub item_def_index: u32,
    pub item_id_high: Option<u32>,
    pub item_id_low: Option<u32>,
    pub item_account_id: Option<u32>,
    pub original_owner_xuid: Option<u64>,
    pub paint_kit: u32,
    pub paint_seed: u32,
    pub paint_wear: f32,
    pub entity_quality: Option<i32>,
    pub stattrak_counter: Option<i32>,
    pub attributes: Vec<InventoryWeaponAttribute>,
    pub custom_name: Option<String>,
    pub stickers: Vec<Sticker>,
}
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct InventoryWeaponAttribute {
    pub definition_index: u32,
    pub raw_value: f32,
    pub raw_value_bits: u32,
}
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct InputHistory {
    pub view_angles: Option<[f32; 3]>,
    pub render_tick_count: Option<i32>,
    pub render_tick_fraction: Option<f32>,
    pub player_tick_count: Option<i32>,
    pub player_tick_fraction: Option<f32>,
    pub cl_interp_fraction: Option<f32>,
    pub sv_interp0: Option<InputHistoryInterpolation>,
    pub sv_interp1: Option<InputHistoryInterpolation>,
    pub player_interp: Option<InputHistoryInterpolation>,
    pub frame_number: Option<i32>,
    pub target_ent_index: Option<i32>,
    pub shoot_position: Option<[f32; 3]>,
    pub target_head_pos_check: Option<[f32; 3]>,
    pub target_abs_pos_check: Option<[f32; 3]>,
    pub target_abs_ang_check: Option<[f32; 3]>,
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize)]
pub struct InputHistoryInterpolation {
    pub src_tick: Option<i32>,
    pub dst_tick: Option<i32>,
    pub fraction: Option<f32>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct UserCmdSubtickMove {
    pub when: f32,
    pub button: u64,
    pub pressed: bool,
    pub analog_forward: f32,
    pub analog_left: f32,
    pub pitch_delta: f32,
    pub yaw_delta: f32,
}

const MISSING_STRING: u32 = u32::MAX;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
enum NumericString {
    Unsigned(u64),
    Signed(i32),
}

/// Immutable strings are stored once per column; rows retain four-byte IDs.
/// Serialization and readers expose the same strings and nulls as owned columns.
#[derive(Debug, Clone, Default)]
pub struct StringDictionary {
    values: Vec<Arc<str>>,
    ids: AHashMap<Arc<str>, u32>,
    numeric_ids: AHashMap<NumericString, u32>,
    rows: Vec<u32>,
}

impl PartialEq for StringDictionary {
    fn eq(&self, other: &Self) -> bool {
        self.len() == other.len() && self.iter().eq(other.iter())
    }
}

impl StringDictionary {
    pub fn len(&self) -> usize { self.rows.len() }

    pub fn get(&self, row: usize) -> Option<&str> {
        let id = *self.rows.get(row)?;
        if id == MISSING_STRING { None } else { self.values.get(id as usize).map(AsRef::as_ref) }
    }

    pub fn iter(&self) -> impl ExactSizeIterator<Item = Option<&str>> {
        self.rows.iter().map(|&id| {
            if id == MISSING_STRING { None } else { Some(self.values[id as usize].as_ref()) }
        })
    }

    fn intern(&mut self, value: &str) -> u32 {
        if let Some(&id) = self.ids.get(value) { return id; }
        let id = u32::try_from(self.values.len()).expect("string dictionary exceeds addressable IDs");
        assert_ne!(id, MISSING_STRING, "string dictionary exhausted its value IDs");
        let value: Arc<str> = Arc::from(value);
        self.ids.insert(Arc::clone(&value), id);
        self.values.push(value);
        id
    }

    pub(crate) fn push(&mut self, value: Option<&str>) {
        let id = value.map_or(MISSING_STRING, |value| self.intern(value));
        self.rows.push(id);
    }

    fn push_numeric(&mut self, value: NumericString) {
        let id = if let Some(&id) = self.numeric_ids.get(&value) {
            id
        } else {
            let text = match value {
                NumericString::Unsigned(value) => value.to_string(),
                NumericString::Signed(value) => value.to_string(),
            };
            let id = self.intern(&text);
            self.numeric_ids.insert(value, id);
            id
        };
        self.rows.push(id);
    }

    fn push_missing(&mut self, count: usize) {
        self.rows.resize(self.rows.len() + count, MISSING_STRING);
    }

    fn slice(&self, indices: &[usize]) -> Self {
        Self {
            values: self.values.clone(), ids: self.ids.clone(), numeric_ids: self.numeric_ids.clone(),
            rows: indices.iter().map(|&index| self.rows[index]).collect(),
        }
    }

    fn append(&mut self, other: &mut Self) {
        let remap: Vec<_> = other.values.iter().map(|value| self.intern(value)).collect();
        self.rows.extend(other.rows.drain(..).map(|id| {
            if id == MISSING_STRING { id } else { remap[id as usize] }
        }));
        other.values.clear();
        other.ids.clear();
        other.numeric_ids.clear();
    }
}

impl Serialize for StringDictionary {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut values = serializer.serialize_seq(Some(self.len()))?;
        for value in self.iter() { values.serialize_element(&value)?; }
        values.end()
    }
}

#[derive(Debug, Clone)]
pub enum VarVec {
    U32(Vec<Option<u32>>),
    Bool(Vec<Option<bool>>),
    U64(Vec<Option<u64>>),
    F32(Vec<Option<f32>>),
    I32(Vec<Option<i32>>),
    String(Vec<Option<String>>),
    SharedString(StringDictionary),
    StringVec(Vec<Vec<String>>),
    U64Vec(Vec<Vec<u64>>),
    U32Vec(Vec<Vec<u32>>),
    XYVec(Vec<Option<[f32; 2]>>),
    XYZVec(Vec<Option<[f32; 3]>>),
    Stickers(Vec<Arc<[Sticker]>>),
    InventoryWeaponCosmetics(Vec<Arc<[InventoryWeaponCosmetic]>>),
    InputHistory(Vec<Arc<[InputHistory]>>),
    UserCmdSubtickMoves(Vec<Arc<[UserCmdSubtickMove]>>),
}

impl PartialEq for VarVec {
    fn eq(&self, other: &Self) -> bool {
        match (self, other) {
            (Self::U32(a), Self::U32(b)) => a == b,
            (Self::Bool(a), Self::Bool(b)) => a == b,
            (Self::U64(a), Self::U64(b)) => a == b,
            (Self::F32(a), Self::F32(b)) => a == b,
            (Self::I32(a), Self::I32(b)) => a == b,
            (Self::String(a), Self::String(b)) => a == b,
            (Self::SharedString(a), Self::SharedString(b)) => a == b,
            (Self::String(a), Self::SharedString(b)) | (Self::SharedString(b), Self::String(a)) => {
                a.iter().map(|value| value.as_deref()).eq(b.iter())
            }
            (Self::StringVec(a), Self::StringVec(b)) => a == b,
            (Self::U64Vec(a), Self::U64Vec(b)) => a == b,
            (Self::U32Vec(a), Self::U32Vec(b)) => a == b,
            (Self::XYVec(a), Self::XYVec(b)) => a == b,
            (Self::XYZVec(a), Self::XYZVec(b)) => a == b,
            (Self::Stickers(a), Self::Stickers(b)) => a == b,
            (Self::InventoryWeaponCosmetics(a), Self::InventoryWeaponCosmetics(b)) => a == b,
            (Self::InputHistory(a), Self::InputHistory(b)) => a == b,
            (Self::UserCmdSubtickMoves(a), Self::UserCmdSubtickMoves(b)) => a == b,
            _ => false,
        }
    }
}

impl VarVec {
    pub fn new(item: &Variant) -> Self {
        match item {
            Variant::Bool(_) => VarVec::Bool(vec![]),
            Variant::I32(_) => VarVec::I32(vec![]),
            Variant::F32(_) => VarVec::F32(vec![]),
            Variant::String(_) => VarVec::String(vec![]),
            Variant::U64(_) => VarVec::U64(vec![]),
            Variant::U32(_) => VarVec::U32(vec![]),
            Variant::StringVec(_) => VarVec::StringVec(vec![]),
            Variant::U64Vec(_) => VarVec::U64Vec(vec![]),
            Variant::U32Vec(_) => VarVec::U32Vec(vec![]),
            Variant::VecXY(_) => VarVec::XYVec(vec![]),
            Variant::VecXYZ(_) => VarVec::XYZVec(vec![]),
            Variant::Stickers(_) => VarVec::Stickers(vec![]),
            Variant::InventoryWeaponCosmetics(_) => VarVec::InventoryWeaponCosmetics(vec![]),
            Variant::InputHistory(_) => VarVec::InputHistory(vec![]),
            Variant::UserCmdSubtickMoves(_) => VarVec::UserCmdSubtickMoves(vec![]),
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct PropColumn {
    pub data: Option<VarVec>,
    pub num_nones: usize,
}

impl PropColumn {
    pub fn new() -> Self {
        PropColumn { data: None, num_nones: 0 }
    }
    pub fn slice_to_new(&self, indicies: &[usize]) -> Option<PropColumn> {
        let data = match &self.data {
            Some(VarVec::Bool(b)) => VarVec::Bool(indicies.iter().map(|x| b[*x]).collect_vec()),
            Some(VarVec::I32(b)) => VarVec::I32(indicies.iter().map(|x| b[*x]).collect_vec()),
            Some(VarVec::F32(b)) => VarVec::F32(indicies.iter().map(|x| b[*x]).collect_vec()),
            Some(VarVec::String(b)) => VarVec::String(indicies.iter().map(|x| b[*x].to_owned()).collect_vec()),
            Some(VarVec::SharedString(b)) => VarVec::SharedString(b.slice(indicies)),
            Some(VarVec::U32(b)) => VarVec::U32(indicies.iter().map(|x| b[*x]).collect_vec()),
            Some(VarVec::U64(b)) => VarVec::U64(indicies.iter().map(|x| b[*x]).collect_vec()),
            Some(VarVec::StringVec(b)) => VarVec::StringVec(indicies.iter().map(|x| b[*x].to_owned()).collect_vec()),
            Some(VarVec::U64Vec(b)) => VarVec::U64Vec(indicies.iter().map(|x| b[*x].to_owned()).collect_vec()),
            Some(VarVec::U32Vec(b)) => VarVec::U32Vec(indicies.iter().map(|x| b[*x].to_owned()).collect_vec()),
            Some(VarVec::XYVec(b)) => VarVec::XYVec(indicies.iter().map(|x| b[*x]).collect_vec()),
            Some(VarVec::XYZVec(b)) => VarVec::XYZVec(indicies.iter().map(|x| b[*x]).collect_vec()),
            Some(VarVec::Stickers(b)) => VarVec::Stickers(indicies.iter().map(|x| Arc::clone(&b[*x])).collect_vec()),
            Some(VarVec::InventoryWeaponCosmetics(b)) => VarVec::InventoryWeaponCosmetics(indicies.iter().map(|x| b[*x].to_owned()).collect_vec()),
            Some(VarVec::InputHistory(b)) => VarVec::InputHistory(indicies.iter().map(|x| Arc::clone(&b[*x])).collect_vec()),
            Some(VarVec::UserCmdSubtickMoves(b)) => VarVec::UserCmdSubtickMoves(indicies.iter().map(|x| Arc::clone(&b[*x])).collect_vec()),
            None => {
                return Some(PropColumn {
                    data: None,
                    num_nones: indicies.len(),
                })
            }
        };
        Some(PropColumn {
            data: Some(data),
            num_nones: 0,
        })
    }
    pub fn len(&self) -> usize {
        match &self.data {
            Some(VarVec::Bool(b)) => b.len(),
            Some(VarVec::I32(b)) => b.len(),
            Some(VarVec::F32(b)) => b.len(),
            Some(VarVec::String(b)) => b.len(),
            Some(VarVec::SharedString(b)) => b.len(),
            Some(VarVec::U32(b)) => b.len(),
            Some(VarVec::U64(b)) => b.len(),
            Some(VarVec::StringVec(b)) => b.len(),
            Some(VarVec::U64Vec(b)) => b.len(),
            Some(VarVec::U32Vec(b)) => b.len(),
            Some(VarVec::XYVec(b)) => b.len(),
            Some(VarVec::XYZVec(b)) => b.len(),
            Some(VarVec::Stickers(b)) => b.len(),
            Some(VarVec::InventoryWeaponCosmetics(b)) => b.len(),
            Some(VarVec::InputHistory(b)) => b.len(),
            Some(VarVec::UserCmdSubtickMoves(b)) => b.len(),
            None => self.num_nones,
        }
    }
    /// Appends `other` in row order, draining same-typed vector storage when possible.
    pub fn extend_from(&mut self, other: &mut PropColumn) {
        match &mut self.data {
            Some(VarVec::Bool(v)) => match &mut other.data {
                Some(VarVec::Bool(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(None);
                    }
                }
                _ => {}
            },
            Some(VarVec::I32(v)) => match &mut other.data {
                Some(VarVec::I32(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(None);
                    }
                }
                _ => {}
            },
            Some(VarVec::F32(v)) => match &mut other.data {
                Some(VarVec::F32(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(None);
                    }
                }
                _ => {}
            },
            Some(VarVec::String(v)) => match &mut other.data {
                Some(VarVec::String(v_other)) => {
                    v.append(v_other);
                }
                Some(VarVec::SharedString(v_other)) => {
                    v.extend(v_other.iter().map(|value| value.map(str::to_owned)));
                    v_other.rows.clear();
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(None);
                    }
                }
                _ => {}
            },
            Some(VarVec::SharedString(v)) => match &mut other.data {
                Some(VarVec::SharedString(v_other)) => v.append(v_other),
                Some(VarVec::String(v_other)) => {
                    for value in v_other.drain(..) { v.push(value.as_deref()); }
                }
                None => v.push_missing(other.num_nones),
                _ => {}
            },
            Some(VarVec::U32(v)) => match &mut other.data {
                Some(VarVec::U32(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(None);
                    }
                }
                _ => {}
            },
            Some(VarVec::U64(v)) => match &mut other.data {
                Some(VarVec::U64(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(None);
                    }
                }
                _ => {}
            },
            Some(VarVec::StringVec(v)) => match &mut other.data {
                Some(VarVec::StringVec(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(vec![]);
                    }
                }
                _ => {}
            },
            Some(VarVec::U64Vec(v)) => match &mut other.data {
                Some(VarVec::U64Vec(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(vec![]);
                    }
                }
                _ => {}
            },
            Some(VarVec::XYVec(v)) => match &mut other.data {
                Some(VarVec::XYVec(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(None);
                    }
                }
                _ => {}
            },
            Some(VarVec::XYZVec(v)) => match &mut other.data {
                Some(VarVec::XYZVec(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(None);
                    }
                }
                _ => {}
            },
            Some(VarVec::Stickers(v)) => match &mut other.data {
                Some(VarVec::Stickers(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(Arc::default());
                    }
                }
                _ => {}
            },
            Some(VarVec::InventoryWeaponCosmetics(v)) => match &mut other.data {
                Some(VarVec::InventoryWeaponCosmetics(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(Arc::default());
                    }
                }
                _ => {}
            },
            Some(VarVec::InputHistory(v)) => match &mut other.data {
                Some(VarVec::InputHistory(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(Arc::default());
                    }
                }
                _ => {}
            },
            Some(VarVec::UserCmdSubtickMoves(v)) => match &mut other.data {
                Some(VarVec::UserCmdSubtickMoves(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(Arc::default());
                    }
                }
                _ => {}
            },
            Some(VarVec::U32Vec(v)) => match &mut other.data {
                Some(VarVec::U32Vec(v_other)) => {
                    v.append(v_other);
                }
                None => {
                    for _ in 0..other.num_nones {
                        v.push(vec![]);
                    }
                }
                _ => {}
            },
            None => match &other.data {
                Some(VarVec::Bool(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::I32(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::U32(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::U64(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::String(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::SharedString(_)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::F32(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::StringVec(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::U64Vec(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::XYVec(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::XYZVec(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::Stickers(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::InventoryWeaponCosmetics(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::U32Vec(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::InputHistory(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                Some(VarVec::UserCmdSubtickMoves(_inner)) => {
                    self.resolve_vec_type(PropColumn::get_type(&other.data));
                    self.extend_from(other);
                }
                None => {
                    self.num_nones += other.num_nones;
                }
            },
        }
    }

    pub fn get_type(v: &Option<VarVec>) -> Option<u32> {
        match v {
            Some(VarVec::Bool(_)) => Some(0),
            Some(VarVec::F32(_)) => Some(1),
            Some(VarVec::I32(_)) => Some(2),
            Some(VarVec::String(_)) => Some(3),
            Some(VarVec::SharedString(_)) => Some(15),
            Some(VarVec::U32(_)) => Some(4),
            Some(VarVec::U64(_)) => Some(5),
            Some(VarVec::StringVec(_)) => Some(6),
            Some(VarVec::U64Vec(_)) => Some(7),
            Some(VarVec::XYVec(_)) => Some(8),
            Some(VarVec::XYZVec(_)) => Some(9),
            Some(VarVec::Stickers(_)) => Some(10),
            Some(VarVec::U32Vec(_)) => Some(11),
            Some(VarVec::InputHistory(_)) => Some(12),
            Some(VarVec::UserCmdSubtickMoves(_)) => Some(13),
            Some(VarVec::InventoryWeaponCosmetics(_)) => Some(14),

            None => None,
        }
    }
    pub fn resolve_vec_type(&mut self, v_type: Option<u32>) {
        if self.data.is_some() {
            return;
        }
        match v_type {
            Some(0) => self.data = Some(VarVec::Bool(vec![])),
            Some(1) => self.data = Some(VarVec::F32(vec![])),
            Some(2) => self.data = Some(VarVec::I32(vec![])),
            Some(3) => self.data = Some(VarVec::String(vec![])),
            Some(4) => self.data = Some(VarVec::U32(vec![])),
            Some(5) => self.data = Some(VarVec::U64(vec![])),
            Some(6) => self.data = Some(VarVec::StringVec(vec![])),
            Some(7) => self.data = Some(VarVec::U64Vec(vec![])),
            Some(8) => self.data = Some(VarVec::XYVec(vec![])),
            Some(9) => self.data = Some(VarVec::XYZVec(vec![])),
            Some(10) => self.data = Some(VarVec::Stickers(vec![])),
            Some(11) => self.data = Some(VarVec::U32Vec(vec![])),
            Some(12) => self.data = Some(VarVec::InputHistory(vec![])),
            Some(13) => self.data = Some(VarVec::UserCmdSubtickMoves(vec![])),
            Some(14) => self.data = Some(VarVec::InventoryWeaponCosmetics(vec![])),
            Some(15) => self.data = Some(VarVec::SharedString(StringDictionary::default())),
            _ => {}
        }
        for _ in 0..self.num_nones {
            self.push(None);
        }
    }
    #[inline(always)]
    pub fn push(&mut self, item: Option<Variant>) {
        match &mut self.data {
            Some(v) => v.push_variant(item),
            None => match item {
                None => self.num_nones += 1,
                Some(p) => {
                    let mut var_vec = VarVec::new(&p);
                    for _ in 0..self.num_nones {
                        var_vec.push_variant(None);
                    }
                    self.num_nones = 0;
                    var_vec.push_variant(Some(p));
                    self.data = Some(var_vec);
                }
            },
        }
    }

    pub(crate) fn push_shared_string(&mut self, value: Option<&str>) {
        if self.data.is_none() {
            if value.is_none() { self.num_nones += 1; return; }
            let mut strings = StringDictionary::default();
            strings.push_missing(self.num_nones);
            self.num_nones = 0;
            self.data = Some(VarVec::SharedString(strings));
        }
        match self.data.as_mut().unwrap() {
            VarVec::SharedString(strings) => strings.push(value),
            VarVec::String(strings) => strings.push(value.map(str::to_owned)),
            other if value.is_none() => other.push_none(),
            _ => {},
        }
    }

    pub(crate) fn push_shared_u64(&mut self, value: u64) {
        self.push_shared_numeric(NumericString::Unsigned(value));
    }

    pub(crate) fn push_shared_i32(&mut self, value: i32) {
        self.push_shared_numeric(NumericString::Signed(value));
    }

    fn push_shared_numeric(&mut self, value: NumericString) {
        if self.data.is_none() {
            let mut strings = StringDictionary::default();
            strings.push_missing(self.num_nones);
            self.num_nones = 0;
            self.data = Some(VarVec::SharedString(strings));
        }
        match self.data.as_mut().unwrap() {
            VarVec::SharedString(strings) => strings.push_numeric(value),
            VarVec::String(strings) => strings.push(Some(match value {
                NumericString::Unsigned(value) => value.to_string(),
                NumericString::Signed(value) => value.to_string(),
            })),
            _ => {},
        }
    }
}

impl VarVec {
    #[inline(always)]
    pub fn push_variant(&mut self, item: Option<Variant>) {
        match item {
            Some(Variant::F32(p)) => match self {
                VarVec::F32(f) => f.push(Some(p)),
                _ => {}
            },
            Some(Variant::I32(p)) => match self {
                VarVec::I32(f) => f.push(Some(p)),
                _ => {}
            },
            Some(Variant::String(p)) => match self {
                VarVec::String(f) => f.push(Some(p)),
                VarVec::SharedString(f) => f.push(Some(&p)),
                _ => {}
            },
            Some(Variant::U32(p)) => match self {
                VarVec::U32(f) => f.push(Some(p)),
                _ => {}
            },
            Some(Variant::U64(p)) => match self {
                VarVec::U64(f) => f.push(Some(p)),
                _ => {}
            },
            Some(Variant::Bool(p)) => match self {
                VarVec::Bool(f) => f.push(Some(p)),
                _ => {}
            },
            Some(Variant::StringVec(p)) => match self {
                VarVec::StringVec(f) => f.push(p),
                _ => {}
            },
            Some(Variant::U64Vec(p)) => match self {
                VarVec::U64Vec(f) => f.push(p),
                _ => {}
            },
            Some(Variant::U32Vec(p)) => match self {
                VarVec::U32Vec(f) => f.push(p),
                _ => {}
            },
            Some(Variant::VecXY(p)) => match self {
                VarVec::XYVec(f) => f.push(Some(p)),
                _ => {}
            },
            Some(Variant::VecXYZ(p)) => match self {
                VarVec::XYZVec(f) => f.push(Some(p)),
                _ => {}
            },
            Some(Variant::Stickers(p)) => match self {
                VarVec::Stickers(f) => f.push(p),
                _ => {}
            },
            Some(Variant::InventoryWeaponCosmetics(p)) => match self {
                VarVec::InventoryWeaponCosmetics(f) => f.push(p),
                _ => {}
            },
            Some(Variant::InputHistory(p)) => match self {
                VarVec::InputHistory(f) => f.push(p),
                _ => {}
            },
            Some(Variant::UserCmdSubtickMoves(p)) => match self {
                VarVec::UserCmdSubtickMoves(f) => f.push(p),
                _ => {}
            },
            None => self.push_none(),
        }
    }
    pub fn push_none(&mut self) {
        match self {
            VarVec::I32(f) => f.push(None),
            VarVec::F32(f) => f.push(None),
            VarVec::String(f) => f.push(None),
            VarVec::SharedString(f) => f.push(None),
            VarVec::U32(f) => f.push(None),
            VarVec::U64(f) => f.push(None),
            VarVec::Bool(f) => f.push(None),
            VarVec::StringVec(f) => f.push(vec![]),
            VarVec::U64Vec(f) => f.push(vec![]),
            VarVec::XYVec(f) => f.push(None),
            VarVec::XYZVec(f) => f.push(None),
            VarVec::U32Vec(f) => f.push(vec![]),
            VarVec::Stickers(f) => f.push(Arc::default()),
            VarVec::InventoryWeaponCosmetics(f) => f.push(Arc::default()),
            VarVec::InputHistory(f) => f.push(Arc::default()),
            VarVec::UserCmdSubtickMoves(f) => f.push(Arc::default()),
        }
    }
}
#[allow(dead_code)]
pub fn filter_to_vec<Wanted>(v: impl IntoIterator<Item = impl TryInto<Wanted>>) -> Vec<Wanted> {
    v.into_iter().filter_map(|x| x.try_into().ok()).collect()
}

impl Serialize for Variant {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        match self {
            Variant::Bool(b) => serializer.serialize_bool(*b),
            Variant::F32(f) => serializer.serialize_f32(*f),
            Variant::I32(i) => serializer.serialize_i32(*i),
            Variant::String(s) => serializer.serialize_str(s),
            Variant::U32(u) => serializer.serialize_u32(*u),
            Variant::U64(u) => serializer.serialize_str(&u.to_string()),
            Variant::StringVec(v) => {
                let mut s = serializer.serialize_seq(Some(v.len()))?;
                for item in v {
                    s.serialize_element(item)?;
                }
                s.end()
            }
            Variant::VecXY(v) => {
                let mut s = serializer.serialize_seq(Some(v.len()))?;
                for item in v {
                    s.serialize_element(item)?;
                }
                s.end()
            }
            Variant::VecXYZ(v) => {
                let mut s = serializer.serialize_seq(Some(v.len()))?;
                for item in v {
                    s.serialize_element(item)?;
                }
                s.end()
            }
            Variant::U32Vec(v) => {
                let mut s = serializer.serialize_seq(Some(v.len()))?;
                for item in v {
                    s.serialize_element(item)?;
                }
                s.end()
            }
            Variant::U64Vec(v) => {
                let mut s = serializer.serialize_seq(Some(v.len()))?;
                for item in v {
                    s.serialize_element(&item.to_string())?;
                }
                s.end()
            }
            Variant::Stickers(v) => {
                let mut s = serializer.serialize_seq(Some(v.len()))?;
                for item in v.iter() {
                    s.serialize_element(&item)?;
                }
                s.end()
            }
            Variant::InventoryWeaponCosmetics(v) => {
                let mut s = serializer.serialize_seq(Some(v.len()))?;
                for item in v.iter() {
                    s.serialize_element(&item)?;
                }
                s.end()
            }
            Variant::InputHistory(v) => {
                let mut s = serializer.serialize_seq(Some(v.len()))?;
                for item in v.iter() {
                    s.serialize_element(&item)?;
                }
                s.end()
            }
            Variant::UserCmdSubtickMoves(v) => {
                let mut s = serializer.serialize_seq(Some(v.len()))?;
                for item in v.iter() {
                    s.serialize_element(&item)?;
                }
                s.end()
            }
        }
    }
}

impl Serialize for PlayerEndMetaData {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        let mut state = serializer.serialize_struct("PlayerEndMetaData", 3)?;
        state.serialize_field("name", &self.name)?;
        let steamid = match self.steamid {
            Some(u) => Some(u.to_string()),
            None => None,
        };
        state.serialize_field("steamid", &steamid)?;
        state.serialize_field("team_number", &self.team_number)?;
        state.end()
    }
}
impl Serialize for EconItem {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        let mut state = serializer.serialize_struct("EconItem", 14)?;
        let steamid = match self.steamid {
            Some(u) => Some(u.to_string()),
            None => None,
        };
        state.serialize_field("steamid", &steamid)?;
        state.serialize_field("account_id", &self.account_id)?;
        state.serialize_field("custom_name", &self.custom_name)?;
        state.serialize_field("def_index", &self.def_index)?;
        state.serialize_field("dropreason", &self.dropreason)?;
        state.serialize_field("item_id", &self.item_id)?;
        state.serialize_field("inventory", &self.inventory)?;
        state.serialize_field("item_id", &self.item_id)?;
        state.serialize_field("paint_index", &self.paint_index)?;
        state.serialize_field("paint_seed", &self.paint_seed)?;
        state.serialize_field("paint_wear", &self.paint_wear)?;
        state.serialize_field("quality", &self.quality)?;
        state.serialize_field("quest_id", &self.quest_id)?;
        state.serialize_field("rarity", &self.rarity)?;
        state.serialize_field("item_name", &self.item_name)?;
        state.serialize_field("skin_name", &self.skin_name)?;
        state.end()
    }
}
impl Serialize for ProjectileRecord {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        let mut state = serializer.serialize_struct("ProjectileRecord", 14)?;
        let steamid = match self.steamid {
            Some(u) => Some(u.to_string()),
            None => None,
        };
        state.serialize_field("steamid", &steamid)?;
        state.serialize_field("grenade_type", &self.grenade_type)?;
        state.serialize_field("name", &self.name)?;
        state.serialize_field("tick", &self.tick)?;
        state.serialize_field("x", &self.x)?;
        state.serialize_field("y", &self.y)?;
        state.serialize_field("z", &self.z)?;
        state.serialize_field("entity_id", &self.entity_id)?;
        state.serialize_field("entity_serial", &self.entity_serial)?;
        state.serialize_field("is_incendiary", &self.is_incendiary)?;
        state.serialize_field("initial_position", &self.initial_position)?;
        state.serialize_field("initial_velocity", &self.initial_velocity)?;
        state.serialize_field(
            "smoke_detonation_position",
            &self.smoke_detonation_position,
        )?;
        state.serialize_field("bounces", &self.bounces)?;
        state.end()
    }
}
#[derive(Debug)]
pub enum BytesVariant {
    Mmap(Mmap),
    Vec(Vec<u8>),
}

impl<Idx> std::ops::Index<Idx> for BytesVariant
where
    Idx: std::slice::SliceIndex<[u8]>,
{
    type Output = Idx::Output;
    #[inline(always)]
    fn index(&self, i: Idx) -> &Self::Output {
        match self {
            Self::Mmap(m) => &m[i],
            Self::Vec(v) => &v[i],
        }
    }
}
impl BytesVariant {
    pub fn get_len(&self) -> usize {
        match self {
            Self::Mmap(m) => m.len(),
            Self::Vec(v) => v.len(),
        }
    }
}

#[derive(Debug, Clone)]
pub struct OutputSerdeHelperStruct {
    pub prop_infos: Vec<PropInfo>,
    pub inner: HashMap<u32, PropColumn>,
}
pub fn soa_to_aos(soa: OutputSerdeHelperStruct) -> Vec<std::collections::HashMap<String, Option<Variant>>> {
    let mut total_rows = 0;
    for (_, v) in &soa.inner {
        total_rows = v.len();
    }
    let mut v = Vec::with_capacity(total_rows);
    for idx in 0..total_rows {
        let mut hm: std::collections::HashMap<String, Option<Variant>> = std::collections::HashMap::with_capacity(soa.prop_infos.len());
        for prop_info in &soa.prop_infos {
            if soa.inner.contains_key(&prop_info.id) {
                match &soa.inner[&prop_info.id].data {
                    None => continue,
                    Some(VarVec::F32(val)) => match val.get(idx) {
                        Some(Some(f)) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::F32(*f))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::I32(val)) => match val.get(idx) {
                        Some(Some(f)) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::I32(*f))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::String(val)) => match val.get(idx) {
                        Some(Some(f)) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::String(f.to_string()))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::SharedString(val)) => hm.insert(
                        prop_info.prop_friendly_name.clone(), val.get(idx).map(|value| Variant::String(value.to_owned())),
                    ),
                    Some(VarVec::U64(val)) => match val.get(idx) {
                        Some(Some(f)) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::String(f.to_string()))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::Bool(val)) => match val.get(idx) {
                        Some(Some(f)) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::Bool(*f))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::U32(val)) => match val.get(idx) {
                        Some(Some(f)) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::U32(*f))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::StringVec(val)) => match val.get(idx) {
                        Some(f) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::StringVec(f.clone()))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::U64Vec(val)) => match val.get(idx) {
                        Some(f) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::U64Vec(f.clone()))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::U32Vec(val)) => match val.get(idx) {
                        Some(f) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::U32Vec(f.clone()))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::XYVec(val)) => match val.get(idx) {
                        Some(Some(f)) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::VecXY(f.clone()))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::XYZVec(val)) => match val.get(idx) {
                        Some(Some(f)) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::VecXYZ(f.clone()))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::Stickers(val)) => match val.get(idx) {
                        Some(f) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::Stickers(f.clone()))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::InventoryWeaponCosmetics(val)) => match val.get(idx) {
                        Some(f) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::InventoryWeaponCosmetics(f.clone()))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::InputHistory(val)) => match val.get(idx) {
                        Some(f) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::InputHistory(f.clone()))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                    Some(VarVec::UserCmdSubtickMoves(val)) => match val.get(idx) {
                        Some(f) => hm.insert(prop_info.prop_friendly_name.clone(), Some(Variant::UserCmdSubtickMoves(f.clone()))),
                        _ => hm.insert(prop_info.prop_friendly_name.clone(), None),
                    },
                };
            }
        }
        v.push(hm);
    }
    v
}

impl Serialize for OutputSerdeHelperStruct {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        let mut map = serializer.serialize_map(Some(self.prop_infos.len()))?;

        for prop_info in &self.prop_infos {
            if self.inner.contains_key(&prop_info.id) {
                match &self.inner[&prop_info.id].data {
                    None => {}
                    Some(VarVec::F32(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::I32(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::String(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::SharedString(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::U64(val)) => {
                        let as_str: Vec<Option<String>> = val
                            .iter()
                            .map(|x| match x {
                                Some(u) => Some(u.to_string()),
                                None => None,
                            })
                            .collect_vec();
                        map.serialize_entry(&prop_info.prop_friendly_name, &as_str)?;
                    }
                    Some(VarVec::Bool(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::U32(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::StringVec(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::U32Vec(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::XYVec(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::XYZVec(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::Stickers(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::InventoryWeaponCosmetics(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::InputHistory(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::UserCmdSubtickMoves(val)) => {
                        map.serialize_entry(&prop_info.prop_friendly_name, val)?;
                    }
                    Some(VarVec::U64Vec(val)) => {
                        let string_sid = val
                            .iter()
                            .map(|v| {
                                let as_sid = v.iter().map(|s| s.to_string()).collect_vec();
                                as_sid
                            })
                            .collect_vec();
                        map.serialize_entry(&prop_info.prop_friendly_name, &string_sid)?;
                    }
                }
            }
        }
        map.end()
    }
}

#[cfg(test)]
mod shared_string_tests {
    use super::{PropColumn, VarVec, Variant};

    fn shared(values: &[Option<&str>]) -> PropColumn {
        let mut column = PropColumn::new();
        for value in values { column.push_shared_string(*value); }
        column
    }

    fn owned(values: &[Option<&str>]) -> PropColumn {
        let mut column = PropColumn::new();
        for value in values { column.push(value.map(|value| Variant::String(value.to_owned()))); }
        column
    }

    #[test]
    fn string_dictionary_preserves_null_empty_unicode_and_shares_values() {
        let values = [None, Some(""), Some("玩家"), Some("same"), None, Some("same")];
        let column = shared(&values);
        assert_eq!(column, owned(&values));
        let Some(VarVec::SharedString(strings)) = &column.data else { panic!("expected dictionary"); };
        assert_eq!(strings.values.len(), 3);
        assert_eq!(strings.get(99), None);
        assert_eq!(serde_json::to_vec(strings).unwrap(), serde_json::to_vec(&values).unwrap());
        assert_eq!(column.slice_to_new(&[5, 0, 1, 2, 5]).unwrap(), owned(&[Some("same"), None, Some(""), Some("玩家"), Some("same")]));
        assert_eq!(shared(&[None, None]), PropColumn { data: None, num_nones: 2 });
    }

    #[test]
    fn string_dictionary_remaps_partition_ids_and_accepts_owned_columns() {
        for destination_shared in [false, true] {
            for source_shared in [false, true] {
                let mut left = if destination_shared { shared(&[Some("a"), None, Some("b")]) } else { owned(&[Some("a"), None, Some("b")]) };
                let mut right = if source_shared { shared(&[Some("b"), Some("c"), Some("a"), Some("")]) } else { owned(&[Some("b"), Some("c"), Some("a"), Some("")]) };
                left.extend_from(&mut right);
                assert_eq!(right.len(), 0);
                left.extend_from(&mut shared(&[None, None]));
                assert_eq!(left, owned(&[Some("a"), None, Some("b"), Some("b"), Some("c"), Some("a"), Some(""), None, None]));
            }
        }
        let mut leading = shared(&[None, None]);
        leading.extend_from(&mut shared(&[Some("x"), None]));
        assert_eq!(leading.len(), 4);
        assert_eq!(leading.data, owned(&[None, None, Some("x"), None]).data);
    }

    #[test]
    fn numeric_strings_preserve_full_u64_and_signed_fallbacks() {
        let mut column = shared(&[None]);
        column.push_shared_u64(u64::MAX);
        column.push_shared_u64(u64::MAX);
        column.push_shared_i32(-1);
        column.push_shared_i32(0);
        column.push_shared_u64(0);
        assert_eq!(column, owned(&[None, Some("18446744073709551615"), Some("18446744073709551615"), Some("-1"), Some("0"), Some("0")]));
        let Some(VarVec::SharedString(strings)) = column.data else { panic!("expected dictionary"); };
        assert_eq!(strings.values.len(), 3);
        assert_eq!(strings.numeric_ids.len(), 4);
    }
}

#[cfg(test)]
mod tests {
    use super::{into_shared_slice, InputHistory, InputHistoryInterpolation, InventoryWeaponCosmetic, PropColumn, Sticker, UserCmdSubtickMove, VarVec, Variant};
    use serde::Serialize;
    use std::sync::Arc;

    fn assert_shared_snapshot_rows<T: Serialize>(
        rows: &[Arc<[T]>],
        snapshot: &Arc<[T]>,
        present: &[bool],
    ) {
        assert_eq!(rows.len(), present.len());
        let expected: Vec<&[T]> = present
            .iter()
            .map(|is_present| if *is_present { snapshot.as_ref() } else { &[] })
            .collect();
        assert_eq!(serde_json::to_vec(rows).unwrap(), serde_json::to_vec(&expected).unwrap());
        for (row, is_present) in rows.iter().zip(present) {
            if *is_present {
                if !snapshot.is_empty() {
                    assert!(Arc::ptr_eq(row, snapshot));
                }
            } else {
                assert!(row.is_empty());
            }
        }
    }

    #[test]
    fn empty_snapshot_writes_and_missing_rows_preserve_empty_sequences() {
        for snapshot in [
            Variant::Stickers(into_shared_slice(Vec::new())),
            Variant::InventoryWeaponCosmetics(into_shared_slice(Vec::new())),
            Variant::InputHistory(into_shared_slice(Vec::new())),
            Variant::UserCmdSubtickMoves(into_shared_slice(Vec::new())),
        ] {
            let mut column = PropColumn { data: None, num_nones: 2 };
            column.push(Some(snapshot.clone()));
            column.push(None);
            column.extend_from(&mut PropColumn { data: None, num_nones: 2 });
            match (&snapshot, &column.data) {
                (Variant::Stickers(empty), Some(VarVec::Stickers(rows))) => {
                    assert_shared_snapshot_rows(rows, empty, &[true; 6]);
                }
                (Variant::InventoryWeaponCosmetics(empty), Some(VarVec::InventoryWeaponCosmetics(rows))) => {
                    assert_shared_snapshot_rows(rows, empty, &[true; 6]);
                }
                (Variant::InputHistory(empty), Some(VarVec::InputHistory(rows))) => {
                    assert_shared_snapshot_rows(rows, empty, &[true; 6]);
                }
                (Variant::UserCmdSubtickMoves(empty), Some(VarVec::UserCmdSubtickMoves(rows))) => {
                    assert_shared_snapshot_rows(rows, empty, &[true; 6]);
                }
                _ => unreachable!(),
            }
        }
    }

    #[test]
    fn nested_snapshots_share_storage_and_serialize_as_sequences() {
        let history = vec![InputHistory {
            view_angles: Some([1.0, 2.0, 3.0]),
            render_tick_count: Some(101),
            render_tick_fraction: Some(0.125),
            player_tick_count: Some(100),
            player_tick_fraction: Some(0.5),
            cl_interp_fraction: None,
            sv_interp0: Some(InputHistoryInterpolation {
                src_tick: Some(98),
                dst_tick: Some(99),
                fraction: Some(0.25),
            }),
            sv_interp1: None,
            player_interp: None,
            frame_number: Some(12),
            target_ent_index: Some(7),
            shoot_position: Some([4.0, 5.0, 6.0]),
            target_head_pos_check: None,
            target_abs_pos_check: None,
            target_abs_ang_check: None,
        }];
        let subticks = vec![UserCmdSubtickMove {
            when: 0.75,
            button: 1_u64 << 40,
            pressed: true,
            analog_forward: 0.25,
            analog_left: -0.5,
            pitch_delta: 1.5,
            yaw_delta: -2.5,
        }];
        let history_json = serde_json::to_vec(&history).unwrap();
        let subticks_json = serde_json::to_vec(&subticks).unwrap();
        let stickers = vec![Sticker {
            slot: 1,
            name: "shared sticker name".to_owned(),
            wear: 0.125,
            id: 477,
            x: 0.25,
            y: -0.5,
            scale: Some(0.75),
            rotation: None,
        }];
        let stickers_json = serde_json::to_vec(&stickers).unwrap();

        for (snapshot, expected_json) in [
            (Variant::InputHistory(into_shared_slice(history)), history_json),
            (Variant::UserCmdSubtickMoves(into_shared_slice(subticks)), subticks_json),
            (Variant::Stickers(into_shared_slice(stickers)), stickers_json),
        ] {
            assert_eq!(serde_json::to_vec(&snapshot).unwrap(), expected_json);
            let clone = snapshot.clone();
            match (&snapshot, &clone) {
                (Variant::InputHistory(original), Variant::InputHistory(cloned)) => {
                    assert!(Arc::ptr_eq(original, cloned));
                }
                (Variant::UserCmdSubtickMoves(original), Variant::UserCmdSubtickMoves(cloned)) => {
                    assert!(Arc::ptr_eq(original, cloned));
                }
                (Variant::Stickers(original), Variant::Stickers(cloned)) => {
                    assert!(Arc::ptr_eq(original, cloned));
                }
                _ => unreachable!(),
            }

            let mut source = PropColumn::new();
            source.push(None);
            source.push(Some(clone));
            source.push(Some(snapshot.clone()));
            source.push(None);
            let mut destination = PropColumn { data: None, num_nones: 1 };
            destination.extend_from(&mut source);
            destination.extend_from(&mut PropColumn { data: None, num_nones: 1 });
            assert_eq!(source.len(), 0);
            let sliced = destination.slice_to_new(&[3, 0, 2, 4, 5]).unwrap();

            match (&snapshot, &destination.data, &sliced.data) {
                (Variant::InputHistory(snapshot), Some(VarVec::InputHistory(rows)), Some(VarVec::InputHistory(sliced_rows))) => {
                    assert_shared_snapshot_rows(rows, snapshot, &[false, false, true, true, false, false]);
                    assert_shared_snapshot_rows(sliced_rows, snapshot, &[true, false, true, false, false]);
                }
                (Variant::UserCmdSubtickMoves(snapshot), Some(VarVec::UserCmdSubtickMoves(rows)), Some(VarVec::UserCmdSubtickMoves(sliced_rows))) => {
                    assert_shared_snapshot_rows(rows, snapshot, &[false, false, true, true, false, false]);
                    assert_shared_snapshot_rows(sliced_rows, snapshot, &[true, false, true, false, false]);
                }
                (Variant::Stickers(snapshot), Some(VarVec::Stickers(rows)), Some(VarVec::Stickers(sliced_rows))) => {
                    assert_shared_snapshot_rows(rows, snapshot, &[false, false, true, true, false, false]);
                    assert_shared_snapshot_rows(sliced_rows, snapshot, &[true, false, true, false, false]);
                }
                _ => unreachable!(),
            }
        }
    }

    #[test]
    fn extend_from_moves_nested_cosmetics_after_leading_nones() {
        let weapon = InventoryWeaponCosmetic {
            item_def_index: 7,
            item_id_high: Some(1),
            item_id_low: Some(2),
            item_account_id: Some(3),
            original_owner_xuid: Some(4),
            paint_kit: 5,
            paint_seed: 6,
            paint_wear: 0.125,
            entity_quality: Some(7),
            stattrak_counter: Some(8),
            attributes: Vec::new(),
            custom_name: Some("kept".to_string()),
            stickers: Vec::new(),
        };
        let mut destination = PropColumn {
            data: None,
            num_nones: 2,
        };
        let mut source = PropColumn {
            data: Some(VarVec::InventoryWeaponCosmetics(vec![Arc::from([
                weapon.clone(),
            ])])),
            num_nones: 0,
        };

        destination.extend_from(&mut source);

        assert_eq!(
            destination.data,
            Some(VarVec::InventoryWeaponCosmetics(vec![
                Arc::from([]),
                Arc::from([]),
                Arc::from([weapon]),
            ]))
        );
        assert_eq!(
            source.data,
            Some(VarVec::InventoryWeaponCosmetics(Vec::new()))
        );
    }
}
