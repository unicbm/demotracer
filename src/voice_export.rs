/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use crate::export::ConversionReport;
use crate::model::{ParsedDemo, ParsedVoiceFrame};
use crate::{io_error, Error, Result};
use std::path::PathBuf;

#[derive(Debug, Clone)]
pub struct VoiceClipWindowExport {
    pub output: PathBuf,
    pub start_tick: Option<i32>,
    pub duration_seconds: f32,
}

#[derive(Debug, Clone)]
pub struct VoiceParsedBatchExportRequest {
    pub xuid: Option<u64>,
    pub client: Option<i32>,
    pub all_speakers: bool,
    pub tick_rate: f32,
    pub windows: Vec<VoiceClipWindowExport>,
}

#[derive(Debug, Clone)]
pub struct VoiceClipExportReport {
    pub path: PathBuf,
    pub frame_count: usize,
    pub selected_xuid: u64,
    pub selected_client: i32,
    pub start_tick: i32,
    pub end_tick: i32,
    pub duration_seconds: f32,
    pub speaker_count: usize,
}

#[derive(Debug, Clone)]
pub struct VoiceClipSpeaker {
    pub xuid: u64,
    pub client: i32,
    pub frame_count: usize,
}

#[cfg(feature = "demoparser")]
mod imp {
    use super::*;
    use ahash::AHashMap;
    use std::fs;
    use std::path::Path;

    const VOICE_DTV_MAGIC: &[u8; 8] = b"DTRVOICE";
    const VOICE_DTV_VERSION: u16 = 2;
    const VOICE_DTV_FLAG_FORMAT: u8 = 0x01;
    const VOICE_DTV_FLAG_SAMPLE_RATE: u8 = 0x02;
    const VOICE_DTV_FLAG_VOICE_LEVEL: u8 = 0x04;
    const VOICE_DTV_FLAG_SEQUENCE_BYTES: u8 = 0x08;
    const VOICE_DTV_FLAG_SECTION_NUMBER: u8 = 0x10;
    const VOICE_DTV_FLAG_UNCOMPRESSED_SAMPLE_OFFSET: u8 = 0x20;
    const VOICE_DTV_FLAG_NUM_PACKETS: u8 = 0x40;
    const VOICE_DTV_FLAG_PACKET_OFFSETS: u8 = 0x80;
    const VOICE_DTV_DEFAULT_FORMAT: i32 = 2;

    #[derive(Debug, Clone)]
    struct VoiceClipWriteOptions {
        xuid: Option<u64>,
        client: Option<i32>,
        all_speakers: bool,
        tick_rate: f32,
    }

    pub fn export_voice_clips_from_parsed(
        parsed_demo: &ParsedDemo,
        request: &VoiceParsedBatchExportRequest,
    ) -> Result<Vec<VoiceClipExportReport>> {
        validate_voice_batch_request(request.tick_rate, &request.windows)?;
        let parsed = ParsedVoiceData {
            demo_stem: parsed_demo.stem.clone(),
            demo_sha256: parsed_demo.demo_sha256.clone(),
            map: Some(parsed_demo.map.clone()),
            frames: filter_voice_frames(&parsed_demo.voice_frames, request.xuid, request.client),
        };
        if parsed.frames.is_empty() {
            return Err(Error::InvalidDemo(
                "no voice frames matched the requested filters".to_string(),
            ));
        }
        write_voice_clip_windows(
            &VoiceClipWriteOptions {
                xuid: request.xuid,
                client: request.client,
                all_speakers: request.all_speakers,
                tick_rate: request.tick_rate,
            },
            &parsed,
            &request.windows,
        )
    }

    struct ParsedVoiceData {
        demo_stem: String,
        demo_sha256: String,
        map: Option<String>,
        frames: Vec<ParsedVoiceFrame>,
    }

    fn validate_voice_batch_request(
        tick_rate: f32,
        windows: &[VoiceClipWindowExport],
    ) -> Result<()> {
        if !tick_rate.is_finite() || tick_rate <= 0.0 {
            return Err(Error::InvalidDemo(
                "voice clip tick rate must be a positive finite value".to_string(),
            ));
        }
        for window in windows {
            if !window.duration_seconds.is_finite() || window.duration_seconds <= 0.0 {
                return Err(Error::InvalidDemo(
                    "voice clip duration must be a positive finite value".to_string(),
                ));
            }
            if !window
                .output
                .extension()
                .and_then(|value| value.to_str())
                .is_some_and(|extension| extension.eq_ignore_ascii_case("dtv"))
            {
                return Err(Error::InvalidDemo(
                    "voice sidecar output must use the .dtv extension".to_string(),
                ));
            }
        }
        Ok(())
    }

