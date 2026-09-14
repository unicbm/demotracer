/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

#![cfg(feature = "demoparser")]

use parser::first_pass::parser_settings::{FirstPassParser, ParserInputs};

fn server_info_packet(host: &str) -> Vec<u8> {
    server_info_with_interval(host, None)
}

fn server_info_with_interval(host: &str, interval: Option<f32>) -> Vec<u8> {
    // Protobuf ServerInfo field 17, wrapped in a Source 2 network packet.
    let mut message = vec![0x8a, 0x01, host.len() as u8];
    message.extend_from_slice(host.as_bytes());
    if let Some(interval) = interval {
        message.push(0x6d); // ServerInfo.tick_interval, fixed32 field 13.
        message.extend_from_slice(&interval.to_le_bytes());
    }
    let mut bits = Vec::new();
    let mut write = |value: u32, count| {
        bits.extend((0..count).map(|bit| ((value >> bit) & 1) as u8));
    };
    write(0x18, 6); // UBitVar message ID 40: low four bits + four extra bits.
    write(2, 4);
    write(message.len() as u32, 8);
    for byte in message {
        write(u32::from(byte), 8);
    }
    let payload: Vec<u8> = bits
        .chunks(8)
        .map(|chunk| {
            chunk
                .iter()
                .enumerate()
                .fold(0, |n, (i, bit)| n | (bit << i))
        })
        .collect();
    let mut packet = vec![0x1a, payload.len() as u8]; // CDemoPacket.data
    packet.extend(payload);
    packet
}

#[test]
fn signon_preserves_platform_host_separately_from_generic_file_header() {
    let huffman = Vec::new();
    let inputs = ParserInputs {
        real_name_to_og_name: Default::default(),
        wanted_players: vec![],
        wanted_player_props: vec![],
        wanted_other_props: vec![],
        wanted_prop_states: Default::default(),
        wanted_ticks: vec![],
        wanted_events: vec![],
        parse_ents: false,
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
    let mut parser = FirstPassParser::new(&inputs);
    parser
        .header
        .insert("server_name".into(), "Counter-Strike 2".into());
    parser
        .parse_packet(&server_info_packet(" 5EGOTV "))
        .unwrap();
    assert_eq!(parser.header["server_info_host_name"], "5EGOTV");
    assert_eq!(parser.header["server_name"], "Counter-Strike 2");

    // A repeated empty ServerInfo must not erase useful metadata.
    parser.parse_packet(&server_info_packet(" ")).unwrap();
    assert_eq!(parser.header["server_info_host_name"], "5EGOTV");

    parser
        .parse_packet(&server_info_with_interval("", Some(1.0 / 128.0)))
        .unwrap();
    assert_eq!(parser.header["server_tick_interval"], "0.0078125");
    for invalid in [0.0, -1.0, f32::NAN, f32::INFINITY] {
        parser
            .parse_packet(&server_info_with_interval("", Some(invalid)))
            .unwrap();
        assert_eq!(parser.header["server_tick_interval"], "0.0078125");
    }
}

#[test]
fn game_tick_wrapper_decodes_signed_wire_values_and_sentinels() {
    use parser::first_pass::read_bits::Bitreader;
    use parser::maps::BASETYPE_DECODERS;
    use parser::second_pass::decoder::QfMapper;
    use parser::second_pass::variants::Variant;
    let mapper = QfMapper {
        idx: 0,
        map: Default::default(),
    };
    // Positive GameTick_t values use signed varint encoding too. Treating
    // 317404 as an unsigned tick doubles a 2479.71875-second attack deadline.
    for tick in [-1_i32, 0, 158702] {
        let mut wire = Vec::new();
        let mut encoded = ((tick as u32) << 1) ^ ((tick >> 31) as u32);
        loop {
            let byte = (encoded & 0x7f) as u8;
            encoded >>= 7;
            wire.push(byte | if encoded == 0 { 0 } else { 0x80 });
            if encoded == 0 {
                break;
            }
        }
        assert_eq!(
            Bitreader::new(&wire)
                .decode(&BASETYPE_DECODERS["GameTick_t"], &mapper)
                .unwrap(),
            Variant::I32(tick)
        );
    }
}

#[test]
fn ammo_preserves_no_clip_sentinel_and_distinct_reserve_elements() {
    use parser::first_pass::prop_controller::WEAPON_RESERVE_AMMO_BASE;
    use parser::first_pass::read_bits::Bitreader;
    use parser::first_pass::sendtables::{get_propinfo, Field, ValueField};
    use parser::second_pass::decoder::Decoder;
    use parser::second_pass::path_ops::FieldPath;
    assert_eq!(Bitreader::new(&[0]).decode_ammo().unwrap(), u32::MAX);
    assert_eq!(Bitreader::new(&[1]).decode_ammo().unwrap(), 0);
    assert_eq!(Bitreader::new(&[31]).decode_ammo().unwrap(), 30);
    let field = Field::Value(ValueField {
        decoder: Decoder::SignedDecoder,
        name: "m_pReserveAmmo".into(),
        should_parse: true,
        prop_id: WEAPON_RESERVE_AMMO_BASE,
        full_name: "CWeaponAK47.m_pReserveAmmo".into(),
    });
    for index in 0..2 {
        let path = FieldPath {
            path: [10, index, 0, 0, 0, 0, 0],
            last: 1,
        };
        assert_eq!(
            get_propinfo(&field, &path).unwrap().prop_id,
            WEAPON_RESERVE_AMMO_BASE + index as u32
        );
    }
    assert!(get_propinfo(
        &field,
        &FieldPath {
            path: [10, 2, 0, 0, 0, 0, 0],
            last: 1
        }
    )
    .is_none());
}
