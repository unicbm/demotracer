use crate::first_pass::prop_controller::ITEM_PURCHASE_DEF_IDX;
use crate::first_pass::prop_controller::GLOVE_ATTRIBUTE_DEF_INDEX_ID;
use crate::first_pass::prop_controller::GLOVE_PAINT_ID;
use crate::first_pass::prop_controller::WEAPON_ATTRIBUTE_DEF_INDEX_ID;
use crate::first_pass::prop_controller::WEAPON_SKIN_ID;
use crate::first_pass::prop_controller::{FLASHBANG_AMMO_ID, MY_WEAPONS_OFFSET};
use crate::first_pass::prop_controller::is_grenade_or_weapon;
use crate::first_pass::read_bits::Bitreader;
use crate::first_pass::read_bits::DemoParserError;
use crate::first_pass::sendtables::find_field;
use crate::first_pass::sendtables::get_decoder_from_field;
use crate::first_pass::sendtables::get_propinfo;
use crate::first_pass::sendtables::Field;
use crate::first_pass::sendtables::FieldInfo;
use crate::second_pass::game_events::GameEventInfo;
use crate::second_pass::other_netmessages::Class;
use crate::second_pass::parser_settings::SecondPassParser;
use crate::second_pass::parser_settings::SpecialIDs;
use crate::second_pass::path_ops::*;
use crate::second_pass::variants::Variant;
use ahash::AHashMap;
use csgoproto::CsvcMsgPacketEntities;
use prost::Message;

const NSERIALBITS: u32 = 17;
const STOP_READING_SYMBOL: u8 = 39;
const HUFFMAN_CODE_MAXLEN: u32 = 17;
const ECON_ATTRIBUTE_SLOTS: u32 = 64;

#[derive(Clone, Copy)]
pub(crate) struct ResolvedEntityField<'a> {
    field: &'a Field,
    info: Option<FieldInfo>,
    decoder: super::decoder::Decoder,
    skip: bool,
}

fn field_cache_key(path: &FieldPath) -> Option<u64> {
    if path.last > 5 {
        return None;
    }
    let mut key = path.last as u64;
    // Preserve every component used by get_propinfo, including components
    // beyond last; dynamic vector indices must never alias a cached field.
    for &component in &path.path[..6] {
        if !(0..1024).contains(&component) {
            return None;
        }
        key = (key << 10) | component as u64;
    }
    Some(key)
}