    fn filter_voice_frames(
        frames: &[ParsedVoiceFrame],
        xuid: Option<u64>,
        client: Option<i32>,
    ) -> Vec<ParsedVoiceFrame> {
        let mut filtered = frames
            .iter()
            .filter(|frame| xuid.map_or(true, |filter| frame.xuid == filter))
            .filter(|frame| client.map_or(true, |filter| frame.client == filter))
            .cloned()
            .collect::<Vec<_>>();
        filtered.sort_by_key(|frame| (frame.xuid, frame.tick));
        filtered
    }

    fn write_voice_clip_windows(
        options: &VoiceClipWriteOptions,
        parsed: &ParsedVoiceData,
        windows: &[VoiceClipWindowExport],
    ) -> Result<Vec<VoiceClipExportReport>> {
        let mut reports = Vec::new();
        for window in windows {
            match write_voice_clip_window(options, parsed, window) {
                Ok(report) => reports.push(report),
                Err(Error::InvalidDemo(message))
                    if windows.len() > 1
                        && message.contains("selected voice window did not contain any frames") => {
                }
                Err(err) => return Err(err),
            }
        }
        if reports.is_empty() && !windows.is_empty() {
            return Err(Error::InvalidDemo(
                "no voice sidecar windows contained frames".to_string(),
            ));
        }
        Ok(reports)
    }

    fn write_voice_clip_window(
        options: &VoiceClipWriteOptions,
        parsed: &ParsedVoiceData,
        window: &VoiceClipWindowExport,
    ) -> Result<VoiceClipExportReport> {
        let duration_ticks = (window.duration_seconds * options.tick_rate)
            .round()
            .max(1.0) as i32;
        let (selected_xuid, start_tick) = match window.start_tick {
            Some(start_tick) => {
                let selected_xuid = if options.all_speakers {
                    0
                } else {
                    options
                        .xuid
                        .or_else(|| {
                            parsed
                                .frames
                                .iter()
                                .find(|frame| frame.tick >= start_tick)
                                .map(|frame| frame.xuid)
                        })
                        .unwrap_or(parsed.frames[0].xuid)
                };
                (selected_xuid, start_tick)
            }
            None if options.all_speakers => {
                (0, select_best_window_all(&parsed.frames, duration_ticks)?)
            }
            None => select_best_window(&parsed.frames, duration_ticks)?,
        };
        let end_tick = start_tick.saturating_add(duration_ticks);

        let mut selected = parsed
            .frames
            .iter()
            .filter(|frame| {
                (options.all_speakers || frame.xuid == selected_xuid)
                    && frame.tick >= start_tick
                    && frame.tick <= end_tick
            })
            .cloned()
            .collect::<Vec<_>>();
        selected.sort_by_key(|frame| (frame.tick, frame.xuid, frame.client));
        if selected.is_empty() {
            return Err(Error::InvalidDemo(
                "selected voice window did not contain any frames".to_string(),
            ));
        }
        let selected_client = if options.all_speakers {
            -1
        } else {
            options.client.unwrap_or(selected[0].client)
        };
        let actual_start_tick = window.start_tick.unwrap_or_else(|| {
            selected
                .first()
                .map(|frame| frame.tick)
                .unwrap_or(start_tick)
        });
        let actual_end_tick = if window.start_tick.is_some() {
            end_tick
        } else {
            selected.last().map(|frame| frame.tick).unwrap_or(end_tick)
        };
        let speakers = summarize_speakers(&selected);

        if let Some(parent) = window
            .output
            .parent()
            .filter(|parent| !parent.as_os_str().is_empty())
        {
            fs::create_dir_all(parent).map_err(|e| io_error(parent, e))?;
        }
        let duration_seconds =
            (actual_end_tick - actual_start_tick).max(0) as f32 / options.tick_rate;
        write_voice_clip_dtv_v2(
            &window.output,
            parsed,
            options.tick_rate,
            selected_xuid,
            selected_client,
            actual_start_tick,
            actual_end_tick,
            &speakers,
            &selected,
        )?;

        Ok(VoiceClipExportReport {
            path: window.output.clone(),
            frame_count: selected.len(),
            selected_xuid,
            selected_client,
            start_tick: actual_start_tick,
            end_tick: actual_end_tick,
            duration_seconds,
            speaker_count: speakers.len(),
        })
    }

