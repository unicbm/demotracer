// CS2 preview payload layout and checksum behavior are based on
// ianlucas/cs2-lib-inspect. See third_party/cs2-lib-inspect/README.vendor.md.
use crate::model::{
    ReplayCosmeticInspect, ReplayItemCosmetic, ReplayWeaponCharm, ReplayWeaponCosmetic,
    ReplayWeaponSticker,
};
use regex::Regex;
use std::sync::OnceLock;

const PREVIEW_COMMAND: &str = "csgo_econ_action_preview ";
const PREVIEW_URL: &str = "steam://run/730/en/+csgo_econ_action_preview%20";
const STEAM_PROTOCOL_CHAR_LIMIT: usize = 300;

pub(crate) fn weapon_inspect(
    cosmetic: &ReplayWeaponCosmetic,
    rarity: Option<u32>,
) -> Option<ReplayCosmeticInspect> {
    let defindex = u32::try_from(cosmetic.weapon_def_index).ok()?;
    if defindex == 0 || cosmetic.paint_kit == 0 || !valid_wear(cosmetic.wear) {
        return None;
    }

    let mut preview = PreviewData {
        defindex,
        paintindex: cosmetic.paint_kit,
        rarity,
        quality: cosmetic.quality.and_then(|value| u32::try_from(value).ok()),
        paintwear: cosmetic.wear.to_bits(),
        paintseed: cosmetic.seed,
        customname: inspect_compatible_custom_name(cosmetic.custom_name.as_deref()),
        ..PreviewData::default()
    };
    if let Some(counter) = cosmetic
        .stattrak_counter
        .and_then(|value| u32::try_from(value).ok())
    {
        preview.killeaterscoretype = Some(0);
        preview.killeatervalue = Some(counter);
    }
    preview.stickers = &cosmetic.stickers;
    preview.keychains = &cosmetic.charms;
    Some(build_inspect(preview))
}

pub(crate) fn item_inspect(
    cosmetic: &ReplayItemCosmetic,
    rarity: Option<u32>,
) -> Option<ReplayCosmeticInspect> {
    let defindex = u32::try_from(cosmetic.item_def_index?).ok()?;
    if defindex == 0 || cosmetic.paint_kit == 0 || !valid_wear(cosmetic.wear) {
        return None;
    }
    Some(build_inspect(PreviewData {
        defindex,
        paintindex: cosmetic.paint_kit,
        rarity,
        paintwear: cosmetic.wear.to_bits(),
        paintseed: cosmetic.seed,
        customname: inspect_compatible_custom_name(cosmetic.custom_name.as_deref()),
        ..PreviewData::default()
    }))
}

#[derive(Default)]
struct PreviewData<'a> {
    defindex: u32,
    paintindex: u32,
    rarity: Option<u32>,
    quality: Option<u32>,
    paintwear: u32,
    paintseed: u32,
    killeaterscoretype: Option<u32>,
    killeatervalue: Option<u32>,
    customname: Option<&'a str>,
    stickers: &'a [ReplayWeaponSticker],
    keychains: &'a [ReplayWeaponCharm],
}

fn valid_wear(wear: f32) -> bool {
    wear.is_finite() && (0.0..=1.0).contains(&wear)
}

fn inspect_compatible_custom_name(custom_name: Option<&str>) -> Option<&str> {
    // Preview consumers reject the whole synthetic item when field 11 violates
    // CS2's NameTag grammar. The original demo value remains separate evidence.
    const NAME_TAG_PATTERN: &str =
        r"\A[A-Za-z0-9`!@#$%^&*+=(){}\[\]/|\\,.?:;'_\p{Han}\p{Hiragana}\p{Katakana}\s]{0,20}\z";
    static NAME_TAG: OnceLock<Regex> = OnceLock::new();

    let custom_name = custom_name?;
    if custom_name.starts_with(' ') {
        return None;
    }
    NAME_TAG
        .get_or_init(|| Regex::new(NAME_TAG_PATTERN).expect("valid inspect name-tag pattern"))
        .is_match(custom_name)
        .then_some(custom_name)
}