#[derive(Debug, Clone)]
pub struct Entity {
    pub cls_id: u32,
    pub entity_id: i32,
    pub serial: u32,
    pub props: AHashMap<u32, Variant>,
    pub entity_type: EntityType,
    pub cosmetic_revision: u64,
    // Synthetic usercmd scalars are written separately from network fields.
    pub usercmd_scalar_cache: Option<Box<[u64; 23]>>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct PlayerMetaData {
    pub player_entity_id: Option<i32>,
    pub steamid: Option<u64>,
    pub controller_entid: Option<i32>,
    pub name: Option<String>,
    pub team_num: Option<u32>,
}
#[derive(Debug, Clone, PartialEq)]
pub enum EntityType {
    PlayerController,
    Rules,
    Projectile,
    Team,
    Normal,
    C4,
}
enum EntityCmd {
    Delete,
    CreateAndUpdate,
    Update,
}

fn is_cosmetic_prop(prop_id: u32, special_ids: &SpecialIDs) -> bool {
    (WEAPON_SKIN_ID..WEAPON_SKIN_ID + ECON_ATTRIBUTE_SLOTS).contains(&prop_id)
        || (WEAPON_ATTRIBUTE_DEF_INDEX_ID
            ..WEAPON_ATTRIBUTE_DEF_INDEX_ID + ECON_ATTRIBUTE_SLOTS)
            .contains(&prop_id)
        || (GLOVE_PAINT_ID..GLOVE_PAINT_ID + ECON_ATTRIBUTE_SLOTS).contains(&prop_id)
        || (GLOVE_ATTRIBUTE_DEF_INDEX_ID
            ..GLOVE_ATTRIBUTE_DEF_INDEX_ID + ECON_ATTRIBUTE_SLOTS)
            .contains(&prop_id)
        || [
            special_ids.item_def,
            special_ids.item_id_high,
            special_ids.item_id_low,
            special_ids.item_account_id,
            special_ids.orig_own_low,
            special_ids.orig_own_high,
            special_ids.entity_quality,
            special_ids.fallback_stattrak,
            special_ids.custom_name,
        ]
        .into_iter()
        .flatten()
        .any(|id| id == prop_id)
}

fn changes_inventory_snapshot(
    field: &Field,
    field_info: Option<FieldInfo>,
    updates_cosmetics: bool,
    special_ids: &SpecialIDs,
) -> bool {
    let Some(fi) = field_info.filter(|fi| fi.should_parse) else {
        return false;
    };
    updates_cosmetics
        || fi.prop_id == MY_WEAPONS_OFFSET
        || fi.prop_id == FLASHBANG_AMMO_ID
        || [
            special_ids.life_state,
            special_ids.h_owner_entity,
            special_ids.teamnum,
            special_ids.player_name,
            special_ids.steamid,
            special_ids.player_pawn,
        ].contains(&Some(fi.prop_id))
        // get_propinfo flattens every inventory slot to a different property ID.
        // Use the schema marker, without assuming a maximum inventory length.
        || match field {
            Field::Value(value) => value.prop_id == MY_WEAPONS_OFFSET,
            Field::Vector(vector) => matches!(vector.field_enum.as_ref(),
                Field::Value(value) if value.prop_id == MY_WEAPONS_OFFSET),
            _ => false,
        }
}

fn econ_attribute_vector_bases(field: &Field) -> Option<(u32, u32)> {
    let Field::Vector(vector) = field else {
        return None;
    };
    let Field::Serializer(serializer) = vector.field_enum.as_ref() else {
        return None;
    };

    let mut has_weapon_values = false;
    let mut has_weapon_definitions = false;
    let mut has_glove_values = false;
    let mut has_glove_definitions = false;
    for field in &serializer.serializer.fields {
        let Field::Value(value) = field else {
            continue;
        };
        match value.prop_id {
            WEAPON_SKIN_ID => has_weapon_values = true,
            WEAPON_ATTRIBUTE_DEF_INDEX_ID => has_weapon_definitions = true,
            GLOVE_PAINT_ID => has_glove_values = true,
            GLOVE_ATTRIBUTE_DEF_INDEX_ID => has_glove_definitions = true,
            _ => {}
        }
    }

    if has_weapon_values && has_weapon_definitions {
        Some((WEAPON_SKIN_ID, WEAPON_ATTRIBUTE_DEF_INDEX_ID))
    } else if has_glove_values && has_glove_definitions {
        Some((GLOVE_PAINT_ID, GLOVE_ATTRIBUTE_DEF_INDEX_ID))
    } else {
        None
    }
}

fn truncate_econ_attribute_vector(entity: &mut Entity, field: &Field, result: &Variant) -> bool {
    let Some((value_base, definition_base)) = econ_attribute_vector_bases(field) else {
        return false;
    };
    let Variant::U32(length) = result else {
        return false;
    };

    let mut removed = false;
    for slot in (*length).min(ECON_ATTRIBUTE_SLOTS)..ECON_ATTRIBUTE_SLOTS {
        removed |= entity.props.remove(&(value_base + slot)).is_some();
        removed |= entity.props.remove(&(definition_base + slot)).is_some();
    }
    if removed {
        entity.cosmetic_revision = entity.cosmetic_revision.wrapping_add(1);
    }
    removed
}

impl<'a> SecondPassParser<'a> {
    pub(crate) fn invalidate_inventory_snapshots(&mut self) {
        self.inventory_generation = self.inventory_generation.wrapping_add(1);
    }

    pub fn parse_packet_ents(&mut self, bytes: &[u8], is_fullpacket: bool) -> Result<(), DemoParserError> {
        if !self.parse_entities {
            return Ok(());
        }
        let msg = match CsvcMsgPacketEntities::decode(bytes) {
            Err(_) => return Err(DemoParserError::MalformedMessage),
            Ok(msg) => msg,
        };

        let mut bitreader = Bitreader::new(msg.entity_data());
        let mut entity_id: i32 = -1;
        let mut events_to_emit = vec![];
        for _ in 0..msg.updated_entries() {
            entity_id += 1 + (bitreader.read_u_bit_var()? as i32);
            // Read 2 bits to know which operation should be done to the entity.
            let cmd = match bitreader.read_nbits(2)? {
                0b01 => EntityCmd::Delete,
                0b11 => EntityCmd::Delete,
                0b10 => EntityCmd::CreateAndUpdate,
                0b00 => EntityCmd::Update,
                _ => return Err(DemoParserError::ImpossibleCmd),
            };

            match cmd {
                EntityCmd::Delete => {
                    self.invalidate_inventory_snapshots();
                    if let Some(sparse) = self.sparse_scalar_columns.as_mut() {
                        sparse.reset_entity(entity_id);
                    }
                    self.projectiles.remove(&entity_id);
                    self.projectile_record_indices.remove(&entity_id);
                    self.weapon_econ_snapshot_cache.get_mut().remove(&entity_id);
                    self.weapon_sticker_cache.get_mut().remove(&entity_id);
                    self.glove_attribute_cache.get_mut().remove(&entity_id);
                    if let Some(entry) = self.entities.get_mut(entity_id as usize) {
                        *entry = None;
                    }
                }
                EntityCmd::CreateAndUpdate => {
                    self.create_new_entity(&mut bitreader, &entity_id, &mut events_to_emit)?;
                    self.update_entity(&mut bitreader, entity_id, false, &mut events_to_emit, is_fullpacket)?;
                }
                EntityCmd::Update => {
                    if msg.has_pvs_vis_bits_deprecated() != 0 {
                        // Most entities pass trough here. Seems like entities that are not updated.
                        if bitreader.read_nbits(2)? & 0x01 == 1 {
                            continue;
                        }
                    }
                    self.update_entity(&mut bitreader, entity_id, false, &mut events_to_emit, is_fullpacket)?;
                }
            }
        }
        if !events_to_emit.is_empty() {
            self.emit_events(events_to_emit)?;
        }
        Ok(())
    }

