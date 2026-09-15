/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use super::*;
use crate::model::source_state::{is_playback_field, SOURCE_FIELDS};

const PLAYER_TICK: u32 = 1;
const MAX_RUN: u32 = 0x0100_0000;

// v2: u32 tick deltas, u32 descriptors, then field-major XOR byte planes.
// Descriptor: field ID in bits 0..6, presence in bit 7, run length minus one
// in bits 8..31. Only present PlayerTick records may have a multi-tick run.
pub(super) fn encode(changes: &[SourceStateChange]) -> Result<(usize, Vec<u8>)> {
    let mut records: Vec<(SourceStateChange, u32)> = Vec::new();
    let mut last_clock: Option<usize> = None;
    let mut count = 0;
    for &change in changes
        .iter()
        .filter(|c| is_playback_field(c.field_id as usize))
    {
        let change_length = change.run_length();
        count += change_length as usize;
        if change.field_id == PLAYER_TICK {
            if let Some(index) = last_clock {
                let (start, length) = &mut records[index];
                if start.is_present()
                    && change.is_present()
                    && *length + change_length <= MAX_RUN
                    && u64::from(start.tick_index) + u64::from(*length)
                        == u64::from(change.tick_index)
                    && start.value_bits.wrapping_add(*length) == change.value_bits
                {
                    *length += change_length;
                    continue;
                }
            }
            last_clock = Some(records.len());
        }
        records.push((change, change_length));
    }
    let mut body = Vec::with_capacity(records.len() * 12);
    let mut previous_tick = 0;
    for (change, _) in &records {
        write_u32(&mut body, change.tick_index - previous_tick)?;
        previous_tick = change.tick_index;
    }
    for (change, length) in &records {
        write_u32(
            &mut body,
            change.field_id | ((change.present & 1) << 7) | ((length - 1) << 8),
        )?;
    }
    let mut counts = [0; SOURCE_FIELDS.len()];
    for (change, _) in &records {
        counts[change.field_id as usize] += 1;
    }
    let mut bases = [0; SOURCE_FIELDS.len()];
    let mut base = body.len();
    for (id, count) in counts.iter().enumerate() {
        bases[id] = base;
        base += count * 4;
    }
    body.resize(base, 0);
    let mut cursors = [0; SOURCE_FIELDS.len()];
    let mut previous_bits = [0; SOURCE_FIELDS.len()];
    for (change, _) in &records {
        let id = change.field_id as usize;
        let previous = &mut previous_bits[id];
        for (byte, value) in (change.value_bits ^ *previous)
            .to_le_bytes()
            .iter()
            .enumerate()
        {
            body[bases[id] + byte * counts[id] + cursors[id]] = *value;
        }
        cursors[id] += 1;
        *previous = change.value_bits;
    }
    Ok((count, body))
}

pub(super) fn validate_header(format: u32, header: &SectionHeader) -> Result<()> {
    if header.section_version == SECTION_VERSION_V1 {
        return require_section_header_shape(
            "source state",
            header.section_version,
            header.element_count,
            header.element_count as usize,
            header.uncompressed_len,
            checked_product(header.element_count as usize, 16, "source state")?,
        );
    }
    if format < 12 || header.section_version != SECTION_VERSION_V2 {
        return Err(Error::InvalidRec(
            "unsupported source state section version".into(),
        ));
    }
    let len = header.uncompressed_len;
    if len % 12 != 0
        || len / 12 > u64::from(header.element_count)
        || (len == 0) != (header.element_count == 0)
    {
        return Err(Error::InvalidRec(
            "invalid compact source state length".into(),
        ));
    }
    Ok(())
}