    #[allow(clippy::too_many_arguments)]
    fn write_voice_clip_dtv_v2(
        output: &Path,
        parsed: &ParsedVoiceData,
        tick_rate: f32,
        selected_xuid: u64,
        selected_client: i32,
        start_tick: i32,
        end_tick: i32,
        speakers: &[VoiceClipSpeaker],
        frames: &[ParsedVoiceFrame],
    ) -> Result<()> {
        let audio_len = frames
            .iter()
            .map(|frame| frame.audio.len() as u64)
            .sum::<u64>();
        let mut speaker_indices = AHashMap::default();
        for (idx, speaker) in speakers.iter().enumerate() {
            speaker_indices.insert((speaker.xuid, speaker.client), idx as u64);
        }

        let audio_capacity = usize::try_from(audio_len).unwrap_or(0);
        let mut out =
            Vec::with_capacity(64 + speakers.len() * 16 + frames.len() * 24 + audio_capacity);
        out.extend_from_slice(VOICE_DTV_MAGIC);
        out.extend_from_slice(&VOICE_DTV_VERSION.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(&tick_rate.to_le_bytes());
        out.extend_from_slice(&start_tick.to_le_bytes());
        out.extend_from_slice(&end_tick.to_le_bytes());
        out.extend_from_slice(&selected_xuid.to_le_bytes());
        out.extend_from_slice(&selected_client.to_le_bytes());
        out.extend_from_slice(&(speakers.len() as u32).to_le_bytes());
        out.extend_from_slice(&(frames.len() as u32).to_le_bytes());
        out.extend_from_slice(&audio_len.to_le_bytes());
        write_dtv_string(&mut out, &parsed.demo_stem);
        write_dtv_string(&mut out, &parsed.demo_sha256);
        write_dtv_string(&mut out, parsed.map.as_deref().unwrap_or_default());

        for speaker in speakers {
            out.extend_from_slice(&speaker.xuid.to_le_bytes());
            out.extend_from_slice(&speaker.client.to_le_bytes());
            out.extend_from_slice(&(speaker.frame_count as u32).to_le_bytes());
        }

        let mut previous_relative_tick = 0u32;
        for frame in frames {
            let relative_tick = frame.tick.saturating_sub(start_tick) as u32;
            write_uvarint(
                &mut out,
                relative_tick.saturating_sub(previous_relative_tick) as u64,
            );
            previous_relative_tick = relative_tick;
            let Some(speaker_index) = speaker_indices.get(&(frame.xuid, frame.client)) else {
                return Err(Error::InvalidDemo(format!(
                    "voice frame speaker missing from speaker table xuid={} client={}",
                    frame.xuid, frame.client
                )));
            };
            write_uvarint(&mut out, *speaker_index);
            write_uvarint(&mut out, frame.audio.len() as u64);

            let mut flags = 0u8;
            if frame.format != VOICE_DTV_DEFAULT_FORMAT {
                flags |= VOICE_DTV_FLAG_FORMAT;
            }
            if frame.sample_rate.is_some() {
                flags |= VOICE_DTV_FLAG_SAMPLE_RATE;
            }
            if frame.voice_level.is_some() {
                flags |= VOICE_DTV_FLAG_VOICE_LEVEL;
            }
            if frame.sequence_bytes.is_some() {
                flags |= VOICE_DTV_FLAG_SEQUENCE_BYTES;
            }
            if frame.section_number.is_some() {
                flags |= VOICE_DTV_FLAG_SECTION_NUMBER;
            }
            if frame.uncompressed_sample_offset.is_some() {
                flags |= VOICE_DTV_FLAG_UNCOMPRESSED_SAMPLE_OFFSET;
            }
            if frame.num_packets.is_some() {
                flags |= VOICE_DTV_FLAG_NUM_PACKETS;
            }
            if !frame.packet_offsets.is_empty() {
                flags |= VOICE_DTV_FLAG_PACKET_OFFSETS;
            }
            out.push(flags);

            if flags & VOICE_DTV_FLAG_FORMAT != 0 {
                write_svarint(&mut out, frame.format as i64);
            }
            if let Some(sample_rate) = frame.sample_rate {
                write_uvarint(&mut out, sample_rate as u64);
            }
            if let Some(voice_level) = frame.voice_level {
                out.extend_from_slice(&voice_level.to_le_bytes());
            }
            if let Some(sequence_bytes) = frame.sequence_bytes {
                write_svarint(&mut out, sequence_bytes as i64);
            }
            if let Some(section_number) = frame.section_number {
                write_uvarint(&mut out, section_number as u64);
            }
            if let Some(uncompressed_sample_offset) = frame.uncompressed_sample_offset {
                write_uvarint(&mut out, uncompressed_sample_offset as u64);
            }
            if let Some(num_packets) = frame.num_packets {
                write_uvarint(&mut out, num_packets as u64);
            }
            if flags & VOICE_DTV_FLAG_PACKET_OFFSETS != 0 {
                write_uvarint(&mut out, frame.packet_offsets.len() as u64);
                for offset in &frame.packet_offsets {
                    write_uvarint(&mut out, *offset as u64);
                }
            }
        }

        for frame in frames {
            out.extend_from_slice(&frame.audio);
        }

        fs::write(output, out).map_err(|e| io_error(output, e))?;
        Ok(())
    }

    fn summarize_speakers(frames: &[ParsedVoiceFrame]) -> Vec<VoiceClipSpeaker> {
        let mut speakers = Vec::new();
        let mut start = 0usize;
        let mut sorted = frames.to_vec();
        sorted.sort_by_key(|frame| (frame.xuid, frame.client));
        while start < sorted.len() {
            let xuid = sorted[start].xuid;
            let client = sorted[start].client;
            let mut end = start + 1;
            while end < sorted.len() && sorted[end].xuid == xuid && sorted[end].client == client {
                end += 1;
            }
            speakers.push(VoiceClipSpeaker {
                xuid,
                client,
                frame_count: end - start,
            });
            start = end;
        }
        speakers.sort_by_key(|speaker| std::cmp::Reverse(speaker.frame_count));
        speakers
    }

    fn select_best_window_all(frames: &[ParsedVoiceFrame], duration_ticks: i32) -> Result<i32> {
        let mut sorted = frames.to_vec();
        sorted.sort_by_key(|frame| frame.tick);
        let mut best: Option<(usize, usize, i32)> = None;
        let mut end = 0usize;
        let mut bytes = 0usize;
        for start in 0..sorted.len() {
            while end < sorted.len()
                && sorted[end].tick <= sorted[start].tick.saturating_add(duration_ticks)
            {
                bytes += sorted[end].audio.len();
                end += 1;
            }
            let count = end.saturating_sub(start);
            let candidate = (bytes, count, sorted[start].tick);
            if best.map_or(true, |current| candidate > current) {
                best = Some(candidate);
            }
            bytes = bytes.saturating_sub(sorted[start].audio.len());
        }
        best.map(|(_, _, tick)| tick).ok_or_else(|| {
            Error::InvalidDemo("no continuous voice window could be selected".to_string())
        })
    }

    fn select_best_window(frames: &[ParsedVoiceFrame], duration_ticks: i32) -> Result<(u64, i32)> {
        let mut best: Option<(usize, usize, usize, u64, i32)> = None;
        let mut start = 0usize;
        while start < frames.len() {
            let xuid = frames[start].xuid;
            let mut end = start;
            while end < frames.len() && frames[end].xuid == xuid {
                end += 1;
            }
            select_best_window_for_player(&frames[start..end], duration_ticks, &mut best);
            start = end;
        }
        best.map(|(_, _, _, xuid, tick)| (xuid, tick))
            .ok_or_else(|| {
                Error::InvalidDemo("no continuous voice window could be selected".to_string())
            })
    }

    fn select_best_window_for_player(
        frames: &[ParsedVoiceFrame],
        duration_ticks: i32,
        best: &mut Option<(usize, usize, usize, u64, i32)>,
    ) {
        let mut end = 0usize;
        let mut bytes = 0usize;
        for start in 0..frames.len() {
            while end < frames.len()
                && frames[end].tick <= frames[start].tick.saturating_add(duration_ticks)
            {
                bytes += frames[end].audio.len();
                end += 1;
            }
            let count = end.saturating_sub(start);
            let candidate = (
                bytes,
                count,
                frames[start].audio.len(),
                frames[start].xuid,
                frames[start].tick,
            );
            if best.map_or(true, |current| candidate > current) {
                *best = Some(candidate);
            }
            bytes = bytes.saturating_sub(frames[start].audio.len());
        }
    }

    fn write_dtv_string(out: &mut Vec<u8>, value: &str) {
        write_uvarint(out, value.len() as u64);
        out.extend_from_slice(value.as_bytes());
    }

    fn write_uvarint(out: &mut Vec<u8>, mut value: u64) {
        while value >= 0x80 {
            out.push((value as u8) | 0x80);
            value >>= 7;
        }
        out.push(value as u8);
    }

    fn write_svarint(out: &mut Vec<u8>, value: i64) {
        let encoded = ((value << 1) ^ (value >> 63)) as u64;
        write_uvarint(out, encoded);
    }
}

#[cfg(not(feature = "demoparser"))]
mod imp {
    use super::*;