    pub fn update_entity(
        &mut self,
        bitreader: &mut Bitreader,
        entity_id: i32,
        is_baseline: bool,
        events_to_emit: &mut Vec<GameEventInfo>,
        is_fullpacket: bool,
    ) -> Result<(), DemoParserError> {
        let n_updates = self.parse_paths(bitreader)?;
        let n_updated_values = self.decode_entity_update(bitreader, entity_id, n_updates, is_fullpacket, is_baseline, events_to_emit)?;
        if n_updated_values > 0 {
            self.gather_extra_info(&entity_id, is_baseline)?;
        }
        Ok(())
    }
    pub fn parse_paths(&mut self, bitreader: &mut Bitreader) -> Result<usize, DemoParserError> {
        /*
        Create a field path by decoding using a Huffman tree.
        The huffman tree can be found at the bottom of entities_utils.rs

        A field path is a "path trough a struct" where
        the struct can have normal fields but also pointers
        to other (nested) structs.

        Example:

        The array will be filled with these:

        Struct Field{
            wanted_information: Option<T>,
            Pointer: bool,
            fields: Option<Vec<Field>>
        },

        (struct is simplified for this example. In reality it also includes field name etc.)


        Path to each of the fields in the below fields list: [
            [0], [1, 0], [1, 1], [2]
        ]
        and they would map to:
        [0] => FloatDecoder,
        [1, 0] => IntegerDecoder,
        [1, 1] => StringDecoder,
        [2] => VectorDecoder,

        fields = [
            Field{
                wanted_information: FloatDecoder,
                pointer: false,
                fields: None,
            },
            Field{
                wanted_information: None,
                pointer: true,
                fields: Some(
                    [
                        Field{
                            wanted_information: IntegerDecoder,
                            pointer: false,
                            fields: Some(
                        },
                        Field{
                            wanted_information: StringDecoder,
                            pointer: flase,
                            fields: Some(
                        }
                    ]
                ),
            },
            Field{
                wanted_information: VectorDecoder,
                pointer: false,
                fields: None,
            },
        ]
        Not sure what the maximum depth of these structs are, but others seem to use
        7 as the max length of field path so maybe that?

        Personally I find this path idea horribly complicated. Why is this chosen over
        the way it was done in source 1 demos?
        */

        // Create an "empty" path ([-1, 0, 0, 0, 0, 0, 0])
        // For perfomance reasons have them always the same len
        let mut fp = generate_fp();
        let mut idx = 0;
        // Do huffman decoding with a lookup table instead of reading one bit at a time
        // and traversing a tree.
        // Here we peek ("HUFFMAN_CODE_MAXLEN" == 17) amount of bits and see from a table which
        // symbol it maps to and how many bits should be consumed from the stream.
        // The symbol is then mapped into an op for filling the field path.
        if self.huffman_lookup_table.len() < (1usize << HUFFMAN_CODE_MAXLEN) {
            return Err(DemoParserError::MalformedMessage);
        }
        loop {
            if bitreader.bits_left < HUFFMAN_CODE_MAXLEN {
                bitreader.refill();
            }

            let peeked_bits = bitreader.peek(HUFFMAN_CODE_MAXLEN);
            // SAFETY: peek() returns at most HUFFMAN_CODE_MAXLEN bits, and the table length was
            // checked above to cover every value in that bit range.
            let (symbol, code_len) =
                unsafe { *self.huffman_lookup_table.get_unchecked(peeked_bits as usize) };
            bitreader.consume(code_len as u32);
            if symbol == STOP_READING_SYMBOL {
                break;
            }
            do_op(symbol, bitreader, &mut fp)?;
            self.write_fp(&mut fp, idx)?;
            idx += 1;
        }
        Ok(idx)
    }