fn build_inspect(preview: PreviewData<'_>) -> ReplayCosmeticInspect {
    let attributes = encode_preview(preview);
    let mut payload = Vec::with_capacity(attributes.len() + 5);
    payload.push(0);
    payload.extend_from_slice(&attributes);
    let crc = crc32(&payload);
    let xcrc = (crc & 0xffff) ^ (attributes.len() as u32).wrapping_mul(crc);
    payload.extend_from_slice(&xcrc.to_be_bytes());

    let hex = uppercase_hex(&payload);
    let command = format!("{PREVIEW_COMMAND}{hex}");
    let steam_url = (PREVIEW_URL.len() + hex.len() <= STEAM_PROTOCOL_CHAR_LIMIT)
        .then(|| format!("{PREVIEW_URL}{hex}"));
    ReplayCosmeticInspect { command, steam_url }
}

fn encode_preview(preview: PreviewData<'_>) -> Vec<u8> {
    let mut output = Vec::new();
    write_varint_field(&mut output, 3, preview.defindex);
    write_varint_field(&mut output, 4, preview.paintindex);
    write_optional_varint_field(&mut output, 5, preview.rarity);
    write_optional_varint_field(&mut output, 6, preview.quality);
    write_varint_field(&mut output, 7, preview.paintwear);
    write_varint_field(&mut output, 8, preview.paintseed);
    write_optional_varint_field(&mut output, 9, preview.killeaterscoretype);
    write_optional_varint_field(&mut output, 10, preview.killeatervalue);
    if let Some(customname) = preview.customname {
        write_bytes_field(&mut output, 11, customname.as_bytes());
    }
    for sticker in preview.stickers {
        let nested = encode_sticker(sticker);
        write_bytes_field(&mut output, 12, &nested);
    }
    for keychain in preview.keychains {
        let nested = encode_keychain(keychain);
        write_bytes_field(&mut output, 20, &nested);
    }
    output
}

fn encode_sticker(sticker: &ReplayWeaponSticker) -> Vec<u8> {
    let mut output = Vec::new();
    write_varint_field(&mut output, 1, u32::from(sticker.slot));
    write_varint_field(&mut output, 2, sticker.sticker_id);
    write_fixed32_field(&mut output, 3, sticker.wear.to_bits());
    if let Some(scale) = sticker.scale {
        write_fixed32_field(&mut output, 4, scale.to_bits());
    }
    if let Some(rotation) = sticker.rotation {
        write_fixed32_field(&mut output, 5, rotation.to_bits());
    }
    write_fixed32_field(&mut output, 7, sticker.offset_x.to_bits());
    write_fixed32_field(&mut output, 8, sticker.offset_y.to_bits());
    output
}

fn encode_keychain(keychain: &ReplayWeaponCharm) -> Vec<u8> {
    let mut output = Vec::new();
    write_varint_field(&mut output, 1, u32::from(keychain.slot));
    write_varint_field(&mut output, 2, keychain.charm_id);
    write_fixed32_field(&mut output, 7, keychain.offset_x.to_bits());
    write_fixed32_field(&mut output, 8, keychain.offset_y.to_bits());
    write_fixed32_field(&mut output, 9, keychain.offset_z.to_bits());
    write_optional_varint_field(&mut output, 10, keychain.seed);
    write_optional_varint_field(&mut output, 11, keychain.highlight);
    write_optional_varint_field(&mut output, 12, keychain.sticker_id);
    output
}

fn write_optional_varint_field(output: &mut Vec<u8>, field: u32, value: Option<u32>) {
    if let Some(value) = value {
        write_varint_field(output, field, value);
    }
}

fn write_varint_field(output: &mut Vec<u8>, field: u32, value: u32) {
    write_varint(output, u64::from(field << 3));
    write_varint(output, u64::from(value));
}

fn write_fixed32_field(output: &mut Vec<u8>, field: u32, value: u32) {
    write_varint(output, u64::from((field << 3) | 5));
    output.extend_from_slice(&value.to_le_bytes());
}

fn write_bytes_field(output: &mut Vec<u8>, field: u32, value: &[u8]) {
    write_varint(output, u64::from((field << 3) | 2));
    write_varint(output, value.len() as u64);
    output.extend_from_slice(value);
}