    pub fn export_voice_clips_from_parsed(
        _parsed_demo: &ParsedDemo,
        _request: &VoiceParsedBatchExportRequest,
    ) -> Result<Vec<VoiceClipExportReport>> {
        Err(Error::FeatureDisabled("demoparser"))
    }
}

pub fn export_voice_clips_from_parsed(
    parsed_demo: &ParsedDemo,
    request: &VoiceParsedBatchExportRequest,
) -> Result<Vec<VoiceClipExportReport>> {
    imp::export_voice_clips_from_parsed(parsed_demo, request)
}

pub fn export_round_voice_sidecars(
    parsed_demo: &ParsedDemo,
    report: &ConversionReport,
) -> Result<Vec<VoiceClipExportReport>> {
    let tick_rate = report.manifest.tick_rate.max(1.0);
    let windows = report
        .manifest
        .rounds
        .iter()
        .filter(|round| round.files > 0)
        .map(|round| {
            let (start_tick, duration_seconds) = round_voice_window(
                round.recording_start_tick,
                round.start_tick,
                round.end_tick,
                tick_rate,
            );
            VoiceClipWindowExport {
                output: report
                    .root
                    .join("voice")
                    .join(format!("round{:02}.dtv", round.round)),
                start_tick: Some(start_tick),
                duration_seconds,
            }
        })
        .filter(|window| voice_window_contains_frames(&parsed_demo.voice_frames, window, tick_rate))
        .collect::<Vec<_>>();
    if windows.is_empty() {
        return Ok(Vec::new());
    }

    export_voice_clips_from_parsed(
        parsed_demo,
        &VoiceParsedBatchExportRequest {
            xuid: None,
            client: None,
            all_speakers: true,
            tick_rate,
            windows,
        },
    )
}

fn voice_window_contains_frames(
    frames: &[ParsedVoiceFrame],
    window: &VoiceClipWindowExport,
    tick_rate: f32,
) -> bool {
    let Some(start_tick) = window.start_tick else {
        return !frames.is_empty();
    };
    let duration_ticks = (window.duration_seconds * tick_rate).round().max(1.0) as i32;
    let end_tick = start_tick.saturating_add(duration_ticks);
    frames
        .iter()
        .any(|frame| frame.tick >= start_tick && frame.tick <= end_tick)
}

fn round_voice_window(
    recording_start_tick: i32,
    live_start_tick: i32,
    end_tick: i32,
    tick_rate: f32,
) -> (i32, f32) {
    let start_tick = recording_start_tick.min(live_start_tick);
    let duration_seconds = (end_tick - start_tick).max(1) as f32 / tick_rate.max(1.0);
    (start_tick, duration_seconds)
}

#[cfg(test)]
mod tests {
    use super::{round_voice_window, voice_window_contains_frames, VoiceClipWindowExport};
    use crate::model::ParsedVoiceFrame;
    use std::path::PathBuf;