    pub fn decode_entity_update(
        &mut self,
        bitreader: &mut Bitreader,
        entity_id: i32,
        n_updates: usize,
        is_fullpacket: bool,
        is_baseline: bool,
        events_to_emit: &mut Vec<GameEventInfo>,
    ) -> Result<usize, DemoParserError> {
        let entity = match self.entities.get_mut(entity_id as usize) {
            Some(Some(entity)) => entity,
            _ => return Err(DemoParserError::EntityNotFound),
        };
        let class = match self.cls_by_id.get(entity.cls_id as usize) {
            Some(cls) => cls,
            None => return Err(DemoParserError::ClassNotFound),
        };

        for path in self.paths.iter().take(n_updates) {
            let key = field_cache_key(path);
            let cache = &mut self.entity_field_cache[entity.cls_id as usize];
            let resolved = match key.and_then(|key| cache.get(&key)).copied() {
                Some(resolved) => resolved,
                None => {
                    let field = find_field(path, &class.serializer)?;
                    let info = get_propinfo(field, path);
                    let decoder = get_decoder_from_field(field)?;
                    let skip = self.entity_projection.as_ref().is_some_and(|plan|
                        info.is_some_and(|info| !plan.keeps(info.prop_id)));
                    let resolved = ResolvedEntityField { field, info, decoder, skip };
                    if let Some(key) = key {
                        // Bound memory on demos containing large dynamic arrays.
                        if cache.len() < 4096 { cache.insert(key, resolved); }
                    }
                    resolved
                }
            };
            let ResolvedEntityField { field, info: field_info, decoder, skip } = resolved;
            if skip {
                bitreader.skip_value(&decoder, self.qf_mapper)?;
                continue;
            }
            let result = bitreader.decode(&decoder, self.qf_mapper)?;

            // A vector-of-serializer length has no FieldInfo, so the generic flattened-prop
            // path cannot retain it. Apply its shrink semantics directly or personalized econ
            // attributes from the class baseline survive past the instance vector's real end.
            if truncate_econ_attribute_vector(entity, field, &result) {
                self.inventory_generation = self.inventory_generation.wrapping_add(1);
            }

            // listen_to_props()
            if self.list_props {
                if let Field::Value(_v) = field {
                    if should_emit_prop_to_listen(&_v.full_name) {
                        self.uniq_prop_names.insert(convert_weapon_prefix_to_general(&_v.full_name));
                    }
                }
            }
            // Custom events
            if !is_baseline {
                SecondPassParser::listen_for_events(
                    entity,
                    &result,
                    field,
                    field_info,
                    &self.prop_controller,
                    &self.prop_controller.special_ids,
                    is_fullpacket,
                    events_to_emit,
                );
            }
            // Debug
            if self.is_debug_mode {
                SecondPassParser::debug_inspect(
                    &result,
                    field,
                    self.tick,
                    field_info,
                    path,
                    is_fullpacket,
                    is_baseline,
                    class,
                    &entity.cls_id,
                    &entity_id,
                );
            }

            let updates_cosmetics = field_info
                .map(|fi| is_cosmetic_prop(fi.prop_id, &self.prop_controller.special_ids))
                .unwrap_or(false);
            if changes_inventory_snapshot(field, field_info, updates_cosmetics, &self.prop_controller.special_ids) {
                self.inventory_generation = self.inventory_generation.wrapping_add(1);
            }
            // Source 2 entity updates are deltas against the class baseline. Econ fields
            // that equal the baseline are intentionally omitted from the entity delta, so
            // the baseline must be applied before the entity-specific values overwrite it.
            if let (Some(sparse), Some(fi)) = (self.sparse_scalar_columns.as_mut(), field_info) {
                if fi.should_parse {
                    sparse.record(entity_id, fi.prop_id, &result);
                }
            }
            SecondPassParser::insert_field(entity, result, field_info, updates_cosmetics);
        }
        Ok(n_updates)
    }

    pub fn debug_inspect(
        _result: &Variant,
        field: &Field,
        _tick: i32,
        field_info: Option<FieldInfo>,
        _path: &FieldPath,
        _is_fullpacket: bool,
        _is_baseline: bool,
        _cls: &Class,
        _cls_id: &u32,
        _entity_id: &i32,
    ) {
        if let Field::Value(_v) = field {
            println!("{:?} {:?} {:?} {:?} {:?}", _path, field_info, _v.full_name, _result, _cls.name);
        }
    }

