/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::model::ParsedDemo;
use crate::{io_error, Error, Result};
use sha2::{Digest, Sha256};
use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

/// Hard ceiling for logical demo input. Parsing already requires the complete
/// demo in memory, so both plain `.dem` files and decompressed `.dem.zst`
/// content must stay within this bound before reaching demoparser.
pub const MAX_DECOMPRESSED_DEMO_BYTES: u64 = 2 * 1024 * 1024 * 1024;

const ZSTD_FRAME_MAGIC: u32 = 0xFD2F_B528;
const ZSTD_SKIPPABLE_MAGIC_BASE: u32 = 0x184D_2A50;
const ZSTD_FRAME_HEADER_MAX_BYTES: usize = 18;
const MAX_ZSTD_WINDOW_LOG: u32 = 27; // 128 MiB decoder history window.
const READ_BUFFER_BYTES: usize = 64 * 1024;

#[derive(Clone, Debug)]
pub struct LoadedDemoInput {
    pub bytes: Vec<u8>,
    pub stem: String,
    pub display_path: String,
    pub compressed: bool,
}

/// Returns true for the supported compressed demo filename form, ignoring
/// ASCII case (for example, `match.dem.zst`).
pub fn is_zstd_demo_path(path: &Path) -> bool {
    path.file_name()
        .and_then(|name| name.to_str())
        .is_some_and(|name| name.to_ascii_lowercase().ends_with(".dem.zst"))
}

/// Returns true for `.dem` and `.dem.zst` paths, ignoring ASCII case.
pub fn is_supported_demo_path(path: &Path) -> bool {
    path.file_name()
        .and_then(|name| name.to_str())
        .is_some_and(|name| {
            let name = name.to_ascii_lowercase();
            name.ends_with(".dem") || name.ends_with(".dem.zst")
        })
}

/// Reads the logical CS2 demo bytes. Zstd is detected from either the
/// `.dem.zst` filename or frame magic and decompressed with a hard output cap.
pub fn read_demo_input_bytes(path: &Path) -> Result<Vec<u8>> {
    read_demo_input_bytes_with_limit(path, MAX_DECOMPRESSED_DEMO_BYTES).map(|(bytes, _)| bytes)
}

/// Loads and, when needed, decompresses a demo without parsing it. Callers can
/// use this to expose a separate decompression stage before invoking
/// `read_loaded_demo_with_options`.
pub fn load_demo_input(path: &Path) -> Result<LoadedDemoInput> {
    let (bytes, compressed) = read_demo_input_bytes_with_limit(path, MAX_DECOMPRESSED_DEMO_BYTES)?;
    Ok(LoadedDemoInput {
        bytes,
        stem: demo_input_stem(path),
        display_path: path.display().to_string(),
        compressed,
    })
}

/// Hashes logical demo content without parsing it or retaining the whole demo
/// in memory. Compressed inputs are hashed after decompression and use the same
/// safety limit as parsing.
pub fn demo_content_sha256(path: &Path) -> Result<String> {
    let (mut reader, compressed) = open_demo_content_reader(path, MAX_DECOMPRESSED_DEMO_BYTES)?;
    let mut hasher = Sha256::new();
    let mut buffer = [0_u8; READ_BUFFER_BYTES];
    let mut total = 0_u64;

    loop {
        let read = reader
            .read(&mut buffer)
            .map_err(|error| demo_content_read_error(path, compressed, error))?;
        if read == 0 {
            break;
        }
        total = total.saturating_add(read as u64);
        enforce_decompressed_limit(path, compressed, total, MAX_DECOMPRESSED_DEMO_BYTES)?;
        hasher.update(&buffer[..read]);
    }

    Ok(format!("{:x}", hasher.finalize()))
}

fn read_demo_input_bytes_with_limit(path: &Path, limit: u64) -> Result<(Vec<u8>, bool)> {
    let (mut reader, compressed) = open_demo_content_reader(path, limit)?;
    let mut output = Vec::new();
    let mut buffer = [0_u8; READ_BUFFER_BYTES];
    let mut total = 0_u64;

    loop {
        let read = reader
            .read(&mut buffer)
            .map_err(|error| demo_content_read_error(path, compressed, error))?;
        if read == 0 {
            break;
        }
        total = total.saturating_add(read as u64);
        enforce_decompressed_limit(path, compressed, total, limit)?;
        reserve_demo_capacity(&mut output, read, path, total)?;
        output.extend_from_slice(&buffer[..read]);
    }

    Ok((output, compressed))
}

fn open_demo_content_reader(path: &Path, limit: u64) -> Result<(Box<dyn Read>, bool)> {
    let mut file = File::open(path).map_err(|error| io_error(path, error))?;
    let mut prefix = [0_u8; ZSTD_FRAME_HEADER_MAX_BYTES];
    let prefix_len = file
        .read(&mut prefix)
        .map_err(|error| io_error(path, error))?;
    file.seek(SeekFrom::Start(0))
        .map_err(|error| io_error(path, error))?;

    let magic_is_zstd = has_zstd_magic(&prefix[..prefix_len]);
    let compressed = is_zstd_demo_path(path)
        || path
            .extension()
            .and_then(|extension| extension.to_str())
            .is_some_and(|extension| extension.eq_ignore_ascii_case("zst"))
        || magic_is_zstd;

    if compressed {
        if magic_is_zstd {
            if let Ok(Some(content_size)) =
                zstd::zstd_safe::get_frame_content_size(&prefix[..prefix_len])
            {
                enforce_decompressed_limit(path, true, content_size, limit)?;
            }
        }
        let mut decoder =
            zstd::stream::read::Decoder::new(file).map_err(|source| Error::ZstdDemo {
                path: path.display().to_string(),
                source,
            })?;
        decoder
            .window_log_max(MAX_ZSTD_WINDOW_LOG)
            .map_err(|source| Error::ZstdDemo {
                path: path.display().to_string(),
                source,
            })?;
        Ok((Box::new(decoder), true))
    } else {
        let file_len = file
            .metadata()
            .map_err(|error| io_error(path, error))?
            .len();
        enforce_demo_limit(path, file_len, limit)?;
        Ok((Box::new(file), false))
    }
}

fn reserve_demo_capacity(
    output: &mut Vec<u8>,
    additional: usize,
    path: &Path,
    decoded_bytes: u64,
) -> Result<()> {
    output
        .try_reserve(additional)
        .map_err(|source| Error::DemoAllocation {
            path: path.display().to_string(),
            decoded_bytes,
            source,
        })
}

fn has_zstd_magic(prefix: &[u8]) -> bool {
    let Some(first_four) = prefix.get(..4) else {
        return false;
    };
    let Ok(bytes) = <[u8; 4]>::try_from(first_four) else {
        return false;
    };
    let magic = u32::from_le_bytes(bytes);
    magic == ZSTD_FRAME_MAGIC || magic & 0xFFFF_FFF0 == ZSTD_SKIPPABLE_MAGIC_BASE
}

fn enforce_decompressed_limit(
    path: &Path,
    _compressed: bool,
    total: u64,
    limit: u64,
) -> Result<()> {
    enforce_demo_limit(path, total, limit)
}

fn enforce_demo_limit(path: &Path, total: u64, limit: u64) -> Result<()> {
    if total > limit {
        return Err(Error::DecompressedDemoTooLarge {
            path: path.display().to_string(),
            limit_bytes: limit,
        });
    }
    Ok(())
}

fn demo_content_read_error(path: &Path, compressed: bool, source: std::io::Error) -> Error {
    if compressed {
        Error::ZstdDemo {
            path: path.display().to_string(),
            source,
        }
    } else {
        io_error(path, source)
    }
}

fn demo_input_stem(path: &Path) -> String {
    let first_stem = path
        .file_stem()
        .and_then(|stem| stem.to_str())
        .unwrap_or("demo");
    if path
        .extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(|extension| extension.eq_ignore_ascii_case("zst"))
    {
        PathBuf::from(first_stem)
            .file_stem()
            .and_then(|stem| stem.to_str())
            .unwrap_or("demo")
            .to_string()
    } else {
        first_stem.to_string()
    }
}

#[cfg(test)]
mod input_tests {
    use super::*;
    use std::fs;
    use std::io::Write;

    #[test]
    fn supported_paths_cover_plain_and_faceit_zstd_names() {
        assert!(is_supported_demo_path(Path::new("match.dem")));
        assert!(is_supported_demo_path(Path::new("MATCH.DEM.ZST")));
        assert!(is_zstd_demo_path(Path::new("match.dem.zst")));
        assert!(!is_zstd_demo_path(Path::new("match.dem")));
        assert!(!is_supported_demo_path(Path::new("match.zst")));
        assert!(!is_supported_demo_path(Path::new("match.dem.zip")));
    }

    #[test]
    fn plain_demo_bytes_and_hash_are_unchanged() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("match.dem");
        let bytes = b"PBDEMS2\0logical demo bytes";
        fs::write(&path, bytes).unwrap();

        let loaded = load_demo_input(&path).unwrap();
        assert_eq!(loaded.bytes, bytes);
        assert_eq!(loaded.stem, "match");
        assert_eq!(loaded.display_path, path.display().to_string());
        assert!(!loaded.compressed);
        assert_eq!(
            demo_content_sha256(&path).unwrap(),
            crate::demo_id::sha256_hex(bytes)
        );
    }

    #[test]
    fn faceit_zstd_demo_loads_with_logical_stem_and_content_hash() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("faceit-match.dem.zst");
        let bytes = b"PBDEMS2\0decompressed demo bytes";
        let compressed = zstd::stream::encode_all(&bytes[..], 1).unwrap();
        fs::write(&path, compressed).unwrap();

        let loaded = load_demo_input(&path).unwrap();
        assert_eq!(loaded.bytes, bytes);
        assert_eq!(loaded.stem, "faceit-match");
        assert_eq!(loaded.display_path, path.display().to_string());
        assert!(loaded.compressed);
        assert_eq!(
            demo_content_sha256(&path).unwrap(),
            crate::demo_id::sha256_hex(bytes)
        );
    }

    #[test]
    fn zstd_magic_is_detected_even_with_dem_extension() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("renamed.dem");
        let bytes = b"PBDEMS2\0compressed despite extension";
        let compressed = zstd::stream::encode_all(&bytes[..], 1).unwrap();
        fs::write(&path, compressed).unwrap();

        let loaded = load_demo_input(&path).unwrap();
        assert_eq!(loaded.bytes, bytes);
        assert_eq!(loaded.stem, "renamed");
        assert!(loaded.compressed);
    }

    #[test]
    fn compressed_output_limit_is_enforced() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("oversize.dem.zst");
        let bytes = vec![b'x'; 1_025];
        let compressed = zstd::stream::encode_all(&bytes[..], 1).unwrap();
        fs::write(&path, compressed).unwrap();

        let error = read_demo_input_bytes_with_limit(&path, 1_024).unwrap_err();
        assert!(matches!(
            error,
            Error::DecompressedDemoTooLarge {
                limit_bytes: 1_024,
                ..
            }
        ));
    }

    #[test]
    fn plain_demo_limit_is_enforced_before_reading() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("oversize.dem");
        fs::write(&path, vec![b'x'; 1_025]).unwrap();

        let error = read_demo_input_bytes_with_limit(&path, 1_024).unwrap_err();

        assert!(matches!(
            error,
            Error::DecompressedDemoTooLarge {
                limit_bytes: 1_024,
                ..
            }
        ));
    }

    #[test]
    fn declared_frame_size_is_rejected_before_payload_decode() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("declared-oversize.dem.zst");
        let bytes = vec![b'x'; 1_025];
        let mut encoder = zstd::stream::write::Encoder::new(Vec::new(), 1).unwrap();
        encoder.include_contentsize(true).unwrap();
        encoder
            .set_pledged_src_size(Some(bytes.len() as u64))
            .unwrap();
        encoder.write_all(&bytes).unwrap();
        let compressed = encoder.finish().unwrap();

        // A complete frame header is sufficient for the declared-size check;
        // keeping only the prefix proves the decoder payload is never needed.
        fs::write(
            &path,
            &compressed[..compressed.len().min(ZSTD_FRAME_HEADER_MAX_BYTES)],
        )
        .unwrap();

        let error = read_demo_input_bytes_with_limit(&path, 1_024).unwrap_err();
        assert!(matches!(
            error,
            Error::DecompressedDemoTooLarge {
                limit_bytes: 1_024,
                ..
            }
        ));
    }

    #[test]
    fn allocation_failure_is_reported_instead_of_panicking() {
        let mut output = Vec::new();
        let error = reserve_demo_capacity(
            &mut output,
            usize::MAX,
            Path::new("too-large.dem.zst"),
            u64::MAX,
        )
        .unwrap_err();

        assert!(matches!(error, Error::DemoAllocation { .. }));
        assert!(error
            .to_string()
            .contains("cannot allocate memory while loading demo"));
    }

    #[test]
    fn corrupt_zstd_input_has_a_specific_error() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("broken.dem.zst");
        fs::write(&path, b"not a zstd frame").unwrap();

        let error = read_demo_input_bytes(&path).unwrap_err();
        assert!(matches!(error, Error::ZstdDemo { .. }));
        assert!(error.to_string().contains("failed to decompress zstd demo"));
    }
}

#[derive(Clone, Copy, Debug)]
pub struct ReadDemoOptions {
    pub collect_voice: bool,
    pub collect_cosmetics: bool,
}

impl Default for ReadDemoOptions {
    fn default() -> Self {
        Self {
            collect_voice: false,
            // Keep the public read_demo/read_demo_bytes API complete. Library callers that
            // cannot export cosmetics explicitly opt into the lean property set instead.
            collect_cosmetics: true,
        }
    }
}

#[cfg(feature = "demoparser")]
mod demoparser_impl {
    use super::*;
    use crate::model::{
        AvatarImageFormat, ParsedAvatarOverride, ParsedEconItem, ParsedGameEvent,
        ParsedInventoryWeaponAttribute, ParsedInventoryWeaponCosmetic, ParsedPlayerTick,
        ParsedProjectile, ParsedScoreboardFlair, ParsedVoiceFrame, ParsedWeaponSticker,
        ProjectileEffectSource, ProjectileKind, ReplayInputHistoryEntry, SubtickMove,
    };
    use ahash::AHashMap;
    use parser::first_pass::parser_settings::{
        check_multithreadability, rm_user_friendly_names, ParserInputs,
    };
    use parser::first_pass::prop_controller::{PropInfo, ENTITY_ID_ID, STEAMID_ID, TICK_ID};
    use parser::first_pass::read_bits::DemoParserError;
    use parser::parse_demo::{DecodePlan, DemoOutput, Parser, ParsingMode};
    use parser::second_pass::collect_data::ProjectileRecord;
    use parser::second_pass::game_events::GameEvent;
    use parser::second_pass::parser_settings::create_huffman_lookup_table;
    use parser::second_pass::variants::{
        InputHistory as ParserInputHistory,
        InventoryWeaponCosmetic as ParserInventoryWeaponCosmetic, PropColumn, VarVec, Variant,
    };
    use rayon::prelude::*;
    use std::collections::{BTreeMap, BTreeSet};
    use std::sync::atomic::AtomicBool;
    use std::sync::Arc;

    const COSMETIC_PLAYER_PROPS: &[&str] = &[
        "inventory_weapon_cosmetics",
        "agent_skin",
        "CCSPlayerController.m_nPawnCharacterDefIndex",
        "active_weapon_original_owner",
        "item_id_high",
        "item_id_low",
        "item_account_id",
        "weapon_skin_id",
        "weapon_paint_seed",
        "weapon_float",
        "custom_name",
        "weapon_stickers",
        "glove_item_idx",
        "glove_paint_id",
        "glove_paint_seed",
        "glove_paint_float",
    ];