    #[test]
    fn round_voice_window_includes_bounded_freeze_preroll() {
        let (start_tick, duration_seconds) = round_voice_window(900, 1_000, 1_540, 64.0);

        assert_eq!(start_tick, 900);
        assert_eq!(duration_seconds, 10.0);
    }

    #[test]
    fn round_voice_window_does_not_start_after_live_tick() {
        let (start_tick, duration_seconds) = round_voice_window(1_100, 1_000, 1_640, 64.0);

        assert_eq!(start_tick, 1_000);
        assert_eq!(duration_seconds, 10.0);
    }

    #[test]
    fn round_voice_window_presence_is_limited_to_the_selected_ticks() {
        let frames = vec![
            ParsedVoiceFrame {
                tick: 100,
                ..ParsedVoiceFrame::default()
            },
            ParsedVoiceFrame {
                tick: 200,
                ..ParsedVoiceFrame::default()
            },
        ];
        let selected = VoiceClipWindowExport {
            output: PathBuf::from("voice/round01.dtv"),
            start_tick: Some(150),
            duration_seconds: 1.0,
        };
        let absent = VoiceClipWindowExport {
            output: PathBuf::from("voice/round02.dtv"),
            start_tick: Some(300),
            duration_seconds: 1.0,
        };

        assert!(voice_window_contains_frames(&frames, &selected, 64.0));
        assert!(!voice_window_contains_frames(&frames, &absent, 64.0));
    }
}