    pub fn insert_field(
        entity: &mut Entity,
        result: Variant,
        field_info: Option<FieldInfo>,
        updates_cosmetics: bool,
    ) {
        if let Some(fi) = field_info {
            if fi.should_parse {
                if fi.prop_id == crate::first_pass::prop_controller::USERCMD_CLIENT_TICK
                    || fi.prop_id.checked_sub(crate::first_pass::prop_controller::USERCMD_VIEWANGLE_X)
                        .is_some_and(|slot| slot < 22)
                {
                    entity.usercmd_scalar_cache = None;
                }
                entity.props.insert(fi.prop_id, result);
                if updates_cosmetics {
                    entity.cosmetic_revision = entity.cosmetic_revision.wrapping_add(1);
                }
            }
        }
    }

    #[inline]
    fn write_fp(&mut self, fp_src: &mut FieldPath, idx: usize) -> Result<(), DemoParserError> {
        match self.paths.get_mut(idx) {
            Some(entry) => *entry = *fp_src,
            // need to extend vec (rare)
            None => {
                // If we have over 100k fields for an entity then something definitely went wrong. Do this to avoid infinite loop/oom
                if idx > 100_000 {
                    return Err(DemoParserError::VectorResizeFailure);
                }
                self.paths.resize(idx + 1, generate_fp());
                match self.paths.get_mut(idx) {
                    Some(entry) => *entry = *fp_src,
                    None => return Err(DemoParserError::VectorResizeFailure),
                }
            }
        }
        Ok(())
    }
    fn create_new_entity(&mut self, bitreader: &mut Bitreader, entity_id: &i32, _events_to_emit: &mut Vec<GameEventInfo>) -> Result<(), DemoParserError> {
        // Class id width is dynamic: ceil(log2(num_classes + 1)). Hardcoded 8 bits
        // capped at 256 classes and broke on patches with more (14154+), causing
        // bitstream desync and cascading EntityNotFound errors. cls_by_id.len()
        // already equals num_classes + 1 (see first_pass::parser::parse_class_info).
        let cls_bits = (self.cls_by_id.len() as f32).log2().ceil() as u32;
        let cls_id: u32 = bitreader.read_nbits(cls_bits)?;
        // Both of these are not used. Don't think they are interesting for the parser
        let serial = bitreader.read_nbits(NSERIALBITS)?;
        let _unknown = bitreader.read_varint();
        let entity_type = self.check_entity_type(&cls_id)?;
        self.invalidate_inventory_snapshots();
        match entity_type {
            EntityType::Projectile => {
                self.projectiles.insert(*entity_id);
            }
            EntityType::Rules => self.rules_entity_id = Some(*entity_id),
            EntityType::C4 => self.c4_entity_id = Some(*entity_id),
            _ => {}
        };
        let entity = Entity {
            entity_id: *entity_id,
            cls_id,
            serial,
            props: AHashMap::with_capacity(0),
            entity_type,
            cosmetic_revision: 0, usercmd_scalar_cache: None,
        };
        self.weapon_econ_snapshot_cache.get_mut().remove(entity_id);
        self.weapon_sticker_cache.get_mut().remove(entity_id);
        self.glove_attribute_cache.get_mut().remove(entity_id);
        if self.entities.len() as i32 <= *entity_id {
            // if corrupt, this can cause oom allocations
            if *entity_id > 100000 {
                return Err(DemoParserError::EntityNotFound);
            }
            self.entities.resize(*entity_id as usize + 1, None);
        }
        match self.entities.get_mut(*entity_id as usize) {
            Some(entry) => *entry = Some(entity),
            None => return Err(DemoParserError::VectorResizeFailure),
        };
        if let Some(sparse) = self.sparse_scalar_columns.as_mut() {
            sparse.reset_entity(*entity_id);
        }
        // Insert baselines
        if let Some(baseline_bytes) = self.baselines.get(&cls_id) {
            let b = &baseline_bytes.clone();
            let mut br = Bitreader::new(&b);
            self.update_entity(&mut br, *entity_id, true, &mut vec![], false)?;
        }
        Ok(())
    }