    const DUCKED_PROP: &str = "ducked";
    const DUCKING_PROP: &str = "ducking";
    const ENTITY_ID_PROP: &str = "entity_id";
    const ROUND_PROP: &str = "total_rounds_played";
    const SINGLE_THREADED_OVERLAY_PROPS: &[&str] = &[
        DUCKED_PROP,
        DUCKING_PROP,
        "usercmd_input_history",
        "usercmd_client_tick",
        "usercmd_attack1_start_history_index",
        "usercmd_attack2_start_history_index",
        "usercmd_subtick_moves",
        "usercmd_buttonstate_1",
        "usercmd_buttonstate_2",
        "usercmd_buttonstate_3",
        "usercmd_forward_move",
        "usercmd_left_move",
        "usercmd_up_move",
        "usercmd_viewangle_x",
        "usercmd_viewangle_y",
        "usercmd_viewangle_z",
        "usercmd_mouse_dx",
        "usercmd_mouse_dy",
        "usercmd_weapon_select",
        "usercmd_left_hand_desired",
    ];

    macro_rules! define_resolved_columns {
        ($($field:ident => $name:literal),+ $(,)?) => {
            struct ResolvedColumns<'a> {
                len: usize,
                $($field: Option<&'a PropColumn>,)+
            }

            impl<'a> ResolvedColumns<'a> {
                fn new(
                    prop_infos: &[PropInfo],
                    df: &'a AHashMap<u32, PropColumn>,
                ) -> Self {
                    let mut by_name: AHashMap<&str, &PropColumn> = AHashMap::default();
                    for info in prop_infos {
                        if let Some(column) = df.get(&info.id) {
                            by_name.insert(info.prop_friendly_name.as_str(), column);
                        }
                    }
                    Self {
                        len: by_name
                            .values()
                            .map(|column| column.len())
                            .max()
                            .unwrap_or_default(),
                        $($field: by_name.get($name).copied(),)+
                    }
                }
            }
        };
    }

    define_resolved_columns! {
        steam_id => "steamid",
        tick => "tick",
        round => "total_rounds_played",
        team_num => "team_num",
        is_airborne => "is_airborne",
        entity_flags => "CCSPlayerPawn.m_fFlags",
        input_history => "usercmd_input_history",
        usercmd_client_tick => "usercmd_client_tick",
        usercmd_attack1_start_history_index => "usercmd_attack1_start_history_index",
        usercmd_attack2_start_history_index => "usercmd_attack2_start_history_index",
        subtick_moves => "usercmd_subtick_moves",
        name => "name",
        is_alive => "is_alive",
        round_in_progress => "round_in_progress",
        is_freeze_period => "is_freeze_period",
        game_time => "game_time",
        origin_x => "X",
        origin_y => "Y",
        origin_z => "Z",
        pitch => "pitch",
        yaw => "yaw",
        buttons => "buttons",
        buttonstate1 => "usercmd_buttonstate_1",
        buttonstate2 => "usercmd_buttonstate_2",
        buttonstate3 => "usercmd_buttonstate_3",
        usercmd_forward_move => "usercmd_forward_move",
        usercmd_left_move => "usercmd_left_move",
        usercmd_up_move => "usercmd_up_move",
        usercmd_pitch => "usercmd_viewangle_x",
        usercmd_yaw => "usercmd_viewangle_y",
        usercmd_roll => "usercmd_viewangle_z",
        usercmd_mouse_dx => "usercmd_mouse_dx",
        usercmd_mouse_dy => "usercmd_mouse_dy",
        usercmd_weapon_select => "usercmd_weapon_select",
        usercmd_left_hand_desired => "usercmd_left_hand_desired",
        item_def_idx => "item_def_idx",
        inventory_as_ids => "inventory_as_ids",
        music_kit_id => "music_kit_id",
        scoreboard_flair => "CCSPlayerController.CCSPlayerController_InventoryServices.m_rank",
        agent_item_def_index => "CCSPlayerController.m_nPawnCharacterDefIndex",
        agent_skin => "agent_skin",
        active_weapon_paint_kit => "weapon_skin_id",
        active_weapon_paint_seed => "weapon_paint_seed",
        active_weapon_paint_wear => "weapon_float",
        active_weapon_original_owner => "active_weapon_original_owner",
        active_weapon_item_account_id => "item_account_id",
        active_weapon_item_id_high => "item_id_high",
        active_weapon_item_id_low => "item_id_low",
        active_weapon_custom_name => "custom_name",
        glove_item_def_index => "glove_item_idx",
        glove_paint_kit => "glove_paint_id",
        glove_paint_seed => "glove_paint_seed",
        glove_paint_wear => "glove_paint_float",
        crosshair_code => "crosshair_code",
        viewmodel_left_handed => "CCSPlayerPawn.m_bLeftHanded",
        viewmodel_fov => "CCSPlayerPawn.m_flViewmodelFOV",
        viewmodel_offset_x => "CCSPlayerPawn.m_flViewmodelOffsetX",
        viewmodel_offset_y => "CCSPlayerPawn.m_flViewmodelOffsetY",
        viewmodel_offset_z => "CCSPlayerPawn.m_flViewmodelOffsetZ",
        scoreboard_score => "score",
        scoreboard_mvps => "mvps",
        scoreboard_kills => "kills_total",
        scoreboard_deaths => "deaths_total",
        scoreboard_assists => "assists_total",
        scoreboard_headshot_kills => "headshot_kills_total",
        scoreboard_damage => "damage_total",
        armor_value => "armor_value",
        has_helmet => "has_helmet",
        has_defuser => "has_defuser",
        round_start_equip_value => "round_start_equip_value",
        equipment_value_total => "equipment_value_total",
        money_saved_total => "money_saved_total",
        cash_spent_this_round => "cash_spent_this_round",
        account_balance => "balance",
        move_type => "move_type",
        duck_amount => "duck_amount",
        duck_speed => "duck_speed",
        ladder_normal => "CCSPlayerPawn.CCSPlayer_MovementServices.m_vecLadderNormal",
        ducked => "ducked",
        ducking => "ducking",
        desires_duck => "CCSPlayerPawn.CCSPlayer_MovementServices.m_bDesiresDuck",
        player_user_id => "user_id",
        player_entity_id => "entity_id",
        player_color => "player_color",
        team_rounds_total => "team_rounds_total",
        team_name => "team_name",
        team_clan_name => "team_clan_name",
    }

    struct SingleThreadedOverlay {
        columns: AHashMap<&'static str, PropColumn>,
    }

    type ParsedInventoryCache = AHashMap<
        (usize, usize),
        (
            Arc<[ParserInventoryWeaponCosmetic]>,
            Arc<[ParsedInventoryWeaponCosmetic]>,
        ),
    >;

    impl SingleThreadedOverlay {
        fn get(&self, friendly_name: &'static str) -> Option<&PropColumn> {
            self.columns.get(friendly_name)
        }
    }

    struct SupplementalColumns {
        tick: PropColumn,
        entity_id: PropColumn,
        steam_id: PropColumn,
        round: PropColumn,
        overlay: SingleThreadedOverlay,
    }

    impl SupplementalColumns {
        fn from_output(mut output: DemoOutput) -> Option<Self> {
            let round_id = friendly_prop_id(&output.prop_controller.prop_infos, ROUND_PROP)?;
            let tick = output.df.remove(&TICK_ID)?;
            let entity_id = output.df.remove(&ENTITY_ID_ID)?;
            let steam_id = output.df.remove(&STEAMID_ID)?;
            let round = output.df.remove(&round_id)?;
            let len = tick.len();
            if entity_id.len() != len || steam_id.len() != len || round.len() != len {
                return None;
            }

            let mut columns = AHashMap::with_capacity(SINGLE_THREADED_OVERLAY_PROPS.len());
            for friendly_name in SINGLE_THREADED_OVERLAY_PROPS {
                let id = friendly_prop_id(&output.prop_controller.prop_infos, friendly_name)?;
                let column = output.df.remove(&id)?;
                if column.len() != len || !overlay_column_type_is_valid(friendly_name, &column) {
                    return None;
                }
                columns.insert(*friendly_name, column);
            }

            Some(Self {
                tick,
                entity_id,
                steam_id,
                round,
                overlay: SingleThreadedOverlay { columns },
            })
        }

        fn align(self, primary: &DemoOutput) -> Option<SingleThreadedOverlay> {
            let primary_round_id =
                friendly_prop_id(&primary.prop_controller.prop_infos, ROUND_PROP)?;
            if !strict_movement_keys_match(
                &self.tick,
                &self.entity_id,
                &self.steam_id,
                &self.round,
                primary.df.get(&TICK_ID)?,
                primary.df.get(&ENTITY_ID_ID)?,
                primary.df.get(&STEAMID_ID)?,
                primary.df.get(&primary_round_id)?,
            ) {
                return None;
            }
            Some(self.overlay)
        }
    }

    fn overlay_column_type_is_valid(friendly_name: &str, column: &PropColumn) -> bool {
        match column.data.as_ref() {
            None => true,
            Some(VarVec::Bool(_)) => matches!(
                friendly_name,
                DUCKED_PROP | DUCKING_PROP | "usercmd_left_hand_desired"
            ),
            Some(VarVec::F32(_)) => matches!(
                friendly_name,
                "usercmd_forward_move"
                    | "usercmd_left_move"
                    | "usercmd_up_move"
                    | "usercmd_viewangle_x"
                    | "usercmd_viewangle_y"
                    | "usercmd_viewangle_z"
            ),
            Some(VarVec::U64(_)) => matches!(
                friendly_name,
                "usercmd_buttonstate_1" | "usercmd_buttonstate_2" | "usercmd_buttonstate_3"
            ),
            Some(VarVec::I32(_)) => matches!(
                friendly_name,
                "usercmd_mouse_dx"
                    | "usercmd_mouse_dy"
                    | "usercmd_weapon_select"
                    | "usercmd_client_tick"
                    | "usercmd_attack1_start_history_index"
                    | "usercmd_attack2_start_history_index"
            ),
            Some(VarVec::InputHistory(_)) => friendly_name == "usercmd_input_history",
            Some(VarVec::UserCmdSubtickMoves(_)) => friendly_name == "usercmd_subtick_moves",
            _ => false,
        }
    }

    #[cfg(test)]
    fn all_none_overlay(len: usize) -> SingleThreadedOverlay {
        SingleThreadedOverlay {
            columns: SINGLE_THREADED_OVERLAY_PROPS
                .iter()
                .map(|friendly_name| {
                    (
                        *friendly_name,
                        PropColumn {
                            data: None,
                            num_nones: len,
                        },
                    )
                })
                .collect(),
        }
    }

    fn friendly_prop_id(prop_infos: &[PropInfo], friendly_name: &str) -> Option<u32> {
        prop_infos
            .iter()
            .rev()
            .find(|info| info.prop_friendly_name == friendly_name)
            .map(|info| info.id)
    }

    #[allow(clippy::too_many_arguments)]
    fn strict_movement_keys_match(
        left_tick: &PropColumn,
        left_entity_id: &PropColumn,
        left_steam_id: &PropColumn,
        left_round: &PropColumn,
        right_tick: &PropColumn,
        right_entity_id: &PropColumn,
        right_steam_id: &PropColumn,
        right_round: &PropColumn,
    ) -> bool {
        let (
            Some(VarVec::I32(left_tick)),
            Some(VarVec::I32(left_entity_id)),
            Some(VarVec::U64(left_steam_id)),
            Some(VarVec::I32(left_round)),
            Some(VarVec::I32(right_tick)),
            Some(VarVec::I32(right_entity_id)),
            Some(VarVec::U64(right_steam_id)),
            Some(VarVec::I32(right_round)),
        ) = (
            left_tick.data.as_ref(),
            left_entity_id.data.as_ref(),
            left_steam_id.data.as_ref(),
            left_round.data.as_ref(),
            right_tick.data.as_ref(),
            right_entity_id.data.as_ref(),
            right_steam_id.data.as_ref(),
            right_round.data.as_ref(),
        )
        else {
            return false;
        };

        let len = left_tick.len();
        if [
            left_entity_id.len(),
            left_steam_id.len(),
            left_round.len(),
            right_tick.len(),
            right_entity_id.len(),
            right_steam_id.len(),
            right_round.len(),
        ]
        .into_iter()
        .any(|column_len| column_len != len)
        {
            return false;
        }
        if left_tick.iter().any(Option::is_none)
            || left_entity_id.iter().any(Option::is_none)
            || left_steam_id.iter().any(Option::is_none)
            || right_tick.iter().any(Option::is_none)
            || right_entity_id.iter().any(Option::is_none)
            || right_steam_id.iter().any(Option::is_none)
        {
            return false;
        }

        left_tick == right_tick
            && left_entity_id == right_entity_id
            && left_steam_id == right_steam_id
            && left_round == right_round
    }

