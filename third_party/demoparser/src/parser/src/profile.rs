//! Opt-in aggregate parser diagnostics. No timers or allocations in the hot
//! path unless a DEMOTRACER_PROFILE flag is enabled in the environment.
use crate::first_pass::prop_controller::PropInfo;
use std::time::{Duration, Instant};

#[derive(Default)]
pub(crate) struct PassProfile {
    enabled: bool,
    frames: u64,
    skipped_frames: u64,
    compressed_frames: u64,
    compressed_bytes: u64,
    decompressed_bytes: u64,
    decompression: Duration,
    entity_messages: u64,
    entity_parse: Duration,
    entity_collect: Duration,
    phases: [Duration; 8],
    phase_calls: [u64; 8],
}

impl PassProfile {
    pub(crate) fn new() -> Self {
        Self {
            enabled: std::env::var_os("DEMOTRACER_PROFILE").is_some(),
            ..Self::default()
        }
    }

    #[inline]
    pub(crate) fn start(&self) -> Option<Instant> {
        self.enabled.then(Instant::now)
    }

    #[inline]
    pub(crate) fn phase(&mut self, phase: usize, started: Option<Instant>) {
        if let Some(started) = started {
            self.phases[phase] += started.elapsed();
            self.phase_calls[phase] += 1;
        }
    }

    #[inline]
    pub(crate) fn frame(&mut self, skipped: bool) {
        if self.enabled {
            self.frames += 1;
            self.skipped_frames += u64::from(skipped);
        }
    }

    #[inline]
    pub(crate) fn decompressed(&mut self, started: Option<Instant>, input: usize, output: usize) {
        if let Some(started) = started {
            self.decompression += started.elapsed();
            self.compressed_frames += 1;
            self.compressed_bytes += input as u64;
            self.decompressed_bytes += output as u64;
        }
    }

    #[inline]
    pub(crate) fn parsed_entities(&mut self, started: Option<Instant>) {
        if let Some(started) = started {
            self.entity_messages += 1;
            self.entity_parse += started.elapsed();
        }
    }

    #[inline]
    pub(crate) fn collected_entities(&mut self, started: Option<Instant>) {
        if let Some(started) = started {
            self.entity_collect += started.elapsed();
        }
    }

    pub(crate) fn report(&self, pass: &str, offset: usize, started: Option<Instant>) {
        if let Some(started) = started {
            eprintln!(
                "[demotracer-profile] pass={pass} offset={offset} loop_ms={:.3} frames={} skipped_frames={} snappy_frames={} snappy_input_bytes={} snappy_output_bytes={} snappy_ms={:.3} entity_messages={} entity_parse_ms={:.3} entity_collect_ms={:.3}",
                started.elapsed().as_secs_f64() * 1000.0,
                self.frames,
                self.skipped_frames,
                self.compressed_frames,
                self.compressed_bytes,
                self.decompressed_bytes,
                self.decompression.as_secs_f64() * 1000.0,
                self.entity_messages,
                self.entity_parse.as_secs_f64() * 1000.0,
                self.entity_collect.as_secs_f64() * 1000.0,
            );
            for (phase, name) in ["packet_envelope", "message_copy", "usercmd", "other_messages", "delta_decode", "full_decode", "full_apply", "delta_apply"].iter().enumerate() {
                eprintln!("[demotracer-profile] pass={pass} phase={name} calls={} ms={:.3}",
                    self.phase_calls[phase], self.phases[phase].as_secs_f64() * 1000.0);
            }
        }
    }
}

#[derive(Clone, Default)]
struct PropertyTiming {
    calls: u64,
    elapsed: Duration,
}

/// Sample only the non-deferred getter and column append work. The caller
/// selects an instrumented row loop so disabled profiling adds no cell checks.
pub(crate) struct PropertyProfile {
    rows: u64,
    sampled_rows: u64,
    deferred_columns: usize,
    timings: Vec<PropertyTiming>,
}

impl PropertyProfile {
    pub(crate) fn from_env(columns: usize, deferred_columns: usize) -> Option<Self> {
        (std::env::var("DEMOTRACER_PROFILE_PROPERTIES").as_deref() == Ok("1"))
            .then(|| Self::new(columns, deferred_columns))
    }

    fn new(columns: usize, deferred_columns: usize) -> Self {
        Self { rows: 0, sampled_rows: 0, deferred_columns, timings: vec![PropertyTiming::default(); columns] }
    }

    pub(crate) fn sample_row(&mut self) -> bool {
        let row = self.rows;
        self.rows += 1;
        // Exactly one sample in each 256-row block. Rotating the position
        // avoids always sampling the same half of a ten-player roster.
        let sample = row % 256 == (row / 256).wrapping_mul(73) % 256;
        self.sampled_rows += u64::from(sample);
        sample
    }

    pub(crate) fn record(&mut self, slot: usize, started: Instant) {
        let elapsed = started.elapsed();
        self.timings[slot].calls += 1;
        self.timings[slot].elapsed += elapsed;
    }

    pub(crate) fn report(&self, props: &[PropInfo]) {
        let mut slots: Vec<_> = (0..self.timings.len()).collect();
        slots.sort_unstable_by(|&left, &right| {
            self.timings[right].elapsed.cmp(&self.timings[left].elapsed).then(left.cmp(&right))
        });
        let measured_columns = self.timings.iter().filter(|timing| timing.calls != 0).count();
        let sampled_ms: f64 = self.timings.iter().map(|timing| timing.elapsed.as_secs_f64() * 1000.0).sum();
        eprintln!(
            "[demotracer-properties] rows={} sampled_rows={} sample_rate=1/256 measured_columns={} deferred_columns={} deferred_cell_samples=0 sampled_get_push_ms={:.3}",
            self.rows, self.sampled_rows, measured_columns, self.deferred_columns, sampled_ms,
        );
        for slot in slots.into_iter().take(15) {
            let timing = &self.timings[slot];
            let elapsed_us = timing.elapsed.as_secs_f64() * 1_000_000.0;
            eprintln!(
                "[demotracer-properties] property={:?} id={} samples={} sampled_ms={:.3} mean_us={:.3}",
                props[slot].prop_friendly_name, props[slot].id, timing.calls,
                elapsed_us / 1000.0,
                if timing.calls == 0 { 0.0 } else { elapsed_us / timing.calls as f64 },
            );
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn property_sampling_has_one_sample_per_block_and_rotates_roster_slots() {
        let mut profile = PropertyProfile::new(3, 1);
        let mut player_slots = [false; 10];
        for block in 0..256 {
            let mut samples = 0;
            for offset in 0..256 {
                if profile.sample_row() {
                    samples += 1;
                    player_slots[(block * 256 + offset) % 10] = true;
                    profile.record(0, Instant::now());
                }
            }
            assert_eq!(samples, 1);
        }
        assert!(player_slots.iter().all(|sampled| *sampled));
        assert_eq!(profile.rows, 65_536);
        assert_eq!(profile.sampled_rows, 256);
        assert_eq!(profile.timings[0].calls, 256);
        assert_eq!(profile.timings[1].calls, 0);
        assert_eq!(profile.timings[2].calls, 0);
    }
}