    pub fn check_entity_type(&self, cls_id: &u32) -> Result<EntityType, DemoParserError> {
        let class = match self.cls_by_id.get(*cls_id as usize) {
            Some(cls) => cls,
            None => {
                return Err(DemoParserError::ClassNotFound);
            }
        };
        match class.name.as_str() {
            "CCSPlayerController" => return Ok(EntityType::PlayerController),
            "CCSGameRulesProxy" => return Ok(EntityType::Rules),
            "CCSTeam" => return Ok(EntityType::Team),
            "CC4" => return Ok(EntityType::C4),
            _ => {}
        }
        let is_projectile_prop =
            (class.name.contains("Projectile") || class.name.contains("Grenade") || class.name.contains("Flash")) && !class.name.contains("Player");
        if is_projectile_prop {
            return Ok(EntityType::Projectile);
        }
        return Ok(EntityType::Normal);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::first_pass::sendtables::{Serializer, SerializerField, ValueField, VectorField};
    use crate::second_pass::decoder::Decoder;

    #[test]
    fn field_cache_keys_distinguish_depth_and_dynamic_indices() {
        let mut path = generate_fp();
        path.path = [0; 7];
        let mut keys = ahash::AHashSet::default();
        for depth in 0..6 {
            path.last = depth;
            for component in 0..6 {
                for value in [1, 255, 1023] {
                    path.path[component] = value;
                    assert!(keys.insert(field_cache_key(&path).unwrap()));
                    path.path[component] = 0;
                }
            }
        }
        path.last = 6;
        assert!(field_cache_key(&path).is_none());
        path.last = 0;
        for value in [-1, 1024] {
            path.path[2] = value;
            assert!(field_cache_key(&path).is_none());
        }
    }

    fn econ_attribute_vector(value_prop_id: u32, definition_prop_id: u32) -> Field {
        let mut definition = ValueField::new(Decoder::UnsignedDecoder, "m_iAttributeDefinitionIndex");
        definition.prop_id = definition_prop_id;
        definition.should_parse = true;
        let mut value = ValueField::new(Decoder::NoscaleDecoder, "m_iRawValue32");
        value.prop_id = value_prop_id;
        value.should_parse = true;
        let attributes = Serializer {
            name: "CEconItemAttribute".to_string(),
            fields: vec![Field::Value(definition), Field::Value(value)],
        };
        Field::Vector(VectorField::new(Field::Serializer(SerializerField::new(&attributes)), None))
    }

    #[test]
    fn inventory_dependencies_cover_metadata_cosmetics_and_unbounded_weapon_slots() {
        let mut ids = SpecialIDs::new();
        ids.life_state = Some(101);
        ids.h_owner_entity = Some(102);
        ids.teamnum = Some(103);
        ids.player_name = Some(104);
        ids.steamid = Some(105);
        ids.player_pawn = Some(106);
        ids.item_def = Some(107);
        ids.item_id_high = Some(108);
        ids.item_id_low = Some(109);
        ids.item_account_id = Some(110);
        ids.orig_own_low = Some(111);
        ids.orig_own_high = Some(112);
        ids.entity_quality = Some(113);
        ids.fallback_stattrak = Some(114);
        ids.custom_name = Some(115);
        let mut value = ValueField::new(Decoder::UnsignedDecoder, "test");
        value.should_parse = true;
        let field = Field::Value(value.clone());
        for prop_id in (101..=115).chain([
            MY_WEAPONS_OFFSET, FLASHBANG_AMMO_ID,
            WEAPON_SKIN_ID, WEAPON_SKIN_ID + 1, WEAPON_SKIN_ID + 2,
            WEAPON_SKIN_ID + 63, WEAPON_ATTRIBUTE_DEF_INDEX_ID + 63,
            GLOVE_PAINT_ID + 63, GLOVE_ATTRIBUTE_DEF_INDEX_ID + 63,
        ]) {
            let info = FieldInfo { decoder: Decoder::UnsignedDecoder, should_parse: true, prop_id };
            let cosmetic = is_cosmetic_prop(prop_id, &ids);
            assert!(changes_inventory_snapshot(&field, Some(info), cosmetic, &ids), "{prop_id}");
            assert!(!changes_inventory_snapshot(&field,
                Some(FieldInfo { should_parse: false, ..info }), cosmetic, &ids));
        }
        for prop_id in [116, WEAPON_SKIN_ID + 64, WEAPON_ATTRIBUTE_DEF_INDEX_ID + 64] {
            let info = FieldInfo { decoder: Decoder::UnsignedDecoder, should_parse: true, prop_id };
            assert!(!changes_inventory_snapshot(&field, Some(info), is_cosmetic_prop(prop_id, &ids), &ids));
        }
        assert!(!changes_inventory_snapshot(&field, None, true, &ids));

        value.prop_id = MY_WEAPONS_OFFSET;
        let field = Field::Value(value.clone());
        let mut path = generate_fp();
        path.last = 2;
        path.path[2] = 250_000;
        let info = get_propinfo(&field, &path).unwrap();
        assert_eq!(info.prop_id, MY_WEAPONS_OFFSET + 250_001);
        assert!(changes_inventory_snapshot(&field, Some(info), false, &ids));
        let vector = Field::Vector(VectorField::new(Field::Value(value), None));
        path.last = 1;
        assert!(changes_inventory_snapshot(&vector, get_propinfo(&vector, &path), false, &ids));
    }

    #[test]
    fn inventory_epoch_survives_unrelated_updates_and_empty_packets() {
        use crate::first_pass::parser_settings::{FirstPassParser, ParserInputs};
        use crate::parse_demo::DecodePlan;
        use std::sync::Arc;

        let huffman = Vec::new();
        let settings = ParserInputs {
            real_name_to_og_name: AHashMap::default(), wanted_players: vec![],
            wanted_player_props: vec![], wanted_other_props: vec![],
            wanted_prop_states: AHashMap::default(), wanted_ticks: vec![], wanted_events: vec![],
            parse_ents: true, parse_projectiles: false, collect_projectile_records: false,
            parse_grenades: false, only_header: false, only_convars: false,
            huffman_lookup_table: &huffman, order_by_steamid: false, list_props: false,
            fallback_bytes: None, cancelled: None,
        };
        let mut first = FirstPassParser::new(&settings);
        first.prop_controller.special_ids.life_state = Some(101);
        let fields = [116, 101, WEAPON_SKIN_ID].into_iter().map(|id| {
            let mut field = ValueField::new(Decoder::UnsignedDecoder, "test");
            field.prop_id = id;
            field.should_parse = true;
            Field::Value(field)
        }).collect();
        first.cls_by_id = Some(Arc::new(vec![Class {
            class_id: 0, name: "Test".to_string(),
            serializer: Serializer { name: "Test".to_string(), fields },
        }]));
        let mut parser = SecondPassParser::new(first.create_first_pass_output().unwrap(),
            0, false, None, DecodePlan::FULL).unwrap();
        parser.entities[1] = Some(Entity {
            cls_id: 0, entity_id: 1, serial: 1, props: AHashMap::default(),
            entity_type: EntityType::Normal, cosmetic_revision: 0, usercmd_scalar_cache: None,
        });
        for (field_index, expected_epoch) in [(0, 0), (1, 1), (1, 2), (2, 3)] {
            let mut path = generate_fp();
            path.last = 0;
            path.path[0] = field_index;
            parser.paths[0] = path;
            // Repeated equal life-state writes remain conservative invalidations.
            parser.decode_entity_update(&mut Bitreader::new(&[0]), 1, 1,
                false, true, &mut vec![]).unwrap();
            assert_eq!(parser.inventory_generation, expected_epoch);
        }
        let empty = CsvcMsgPacketEntities::default().encode_to_vec();
        parser.parse_packet_ents(&empty, false).unwrap();
        assert_eq!(parser.inventory_generation, 3);

        // Six low bits encode entity delta 1; the next two encode Delete.
        let deletion = CsvcMsgPacketEntities {
            updated_entries: Some(1), entity_data: Some(vec![0b0100_0001].into()),
            ..Default::default()
        }.encode_to_vec();
        parser.parse_packet_ents(&deletion, false).unwrap();
        assert!(parser.entities[1].is_none());
        assert_eq!(parser.inventory_generation, 4);
    }

    #[test]
    fn cosmetic_revision_tracks_only_fields_used_by_the_cosmetic_snapshot() {
        let mut special_ids = SpecialIDs::new();
        special_ids.item_def = Some(42);
        special_ids.custom_name = Some(43);

        assert!(is_cosmetic_prop(42, &special_ids));
        assert!(is_cosmetic_prop(43, &special_ids));
        assert!(is_cosmetic_prop(WEAPON_SKIN_ID, &special_ids));
        assert!(is_cosmetic_prop(
            WEAPON_ATTRIBUTE_DEF_INDEX_ID + 63,
            &special_ids
        ));
        assert!(!is_cosmetic_prop(
            WEAPON_ATTRIBUTE_DEF_INDEX_ID + 64,
            &special_ids
        ));
        assert!(is_cosmetic_prop(GLOVE_PAINT_ID + 63, &special_ids));
        assert!(is_cosmetic_prop(
            GLOVE_ATTRIBUTE_DEF_INDEX_ID + 63,
            &special_ids
        ));
        assert!(!is_cosmetic_prop(44, &special_ids));
    }

    #[test]
    fn entity_delta_preserves_untouched_baseline_econ_fields() {
        let mut entity = Entity {
            cls_id: 0,
            entity_id: 1,
            serial: 1,
            props: AHashMap::with_capacity(0),
            entity_type: EntityType::Normal,
            cosmetic_revision: 0, usercmd_scalar_cache: None,
        };
        let field = |prop_id| FieldInfo {
            decoder: crate::second_pass::decoder::Decoder::NoscaleDecoder,
            should_parse: true,
            prop_id,
        };

        SecondPassParser::insert_field(
            &mut entity,
            Variant::U32(6),
            Some(field(WEAPON_ATTRIBUTE_DEF_INDEX_ID)),
            true,
        );
        SecondPassParser::insert_field(
            &mut entity,
            Variant::F32(415.0),
            Some(field(WEAPON_SKIN_ID)),
            true,
        );
        SecondPassParser::insert_field(
            &mut entity,
            Variant::F32(602.0),
            Some(field(WEAPON_SKIN_ID + 1)),
            true,
        );

        assert_eq!(
            entity.props.get(&WEAPON_ATTRIBUTE_DEF_INDEX_ID),
            Some(&Variant::U32(6))
        );
        assert_eq!(
            entity.props.get(&WEAPON_SKIN_ID),
            Some(&Variant::F32(415.0))
        );
        assert_eq!(
            entity.props.get(&(WEAPON_SKIN_ID + 1)),
            Some(&Variant::F32(602.0))
        );
    }

    #[test]
    fn econ_attribute_vector_shrink_discards_personalized_baseline_tail() {
        let mut entity = Entity {
            cls_id: 0,
            entity_id: 1,
            serial: 1,
            props: AHashMap::with_capacity(0),
            entity_type: EntityType::Normal,
            cosmetic_revision: 0, usercmd_scalar_cache: None,
        };
        for slot in 0..11 {
            entity.props.insert(WEAPON_ATTRIBUTE_DEF_INDEX_ID + slot, Variant::U32(6 + slot));
            entity.props.insert(WEAPON_SKIN_ID + slot, Variant::F32(slot as f32));
        }
        entity.props.insert(GLOVE_PAINT_ID + 3, Variant::F32(99.0));
        entity.props.insert(42, Variant::U32(7));

        let field = econ_attribute_vector(WEAPON_SKIN_ID, WEAPON_ATTRIBUTE_DEF_INDEX_ID);
        assert!(truncate_econ_attribute_vector(&mut entity, &field, &Variant::U32(3)));

        for slot in 0..3 {
            assert!(entity.props.contains_key(&(WEAPON_ATTRIBUTE_DEF_INDEX_ID + slot)));
            assert!(entity.props.contains_key(&(WEAPON_SKIN_ID + slot)));
        }
        for slot in 3..11 {
            assert!(!entity.props.contains_key(&(WEAPON_ATTRIBUTE_DEF_INDEX_ID + slot)));
            assert!(!entity.props.contains_key(&(WEAPON_SKIN_ID + slot)));
        }
        assert_eq!(entity.props.get(&(GLOVE_PAINT_ID + 3)), Some(&Variant::F32(99.0)));
        assert_eq!(entity.props.get(&42), Some(&Variant::U32(7)));
        assert_eq!(entity.cosmetic_revision, 1);
        assert!(!truncate_econ_attribute_vector(&mut entity, &field, &Variant::U32(3)));
        assert!(!truncate_econ_attribute_vector(&mut entity, &field, &Variant::U32(64)));
        assert_eq!(entity.cosmetic_revision, 1);
    }
}

fn should_emit_prop_to_listen(prop_name: &str) -> bool {
    match prop_name.split(".").next() {
        Some("CCSGameRulesProxy") => return true,
        Some("CCSTeam") => return true,
        Some("CCSPlayerPawn") => return true,
        Some("CCSPlayerController") => return true,
        _ => {}
    };
    if is_weapon_prop(prop_name) || is_grenade_prop(prop_name) {
        return true;
    }
    false
}
fn convert_weapon_prefix_to_general(full_name: &str) -> String {
    let split_at_dot: Vec<&str> = full_name.split(".").collect();
    let grenade_or_weapon = is_grenade_or_weapon(full_name);
    // Strip first part of name from grenades and weapons.
    // if weapon prop: CAK47.m_iClip1 => m_iClip1
    // if grenade: CSmokeGrenadeProjectile.CBodyComponentBaseAnimGraph.m_cellX => CBodyComponentBaseAnimGraph.m_cellX
    if is_grenade_prop(full_name) {
        return "Grenade.".to_owned() + &split_at_dot[1..].join(".");
    }
    match grenade_or_weapon {
        true => "Weapon.".to_owned() + &split_at_dot[1..].join("."),
        false => full_name.to_string(),
    }
}
fn is_weapon_prop(full_name: &str) -> bool {
    let split_at_dot: Vec<&str> = full_name.split(".").collect();
    let is_weapon_prop =
        (split_at_dot[0].contains("Weapon") || split_at_dot[0].contains("AK")) && !split_at_dot[0].contains("Player") || split_at_dot[0].contains("CDEagle");
    is_weapon_prop
}
fn is_grenade_prop(full_name: &str) -> bool {
    if full_name.contains("CCSPlayer") {
        return false;
    }
    let parts = vec!["Molo", "Inc", "Infer", "Projectile", "Grenade", "Flash"];
    for part in parts {
        if full_name.contains(part) {
            return true;
        }
    }
    false
}