pub(super) fn decode(
    body: &[u8],
    count: usize,
    tick_count: usize,
    version: u32,
) -> Result<Vec<SourceStateChange>> {
    if version == SECTION_VERSION_V1 {
        let mut values = reserved_vec(count, "source state changes")?;
        let mut reader = Cursor::new(body);
        for _ in 0..count {
            values.push(SourceStateChange {
                tick_index: read_u32(&mut reader)?,
                field_id: read_u32(&mut reader)?,
                value_bits: read_u32(&mut reader)?,
                present: read_u32(&mut reader)?,
            });
            if values.last().unwrap().present > 1 {
                return Err(Error::InvalidRec(
                    "invalid legacy source state presence".into(),
                ));
            }
        }
        validate_source_changes(&values, tick_count)?;
        return Ok(values);
    }
    let encoded_count = body.len() / 12;
    let column_bytes = encoded_count * 4;
    let mut ticks = Cursor::new(&body[..column_bytes]);
    let mut descriptors = Cursor::new(&body[column_bytes..column_bytes * 2]);
    let mut counts = [0; SOURCE_FIELDS.len()];
    for descriptor in body[column_bytes..column_bytes * 2].chunks_exact(4) {
        let id = (u32::from_le_bytes(descriptor.try_into().unwrap()) & 0x7f) as usize;
        let count = counts
            .get_mut(id)
            .ok_or_else(|| Error::InvalidRec("invalid source state field".into()))?;
        *count += 1;
    }
    let mut bases = [0; SOURCE_FIELDS.len()];
    let mut base = column_bytes * 2;
    for (id, count) in counts.iter().enumerate() {
        bases[id] = base;
        base += count * 4;
    }
    let mut cursors = [0; SOURCE_FIELDS.len()];
    let mut records = reserved_vec(encoded_count, "compact source state")?;
    let mut previous_bits = [0; SOURCE_FIELDS.len()];
    let mut tick_index = 0_u32;
    let mut previous_key = None;
    let mut clock_end = 0_u64;
    let mut expanded_count = 0_u64;
    for _ in 0..encoded_count {
        tick_index = tick_index
            .checked_add(read_u32(&mut ticks)?)
            .ok_or_else(|| Error::InvalidRec("source state tick delta overflow".into()))?;
        let descriptor = read_u32(&mut descriptors)?;
        let field_id = descriptor & 0x7f;
        let present = (descriptor >> 7) & 1;
        let length = (descriptor >> 8) + 1;
        let previous = previous_bits
            .get_mut(field_id as usize)
            .ok_or_else(|| Error::InvalidRec("invalid source state field".into()))?;
        let id = field_id as usize;
        let xor = u32::from_le_bytes(std::array::from_fn(|byte| {
            body[bases[id] + byte * counts[id] + cursors[id]]
        }));
        cursors[id] += 1;
        *previous ^= xor;
        let change = SourceStateChange {
            tick_index,
            field_id,
            value_bits: *previous,
            present,
        };
        let key = (tick_index, field_id);
        let end = u64::from(tick_index) + u64::from(length);
        if !change.valid(tick_count)
            || previous_key.is_some_and(|prev| prev >= key)
            || (length > 1 && (field_id != PLAYER_TICK || present != 1))
            || end > tick_count as u64
            || (field_id == PLAYER_TICK && u64::from(tick_index) < clock_end)
        {
            return Err(Error::InvalidRec(
                "invalid or overlapping source state run".into(),
            ));
        }
        if field_id == PLAYER_TICK {
            clock_end = end;
        }
        previous_key = Some(key);
        expanded_count += u64::from(length);
        if expanded_count > count as u64 {
            return Err(Error::InvalidRec(
                "source state expanded count mismatch".into(),
            ));
        }
        records.push(SourceStateChange {
            present: present | ((length - 1) << 1),
            ..change
        });
    }
    if expanded_count != count as u64 {
        return Err(Error::InvalidRec(
            "source state expanded count mismatch".into(),
        ));
    }
    // Preserve runs through the GUI, managed reader and native timeline.
    // Queries derive the exact clock value without allocating per-tick entries.
    Ok(records)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn change(tick_index: u32, field_id: u32, value_bits: u32, present: u32) -> SourceStateChange {
        SourceStateChange {
            tick_index,
            field_id,
            value_bits,
            present,
        }
    }

    #[test]
    fn compact_clocks_preserve_wrap_jumps_absence_and_float_bits() {
        let input = vec![
            change(0, 0, 12345, 1),
            change(0, 1, u32::MAX - 1, 1),
            change(0, 2, (-0.0_f32).to_bits(), 1),
            change(0, 52, 30, 1),
            change(1, 1, u32::MAX, 1),
            change(1, 2, 1.0_f32.to_bits(), 1),
            change(2, 1, 0, 1),
            change(3, 1, 0, 0),
            change(4, 1, 100, 1),
            change(5, 1, 101, 1),
            change(5, 2, 0, 0),
        ];
        let (count, body) = encode(&input).unwrap();
        assert_eq!(count, 9);
        assert_eq!(body.len(), 72);
        let parsed = decode(&body, count, 8, 2).unwrap();
        assert_eq!(
            parsed,
            vec![
                change(0, 1, u32::MAX - 1, 5),
                change(0, 2, (-0.0_f32).to_bits(), 1),
                change(1, 2, 1.0_f32.to_bits(), 1),
                change(3, 1, 0, 0),
                change(4, 1, 100, 3),
                change(5, 2, 0, 0),
            ]
        );
        validate_source_changes(&parsed, 8).unwrap();
        assert_eq!(encode(&parsed).unwrap(), (count, body));
    }

    #[test]
    fn compact_reader_rejects_bad_runs_and_counts() {
        let (count, body) = encode(&[change(0, 1, 10, 7), change(4, 1, 99, 1)]).unwrap();
        assert!(decode(&body, count - 1, 5, 2).is_err());
        assert!(decode(&body, count + 1, 5, 2).is_err());
        assert!(decode(&body, count, 4, 2).is_err());
        let mut overlapping = body.clone();
        overlapping[4..8].copy_from_slice(&3_u32.to_le_bytes());
        assert!(decode(&overlapping, count, 5, 2).is_err());
        let mut invalid_field_run = body.clone();
        invalid_field_run[8..12].copy_from_slice(&0x0382_u32.to_le_bytes());
        assert!(decode(&invalid_field_run, count, 5, 2).is_err());
        let mut absent_run = body.clone();
        absent_run[8..12].copy_from_slice(&0x0301_u32.to_le_bytes());
        assert!(decode(&absent_run, count, 5, 2).is_err());
        let mut overflow = body;
        overflow[0..4].copy_from_slice(&u32::MAX.to_le_bytes());
        assert!(decode(&overflow, count, 5, 2).is_err());
    }

    #[test]
    fn legacy_presence_cannot_smuggle_a_run() {
        let mut body = Vec::new();
        for value in [0, 1, 100, 3] {
            write_u32(&mut body, value).unwrap();
        }
        assert!(decode(&body, 1, 2, 1).is_err());
        body[12..16].copy_from_slice(&1_u32.to_le_bytes());
        assert_eq!(decode(&body, 1, 2, 1).unwrap(), vec![change(0, 1, 100, 1)]);
    }

    #[test]
    fn empty_and_legacy_sections_keep_version_and_shape_guards() {
        let mut header = SectionHeader {
            section_id: 9,
            section_version: 2,
            codec: 0,
            element_count: 0,
            uncompressed_len: 0,
            compressed_len: 0,
        };
        assert!(validate_header(12, &header).is_ok());
        assert!(validate_header(11, &header).is_err());
        assert!(decode(&[], 0, 0, 2).unwrap().is_empty());
        header.element_count = 1;
        assert!(validate_header(12, &header).is_err());
        header.uncompressed_len = 13;
        assert!(validate_header(12, &header).is_err());
        header.uncompressed_len = 24;
        assert!(validate_header(12, &header).is_err());
    }
}