    fn parse_demo_channels<'a>(
        bytes: &[u8],
        settings: ParserInputs<'a>,
    ) -> std::result::Result<(DemoOutput, Option<SingleThreadedOverlay>), DemoParserError> {
        if check_multithreadability(&settings.wanted_player_props)
            || !settings.parse_ents
            || settings.order_by_steamid
            || !settings.wanted_players.is_empty()
            || !settings.wanted_ticks.is_empty()
            || !settings.wanted_prop_states.is_empty()
            || settings.only_header
            || settings.only_convars
            || settings.list_props
            || settings.fallback_bytes.is_some()
        {
            return parse_demo_once(bytes, settings, ParsingMode::Normal)
                .map(|output| (output, None));
        }

        let Some(entity_id_real) = wanted_real_prop(&settings, ENTITY_ID_PROP) else {
            return parse_demo_once(bytes, settings, ParsingMode::Normal)
                .map(|output| (output, None));
        };
        let Some(round_real) = wanted_real_prop(&settings, ROUND_PROP) else {
            return parse_demo_once(bytes, settings, ParsingMode::Normal)
                .map(|output| (output, None));
        };
        let Some(overlay_real_props) = SINGLE_THREADED_OVERLAY_PROPS
            .iter()
            .map(|friendly_name| wanted_real_prop(&settings, friendly_name))
            .collect::<Option<Vec<_>>>()
        else {
            return parse_demo_once(bytes, settings, ParsingMode::Normal)
                .map(|output| (output, None));
        };
        let mut supplemental_real_props = overlay_real_props.clone();
        supplemental_real_props.push(entity_id_real);
        supplemental_real_props.push(round_real);

        let mut supplemental_settings = settings.clone();
        supplemental_settings
            .wanted_player_props
            .retain(|prop| supplemental_real_props.contains(prop));
        supplemental_settings.wanted_other_props.clear();
        supplemental_settings.wanted_events.clear();
        supplemental_settings.parse_projectiles = false;
        supplemental_settings.collect_projectile_records = false;
        supplemental_settings.parse_grenades = false;

        let supplemental_output = match parse_demo_once_with_decode_plan(
            bytes,
            supplemental_settings,
            ParsingMode::ForceSingleThreaded,
            DecodePlan::PLAYER_ROWS_ONLY,
        ) {
            Ok(output) => output,
            Err(_) => {
                return parse_demo_once(bytes, settings, ParsingMode::Normal)
                    .map(|output| (output, None));
            }
        };
        let Some(supplemental) = SupplementalColumns::from_output(supplemental_output) else {
            return parse_demo_once(bytes, settings, ParsingMode::Normal)
                .map(|output| (output, None));
        };

        let mut primary_settings = settings.clone();
        primary_settings
            .wanted_player_props
            .retain(|prop| !overlay_real_props.contains(prop));
        if !check_multithreadability(&primary_settings.wanted_player_props) {
            return parse_demo_once(bytes, settings, ParsingMode::Normal)
                .map(|output| (output, None));
        }

        let primary = match parse_demo_once(bytes, primary_settings, ParsingMode::Normal) {
            Ok(output) => output,
            Err(_) => {
                return parse_demo_once(bytes, settings, ParsingMode::Normal)
                    .map(|output| (output, None));
            }
        };
        let Some(overlay) = supplemental.align(&primary) else {
            return parse_demo_once(bytes, settings, ParsingMode::Normal)
                .map(|output| (output, None));
        };
        Ok((primary, Some(overlay)))
    }

    fn wanted_real_prop(settings: &ParserInputs<'_>, friendly_name: &str) -> Option<String> {
        settings
            .real_name_to_og_name
            .iter()
            .find(|(real_name, original_name)| {
                original_name.as_str() == friendly_name
                    && settings.wanted_player_props.contains(real_name)
            })
            .map(|(real_name, _)| real_name.clone())
    }

    fn parse_demo_once<'a>(
        bytes: &[u8],
        settings: ParserInputs<'a>,
        mode: ParsingMode,
    ) -> std::result::Result<DemoOutput, DemoParserError> {
        parse_demo_once_with_decode_plan(bytes, settings, mode, DecodePlan::FULL_PLAYER_ROWS)
    }

    fn parse_demo_once_with_decode_plan<'a>(
        bytes: &[u8],
        settings: ParserInputs<'a>,
        mode: ParsingMode,
        decode_plan: DecodePlan,
    ) -> std::result::Result<DemoOutput, DemoParserError> {
        Parser::new_with_decode_plan(settings, mode, decode_plan).parse_demo(bytes)
    }

    pub fn read_demo(path: &Path) -> Result<ParsedDemo> {
        read_demo_with_options(path, ReadDemoOptions::default())
    }

    pub fn read_demo_with_options(path: &Path, options: ReadDemoOptions) -> Result<ParsedDemo> {
        read_demo_with_options_and_cancel(path, options, None)
    }

    pub fn read_demo_with_options_and_cancel(
        path: &Path,
        options: ReadDemoOptions,
        cancelled: Option<&AtomicBool>,
    ) -> Result<ParsedDemo> {
        let input = load_demo_input(path)?;
        read_loaded_demo_with_options_and_cancel(&input, options, cancelled)
    }

    pub fn read_demo_bytes(bytes: &[u8], stem: &str, display_path: &str) -> Result<ParsedDemo> {
        read_demo_bytes_with_options(bytes, stem, display_path, ReadDemoOptions::default())
    }

    pub fn read_loaded_demo_with_options(
        input: &LoadedDemoInput,
        options: ReadDemoOptions,
    ) -> Result<ParsedDemo> {
        read_loaded_demo_with_options_and_cancel(input, options, None)
    }

    pub fn read_loaded_demo_with_options_and_cancel(
        input: &LoadedDemoInput,
        options: ReadDemoOptions,
        cancelled: Option<&AtomicBool>,
    ) -> Result<ParsedDemo> {
        read_demo_bytes_with_options_and_cancel(
            &input.bytes,
            &input.stem,
            &input.display_path,
            options,
            cancelled,
        )
    }

    pub fn read_demo_bytes_with_options(
        bytes: &[u8],
        stem: &str,
        display_path: &str,
        options: ReadDemoOptions,
    ) -> Result<ParsedDemo> {
        read_demo_bytes_with_options_and_cancel(bytes, stem, display_path, options, None)
    }

    pub fn read_demo_bytes_with_options_and_cancel(
        bytes: &[u8],
        stem: &str,
        display_path: &str,
        options: ReadDemoOptions,
        cancelled: Option<&AtomicBool>,
    ) -> Result<ParsedDemo> {
        let wanted_props = vec![
            "X",
            "Y",
            "Z",
            "pitch",
            "yaw",
            "buttons",
            "usercmd_viewangle_x",
            "usercmd_viewangle_y",
            "usercmd_viewangle_z",
            "usercmd_buttonstate_1",
            "usercmd_buttonstate_2",
            "usercmd_buttonstate_3",
            "usercmd_forward_move",
            "usercmd_left_move",
            "usercmd_up_move",
            "usercmd_mouse_dx",
            "usercmd_mouse_dy",
            "usercmd_weapon_select",
            "usercmd_left_hand_desired",
            "usercmd_input_history",
            "usercmd_client_tick",
            "usercmd_attack1_start_history_index",
            "usercmd_attack2_start_history_index",
            "item_def_idx",
            "inventory_as_ids",
            "inventory_weapon_cosmetics",
            "music_kit_id",
            "CCSPlayerController.CCSPlayerController_InventoryServices.m_rank",
            "agent_skin",
            "CCSPlayerController.m_nPawnCharacterDefIndex",
            "active_weapon_original_owner",
            "item_id_high",
            "item_id_low",
            "item_account_id",
            "weapon_skin_id",
            "weapon_paint_seed",
            "weapon_float",
            "custom_name",
            "weapon_stickers",
            "glove_item_idx",
            "glove_paint_id",
            "glove_paint_seed",
            "glove_paint_float",
            "crosshair_code",
            "CCSPlayerPawn.m_bLeftHanded",
            "CCSPlayerPawn.m_flViewmodelFOV",
            "CCSPlayerPawn.m_flViewmodelOffsetX",
            "CCSPlayerPawn.m_flViewmodelOffsetY",
            "CCSPlayerPawn.m_flViewmodelOffsetZ",
            "score",
            "mvps",
            "kills_total",
            "deaths_total",
            "assists_total",
            "headshot_kills_total",
            "damage_total",
            "user_id",
            "entity_id",
            "player_color",
            "armor_value",
            "has_helmet",
            "has_defuser",
            "round_start_equip_value",
            "equipment_value_total",
            "money_saved_total",
            "cash_spent_this_round",
            "balance",
            "team_num",
            "is_alive",
            "is_airborne",
            "move_type",
            "CCSPlayerPawn.m_fFlags",
            "duck_amount",
            "duck_speed",
            "ducked",
            "ducking",
            "CCSPlayerPawn.CCSPlayer_MovementServices.m_bDesiresDuck",
            "CCSPlayerPawn.CCSPlayer_MovementServices.m_vecLadderNormal",
            "round_in_progress",
            "is_freeze_period",
            "total_rounds_played",
            "game_time",
            "usercmd_subtick_moves",
        ]
        .into_iter()
        .filter(|prop| should_collect_player_prop(options, prop))
        .map(str::to_string)
        .collect::<Vec<_>>();

        let real_props =
            rm_user_friendly_names(&wanted_props).map_err(|e| Error::Parser(format!("{e:?}")))?;
        let wanted_other_props = vec!["team_rounds_total", "team_name", "team_clan_name"]
            .into_iter()
            .map(str::to_string)
            .collect::<Vec<_>>();
        let real_other_props = rm_user_friendly_names(&wanted_other_props)
            .map_err(|e| Error::Parser(format!("{e:?}")))?;
        let mut real_name_to_og_name = AHashMap::default();
        for (real, friendly) in real_props.iter().zip(&wanted_props) {
            real_name_to_og_name.insert(real.clone(), friendly.clone());
        }
        for (real, friendly) in real_other_props.iter().zip(&wanted_other_props) {
            real_name_to_og_name.insert(real.clone(), friendly.clone());
        }

        let demo_sha256 = crate::demo_id::sha256_hex(bytes);
        let huf = create_huffman_lookup_table();
        let settings = ParserInputs {
            real_name_to_og_name,
            wanted_players: vec![],
            wanted_player_props: real_props,
            wanted_other_props: real_other_props,
            wanted_prop_states: AHashMap::default(),
            wanted_ticks: vec![],
            wanted_events: vec![
                "round_freeze_end".to_string(),
                "round_end".to_string(),
                "round_announce_match_start".to_string(),
                "cs_win_panel_match".to_string(),
                "round_start".to_string(),
                "bomb_beginplant".to_string(),
                "bomb_planted".to_string(),
                "bomb_dropped".to_string(),
                "bomb_pickup".to_string(),
                "item_pickup".to_string(),
                "weapon_fire".to_string(),
                "player_hurt".to_string(),
                "player_death".to_string(),
                "chat_message".to_string(),
                "server_message".to_string(),
                "smokegrenade_detonate".to_string(),
                "flashbang_detonate".to_string(),
                "hegrenade_detonate".to_string(),
                "molotov_detonate".to_string(),
                "inferno_startburn".to_string(),
                "decoy_started".to_string(),
                "decoy_detonate".to_string(),
            ],
            parse_ents: true,
            parse_projectiles: false,
            collect_projectile_records: true,
            parse_grenades: false,
            only_header: false,
            only_convars: false,
            huffman_lookup_table: &huf,
            order_by_steamid: false,
            list_props: false,
            fallback_bytes: None,
            cancelled,
        };
        let (mut output, single_threaded_overlay) =
            parse_demo_channels(bytes, settings).map_err(|e| Error::Parser(format!("{e:?}")))?;

        // These nested columns are already owned by the parser output. Take them out so row
        // materialization can move their vectors and strings instead of deep-cloning every tick.
        let inventory_weapon_cosmetics_id = output
            .prop_controller
            .prop_infos
            .iter()
            .find(|info| info.prop_friendly_name == "inventory_weapon_cosmetics")
            .map(|info| info.id);
        let weapon_stickers_id = output
            .prop_controller
            .prop_infos
            .iter()
            .find(|info| info.prop_friendly_name == "weapon_stickers")
            .map(|info| info.id);
        let mut inventory_weapon_cosmetics_column =
            inventory_weapon_cosmetics_id.and_then(|id| output.df.remove(&id));
        let mut weapon_stickers_column = weapon_stickers_id.and_then(|id| output.df.remove(&id));

        let header = output.header.unwrap_or_default();
        let map = header
            .get("map_name")
            .cloned()
            .unwrap_or_else(|| "unknown".to_string());
        let demo_patch_version = header_i32(&header, "patch_version");
        let demo_version_name = header_text(&header, "demo_version_name");
        let server_name = header_text(&header, "server_name");
        let playback_time_seconds =
            header_f32(&header, "playback_time").filter(|value| value.is_finite() && *value >= 0.0);

        let columns = ResolvedColumns::new(&output.prop_controller.prop_infos, &output.df);
        macro_rules! overlay_column {
            ($friendly_name:literal, $primary:expr) => {
                single_threaded_overlay
                    .as_ref()
                    .and_then(|overlay| overlay.get($friendly_name))
                    .or($primary)
            };
        }
        let subtick_moves_column = overlay_column!("usercmd_subtick_moves", columns.subtick_moves);
        let input_history_column = overlay_column!("usercmd_input_history", columns.input_history);
        let usercmd_client_tick_column =
            overlay_column!("usercmd_client_tick", columns.usercmd_client_tick);
        let usercmd_attack1_start_history_index_column = overlay_column!(
            "usercmd_attack1_start_history_index",
            columns.usercmd_attack1_start_history_index
        );
        let usercmd_attack2_start_history_index_column = overlay_column!(
            "usercmd_attack2_start_history_index",
            columns.usercmd_attack2_start_history_index
        );
        let buttonstate1_column = overlay_column!("usercmd_buttonstate_1", columns.buttonstate1);
        let buttonstate2_column = overlay_column!("usercmd_buttonstate_2", columns.buttonstate2);
        let buttonstate3_column = overlay_column!("usercmd_buttonstate_3", columns.buttonstate3);
        let usercmd_forward_move_column =
            overlay_column!("usercmd_forward_move", columns.usercmd_forward_move);
        let usercmd_left_move_column =
            overlay_column!("usercmd_left_move", columns.usercmd_left_move);
        let usercmd_up_move_column = overlay_column!("usercmd_up_move", columns.usercmd_up_move);
        let usercmd_pitch_column = overlay_column!("usercmd_viewangle_x", columns.usercmd_pitch);
        let usercmd_yaw_column = overlay_column!("usercmd_viewangle_y", columns.usercmd_yaw);
        let usercmd_roll_column = overlay_column!("usercmd_viewangle_z", columns.usercmd_roll);
        let usercmd_mouse_dx_column = overlay_column!("usercmd_mouse_dx", columns.usercmd_mouse_dx);
        let usercmd_mouse_dy_column = overlay_column!("usercmd_mouse_dy", columns.usercmd_mouse_dy);
        let usercmd_weapon_select_column =
            overlay_column!("usercmd_weapon_select", columns.usercmd_weapon_select);
        let usercmd_left_hand_desired_column = overlay_column!(
            "usercmd_left_hand_desired",
            columns.usercmd_left_hand_desired
        );
        let ducked_column = overlay_column!("ducked", columns.ducked);
        let ducking_column = overlay_column!("ducking", columns.ducking);
        let mut row_order = Vec::with_capacity(columns.len);
        for idx in 0..columns.len {
            let steam_id = get_u64(columns.steam_id, idx).unwrap_or_default();
            if steam_id == 0 {
                continue;
            }
            let tick = get_i32(columns.tick, idx).unwrap_or_default();
            let round = get_u32(columns.round, idx).unwrap_or_default();
            row_order.push((idx, round, tick, steam_id));
        }
        row_order.sort_by_key(|(_, round, tick, steam_id)| (*round, *tick, *steam_id));

        let mut parsed_inventory_cache = ParsedInventoryCache::default();
        let row_materialization = row_order
            .into_iter()
            .map(|(idx, round, tick, steam_id)| {
                (
                    idx,
                    round,
                    tick,
                    steam_id,
                    take_inventory_weapon_cosmetics(
                        &mut inventory_weapon_cosmetics_column,
                        idx,
                        &mut parsed_inventory_cache,
                    )
                    .unwrap_or_default(),
                    take_weapon_stickers(&mut weapon_stickers_column, idx).unwrap_or_default(),
                )
            })
            .collect::<Vec<_>>();
        drop(inventory_weapon_cosmetics_column);
        drop(weapon_stickers_column);

        let mut rows = row_materialization
            .into_par_iter()
            .map(
                |(
                    idx,
                    round,
                    tick,
                    steam_id,
                    inventory_weapon_cosmetics,
                    active_weapon_stickers,
                )| {
                    let team_num = get_u32(columns.team_num, idx).unwrap_or_default() as u8;
                    let is_airborne = get_bool(columns.is_airborne, idx).unwrap_or(false);
                    let explicit_flags = get_u32(columns.entity_flags, idx);
                    let (subtick_moves, subtick_button_truncated) =
                        get_subtick_moves(subtick_moves_column, idx).unwrap_or_default();
                    let buttonstate1 = get_u64(buttonstate1_column, idx);
                    let buttonstate2 = get_u64(buttonstate2_column, idx);
                    let buttonstate3 = get_u64(buttonstate3_column, idx);
                    ParsedPlayerTick {
                        tick,
                        steam_id,
                        name: get_string(columns.name, idx).unwrap_or_default(),
                        team_num,
                        is_alive: get_bool(columns.is_alive, idx).unwrap_or(false),
                        round,
                        round_in_progress: get_bool(columns.round_in_progress, idx)
                            .unwrap_or(false),
                        is_freeze_period: get_bool(columns.is_freeze_period, idx).unwrap_or(false),
                        game_time: get_f32(columns.game_time, idx),
                        origin: [
                            get_f32(columns.origin_x, idx).unwrap_or_default(),
                            get_f32(columns.origin_y, idx).unwrap_or_default(),
                            get_f32(columns.origin_z, idx).unwrap_or_default(),
                        ],
                        // CS2 Demo serializers do not expose the pawn's actual locomotion
                        // velocity. This is filled from the ordered position chain below.
                        velocity: [0.0, 0.0, 0.0],
                        pitch: get_f32(columns.pitch, idx).unwrap_or_default(),
                        yaw: get_f32(columns.yaw, idx).unwrap_or_default(),
                        buttons: get_u64(columns.buttons, idx).unwrap_or_default(),
                        buttonstates_present: buttonstate1.is_some()
                            || buttonstate2.is_some()
                            || buttonstate3.is_some(),
                        buttonstate1: buttonstate1.unwrap_or_default(),
                        buttonstate2: buttonstate2.unwrap_or_default(),
                        buttonstate3: buttonstate3.unwrap_or_default(),
                        usercmd_forward_move: get_f32(usercmd_forward_move_column, idx),
                        usercmd_left_move: get_f32(usercmd_left_move_column, idx),
                        usercmd_up_move: get_f32(usercmd_up_move_column, idx),
                        usercmd_pitch: get_f32(usercmd_pitch_column, idx),
                        usercmd_yaw: get_f32(usercmd_yaw_column, idx),
                        usercmd_roll: get_f32(usercmd_roll_column, idx),
                        usercmd_mouse_dx: get_i32(usercmd_mouse_dx_column, idx),
                        usercmd_mouse_dy: get_i32(usercmd_mouse_dy_column, idx),
                        usercmd_weapon_select: get_i32(usercmd_weapon_select_column, idx),
                        usercmd_left_hand_desired: get_bool(usercmd_left_hand_desired_column, idx),
                        usercmd_client_tick: get_i32(usercmd_client_tick_column, idx),
                        usercmd_attack1_start_history_index: get_i32(
                            usercmd_attack1_start_history_index_column,
                            idx,
                        )
                        .unwrap_or(-1),
                        usercmd_attack2_start_history_index: get_i32(
                            usercmd_attack2_start_history_index_column,
                            idx,
                        )
                        .unwrap_or(-1),
                        input_history: get_input_history(input_history_column, idx)
                            .unwrap_or_default(),
                        item_def_idx: get_i32(columns.item_def_idx, idx)
                            .or_else(|| get_u32(columns.item_def_idx, idx).map(|v| v as i32))
                            .unwrap_or(-1),
                        inventory_as_ids: get_u32_vec(columns.inventory_as_ids, idx)
                            .unwrap_or_default()
                            .into_iter()
                            .map(|v| v as i32)
                            .collect(),
                        inventory_weapon_cosmetics,
                        music_kit_id: get_u32(columns.music_kit_id, idx)
                            .filter(|value| *value != 0),
                        scoreboard_flair: get_scoreboard_flair(columns.scoreboard_flair, idx),
                        agent_item_def_index: get_u32(columns.agent_item_def_index, idx)
                            .filter(|value| *value != 0),
                        agent_skin: get_string(columns.agent_skin, idx)
                            .and_then(normalize_agent_skin),
                        active_weapon_paint_kit: get_u32(columns.active_weapon_paint_kit, idx),
                        active_weapon_paint_seed: get_u32(columns.active_weapon_paint_seed, idx),
                        active_weapon_paint_wear: get_f32(columns.active_weapon_paint_wear, idx),
                        active_weapon_original_owner_steam_id: get_string(
                            columns.active_weapon_original_owner,
                            idx,
                        )
                        .and_then(|value| value.parse::<u64>().ok())
                        .filter(|value| *value != 0),
                        active_weapon_item_account_id: get_u32(
                            columns.active_weapon_item_account_id,
                            idx,
                        )
                        .filter(|value| *value != 0),
                        active_weapon_item_id: combine_item_id(
                            get_u32(columns.active_weapon_item_id_high, idx),
                            get_u32(columns.active_weapon_item_id_low, idx),
                        )
                        .filter(|value| *value != 0),
                        active_weapon_custom_name: get_string(
                            columns.active_weapon_custom_name,
                            idx,
                        )
                        .and_then(normalize_custom_name),
                        active_weapon_stickers,
                        glove_item_def_index: get_i32(columns.glove_item_def_index, idx),
                        glove_paint_kit: get_u32(columns.glove_paint_kit, idx),
                        glove_paint_seed: get_u32(columns.glove_paint_seed, idx),
                        glove_paint_wear: get_f32(columns.glove_paint_wear, idx),
                        crosshair_code: get_string(columns.crosshair_code, idx)
                            .and_then(normalize_crosshair_code),
                        viewmodel_left_handed: get_bool(columns.viewmodel_left_handed, idx),
                        viewmodel_fov: get_f32(columns.viewmodel_fov, idx),
                        viewmodel_offset_x: get_f32(columns.viewmodel_offset_x, idx),
                        viewmodel_offset_y: get_f32(columns.viewmodel_offset_y, idx),
                        viewmodel_offset_z: get_f32(columns.viewmodel_offset_z, idx),
                        scoreboard_score: get_i32(columns.scoreboard_score, idx),
                        scoreboard_mvps: get_u32(columns.scoreboard_mvps, idx),
                        scoreboard_kills: get_u32(columns.scoreboard_kills, idx),
                        scoreboard_deaths: get_u32(columns.scoreboard_deaths, idx),
                        scoreboard_assists: get_u32(columns.scoreboard_assists, idx),
                        scoreboard_headshot_kills: get_u32(columns.scoreboard_headshot_kills, idx),
                        scoreboard_damage: get_u32(columns.scoreboard_damage, idx),
                        armor_value: get_u32(columns.armor_value, idx).unwrap_or_default(),
                        has_helmet: get_bool(columns.has_helmet, idx).unwrap_or(false),
                        has_defuser: get_bool(columns.has_defuser, idx).unwrap_or(false),
                        round_start_equip_value: get_u32(columns.round_start_equip_value, idx)
                            .unwrap_or_default(),
                        equipment_value_total: get_u32(columns.equipment_value_total, idx)
                            .unwrap_or_default(),
                        money_saved_total: get_u32(columns.money_saved_total, idx)
                            .unwrap_or_default(),
                        cash_spent_this_round: get_u32(columns.cash_spent_this_round, idx)
                            .unwrap_or_default(),
                        account_balance: get_u32(columns.account_balance, idx),
                        entity_flags: explicit_flags.unwrap_or(if is_airborne { 0 } else { 1 }),
                        move_type: get_u32(columns.move_type, idx).unwrap_or(2) as u8,
                        duck_amount: get_f32(columns.duck_amount, idx),
                        duck_speed: get_f32(columns.duck_speed, idx),
                        ladder_normal: get_vec3(columns.ladder_normal, idx),
                        ducked: get_bool(ducked_column, idx),
                        ducking: get_bool(ducking_column, idx),
                        desires_duck: get_bool(columns.desires_duck, idx),
                        subtick_moves,
                        subtick_button_truncated,
                        player_user_id: get_i32(columns.player_user_id, idx),
                        player_entity_id: get_i32(columns.player_entity_id, idx),
                        player_color: get_string(columns.player_color, idx),
                        team_rounds_total: get_u32(columns.team_rounds_total, idx),
                        team_name: get_string(columns.team_name, idx),
                        team_clan_name: get_string(columns.team_clan_name, idx),
                    }
                },
            )
            .collect::<Vec<_>>();

        repair_short_global_tick_gaps(&mut rows);
        let tick_rate = estimate_tick_rate(&rows).unwrap_or(64.0);
        derive_observed_player_velocities(&mut rows, tick_rate);
        let mut round_freeze_end_ticks = output
            .game_events
            .iter()
            .filter(|event| event.name == "round_freeze_end")
            .map(|event| event.tick)
            .collect::<Vec<_>>();
        round_freeze_end_ticks.sort_unstable();
        round_freeze_end_ticks.dedup();
        let mut round_end_ticks = output
            .game_events
            .iter()
            .filter(|event| event.name == "round_end")
            .map(|event| event.tick)
            .collect::<Vec<_>>();
        round_end_ticks.sort_unstable();
        round_end_ticks.dedup();
        repair_round_phase_after_events(&mut rows, &round_freeze_end_ticks, &round_end_ticks);
        let mut bomb_beginplant_ticks = event_ticks(&output.game_events, "bomb_beginplant");
        let mut bomb_planted_ticks = event_ticks(&output.game_events, "bomb_planted");
        bomb_beginplant_ticks.sort_unstable();
        bomb_beginplant_ticks.dedup();
        bomb_planted_ticks.sort_unstable();
        bomb_planted_ticks.dedup();
        let events = parse_game_events(&output.game_events);
        let projectiles =
            parse_projectile_records(&output.projectiles, tick_rate, &output.game_events);
        let mut voice_frames = Vec::new();
        if options.collect_voice {
            for (tick, msg) in &output.voice_data {
                let Some(audio) = msg.audio.as_ref() else {
                    continue;
                };
                let Some(data) = audio.voice_data.as_ref() else {
                    continue;
                };
                if data.is_empty() {
                    continue;
                }
                let xuid = msg.xuid.unwrap_or_default();
                if xuid == 0 {
                    continue;
                }
                voice_frames.push(ParsedVoiceFrame {
                    tick: *tick,
                    xuid,
                    client: msg.client.unwrap_or(-1),
                    format: audio.format.unwrap_or_default(),
                    sample_rate: audio.sample_rate,
                    voice_level: audio.voice_level,
                    sequence_bytes: audio.sequence_bytes,
                    section_number: audio.section_number,
                    uncompressed_sample_offset: audio.uncompressed_sample_offset,
                    num_packets: audio.num_packets,
                    packet_offsets: audio.packet_offsets.clone(),
                    audio: data.to_vec(),
                });
            }
            voice_frames.sort_by_key(|frame| (frame.xuid, frame.tick));
        }
        let avatar_overrides = parse_avatar_overrides(
            output.server_avatar_overrides,
            rows.iter().map(|row| row.steam_id),
        );
        let econ_items = if options.collect_cosmetics {
            parse_econ_items(output.skins)
        } else {
            Vec::new()
        };

        Ok(ParsedDemo {
            path: display_path.to_string(),
            stem: stem.to_string(),
            demo_sha256,
            map,
            demo_patch_version,
            demo_version_name,
            server_name,
            playback_time_seconds,
            tick_rate,
            round_freeze_end_ticks,
            bomb_beginplant_ticks,
            bomb_planted_ticks,
            rows,
            projectiles,
            voice_frames,
            events,
            avatar_overrides,
            econ_items,
        })
    }

    fn should_collect_player_prop(options: ReadDemoOptions, prop: &str) -> bool {
        options.collect_cosmetics || !COSMETIC_PLAYER_PROPS.contains(&prop)
    }

    fn parse_avatar_overrides(
        raw: Vec<(String, Vec<u8>)>,
        player_steam_ids: impl IntoIterator<Item = u64>,
    ) -> Vec<ParsedAvatarOverride> {
        let mut by_steam_id: BTreeMap<u64, BTreeMap<String, (AvatarImageFormat, Vec<u8>)>> =
            BTreeMap::new();
        let mut default_variants: BTreeMap<String, (AvatarImageFormat, Vec<u8>)> = BTreeMap::new();
        for (key, bytes) in raw {
            if bytes.is_empty() {
                continue;
            }
            let sha256 = crate::demo_id::sha256_hex(&bytes);
            if key.trim().eq_ignore_ascii_case("default") {
                default_variants
                    .entry(sha256)
                    .or_insert_with(|| (detect_avatar_image_format(&bytes), bytes));
                continue;
            }
            let Ok(steam_id) = key.trim().parse::<u64>() else {
                continue;
            };
            by_steam_id
                .entry(steam_id)
                .or_default()
                .entry(sha256)
                .or_insert_with(|| (detect_avatar_image_format(&bytes), bytes));
        }

        let mut parsed = by_steam_id
            .into_iter()
            .filter_map(|(steam_id, mut variants)| {
                if variants.len() != 1 {
                    return None;
                }
                let (sha256, (format, bytes)) = variants.pop_first()?;
                Some((
                    steam_id,
                    ParsedAvatarOverride {
                        steam_id,
                        format,
                        sha256,
                        source: "ServerAvatarOverrides".to_string(),
                        bytes,
                    },
                ))
            })
            .collect::<BTreeMap<_, _>>();

        if default_variants.len() == 1 {
            if let Some((sha256, (format, bytes))) = default_variants.pop_first() {
                for steam_id in player_steam_ids
                    .into_iter()
                    .filter(|steam_id| *steam_id != 0)
                {
                    parsed
                        .entry(steam_id)
                        .or_insert_with(|| ParsedAvatarOverride {
                            steam_id,
                            format,
                            sha256: sha256.clone(),
                            source: "ServerAvatarOverrides".to_string(),
                            bytes: bytes.clone(),
                        });
                }
            }
        }

        parsed.into_values().collect()
    }

    fn parse_econ_items(
        items: Vec<parser::second_pass::parser_settings::EconItem>,
    ) -> Vec<ParsedEconItem> {
        items
            .into_iter()
            .map(|item| ParsedEconItem {
                steam_id: item.steamid,
                item_def_index: item.def_index,
                paint_kit: item.paint_index,
                paint_seed: item.paint_seed,
                paint_wear_raw: item.paint_wear,
                paint_wear: item.paint_wear.map(f32::from_bits),
                custom_name: item.custom_name,
                item_name: item.item_name,
                skin_name: item.skin_name,
            })
            .collect()
    }

    fn detect_avatar_image_format(bytes: &[u8]) -> AvatarImageFormat {
        if bytes.starts_with(b"\x89PNG\r\n\x1a\n") {
            AvatarImageFormat::Png
        } else if bytes.starts_with(&[0xff, 0xd8, 0xff]) {
            AvatarImageFormat::Jpeg
        } else {
            AvatarImageFormat::Binary
        }
    }

    pub fn read_demo_header_map_bytes(bytes: &[u8]) -> Result<Option<String>> {
        let huf = create_huffman_lookup_table();
        let settings = ParserInputs {
            real_name_to_og_name: AHashMap::default(),
            wanted_players: Vec::new(),
            wanted_player_props: Vec::new(),
            wanted_other_props: Vec::new(),
            wanted_prop_states: AHashMap::default(),
            wanted_ticks: Vec::new(),
            wanted_events: Vec::new(),
            parse_ents: false,
            parse_projectiles: false,
            collect_projectile_records: false,
            parse_grenades: false,
            only_header: true,
            only_convars: false,
            huffman_lookup_table: &huf,
            order_by_steamid: false,
            list_props: false,
            fallback_bytes: None,
            cancelled: None,
        };
        let mut parser = Parser::new(settings, ParsingMode::ForceSingleThreaded);
        let output = parser
            .parse_demo(bytes)
            .map_err(|e| Error::Parser(format!("{e:?}")))?;
        let header = output.header.unwrap_or_default();
        Ok(header.get("map_name").cloned())
    }

    fn parse_projectile_records(
        records: &[ProjectileRecord],
        tick_rate: f32,
        game_events: &[GameEvent],
    ) -> Vec<ParsedProjectile> {
        let mut grouped: AHashMap<ProjectileKey, ProjectileCandidate> = AHashMap::default();
        for record in records {
            let steam_id = record.steamid.unwrap_or_default();
            if steam_id == 0 {
                continue;
            }
            let tick = record.tick.unwrap_or_default();
            let grenade_type = record.grenade_type.clone().unwrap_or_default();
            let initial_position = match record.initial_position {
                Some(value) if vec3_is_meaningful(value) => value,
                _ => continue,
            };
            let initial_velocity = match record.initial_velocity {
                Some(value) if vec3_is_meaningful(value) => value,
                _ => continue,
            };
            let detonation_position = record.smoke_detonation_position.unwrap_or_default();
            let entity_id = record.entity_id.unwrap_or_default();
            let key =
                ProjectileKey::new(steam_id, &grenade_type, initial_position, initial_velocity);
            let entry = grouped.entry(key).or_insert_with(|| ProjectileCandidate {
                entity_id,
                projectile: ParsedProjectile {
                    tick,
                    steam_id,
                    name: record.name.clone().unwrap_or_default(),
                    kind: ProjectileKind::from_grenade_type(&grenade_type),
                    weapon_def_index: ProjectileKind::weapon_def_index_from_grenade_type(
                        &grenade_type,
                    ),
                    grenade_type,
                    initial_position,
                    initial_velocity,
                    detonation_position: [0.0, 0.0, 0.0],
                    effect_position: [0.0, 0.0, 0.0],
                    effect_tick: None,
                    effect_source: ProjectileEffectSource::Unknown,
                    effect_confidence: 0.0,
                },
            });
            if tick < entry.projectile.tick {
                entry.projectile.tick = tick;
                entry.entity_id = entity_id;
            }
            if vec3_is_meaningful(detonation_position) {
                entry.projectile.detonation_position = detonation_position;
            }
        }

        let mut candidates = grouped.into_values().collect::<Vec<_>>();
        candidates
            .sort_by_key(|candidate| (candidate.projectile.tick, candidate.projectile.steam_id));
        attach_projectile_effects(&mut candidates, game_events, tick_rate);
        let mut projectiles = candidates
            .into_iter()
            .map(|candidate| candidate.projectile)
            .collect::<Vec<_>>();
        projectiles.sort_by_key(|projectile| (projectile.tick, projectile.steam_id));
        projectiles
    }

    #[derive(Clone, Debug)]
    struct ProjectileCandidate {
        projectile: ParsedProjectile,
        entity_id: i32,
    }

    #[derive(Clone, Debug)]
    struct ProjectileEffectEvent {
        tick: i32,
        entity_id: Option<i32>,
        steam_id: Option<u64>,
        kind: ProjectileKind,
        position: [f32; 3],
        source: ProjectileEffectSource,
    }

    fn attach_projectile_effects(
        candidates: &mut [ProjectileCandidate],
        game_events: &[GameEvent],
        tick_rate: f32,
    ) {
        let max_delta_ticks = seconds_to_ticks(20.0, tick_rate).max(1);
        let events = game_events
            .iter()
            .filter_map(effect_event_from_game_event)
            .collect::<Vec<_>>();
        let mut used = vec![false; events.len()];

        for candidate in candidates {
            if vec3_is_meaningful(candidate.projectile.detonation_position) {
                candidate.projectile.effect_position = candidate.projectile.detonation_position;
                candidate.projectile.effect_source = ProjectileEffectSource::SmokeDetonationProp;
                candidate.projectile.effect_confidence = 0.90;
            }

            let best = events
                .iter()
                .enumerate()
                .filter(|(idx, event)| {
                    !used[*idx] && effect_can_match(candidate, event, max_delta_ticks)
                })
                .min_by_key(|(_, event)| effect_match_score(candidate, event));

            if let Some((idx, event)) = best {
                used[idx] = true;
                candidate.projectile.effect_position = event.position;
                candidate.projectile.effect_tick = Some(event.tick);
                candidate.projectile.effect_source = event.source;
                candidate.projectile.effect_confidence = effect_confidence(candidate, event);
                candidate.projectile.detonation_position = event.position;
            }
        }
    }

    fn effect_event_from_game_event(event: &GameEvent) -> Option<ProjectileEffectEvent> {
        let (kind, source) = match event.name.as_str() {
            "smokegrenade_detonate" => (
                ProjectileKind::Smoke,
                ProjectileEffectSource::SmokeDetonationEvent,
            ),
            "flashbang_detonate" => (
                ProjectileKind::Flash,
                ProjectileEffectSource::FlashDetonationEvent,
            ),
            "hegrenade_detonate" => (
                ProjectileKind::He,
                ProjectileEffectSource::HeDetonationEvent,
            ),
            "molotov_detonate" => (
                ProjectileKind::Molotov,
                ProjectileEffectSource::MolotovDetonationEvent,
            ),
            "inferno_startburn" => (
                ProjectileKind::Molotov,
                ProjectileEffectSource::InfernoStartBurnEvent,
            ),
            "decoy_started" => (
                ProjectileKind::Decoy,
                ProjectileEffectSource::DecoyStartedEvent,
            ),
            "decoy_detonate" => (
                ProjectileKind::Decoy,
                ProjectileEffectSource::DecoyDetonationEvent,
            ),
            _ => return None,
        };
        let position = event_vec3(event)?;
        if !vec3_is_meaningful(position) {
            return None;
        }
        Some(ProjectileEffectEvent {
            tick: event.tick,
            entity_id: event_field_i32(event, "entityid"),
            steam_id: event_steam_id(event),
            kind,
            position,
            source,
        })
    }

    fn effect_can_match(
        candidate: &ProjectileCandidate,
        event: &ProjectileEffectEvent,
        max_delta_ticks: i32,
    ) -> bool {
        if candidate.projectile.kind != event.kind {
            return false;
        }
        if let Some(steam_id) = event.steam_id {
            if steam_id != candidate.projectile.steam_id {
                return false;
            }
        }
        let delta = event.tick - candidate.projectile.tick;
        delta >= -2 && delta <= max_delta_ticks
    }

    fn effect_match_score(
        candidate: &ProjectileCandidate,
        event: &ProjectileEffectEvent,
    ) -> (u8, u8, i32) {
        let entity_rank = if event_source_uses_projectile_entity(event.source)
            && candidate.entity_id > 0
            && event.entity_id == Some(candidate.entity_id)
        {
            0
        } else {
            1
        };
        let steam_rank = if event.steam_id == Some(candidate.projectile.steam_id) {
            0
        } else {
            1
        };
        (
            entity_rank,
            steam_rank,
            event.tick - candidate.projectile.tick,
        )
    }

    fn effect_confidence(candidate: &ProjectileCandidate, event: &ProjectileEffectEvent) -> f32 {
        if event_source_uses_projectile_entity(event.source)
            && candidate.entity_id > 0
            && event.entity_id == Some(candidate.entity_id)
        {
            1.0
        } else if event.steam_id == Some(candidate.projectile.steam_id) {
            0.85
        } else {
            0.65
        }
    }

    fn event_source_uses_projectile_entity(source: ProjectileEffectSource) -> bool {
        !matches!(source, ProjectileEffectSource::InfernoStartBurnEvent)
    }

    fn event_vec3(event: &GameEvent) -> Option<[f32; 3]> {
        let x = event_field_f32(event, "x")
            .or_else(|| event_field_f32(event, "pos_x"))
            .or_else(|| event_field_f32(event, "origin_x"))?;
        let y = event_field_f32(event, "y")
            .or_else(|| event_field_f32(event, "pos_y"))
            .or_else(|| event_field_f32(event, "origin_y"))?;
        let z = event_field_f32(event, "z")
            .or_else(|| event_field_f32(event, "pos_z"))
            .or_else(|| event_field_f32(event, "origin_z"))?;
        Some([x, y, z])
    }

    fn event_steam_id(event: &GameEvent) -> Option<u64> {
        [
            "user_steamid",
            "steamid",
            "thrower_steamid",
            "attacker_steamid",
        ]
        .iter()
        .find_map(|name| event_field_u64(event, name))
    }

    fn event_field_f32(event: &GameEvent, name: &str) -> Option<f32> {
        match event_field(event, name)? {
            Variant::F32(value) => Some(*value),
            Variant::I32(value) => Some(*value as f32),
            Variant::U32(value) => Some(*value as f32),
            Variant::String(value) => value.parse::<f32>().ok(),
            _ => None,
        }
    }

    fn event_field_i32(event: &GameEvent, name: &str) -> Option<i32> {
        match event_field(event, name)? {
            Variant::I32(value) => Some(*value),
            Variant::U32(value) => i32::try_from(*value).ok(),
            Variant::U64(value) => i32::try_from(*value).ok(),
            Variant::String(value) => value.parse::<i32>().ok(),
            _ => None,
        }
    }

    fn event_field_u64(event: &GameEvent, name: &str) -> Option<u64> {
        match event_field(event, name)? {
            Variant::U64(value) => Some(*value),
            Variant::U32(value) => Some(u64::from(*value)),
            Variant::I32(value) => u64::try_from(*value).ok(),
            Variant::String(value) => value.parse::<u64>().ok(),
            _ => None,
        }
    }

    fn event_field_bool(event: &GameEvent, name: &str) -> Option<bool> {
        match event_field(event, name)? {
            Variant::Bool(value) => Some(*value),
            Variant::I32(value) => Some(*value != 0),
            Variant::U32(value) => Some(*value != 0),
            Variant::String(value) => match value.trim().to_ascii_lowercase().as_str() {
                "1" | "true" | "yes" | "on" => Some(true),
                "0" | "false" | "no" | "off" => Some(false),
                _ => None,
            },
            _ => None,
        }
    }

    fn event_field_string(event: &GameEvent, name: &str) -> Option<String> {
        match event_field(event, name)? {
            Variant::String(value) => Some(value.clone()),
            Variant::U64(value) => Some(value.to_string()),
            Variant::U32(value) => Some(value.to_string()),
            Variant::I32(value) => Some(value.to_string()),
            _ => None,
        }
    }

    fn event_field<'a>(event: &'a GameEvent, name: &str) -> Option<&'a Variant> {
        event
            .fields
            .iter()
            .find(|field| field.name == name)
            .and_then(|field| field.data.as_ref())
    }

    fn parse_game_events(events: &[GameEvent]) -> Vec<ParsedGameEvent> {
        events
            .iter()
            .filter_map(parsed_game_event)
            .collect::<Vec<_>>()
    }

    fn parsed_game_event(event: &GameEvent) -> Option<ParsedGameEvent> {
        let supported = matches!(
            event.name.as_str(),
            "round_start"
                | "round_freeze_end"
                | "round_end"
                | "round_announce_match_start"
                | "cs_win_panel_match"
                | "bomb_beginplant"
                | "bomb_planted"
                | "bomb_dropped"
                | "bomb_pickup"
                | "item_pickup"
                | "weapon_fire"
                | "player_hurt"
                | "player_death"
                | "chat_message"
                | "server_message"
        );
        if !supported {
            return None;
        }
        let chat_text = match event.name.as_str() {
            "chat_message" => event_field_string(event, "chat_message"),
            "server_message" => event_field_string(event, "server_message"),
            _ => None,
        }
        .and_then(normalize_chat_text);
        if matches!(event.name.as_str(), "chat_message" | "server_message") && chat_text.is_none() {
            return None;
        }
        let chat_message_name =
            event_field_string(event, "chat_message_name").and_then(normalize_chat_message_name);
        let chat_flag = event_field_bool(event, "chat");
        if event.name == "chat_message"
            && !is_supported_chat_message(chat_message_name.as_deref(), chat_flag)
        {
            return None;
        }
        let chat_scope = match event.name.as_str() {
            "server_message" => Some("server".to_string()),
            "chat_message" => Some(infer_chat_scope(chat_message_name.as_deref())),
            _ => None,
        };

        Some(ParsedGameEvent {
            tick: event.tick,
            name: event.name.clone(),
            user_steam_id: event_steam_id(event),
            attacker_steam_id: event_field_u64(event, "attacker_steamid")
                .or_else(|| event_field_u64(event, "attacker")),
            victim_steam_id: event_field_u64(event, "victim_steamid")
                .or_else(|| event_field_u64(event, "userid")),
            weapon_def_index: event_field_i32(event, "defindex")
                .or_else(|| event_field_i32(event, "item_def_index"))
                .or_else(|| event_field_i32(event, "weapon_id")),
            item_name: event_field_string(event, "item")
                .or_else(|| event_field_string(event, "weapon"))
                .or_else(|| event_field_string(event, "weapon_name")),
            entity_id: event_field_i32(event, "entindex")
                .or_else(|| event_field_i32(event, "entityid")),
            damage: event_field_i32(event, "dmg_health")
                .or_else(|| event_field_i32(event, "damage")),
            health: event_field_i32(event, "health"),
            winner_side: (event.name == "round_end")
                .then(|| event_winner_side(event))
                .flatten(),
            chat_text,
            chat_scope,
            chat_message_name,
        })
    }

    fn event_winner_side(event: &GameEvent) -> Option<u8> {
        if let Some(value) = event_field_i32(event, "winner") {
            return matches!(value, 2 | 3).then_some(value as u8);
        }
        let normalized = event_field_string(event, "winner")?
            .trim()
            .to_ascii_lowercase()
            .replace(['-', '_', ' '], "");
        match normalized.as_str() {
            "2" | "t" | "terrorist" | "terrorists" => Some(2),
            "3" | "ct" | "counterterrorist" | "counterterrorists" => Some(3),
            _ => None,
        }
    }

    fn normalize_chat_text(value: String) -> Option<String> {
        let cleaned = value
            .trim()
            .chars()
            .filter(|ch| !ch.is_control() || *ch == '\t')
            .take(256)
            .collect::<String>()
            .trim()
            .to_string();
        (!cleaned.is_empty()).then_some(cleaned)
    }

    fn normalize_chat_message_name(value: String) -> Option<String> {
        let cleaned = value
            .trim()
            .chars()
            .filter(|ch| ch.is_ascii_alphanumeric() || *ch == '_' || *ch == '#')
            .take(96)
            .collect::<String>();
        (!cleaned.is_empty()).then_some(cleaned)
    }

    fn is_supported_chat_message(message_name: Option<&str>, chat: Option<bool>) -> bool {
        if chat == Some(true) {
            return true;
        }
        let normalized = message_name
            .unwrap_or_default()
            .trim_start_matches('#')
            .to_ascii_lowercase();
        normalized.contains("chat") || normalized.contains("server") || normalized.contains("admin")
    }

    fn infer_chat_scope(message_name: Option<&str>) -> String {
        let normalized = message_name
            .unwrap_or_default()
            .trim_start_matches('#')
            .to_ascii_lowercase();
        if normalized.contains("server") || normalized.contains("admin") {
            return "server".to_string();
        }
        if normalized.contains("all") {
            return "all".to_string();
        }
        if normalized.contains("team")
            || normalized.contains("chat_t")
            || normalized.contains("chat_ct")
        {
            return "team".to_string();
        }
        "all".to_string()
    }

    #[derive(Clone, Debug, Eq, Hash, PartialEq)]
    struct ProjectileKey {
        steam_id: u64,
        grenade_type: String,
        initial_position: [u32; 3],
        initial_velocity: [u32; 3],
    }

    impl ProjectileKey {
        fn new(
            steam_id: u64,
            grenade_type: &str,
            initial_position: [f32; 3],
            initial_velocity: [f32; 3],
        ) -> Self {
            Self {
                steam_id,
                grenade_type: grenade_type.to_string(),
                initial_position: initial_position.map(f32::to_bits),
                initial_velocity: initial_velocity.map(f32::to_bits),
            }
        }
    }

    fn vec3_is_meaningful(value: [f32; 3]) -> bool {
        value.iter().all(|component| component.is_finite())
            && value.iter().any(|component| component.abs() > f32::EPSILON)
    }

    fn seconds_to_ticks(seconds: f32, tick_rate: f32) -> i32 {
        if !seconds.is_finite() || !tick_rate.is_finite() || seconds <= 0.0 || tick_rate <= 0.0 {
            return 0;
        }
        (seconds * tick_rate).round() as i32
    }

    const MAX_REPAIRABLE_GLOBAL_GAP_TICKS: i32 = 8;
    const MAX_PLAYER_VELOCITY_COMPONENT: f32 = 4096.0;

    /// Some GOTV demos omit a short run of complete entity snapshot ticks even
    /// though the surrounding ticks and later game state are valid. Fill only
    /// those unambiguous global holes, capped at 125 ms for a 64-tick demo.
    /// Per-player holes, lifecycle changes, and longer gaps remain untouched so
    /// replay synthesis can still fail closed instead of inventing an unsafe
    /// trajectory.
    fn repair_short_global_tick_gaps(rows: &mut Vec<ParsedPlayerTick>) -> usize {
        if rows.len() < 2 {
            return 0;
        }

        let global_ticks = rows
            .iter()
            .map(|row| row.tick)
            .collect::<BTreeSet<_>>()
            .into_iter()
            .collect::<Vec<_>>();
        let missing_tick_ranges = global_ticks
            .windows(2)
            .filter_map(|pair| {
                let missing_count = pair[1].saturating_sub(pair[0]).saturating_sub(1);
                (1..=MAX_REPAIRABLE_GLOBAL_GAP_TICKS)
                    .contains(&missing_count)
                    .then_some((pair[0], pair[1]))
            })
            .collect::<Vec<_>>();
        if missing_tick_ranges.is_empty() {
            return 0;
        }

        let mut row_index = BTreeMap::new();
        let mut ambiguous = BTreeSet::new();
        for (index, row) in rows.iter().enumerate() {
            if row.steam_id == 0 {
                continue;
            }
            let key = (row.tick, row.steam_id);
            if row_index.insert(key, index).is_some() {
                ambiguous.insert(key);
            }
        }

        let mut additions = Vec::new();
        for (before_tick, after_tick) in missing_tick_ranges {
            let steam_ids = row_index
                .range((before_tick, 0)..=(before_tick, u64::MAX))
                .map(|((_tick, steam_id), _index)| *steam_id)
                .collect::<BTreeSet<_>>();
            for steam_id in steam_ids {
                let before_key = (before_tick, steam_id);
                let after_key = (after_tick, steam_id);
                if ambiguous.contains(&before_key) || ambiguous.contains(&after_key) {
                    continue;
                }
                let (Some(&before_index), Some(&after_index)) =
                    (row_index.get(&before_key), row_index.get(&after_key))
                else {
                    continue;
                };
                let before = &rows[before_index];
                let after = &rows[after_index];
                if !stable_across_missing_tick(before, after) {
                    continue;
                }
                for missing_tick in before_tick + 1..after_tick {
                    additions.push(interpolate_missing_tick(before, after, missing_tick));
                }
            }
        }

        let repaired = additions.len();
        rows.extend(additions);
        rows.sort_by_key(|row| (row.round, row.tick, row.steam_id));
        repaired
    }

    /// CS2 demos expose player origins but not the pawn's actual locomotion
    /// velocity (`m_vecVelocity`/`m_vecAbsVelocity`). Derive the observed
    /// velocity after rows are ordered, using the detected demo tick rate and
    /// refusing to differentiate across a round or pawn lifecycle edge.
    fn derive_observed_player_velocities(rows: &mut [ParsedPlayerTick], tick_rate: f32) {
        if !tick_rate.is_finite() || tick_rate <= 0.0 {
            return;
        }

        let mut previous = BTreeMap::<u64, (u32, i32, u8, bool, Option<i32>, [f32; 3])>::new();
        for row in rows {
            let velocity = previous
                .get(&row.steam_id)
                .and_then(|(round, tick, team_num, is_alive, entity_id, origin)| {
                    let tick_delta = row.tick.checked_sub(*tick)?;
                    let same_entity = match (*entity_id, row.player_entity_id) {
                        (Some(before), Some(after)) => before == after,
                        (None, None) => true,
                        _ => false,
                    };
                    if row.steam_id == 0
                        || row.round != *round
                        || row.team_num != *team_num
                        || !*is_alive
                        || !row.is_alive
                        || !same_entity
                        || tick_delta <= 0
                    {
                        return None;
                    }

                    let scale = tick_rate / tick_delta as f32;
                    let velocity =
                        std::array::from_fn(|axis| (row.origin[axis] - origin[axis]) * scale);
                    velocity
                        .iter()
                        .all(|component| {
                            component.is_finite()
                                && component.abs() <= MAX_PLAYER_VELOCITY_COMPONENT
                        })
                        .then_some(velocity)
                })
                .unwrap_or([0.0, 0.0, 0.0]);

            row.velocity = velocity;
            previous.insert(
                row.steam_id,
                (
                    row.round,
                    row.tick,
                    row.team_num,
                    row.is_alive,
                    row.player_entity_id,
                    row.origin,
                ),
            );
        }
    }

    fn stable_across_missing_tick(before: &ParsedPlayerTick, after: &ParsedPlayerTick) -> bool {
        // A complete GOTV snapshot gap can straddle round_freeze_end, so the
        // phase flags may legitimately differ across it. The authoritative
        // event pass below repairs those flags after interpolation. Keep the
        // player and pawn lifecycle checks strict here.
        before.steam_id == after.steam_id
            && before.round == after.round
            && before.team_num == after.team_num
            && before.is_alive == after.is_alive
            && before.player_user_id == after.player_user_id
            && before.player_entity_id == after.player_entity_id
    }

    fn interpolate_missing_tick(
        before: &ParsedPlayerTick,
        after: &ParsedPlayerTick,
        tick: i32,
    ) -> ParsedPlayerTick {
        let mut row = before.clone();
        row.tick = tick;
        let tick_fraction = (tick - before.tick) as f32 / (after.tick - before.tick) as f32;
        row.origin = lerp_vec3(before.origin, after.origin, tick_fraction);
        row.velocity = lerp_vec3(before.velocity, after.velocity, tick_fraction);
        row.pitch = lerp_angle(before.pitch, after.pitch, tick_fraction);
        row.yaw = lerp_angle(before.yaw, after.yaw, tick_fraction);
        row.game_time = match (before.game_time, after.game_time) {
            (Some(before), Some(after)) if before.is_finite() && after.is_finite() => {
                Some(before + (after - before) * tick_fraction)
            }
            _ => before.game_time,
        };
        row.duck_amount = lerp_option(before.duck_amount, after.duck_amount, tick_fraction);
        row.duck_speed = lerp_option(before.duck_speed, after.duck_speed, tick_fraction);
        row.ladder_normal = match (before.ladder_normal, after.ladder_normal) {
            (Some(before), Some(after)) => Some(lerp_vec3(before, after, tick_fraction)),
            _ => before.ladder_normal,
        };
        // A missing packet provides no defensible subtick timing. Keep the
        // previous command state but do not replay its subtick edges twice.
        row.subtick_moves.clear();
        row.subtick_button_truncated = 0;
        row.usercmd_client_tick = None;
        row.usercmd_attack1_start_history_index = -1;
        row.usercmd_attack2_start_history_index = -1;
        row.input_history.clear();
        row
    }

    fn lerp_vec3(before: [f32; 3], after: [f32; 3], fraction: f32) -> [f32; 3] {
        [
            before[0] + (after[0] - before[0]) * fraction,
            before[1] + (after[1] - before[1]) * fraction,
            before[2] + (after[2] - before[2]) * fraction,
        ]
    }

    fn lerp_angle(before: f32, after: f32, fraction: f32) -> f32 {
        let delta = (after - before + 180.0).rem_euclid(360.0) - 180.0;
        before + delta * fraction
    }

    fn lerp_option(before: Option<f32>, after: Option<f32>, fraction: f32) -> Option<f32> {
        match (before, after) {
            (Some(before), Some(after)) if before.is_finite() && after.is_finite() => {
                Some(before + (after - before) * fraction)
            }
            _ => before,
        }
    }

    fn repair_round_phase_after_events(
        rows: &mut [ParsedPlayerTick],
        freeze_end_ticks: &[i32],
        round_end_ticks: &[i32],
    ) {
        if rows.is_empty() || freeze_end_ticks.is_empty() {
            return;
        }

        let mut start = 0;
        while start < rows.len() {
            let round = rows[start].round;
            let mut end = start + 1;
            while end < rows.len() && rows[end].round == round {
                end += 1;
            }

            let phase = {
                let round_rows = &rows[start..end];
                let min_tick = round_rows[0].tick;
                let max_tick = round_rows[round_rows.len() - 1].tick;
                let first_candidate = freeze_end_ticks.partition_point(|tick| *tick < min_tick);
                let after_last_candidate =
                    freeze_end_ticks.partition_point(|tick| *tick <= max_tick);
                let candidates = &freeze_end_ticks[first_candidate..after_last_candidate];

                if is_pistol_round_number(round) {
                    candidates.last().copied().map(|freeze_end| {
                        let round_end = round_end_after(round_end_ticks, freeze_end, max_tick);
                        (freeze_end, round_end, (0, 0))
                    })
                } else {
                    candidates
                        .iter()
                        .copied()
                        .map(|freeze_end| {
                            let round_end = round_end_after(round_end_ticks, freeze_end, max_tick);
                            let score = round_phase_candidate_score(
                                round_rows, max_tick, freeze_end, round_end,
                            );
                            (freeze_end, round_end, score)
                        })
                        .max_by_key(|(freeze_end, _round_end, score)| (*score, *freeze_end))
                }
            };

            if let Some((freeze_end, round_end, _score)) = phase {
                for row in &mut rows[start..end] {
                    if row.tick >= freeze_end {
                        row.is_freeze_period = false;
                    }
                    if row.tick >= freeze_end
                        && round_end.map_or(true, |round_end| row.tick < round_end)
                    {
                        row.round_in_progress = true;
                    } else if round_end.is_some_and(|round_end| row.tick >= round_end) {
                        row.round_in_progress = false;
                    }
                }
            }
            start = end;
        }
    }

    fn is_pistol_round_number(round: u32) -> bool {
        round == 0 || round == 12
    }

    fn round_end_after(round_end_ticks: &[i32], freeze_end: i32, max_tick: i32) -> Option<i32> {
        round_end_ticks
            .iter()
            .copied()
            .filter(|tick| *tick >= freeze_end && *tick <= max_tick)
            .min()
    }

    fn round_phase_candidate_score(
        round_rows: &[ParsedPlayerTick],
        max_tick: i32,
        freeze_end: i32,
        round_end: Option<i32>,
    ) -> (usize, usize) {
        let live_end = round_end.unwrap_or(max_tick.saturating_add(1));
        let mut players = BTreeSet::new();
        let mut live_rows = 0_usize;
        for row in round_rows {
            if row.tick < freeze_end
                || row.tick >= live_end
                || !row.is_alive
                || row.steam_id == 0
                || !matches!(row.team_num, 2 | 3)
            {
                continue;
            }
            players.insert(row.steam_id);
            live_rows += 1;
        }
        (players.len(), live_rows)
    }

    fn event_ticks(events: &[parser::second_pass::game_events::GameEvent], name: &str) -> Vec<i32> {
        events
            .iter()
            .filter(|event| event.name == name)
            .map(|event| event.tick)
            .collect()
    }

    fn estimate_tick_rate(rows: &[ParsedPlayerTick]) -> Option<f32> {
        let mut first = None;
        let mut last = None;
        for row in rows {
            if let Some(time) = row.game_time {
                if time.is_finite() {
                    first.get_or_insert((row.tick, time));
                    last = Some((row.tick, time));
                }
            }
        }
        let (first_tick, first_time) = first?;
        let (last_tick, last_time) = last?;
        let dt = last_time - first_time;
        let ticks = (last_tick - first_tick) as f32;
        if dt > 0.0 && ticks > 0.0 {
            let rate = ticks / dt;
            if (16.0..=256.0).contains(&rate) {
                return Some(rate.round());
            }
        }
        None
    }

    fn get_f32(column: Option<&PropColumn>, idx: usize) -> Option<f32> {
        match column?.data.as_ref()? {
            VarVec::F32(v) => v.get(idx).copied().flatten(),
            VarVec::I32(v) => v.get(idx).copied().flatten().map(|v| v as f32),
            VarVec::U32(v) => v.get(idx).copied().flatten().map(|v| v as f32),
            _ => None,
        }
    }

    fn get_i32(column: Option<&PropColumn>, idx: usize) -> Option<i32> {
        match column?.data.as_ref()? {
            VarVec::I32(v) => v.get(idx).copied().flatten(),
            VarVec::U32(v) => v.get(idx).copied().flatten().map(|v| v as i32),
            VarVec::U64(v) => v.get(idx).copied().flatten().map(|v| v as i32),
            VarVec::F32(v) => v.get(idx).copied().flatten().and_then(f32_to_whole_i32),
            _ => None,
        }
    }

    fn f32_to_whole_i32(value: f32) -> Option<i32> {
        if value.is_finite()
            && value.fract() == 0.0
            && value >= i32::MIN as f32
            && value <= i32::MAX as f32
        {
            Some(value as i32)
        } else {
            None
        }
    }

    fn get_u32(column: Option<&PropColumn>, idx: usize) -> Option<u32> {
        match column?.data.as_ref()? {
            VarVec::U32(v) => v.get(idx).copied().flatten(),
            VarVec::I32(v) => v.get(idx).copied().flatten().map(|v| v as u32),
            VarVec::U64(v) => v.get(idx).copied().flatten().map(|v| v as u32),
            VarVec::F32(v) => v.get(idx).copied().flatten().and_then(f32_to_whole_u32),
            _ => None,
        }
    }

    fn f32_to_whole_u32(value: f32) -> Option<u32> {
        if value.is_finite() && value >= 0.0 && value.fract() == 0.0 && value <= u32::MAX as f32 {
            Some(value as u32)
        } else {
            None
        }
    }

    fn get_u64(column: Option<&PropColumn>, idx: usize) -> Option<u64> {
        match column?.data.as_ref()? {
            VarVec::U64(v) => v.get(idx).copied().flatten(),
            VarVec::U32(v) => v.get(idx).copied().flatten().map(u64::from),
            VarVec::I32(v) => v.get(idx).copied().flatten().map(|v| v as u64),
            VarVec::String(v) => v
                .get(idx)
                .and_then(|v| v.as_ref())
                .and_then(|v| v.parse::<u64>().ok()),
            _ => None,
        }
    }

    fn get_u32_vec(column: Option<&PropColumn>, idx: usize) -> Option<Vec<u32>> {
        match column?.data.as_ref()? {
            VarVec::U32Vec(v) => v.get(idx).cloned(),
            _ => None,
        }
    }

    fn get_vec3(column: Option<&PropColumn>, idx: usize) -> Option<[f32; 3]> {
        match column?.data.as_ref()? {
            VarVec::XYZVec(v) => v.get(idx).copied().flatten(),
            _ => None,
        }
    }

    fn get_subtick_moves(
        column: Option<&PropColumn>,
        idx: usize,
    ) -> Option<(Vec<SubtickMove>, usize)> {
        match column?.data.as_ref()? {
            VarVec::UserCmdSubtickMoves(v) => {
                let raw = v.get(idx)?;
                let mut truncated = 0_usize;
                let moves = raw
                    .iter()
                    .map(|subtick| {
                        if subtick.button > u32::MAX as u64 {
                            truncated += 1;
                        }
                        SubtickMove {
                            when: subtick.when,
                            button: subtick.button as u32,
                            pressed: if subtick.pressed { 1.0 } else { 0.0 },
                            analog_forward: subtick.analog_forward,
                            analog_left: subtick.analog_left,
                            pitch_delta: subtick.pitch_delta,
                            yaw_delta: subtick.yaw_delta,
                        }
                    })
                    .collect();
                Some((moves, truncated))
            }
            _ => None,
        }
    }

    fn get_input_history(
        column: Option<&PropColumn>,
        idx: usize,
    ) -> Option<Vec<ReplayInputHistoryEntry>> {
        let raw = match column?.data.as_ref()? {
            VarVec::InputHistory(values) => values.get(idx)?,
            _ => return None,
        };
        Some(raw.iter().map(map_input_history_entry).collect())
    }

    fn map_input_history_entry(raw: &ParserInputHistory) -> ReplayInputHistoryEntry {
        use crate::model::*;

        let mut out = ReplayInputHistoryEntry::default();
        macro_rules! set_scalar {
            ($source:expr, $field:ident, $flag:ident) => {
                if let Some(value) = $source {
                    out.$field = value;
                    out.fields |= $flag;
                }
            };
        }
        set_scalar!(
            raw.view_angles,
            view_angles,
            INPUT_HISTORY_FIELD_VIEW_ANGLES
        );
        set_scalar!(
            raw.render_tick_count,
            render_tick_count,
            INPUT_HISTORY_FIELD_RENDER_TICK_COUNT
        );
        set_scalar!(
            raw.render_tick_fraction,
            render_tick_fraction,
            INPUT_HISTORY_FIELD_RENDER_TICK_FRACTION
        );
        set_scalar!(
            raw.player_tick_count,
            player_tick_count,
            INPUT_HISTORY_FIELD_PLAYER_TICK_COUNT
        );
        set_scalar!(
            raw.player_tick_fraction,
            player_tick_fraction,
            INPUT_HISTORY_FIELD_PLAYER_TICK_FRACTION
        );
        set_scalar!(
            raw.cl_interp_fraction,
            cl_interp_fraction,
            INPUT_HISTORY_FIELD_CL_INTERP_FRACTION
        );
        map_input_history_interp(
            raw.sv_interp0,
            &mut out.sv_interp0_src_tick,
            &mut out.sv_interp0_dst_tick,
            &mut out.sv_interp0_fraction,
            &mut out.fields,
            INPUT_HISTORY_FIELD_SV_INTERP0_SRC_TICK,
            INPUT_HISTORY_FIELD_SV_INTERP0_DST_TICK,
            INPUT_HISTORY_FIELD_SV_INTERP0_FRACTION,
        );
        map_input_history_interp(
            raw.sv_interp1,
            &mut out.sv_interp1_src_tick,
            &mut out.sv_interp1_dst_tick,
            &mut out.sv_interp1_fraction,
            &mut out.fields,
            INPUT_HISTORY_FIELD_SV_INTERP1_SRC_TICK,
            INPUT_HISTORY_FIELD_SV_INTERP1_DST_TICK,
            INPUT_HISTORY_FIELD_SV_INTERP1_FRACTION,
        );
        map_input_history_interp(
            raw.player_interp,
            &mut out.player_interp_src_tick,
            &mut out.player_interp_dst_tick,
            &mut out.player_interp_fraction,
            &mut out.fields,
            INPUT_HISTORY_FIELD_PLAYER_INTERP_SRC_TICK,
            INPUT_HISTORY_FIELD_PLAYER_INTERP_DST_TICK,
            INPUT_HISTORY_FIELD_PLAYER_INTERP_FRACTION,
        );
        set_scalar!(
            raw.frame_number,
            frame_number,
            INPUT_HISTORY_FIELD_FRAME_NUMBER
        );
        set_scalar!(
            raw.target_ent_index,
            target_ent_index,
            INPUT_HISTORY_FIELD_TARGET_ENT_INDEX
        );
        set_scalar!(
            raw.shoot_position,
            shoot_position,
            INPUT_HISTORY_FIELD_SHOOT_POSITION
        );
        set_scalar!(
            raw.target_head_pos_check,
            target_head_pos_check,
            INPUT_HISTORY_FIELD_TARGET_HEAD_POS_CHECK
        );
        set_scalar!(
            raw.target_abs_pos_check,
            target_abs_pos_check,
            INPUT_HISTORY_FIELD_TARGET_ABS_POS_CHECK
        );
        set_scalar!(
            raw.target_abs_ang_check,
            target_abs_ang_check,
            INPUT_HISTORY_FIELD_TARGET_ABS_ANG_CHECK
        );
        out
    }

    #[allow(clippy::too_many_arguments)]
    fn map_input_history_interp(
        raw: Option<parser::second_pass::variants::InputHistoryInterpolation>,
        src_tick: &mut i32,
        dst_tick: &mut i32,
        fraction: &mut f32,
        fields: &mut u32,
        src_flag: u32,
        dst_flag: u32,
        fraction_flag: u32,
    ) {
        let Some(raw) = raw else { return };
        if let Some(value) = raw.src_tick {
            *src_tick = value;
            *fields |= src_flag;
        }
        if let Some(value) = raw.dst_tick {
            *dst_tick = value;
            *fields |= dst_flag;
        }
        if let Some(value) = raw.fraction {
            *fraction = value;
            *fields |= fraction_flag;
        }
    }

    fn get_bool(column: Option<&PropColumn>, idx: usize) -> Option<bool> {
        match column?.data.as_ref()? {
            VarVec::Bool(v) => v.get(idx).copied().flatten(),
            VarVec::U32(v) => v.get(idx).copied().flatten().map(|v| v != 0),
            VarVec::I32(v) => v.get(idx).copied().flatten().map(|v| v != 0),
            _ => None,
        }
    }

    fn get_string(column: Option<&PropColumn>, idx: usize) -> Option<String> {
        match column?.data.as_ref()? {
            VarVec::String(v) => v.get(idx).cloned().flatten(),
            _ => None,
        }
    }

    fn take_weapon_stickers(
        column: &mut Option<PropColumn>,
        idx: usize,
    ) -> Option<Vec<ParsedWeaponSticker>> {
        let stickers = match column.as_mut()?.data.as_mut()? {
            VarVec::Stickers(v) => std::mem::take(v.get_mut(idx)?),
            _ => return None,
        };
        let parsed = stickers
            .into_iter()
            .filter_map(|sticker| {
                let slot = u8::try_from(sticker.slot).ok()?;
                if slot > 4
                    || sticker.id == 0
                    || !sticker.wear.is_finite()
                    || !(0.0..=1.0).contains(&sticker.wear)
                    || !sticker.x.is_finite()
                    || !sticker.y.is_finite()
                    || sticker.scale.is_some_and(|value| !value.is_finite())
                    || sticker.rotation.is_some_and(|value| !value.is_finite())
                {
                    return None;
                }
                Some(ParsedWeaponSticker {
                    slot,
                    sticker_id: sticker.id,
                    wear: sticker.wear,
                    offset_x: sticker.x,
                    offset_y: sticker.y,
                    scale: sticker.scale,
                    rotation: sticker.rotation,
                })
            })
            .collect::<Vec<_>>();
        (!parsed.is_empty()).then_some(parsed)
    }

    fn take_inventory_weapon_cosmetics(
        column: &mut Option<PropColumn>,
        idx: usize,
        cache: &mut ParsedInventoryCache,
    ) -> Option<Arc<[ParsedInventoryWeaponCosmetic]>> {
        let weapons = match column.as_mut()?.data.as_mut()? {
            VarVec::InventoryWeaponCosmetics(v) => std::mem::take(v.get_mut(idx)?),
            _ => return None,
        };
        let cache_key = (weapons.as_ptr() as usize, weapons.len());
        if let Some((cached_source, cached_parsed)) = cache.get(&cache_key) {
            debug_assert!(Arc::ptr_eq(cached_source, &weapons));
            return Some(Arc::clone(cached_parsed));
        }
        let parsed = weapons
            .iter()
            .filter_map(|weapon| {
                let item_def_index = i32::try_from(weapon.item_def_index).ok()?;
                let stickers = weapon
                    .stickers
                    .iter()
                    .filter_map(|sticker| {
                        let slot = u8::try_from(sticker.slot).ok()?;
                        if slot > 4
                            || sticker.id == 0
                            || !sticker.wear.is_finite()
                            || !(0.0..=1.0).contains(&sticker.wear)
                            || !sticker.x.is_finite()
                            || !sticker.y.is_finite()
                            || sticker.scale.is_some_and(|value| !value.is_finite())
                            || sticker.rotation.is_some_and(|value| !value.is_finite())
                        {
                            return None;
                        }
                        Some(ParsedWeaponSticker {
                            slot,
                            sticker_id: sticker.id,
                            wear: sticker.wear,
                            offset_x: sticker.x,
                            offset_y: sticker.y,
                            scale: sticker.scale,
                            rotation: sticker.rotation,
                        })
                    })
                    .collect::<Vec<_>>();
                Some(ParsedInventoryWeaponCosmetic {
                    item_def_index,
                    item_id_high: weapon.item_id_high,
                    item_id_low: weapon.item_id_low,
                    item_account_id: weapon.item_account_id,
                    original_owner_xuid: weapon.original_owner_xuid,
                    paint_kit: weapon.paint_kit,
                    paint_seed: weapon.paint_seed,
                    paint_wear: weapon.paint_wear,
                    entity_quality: weapon.entity_quality,
                    stattrak_counter: weapon.stattrak_counter,
                    attributes: weapon
                        .attributes
                        .iter()
                        .map(|attribute| ParsedInventoryWeaponAttribute {
                            definition_index: attribute.definition_index,
                            raw_value: attribute.raw_value,
                            raw_value_bits: attribute.raw_value_bits,
                        })
                        .collect(),
                    custom_name: weapon.custom_name.clone().and_then(normalize_custom_name),
                    stickers,
                })
            })
            .collect::<Arc<[_]>>();
        if parsed.is_empty() {
            return None;
        }
        cache.insert(cache_key, (weapons, Arc::clone(&parsed)));
        Some(parsed)
    }

    fn get_scoreboard_flair(
        column: Option<&PropColumn>,
        idx: usize,
    ) -> Option<ParsedScoreboardFlair> {
        let item_def_index = get_u64(column, idx).and_then(|value| u32::try_from(value).ok())?;

        Some(ParsedScoreboardFlair { item_def_index })
    }

    fn combine_item_id(high: Option<u32>, low: Option<u32>) -> Option<u64> {
        Some((u64::from(high?) << 32) | u64::from(low?))
    }

    fn normalize_crosshair_code(value: String) -> Option<String> {
        let trimmed = value.trim();
        if trimmed.is_empty() || trimmed.len() > 128 {
            None
        } else {
            Some(trimmed.to_string())
        }
    }

    fn normalize_agent_skin(value: String) -> Option<String> {
        let trimmed = value.trim();
        if trimmed.is_empty()
            || trimmed.len() > 128
            || !trimmed
                .chars()
                .all(|ch| ch.is_ascii_alphanumeric() || ch == '_')
        {
            return None;
        }
        Some(trimmed.to_ascii_lowercase())
    }

    fn normalize_custom_name(value: String) -> Option<String> {
        let trimmed = value.trim().trim_matches('\0').trim();
        if trimmed.is_empty() {
            return None;
        }
        let cleaned = trimmed
            .chars()
            .filter(|ch| !ch.is_control() || *ch == '\t')
            .collect::<String>();
        let cleaned = cleaned.trim();
        if cleaned.is_empty() {
            None
        } else {
            Some(cleaned.chars().take(128).collect())
        }
    }

    fn header_text(header: &AHashMap<String, String>, key: &str) -> Option<String> {
        header
            .get(key)
            .map(String::as_str)
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(str::to_string)
    }

    fn header_i32(header: &AHashMap<String, String>, key: &str) -> Option<i32> {
        header_text(header, key).and_then(|value| value.parse::<i32>().ok())
    }

    fn header_f32(header: &AHashMap<String, String>, key: &str) -> Option<f32> {
        header_text(header, key).and_then(|value| value.parse::<f32>().ok())
    }

    #[cfg(test)]
    mod tests {
        use super::*;
        use std::sync::atomic::AtomicBool;

        #[test]
        fn cancelled_read_stops_before_touching_demo_bytes() {
            let cancelled = AtomicBool::new(true);
            let error = read_demo_bytes_with_options_and_cancel(
                &[],
                "cancelled",
                "cancelled.dem",
                ReadDemoOptions::default(),
                Some(&cancelled),
            )
            .unwrap_err();

            assert!(matches!(error, Error::Parser(message) if message == "Cancelled"));
        }

        #[test]
        fn demo_version_headers_are_trimmed_and_optional() {
            let mut header = AHashMap::default();
            header.insert("patch_version".to_string(), " 14228 ".to_string());
            header.insert("demo_version_name".to_string(), " 2.0.0.0 ".to_string());

            assert_eq!(header_i32(&header, "patch_version"), Some(14_228));
            assert_eq!(
                header_text(&header, "demo_version_name").as_deref(),
                Some("2.0.0.0")
            );

            header.insert("patch_version".to_string(), "invalid".to_string());
            header.insert("demo_version_name".to_string(), "   ".to_string());
            assert_eq!(header_i32(&header, "patch_version"), None);
            assert_eq!(header_text(&header, "demo_version_name"), None);
        }

        #[test]
        fn playback_time_header_is_parsed_as_seconds() {
            let mut header = AHashMap::default();
            header.insert("playback_time".to_string(), " 3034.921875 ".to_string());

            assert_eq!(header_f32(&header, "playback_time"), Some(3034.921875));
        }

        #[test]
        fn default_avatar_override_expands_to_demo_players() {
            let png = b"\x89PNG\r\n\x1a\nblast".to_vec();
            let expected_sha256 = crate::demo_id::sha256_hex(&png);

            let parsed = parse_avatar_overrides(
                vec![("default".to_string(), png.clone())],
                [76561198000000002, 0, 76561198000000001, 76561198000000002],
            );

            assert_eq!(parsed.len(), 2);
            assert_eq!(parsed[0].steam_id, 76561198000000001);
            assert_eq!(parsed[1].steam_id, 76561198000000002);
            assert!(parsed
                .iter()
                .all(|avatar| avatar.sha256 == expected_sha256 && avatar.bytes == png));
        }

        #[test]
        fn player_avatar_override_takes_precedence_over_default() {
            let default_png = b"\x89PNG\r\n\x1a\ndefault".to_vec();
            let player_png = b"\x89PNG\r\n\x1a\nplayer".to_vec();
            let player_sha256 = crate::demo_id::sha256_hex(&player_png);

            let parsed = parse_avatar_overrides(
                vec![
                    ("default".to_string(), default_png),
                    ("76561198000000001".to_string(), player_png),
                ],
                [76561198000000001],
            );

            assert_eq!(parsed.len(), 1);
            assert_eq!(parsed[0].steam_id, 76561198000000001);
            assert_eq!(parsed[0].sha256, player_sha256);
        }

        #[test]
        fn round_winner_accepts_numeric_and_named_sides() {
            fn round_end(data: Variant) -> GameEvent {
                GameEvent {
                    name: "round_end".to_string(),
                    tick: 100,
                    fields: vec![parser::second_pass::game_events::EventField {
                        name: "winner".to_string(),
                        data: Some(data),
                    }],
                }
            }

            assert_eq!(event_winner_side(&round_end(Variant::I32(2))), Some(2));
            assert_eq!(
                event_winner_side(&round_end(Variant::String(
                    "Counter-Terrorists".to_string()
                ))),
                Some(3)
            );
            assert_eq!(event_winner_side(&round_end(Variant::I32(1))), None);
        }

        #[test]
        fn lean_read_skips_only_opt_in_cosmetic_props() {
            let lean = ReadDemoOptions {
                collect_voice: false,
                collect_cosmetics: false,
            };

            assert_eq!(COSMETIC_PLAYER_PROPS.len(), 16);
            assert_eq!(
                COSMETIC_PLAYER_PROPS
                    .iter()
                    .copied()
                    .collect::<BTreeSet<_>>()
                    .len(),
                COSMETIC_PLAYER_PROPS.len()
            );
            for prop in COSMETIC_PLAYER_PROPS {
                assert!(!should_collect_player_prop(lean, prop), "{prop}");
                assert!(should_collect_player_prop(ReadDemoOptions::default(), prop));
            }
            for prop in [
                "item_def_idx",
                "inventory_as_ids",
                "music_kit_id",
                "CCSPlayerController.CCSPlayerController_InventoryServices.m_rank",
                "crosshair_code",
            ] {
                assert!(should_collect_player_prop(lean, prop), "{prop}");
            }
        }

        #[test]
        fn strict_movement_alignment_accepts_identical_duplicate_rows() {
            let tick = i32_column(&[Some(100), Some(100), Some(101)]);
            let entity_id = i32_column(&[Some(7), Some(7), Some(9)]);
            let steam_id = u64_column(&[Some(70), Some(70), Some(90)]);
            let round = i32_column(&[Some(1), Some(1), None]);

            assert!(strict_movement_keys_match(
                &tick, &entity_id, &steam_id, &round, &tick, &entity_id, &steam_id, &round,
            ));
        }

        #[test]
        fn strict_movement_alignment_rejects_reordered_equal_multiset() {
            let left_tick = i32_column(&[Some(100), Some(100), Some(101)]);
            let left_entity_id = i32_column(&[Some(7), Some(7), Some(9)]);
            let left_steam_id = u64_column(&[Some(70), Some(70), Some(90)]);
            let left_round = i32_column(&[Some(1), Some(1), Some(1)]);
            let right_tick = i32_column(&[Some(100), Some(101), Some(100)]);
            let right_entity_id = i32_column(&[Some(7), Some(9), Some(7)]);
            let right_steam_id = u64_column(&[Some(70), Some(90), Some(70)]);
            let right_round = i32_column(&[Some(1), Some(1), Some(1)]);

            assert!(!strict_movement_keys_match(
                &left_tick,
                &left_entity_id,
                &left_steam_id,
                &left_round,
                &right_tick,
                &right_entity_id,
                &right_steam_id,
                &right_round,
            ));
        }

        #[test]
        fn strict_movement_alignment_rejects_missing_or_wrong_type_keys() {
            let tick = i32_column(&[Some(100), Some(101)]);
            let missing_entity_id = i32_column(&[Some(7), None]);
            let entity_id = i32_column(&[Some(7), Some(9)]);
            let steam_id = u64_column(&[Some(70), Some(90)]);
            let round = i32_column(&[Some(1), Some(1)]);
            assert!(!strict_movement_keys_match(
                &tick,
                &missing_entity_id,
                &steam_id,
                &round,
                &tick,
                &entity_id,
                &steam_id,
                &round,
            ));

            let wrong_tick_type = u32_column(&[Some(100), Some(101)]);
            assert!(!strict_movement_keys_match(
                &wrong_tick_type,
                &entity_id,
                &steam_id,
                &round,
                &tick,
                &entity_id,
                &steam_id,
                &round,
            ));
        }

        #[test]
        fn all_none_columns_remain_a_valid_overlay() {
            let overlay = all_none_overlay(3);
            assert_eq!(overlay.columns.len(), SINGLE_THREADED_OVERLAY_PROPS.len());
            for friendly_name in SINGLE_THREADED_OVERLAY_PROPS {
                let column = overlay.get(friendly_name).unwrap();
                assert!(overlay_column_type_is_valid(friendly_name, column));
                assert_eq!(column.len(), 3);
            }
        }

        #[test]
        fn global_single_tick_gap_is_interpolated_for_stable_players() {
            let mut rows = vec![
                gap_row(70, 10, 0.0),
                gap_row(90, 10, 20.0),
                gap_row(70, 12, 4.0),
                gap_row(90, 12, 24.0),
            ];
            rows[0].yaw = 179.0;
            rows[2].yaw = -179.0;

            assert_eq!(repair_short_global_tick_gaps(&mut rows), 2);
            let repaired = rows
                .iter()
                .find(|row| row.steam_id == 70 && row.tick == 11)
                .unwrap();
            assert_eq!(repaired.origin, [2.0, 1.0, 2.0]);
            assert_eq!(repaired.velocity, [3.0, 4.0, 5.0]);
            assert_eq!(repaired.game_time, Some(11.0));
            assert_eq!(repaired.yaw, 180.0);
            assert!(repaired.subtick_moves.is_empty());
        }

        #[test]
        fn global_tick_gap_across_freeze_end_is_repaired() {
            let mut rows = vec![
                gap_row(70, 10, 0.0),
                gap_row(90, 10, 20.0),
                gap_row(70, 12, 4.0),
                gap_row(90, 12, 24.0),
            ];
            for row in &mut rows[..2] {
                row.round_in_progress = false;
                row.is_freeze_period = true;
            }
            for row in &mut rows[2..] {
                row.round_in_progress = true;
                row.is_freeze_period = false;
            }

            assert_eq!(repair_short_global_tick_gaps(&mut rows), 2);
            repair_round_phase_after_events(&mut rows, &[11], &[]);

            for steam_id in [70, 90] {
                let freeze = rows
                    .iter()
                    .find(|row| row.steam_id == steam_id && row.tick == 10)
                    .unwrap();
                let repaired = rows
                    .iter()
                    .find(|row| row.steam_id == steam_id && row.tick == 11)
                    .unwrap();
                assert!(freeze.is_freeze_period);
                assert!(!freeze.round_in_progress);
                assert!(!repaired.is_freeze_period);
                assert!(repaired.round_in_progress);
            }
        }

        #[test]
        fn short_global_tick_gap_is_linearly_interpolated() {
            let mut rows = vec![
                gap_row(70, 10, 0.0),
                gap_row(90, 10, 20.0),
                gap_row(70, 15, 10.0),
                gap_row(90, 15, 30.0),
            ];
            rows[0].yaw = 170.0;
            rows[2].yaw = -170.0;

            assert_eq!(repair_short_global_tick_gaps(&mut rows), 8);
            let repaired = rows
                .iter()
                .find(|row| row.steam_id == 70 && row.tick == 12)
                .unwrap();
            assert_eq!(repaired.origin, [4.0, 1.0, 2.0]);
            assert_eq!(repaired.game_time, Some(12.0));
            assert_eq!(repaired.yaw, 178.0);
            assert!(repaired.subtick_moves.is_empty());
            assert!((11..15).all(|tick| rows
                .iter()
                .any(|row| row.steam_id == 90 && row.tick == tick)));
        }

        #[test]
        fn long_global_tick_gap_remains_rejected() {
            let mut rows = vec![
                gap_row(70, 10, 0.0),
                gap_row(90, 10, 20.0),
                gap_row(70, 20, 10.0),
                gap_row(90, 20, 30.0),
            ];

            assert_eq!(repair_short_global_tick_gaps(&mut rows), 0);
            assert!(!rows.iter().any(|row| (11..20).contains(&row.tick)));
        }

        #[test]
        fn player_only_gap_is_not_treated_as_a_global_packet_hole() {
            let mut rows = vec![
                gap_row(70, 10, 0.0),
                gap_row(90, 10, 20.0),
                gap_row(90, 11, 22.0),
                gap_row(70, 12, 4.0),
                gap_row(90, 12, 24.0),
            ];

            assert_eq!(repair_short_global_tick_gaps(&mut rows), 0);
            assert!(!rows.iter().any(|row| row.steam_id == 70 && row.tick == 11));
        }

        #[test]
        fn global_gap_does_not_invent_a_player_lifecycle_transition() {
            let mut rows = vec![
                gap_row(70, 10, 0.0),
                gap_row(90, 10, 20.0),
                gap_row(70, 12, 4.0),
                gap_row(90, 12, 24.0),
            ];
            rows[2].is_alive = false;

            assert_eq!(repair_short_global_tick_gaps(&mut rows), 1);
            assert!(!rows.iter().any(|row| row.steam_id == 70 && row.tick == 11));
            assert!(rows.iter().any(|row| row.steam_id == 90 && row.tick == 11));
        }

        #[test]
        fn rebuilt_velocity_matches_the_current_position_interval() {
            let mut rows = vec![
                gap_row(70, 10, 0.0),
                gap_row(70, 11, 1.0),
                gap_row(70, 12, 3.0),
            ];
            rows[0].velocity = [999.0, 999.0, 999.0];
            rows[1].velocity = [888.0, 888.0, 888.0];
            rows[2].velocity = [64.0, 0.0, 0.0];

            derive_observed_player_velocities(&mut rows, 64.0);

            assert_eq!(rows[0].velocity, [0.0, 0.0, 0.0]);
            assert_eq!(rows[1].velocity, [64.0, 0.0, 0.0]);
            assert_eq!(rows[2].velocity, [128.0, 0.0, 0.0]);
        }

        #[test]
        fn rebuilt_velocity_stops_at_round_and_pawn_boundaries() {
            let mut before = gap_row(70, 10, 0.0);
            before.round = 1;
            before.player_entity_id = Some(7);
            let mut new_round = gap_row(70, 11, 1000.0);
            new_round.round = 2;
            new_round.player_entity_id = Some(8);
            let mut same_pawn = gap_row(70, 12, 1001.0);
            same_pawn.round = 2;
            same_pawn.player_entity_id = Some(8);
            let mut rows = vec![before, new_round, same_pawn];

            derive_observed_player_velocities(&mut rows, 64.0);

            assert_eq!(rows[0].velocity, [0.0, 0.0, 0.0]);
            assert_eq!(rows[1].velocity, [0.0, 0.0, 0.0]);
            assert_eq!(rows[2].velocity, [64.0, 0.0, 0.0]);
        }

        #[test]
        fn freeze_end_event_repairs_sticky_freeze_period_rows() {
            let mut rows = vec![
                row(1, 90, true),
                row(1, 99, true),
                row(1, 100, true),
                row(1, 120, true),
                row(1, 130, true),
            ];

            repair_round_phase_after_events(&mut rows, &[100], &[130]);

            assert!(rows[0].is_freeze_period);
            assert!(rows[1].is_freeze_period);
            assert!(!rows[2].is_freeze_period);
            assert!(!rows[3].is_freeze_period);
            assert!(!rows[4].is_freeze_period);
            assert!(!rows[1].round_in_progress);
            assert!(rows[2].round_in_progress);
            assert!(rows[3].round_in_progress);
            assert!(!rows[4].round_in_progress);
        }

        #[test]
        fn pistol_round_uses_latest_freeze_end_candidate() {
            let mut rows = vec![
                row(0, 90, true),
                row(0, 120, true),
                row(0, 1_490, true),
                row(0, 1_500, true),
                row(0, 1_520, true),
            ];

            repair_round_phase_after_events(&mut rows, &[100, 1_500], &[2_000]);

            assert!(rows[0].is_freeze_period);
            assert!(rows[1].is_freeze_period);
            assert!(rows[2].is_freeze_period);
            assert!(!rows[3].is_freeze_period);
            assert!(!rows[4].is_freeze_period);
            assert!(!rows[2].round_in_progress);
            assert!(rows[3].round_in_progress);
            assert!(rows[4].round_in_progress);
        }

        #[test]
        fn phase_repair_prefers_candidate_with_live_player_rows() {
            let mut rows = vec![
                row(1, 110, true),
                row(1, 120, true),
                row(1, 190, true),
                live_row(1, 200, true),
                live_row(1, 220, true),
                live_row(1, 240, true),
            ];

            repair_round_phase_after_events(&mut rows, &[100, 200], &[130, 260]);

            assert!(rows[2].is_freeze_period);
            assert!(!rows[3].is_freeze_period);
            assert!(rows[3].round_in_progress);
            assert!(rows[4].round_in_progress);
            assert!(rows[5].round_in_progress);
        }

        fn row(round: u32, tick: i32, is_freeze_period: bool) -> ParsedPlayerTick {
            ParsedPlayerTick {
                round,
                tick,
                is_freeze_period,
                ..ParsedPlayerTick::default()
            }
        }

        fn live_row(round: u32, tick: i32, is_freeze_period: bool) -> ParsedPlayerTick {
            ParsedPlayerTick {
                steam_id: tick as u64,
                team_num: 2,
                is_alive: true,
                ..row(round, tick, is_freeze_period)
            }
        }

        fn gap_row(steam_id: u64, tick: i32, x: f32) -> ParsedPlayerTick {
            ParsedPlayerTick {
                tick,
                steam_id,
                team_num: 2,
                is_alive: true,
                round: 1,
                round_in_progress: true,
                origin: [x, 1.0, 2.0],
                velocity: [3.0, 4.0, 5.0],
                game_time: Some(tick as f32),
                player_user_id: Some(steam_id as i32),
                player_entity_id: Some(steam_id as i32),
                ..ParsedPlayerTick::default()
            }
        }

        fn i32_column(values: &[Option<i32>]) -> PropColumn {
            PropColumn {
                data: Some(VarVec::I32(values.to_vec())),
                num_nones: 0,
            }
        }

        fn u32_column(values: &[Option<u32>]) -> PropColumn {
            PropColumn {
                data: Some(VarVec::U32(values.to_vec())),
                num_nones: 0,
            }
        }

        fn u64_column(values: &[Option<u64>]) -> PropColumn {
            PropColumn {
                data: Some(VarVec::U64(values.to_vec())),
                num_nones: 0,
            }
        }
    }
}