fn write_varint(output: &mut Vec<u8>, mut value: u64) {
    while value >= 0x80 {
        output.push((value as u8 & 0x7f) | 0x80);
        value >>= 7;
    }
    output.push(value as u8);
}

fn crc32(bytes: &[u8]) -> u32 {
    let mut crc = u32::MAX;
    for byte in bytes {
        crc ^= u32::from(*byte);
        for _ in 0..8 {
            let mask = 0_u32.wrapping_sub(crc & 1);
            crc = (crc >> 1) ^ (0xedb8_8320 & mask);
        }
    }
    !crc
}

fn uppercase_hex(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789ABCDEF";
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        output.push(HEX[usize::from(byte >> 4)] as char);
        output.push(HEX[usize::from(byte & 0x0f)] as char);
    }
    output
}

#[cfg(test)]
mod tests {
    use super::*;

    fn item(defindex: i32, paint_kit: u32, seed: u32, wear: f32) -> ReplayItemCosmetic {
        ReplayItemCosmetic {
            item_def_index: Some(defindex),
            paint_kit,
            seed,
            wear,
            custom_name: None,
            inspect: None,
        }
    }

    #[test]
    fn crimson_kimono_glove_matches_native_preview_payload() {
        let inspect = item_inspect(
            &item(5034, 10033, 772, f32::from_bits(1_032_289_721)),
            Some(6),
        )
        .unwrap();
        assert_eq!(
            inspect.command,
            "csgo_econ_action_preview 0018AA2720B14E280638B9FB9DEC034084063BFD7E70"
        );
        assert_eq!(
            inspect.steam_url.as_deref(),
            Some(concat!(
                "steam://run/730/en/+csgo_econ_action_preview%20",
                "0018AA2720B14E280638B9FB9DEC034084063BFD7E70"
            ))
        );
    }

    #[test]
    fn butterfly_marble_fade_matches_native_preview_payload() {
        let inspect =
            item_inspect(&item(515, 413, 284, f32::from_bits(1_028_123_081)), Some(6)).unwrap();
        assert_eq!(
            inspect.command,
            "csgo_econ_action_preview 00188304209D03280638C9D39FEA03409C02487E8790"
        );
    }

    #[test]
    fn incompatible_custom_name_does_not_invalidate_preview_payload() {
        let mut cosmetic = item(515, 568, 142, f32::from_bits(0x3c43_cb58));
        cosmetic.custom_name = Some("千古风流今在此，万里功名莫放休".to_string());

        let inspect = item_inspect(&cosmetic, Some(6)).unwrap();

        assert_eq!(
            inspect.command,
            "csgo_econ_action_preview 0018830420B804280638D8968FE203408E015A2FE4E0"
        );
        assert_eq!(
            inspect_compatible_custom_name(Some("千古风流今在此,万里功名莫放休")),
            Some("千古风流今在此,万里功名莫放休")
        );
        assert_eq!(inspect_compatible_custom_name(Some("not-compatible")), None);
    }

    #[test]
    fn long_sticker_payload_keeps_command_without_steam_url() {
        let stickers = (0..5)
            .map(|slot| ReplayWeaponSticker {
                slot,
                sticker_id: 7_901,
                wear: 0.123,
                offset_x: -0.25,
                offset_y: 0.5,
                scale: Some(1.0),
                rotation: Some(180.0),
            })
            .collect();
        let cosmetic = ReplayWeaponCosmetic {
            weapon_def_index: 7,
            paint_kit: 1_801,
            seed: 999,
            wear: 0.01,
            quality: Some(9),
            stattrak_counter: Some(12_345),
            original_owner_steam_id: None,
            item_account_id: None,
            item_id: None,
            custom_name: Some("a sufficiently long custom name".to_string()),
            stickers,
            charms: Vec::new(),
            inspect: None,
        };
        let inspect = weapon_inspect(&cosmetic, Some(6)).unwrap();
        assert!(inspect.command.starts_with(PREVIEW_COMMAND));
        assert!(inspect.steam_url.is_none());
    }
}