#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo;
#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo_bytes;
#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo_bytes_with_options;
#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo_bytes_with_options_and_cancel;
#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo_header_map_bytes;
#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo_with_options;
#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_demo_with_options_and_cancel;
#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_loaded_demo_with_options;
#[cfg(feature = "demoparser")]
pub use demoparser_impl::read_loaded_demo_with_options_and_cancel;

#[cfg(not(feature = "demoparser"))]
pub fn read_demo(_path: &Path) -> Result<ParsedDemo> {
    Err(Error::FeatureDisabled("demoparser"))
}

#[cfg(not(feature = "demoparser"))]
pub fn read_demo_with_options(_path: &Path, _options: ReadDemoOptions) -> Result<ParsedDemo> {
    Err(Error::FeatureDisabled("demoparser"))
}

#[cfg(not(feature = "demoparser"))]
pub fn read_demo_bytes(_bytes: &[u8], _stem: &str, _display_path: &str) -> Result<ParsedDemo> {
    Err(Error::FeatureDisabled("demoparser"))
}

#[cfg(not(feature = "demoparser"))]
pub fn read_demo_bytes_with_options(
    _bytes: &[u8],
    _stem: &str,
    _display_path: &str,
    _options: ReadDemoOptions,
) -> Result<ParsedDemo> {
    Err(Error::FeatureDisabled("demoparser"))
}

#[cfg(not(feature = "demoparser"))]
pub fn read_loaded_demo_with_options(
    _input: &LoadedDemoInput,
    _options: ReadDemoOptions,
) -> Result<ParsedDemo> {
    Err(Error::FeatureDisabled("demoparser"))
}

#[cfg(not(feature = "demoparser"))]
pub fn read_demo_header_map_bytes(_bytes: &[u8]) -> Result<Option<String>> {
    Err(Error::FeatureDisabled("demoparser"))
}
