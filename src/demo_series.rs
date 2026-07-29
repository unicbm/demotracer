use crate::demo_reader::{
    demo_content_sha256, is_supported_demo_path, read_demo_with_options, ReadDemoOptions,
};
use crate::model::{
    ParsedAvatarOverride, ParsedDemo, ParsedEconItem, ParsedGameEvent, ParsedPlayerTick,
};
use crate::{io_error, Error, Result};
use sha2::{Digest, Sha256};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::SystemTime;

const SERIES_HASH_DOMAIN: &[u8] = b"cs2-demotracer-segment-set-v1\0";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DemoSourcePart {
    pub part: u32,
    pub path: PathBuf,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DemoSourceSet {
    pub logical_stem: String,
    pub parts: Vec<DemoSourcePart>,
}

impl DemoSourceSet {
    pub fn primary_path(&self) -> &Path {
        &self.parts[0].path
    }

    pub fn is_segmented(&self) -> bool {
        self.parts.len() > 1
    }

    pub fn paths(&self) -> impl Iterator<Item = &Path> {
        self.parts.iter().map(|part| part.path.as_path())
    }

    pub fn metadata(&self) -> Result<DemoSourceMetadata> {
        let mut size_bytes = 0_u64;
        let mut modified = None::<SystemTime>;
        for part in &self.parts {
            let metadata = fs::metadata(&part.path).map_err(|error| io_error(&part.path, error))?;
            size_bytes = size_bytes.checked_add(metadata.len()).ok_or_else(|| {
                Error::InvalidDemo("combined demo source size exceeds the supported range".into())
            })?;
            if let Ok(value) = metadata.modified() {
                modified = Some(modified.map_or(value, |current| current.max(value)));
            }
        }
        Ok(DemoSourceMetadata {
            size_bytes,
            modified,
        })
    }
}

#[derive(Clone, Copy, Debug, Default)]
pub struct DemoSourceMetadata {
    pub size_bytes: u64,
    pub modified: Option<SystemTime>,
}

#[derive(Clone, Debug)]
struct SegmentFileName {
    base: String,
    part: u32,
}

#[derive(Clone, Copy, Debug)]
struct CompletedRound {
    row_min_tick: i32,
    row_max_tick: i32,
    freeze_end_tick: i32,
    round_end_tick: i32,
    winner_side: u8,
    start_score_total: Option<u32>,
}

pub fn resolve_demo_source(path: &Path) -> Result<DemoSourceSet> {
    if !is_supported_demo_path(path) || !path.is_file() {
        return Err(Error::InvalidDemo(format!(
            "demo source does not exist or has an unsupported extension: {}",
            path.display()
        )));
    }
    let Some(selected) = segment_file_name(path) else {
        return Ok(single_source(path));
    };
    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    let mut matching = BTreeMap::<u32, PathBuf>::new();
    for entry in fs::read_dir(parent).map_err(|error| io_error(parent, error))? {
        let entry = entry.map_err(|error| io_error(parent, error))?;
        let candidate = entry.path();
        if !candidate.is_file() || !is_supported_demo_path(&candidate) {
            continue;
        }
        let Some(segment) = segment_file_name(&candidate) else {
            continue;
        };
        if !segment.base.eq_ignore_ascii_case(&selected.base) {
            continue;
        }
        if let Some(previous) = matching.insert(segment.part, candidate.clone()) {
            return Err(Error::InvalidDemo(format!(
                "ambiguous demo segment p{}: {} and {}",
                segment.part,
                previous.display(),
                candidate.display()
            )));
        }
    }

    if matching.len() < 2 {
        if selected.part > 1 && !matching.contains_key(&1) {
            return Err(Error::InvalidDemo(format!(
                "demo segment {} is missing its p1 sibling",
                path.display()
            )));
        }
        return Ok(single_source(path));
    }
    let Some(last_part) = matching.keys().next_back().copied() else {
        return Ok(single_source(path));
    };
    for part in 1..=last_part {
        if !matching.contains_key(&part) {
            return Err(Error::InvalidDemo(format!(
                "demo segment set {} is missing p{part}",
                selected.base
            )));
        }
    }

    let logical_stem = matching
        .get(&1)
        .and_then(|path| segment_file_name(path))
        .map(|segment| segment.base)
        .unwrap_or(selected.base);
    Ok(DemoSourceSet {
        logical_stem,
        parts: matching
            .into_iter()
            .map(|(part, path)| DemoSourcePart { part, path })
            .collect(),
    })
}

pub fn group_demo_sources(paths: impl IntoIterator<Item = PathBuf>) -> Result<Vec<DemoSourceSet>> {
    let mut grouped = BTreeMap::<String, DemoSourceSet>::new();
    for path in paths {
        let source = resolve_demo_source(&path)?;
        let key = source
            .primary_path()
            .to_string_lossy()
            .replace('\\', "/")
            .to_ascii_lowercase();
        grouped.entry(key).or_insert(source);
    }
    Ok(grouped.into_values().collect())
}

pub fn demo_source_sha256(source: &DemoSourceSet) -> Result<String> {
    compound_demo_sha256(&demo_source_part_hashes(source)?)
}

fn demo_source_part_hashes(source: &DemoSourceSet) -> Result<Vec<String>> {
    source
        .parts
        .iter()
        .map(|part| demo_content_sha256(&part.path))
        .collect()
}

pub fn resolve_demo_source_for_sha256(
    path: &Path,
    expected_sha256: &str,
) -> Result<Option<DemoSourceSet>> {
    let source = match resolve_demo_source(path) {
        Ok(source) => source,
        Err(source_error) => {
            if demo_content_sha256(path)?.eq_ignore_ascii_case(expected_sha256) {
                return Ok(Some(single_source(path)));
            }
            return Err(source_error);
        }
    };
    let hashes = demo_source_part_hashes(&source)?;
    if compound_demo_sha256(&hashes)?.eq_ignore_ascii_case(expected_sha256) {
        return Ok(Some(source));
    }
    if source.is_segmented() {
        let selected_part = segment_file_name(path).map(|segment| segment.part);
        if source.parts.iter().zip(hashes).any(|(part, hash)| {
            Some(part.part) == selected_part && hash.eq_ignore_ascii_case(expected_sha256)
        }) {
            return Ok(Some(single_source(path)));
        }
    }
    Ok(None)
}

pub fn read_demo_source_with_options(
    source: &DemoSourceSet,
    options: ReadDemoOptions,
) -> Result<ParsedDemo> {
    let parts = source
        .parts
        .iter()
        .map(|part| read_demo_with_options(&part.path, options))
        .collect::<Result<Vec<_>>>()?;
    merge_parsed_demo_parts(source, parts)
}

fn single_source(path: &Path) -> DemoSourceSet {
    DemoSourceSet {
        logical_stem: demo_stem(path),
        parts: vec![DemoSourcePart {
            part: 1,
            path: path.to_path_buf(),
        }],
    }
}

fn demo_stem(path: &Path) -> String {
    let name = path
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_else(|| "demo.dem".to_string());
    strip_demo_extension(&name)
        .map(str::to_string)
        .unwrap_or(name)
}

fn segment_file_name(path: &Path) -> Option<SegmentFileName> {
    let name = path.file_name()?.to_str()?;
    let stem = strip_demo_extension(name)?;
    let marker = stem.to_ascii_lowercase().rfind("-p")?;
    let digits = &stem[marker + 2..];
    if digits.is_empty() || !digits.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }
    let part = digits.parse::<u32>().ok().filter(|part| *part > 0)?;
    let base = stem[..marker].trim_end().to_string();
    (!base.is_empty()).then_some(SegmentFileName { base, part })
}

fn strip_demo_extension(name: &str) -> Option<&str> {
    let lower = name.to_ascii_lowercase();
    let suffix_len = if lower.ends_with(".dem.zst") {
        ".dem.zst".len()
    } else if lower.ends_with(".dem") {
        ".dem".len()
    } else {
        return None;
    };
    Some(&name[..name.len() - suffix_len])
}

fn compound_demo_sha256(hashes: &[String]) -> Result<String> {
    let Some(first) = hashes.first() else {
        return Err(Error::InvalidDemo("demo segment set is empty".into()));
    };
    if hashes.len() == 1 {
        return Ok(first.clone());
    }
    if hashes
        .iter()
        .any(|hash| hash.len() != 64 || !hash.bytes().all(|byte| byte.is_ascii_hexdigit()))
    {
        return Err(Error::InvalidDemo(
            "demo segment contains an invalid content hash".into(),
        ));
    }
    let mut hasher = Sha256::new();
    hasher.update(SERIES_HASH_DOMAIN);
    hasher.update((hashes.len() as u32).to_le_bytes());
    for (index, hash) in hashes.iter().enumerate() {
        hasher.update(((index + 1) as u32).to_le_bytes());
        hasher.update(hash.as_bytes());
    }
    Ok(format!("{:x}", hasher.finalize()))
}

fn merge_parsed_demo_parts(
    source: &DemoSourceSet,
    mut parts: Vec<ParsedDemo>,
) -> Result<ParsedDemo> {
    if parts.len() != source.parts.len() || parts.is_empty() {
        return Err(Error::InvalidDemo(
            "parsed demo segment count does not match the source set".into(),
        ));
    }
    if parts.len() == 1 {
        return Ok(parts.remove(0));
    }
    validate_segment_metadata(&parts)?;

    let completed = parts
        .iter()
        .map(completed_rounds)
        .collect::<Result<Vec<_>>>()?;
    validate_round_continuity(&parts, &completed)?;

    let first_match_start = canonical_match_start(&parts[0], &completed[0])?;
    let last_index = parts.len() - 1;
    let final_match_end = canonical_match_end(&parts[last_index], &completed[last_index])?;
    let part_hashes = parts
        .iter()
        .map(|part| part.demo_sha256.clone())
        .collect::<Vec<_>>();
    let demo_sha256 = compound_demo_sha256(&part_hashes)?;
    let map = parts[0].map.clone();
    let demo_patch_version = parts[0].demo_patch_version;
    let demo_version_name = parts[0].demo_version_name.clone();
    let server_name = parts[0].server_name.clone();
    let tick_rate = parts[0].tick_rate;

    let mut rows = Vec::new();
    let mut projectiles = Vec::new();
    let mut voice_frames = Vec::new();
    let mut events = Vec::new();
    let mut round_freeze_end_ticks = Vec::new();
    let mut bomb_beginplant_ticks = Vec::new();
    let mut bomb_planted_ticks = Vec::new();
    let mut avatars = BTreeMap::<u64, ParsedAvatarOverride>::new();
    let mut econ_items = BTreeMap::<EconItemKey, ParsedEconItem>::new();
    let mut previous_max_tick = None::<i32>;

    for (index, mut part) in parts.into_iter().enumerate() {
        for avatar in part.avatar_overrides.drain(..) {
            avatars.insert(avatar.steam_id, avatar);
        }
        for item in part.econ_items.drain(..) {
            econ_items.insert(EconItemKey::from(&item), item);
        }

        let rounds = &completed[index];
        part.rows.retain(|row| rounds.contains_key(&row.round));
        part.round_freeze_end_ticks = rounds.values().map(|round| round.freeze_end_tick).collect();
        part.bomb_beginplant_ticks
            .retain(|tick| tick_in_completed_round(*tick, rounds));
        part.bomb_planted_ticks
            .retain(|tick| tick_in_completed_round(*tick, rounds));
        part.projectiles.retain(|projectile| {
            tick_in_completed_round(projectile.tick, rounds)
                || projectile
                    .effect_tick
                    .is_some_and(|tick| tick_in_completed_round(tick, rounds))
        });
        part.voice_frames
            .retain(|frame| tick_in_completed_round(frame.tick, rounds));
        part.events = retained_events(
            part.events,
            rounds,
            (index == 0).then_some(&first_match_start),
            (index == last_index).then_some(&final_match_end),
        );

        let (part_min_tick, part_max_tick) = retained_tick_range(&part).ok_or_else(|| {
            Error::InvalidDemo(format!(
                "demo segment p{} contains no completed round data",
                source.parts[index].part
            ))
        })?;
        let offset = previous_max_tick
            .map(|previous| i64::from(previous) + 1 - i64::from(part_min_tick))
            .unwrap_or(0);
        shift_part_ticks(&mut part, offset, source.parts[index].part)?;
        previous_max_tick = Some(checked_shift_tick(
            part_max_tick,
            offset,
            source.parts[index].part,
        )?);

        rows.extend(part.rows);
        projectiles.extend(part.projectiles);
        voice_frames.extend(part.voice_frames);
        events.extend(part.events);
        round_freeze_end_ticks.extend(part.round_freeze_end_ticks);
        bomb_beginplant_ticks.extend(part.bomb_beginplant_ticks);
        bomb_planted_ticks.extend(part.bomb_planted_ticks);
    }

    rows.sort_by_key(|row| (row.round, row.tick, row.steam_id));
    projectiles.sort_by_key(|projectile| projectile.tick);
    voice_frames.sort_by_key(|frame| (frame.xuid, frame.tick));
    events.sort_by_key(|event| event.tick);
    round_freeze_end_ticks.sort_unstable();
    round_freeze_end_ticks.dedup();
    bomb_beginplant_ticks.sort_unstable();
    bomb_beginplant_ticks.dedup();
    bomb_planted_ticks.sort_unstable();
    bomb_planted_ticks.dedup();

    if rows.is_empty() {
        return Err(Error::InvalidDemo(
            "merged demo contains no player rows".into(),
        ));
    }

    // Metadata compatibility was validated before the parts were consumed. The
    // public path remains the primary local source, while the logical stem and
    // compound hash identify the one merged match.
    Ok(ParsedDemo {
        path: source.primary_path().display().to_string(),
        stem: source.logical_stem.clone(),
        demo_sha256,
        map,
        demo_patch_version,
        demo_version_name,
        server_name,
        playback_time_seconds: None,
        tick_rate,
        round_freeze_end_ticks,
        bomb_beginplant_ticks,
        bomb_planted_ticks,
        rows,
        projectiles,
        voice_frames,
        events,
        avatar_overrides: avatars.into_values().collect(),
        econ_items: econ_items.into_values().collect(),
    })
}

fn validate_segment_metadata(parts: &[ParsedDemo]) -> Result<()> {
    let first = &parts[0];
    let first_roster = stable_roster(first);
    if first_roster.len() < 8 {
        return Err(Error::InvalidDemo(format!(
            "demo segment p1 has insufficient roster evidence: {} players",
            first_roster.len()
        )));
    }
    for (index, part) in parts.iter().enumerate().skip(1) {
        if !part.map.eq_ignore_ascii_case(&first.map) {
            return Err(segment_mismatch(index, "map", &first.map, &part.map));
        }
        if !part.tick_rate.is_finite()
            || !first.tick_rate.is_finite()
            || (part.tick_rate - first.tick_rate).abs() > 0.01
        {
            return Err(segment_mismatch(
                index,
                "tick rate",
                &first.tick_rate.to_string(),
                &part.tick_rate.to_string(),
            ));
        }
        require_matching_optional(
            index,
            "patch version",
            first.demo_patch_version,
            part.demo_patch_version,
        )?;
        require_matching_optional_ref(
            index,
            "demo version",
            first.demo_version_name.as_deref(),
            part.demo_version_name.as_deref(),
        )?;
        require_matching_optional_ref(
            index,
            "server name",
            first.server_name.as_deref(),
            part.server_name.as_deref(),
        )?;

        let roster = stable_roster(part);
        if roster.len() < 8 {
            return Err(Error::InvalidDemo(format!(
                "demo segment p{} has insufficient roster evidence: {} players",
                index + 1,
                roster.len()
            )));
        }
        let smaller = first_roster.len().min(roster.len());
        let shared = first_roster.intersection(&roster).count();
        let required = if smaller >= 9 { 8 } else { smaller };
        if shared < required {
            return Err(Error::InvalidDemo(format!(
                "demo segment p{} roster mismatch: only {shared} of {smaller} players overlap",
                index + 1
            )));
        }
    }
    Ok(())
}

fn require_matching_optional<T>(
    index: usize,
    label: &str,
    left: Option<T>,
    right: Option<T>,
) -> Result<()>
where
    T: std::fmt::Display + PartialEq,
{
    if let (Some(left), Some(right)) = (left, right) {
        if left != right {
            return Err(segment_mismatch(
                index,
                label,
                &left.to_string(),
                &right.to_string(),
            ));
        }
    }
    Ok(())
}

fn require_matching_optional_ref(
    index: usize,
    label: &str,
    left: Option<&str>,
    right: Option<&str>,
) -> Result<()> {
    if let (Some(left), Some(right)) = (left, right) {
        if !left.trim().eq_ignore_ascii_case(right.trim()) {
            return Err(segment_mismatch(index, label, left, right));
        }
    }
    Ok(())
}

fn segment_mismatch(index: usize, label: &str, left: &str, right: &str) -> Error {
    Error::InvalidDemo(format!(
        "demo segment p{} {label} mismatch: {left:?} != {right:?}",
        index + 1
    ))
}

fn stable_roster(parsed: &ParsedDemo) -> BTreeSet<u64> {
    parsed
        .rows
        .iter()
        .filter(|row| row.steam_id != 0 && matches!(row.team_num, 2 | 3))
        .map(|row| row.steam_id)
        .collect()
}

fn completed_rounds(parsed: &ParsedDemo) -> Result<BTreeMap<u32, CompletedRound>> {
    let mut spans = BTreeMap::<u32, (i32, i32)>::new();
    let mut rounds_by_tick = BTreeMap::<i32, BTreeMap<u32, usize>>::new();
    for row in &parsed.rows {
        let span = spans.entry(row.round).or_insert((row.tick, row.tick));
        span.0 = span.0.min(row.tick);
        span.1 = span.1.max(row.tick);
        *rounds_by_tick
            .entry(row.tick)
            .or_default()
            .entry(row.round)
            .or_default() += 1;
    }

    let match_start_tick = parsed
        .events
        .iter()
        .filter(|event| event.name == "round_announce_match_start")
        .map(|event| event.tick)
        .max();
    let match_end_tick = parsed
        .events
        .iter()
        .filter(|event| event.name == "cs_win_panel_match")
        .map(|event| event.tick)
        .min();
    let mut winners_by_tick = BTreeMap::<i32, BTreeSet<u8>>::new();
    for event in parsed.events.iter().filter(|event| {
        event.name == "round_end"
            && matches!(event.winner_side, Some(2 | 3))
            && match_start_tick.is_none_or(|start| event.tick >= start)
            && match_end_tick.is_none_or(|end| event.tick <= end)
    }) {
        winners_by_tick
            .entry(event.tick)
            .or_default()
            .insert(event.winner_side.expect("filtered above"));
    }

    let mut freeze_ticks = parsed.round_freeze_end_ticks.clone();
    freeze_ticks.sort_unstable();
    freeze_ticks.dedup();

    let mut completed = BTreeMap::new();
    let mut previous_round_end_tick = match_start_tick
        .map(|tick| tick.saturating_sub(1))
        .unwrap_or(i32::MIN);
    for (round_end_tick, winners) in winners_by_tick {
        if winners.len() != 1 {
            return Err(Error::InvalidDemo(format!(
                "conflicting round winners at tick {round_end_tick}"
            )));
        }
        let Some(freeze_end_tick) = freeze_ticks
            .iter()
            .copied()
            .filter(|tick| *tick > previous_round_end_tick && *tick <= round_end_tick)
            .next_back()
        else {
            previous_round_end_tick = round_end_tick;
            continue;
        };
        let Some(round) = modal_round_at_tick(&rounds_by_tick, freeze_end_tick)
            .or_else(|| predecessor_round(&rounds_by_tick, round_end_tick))
        else {
            previous_round_end_tick = round_end_tick;
            continue;
        };
        let Some(&(row_min_tick, row_max_tick)) = spans.get(&round) else {
            previous_round_end_tick = round_end_tick;
            continue;
        };
        let Some(live_start_tick) = parsed
            .rows
            .iter()
            .filter(|row| {
                row.round == round
                    && row.tick >= freeze_end_tick
                    && row.tick <= round_end_tick
                    && row.round_in_progress
                    && !row.is_freeze_period
                    && row.steam_id != 0
                    && matches!(row.team_num, 2 | 3)
            })
            .map(|row| row.tick)
            .min()
        else {
            previous_round_end_tick = round_end_tick;
            continue;
        };
        let winner_side = *winners.iter().next().expect("one winner checked above");
        completed.insert(
            round,
            CompletedRound {
                row_min_tick,
                row_max_tick,
                freeze_end_tick,
                round_end_tick,
                winner_side,
                start_score_total: score_total_at_round_start(
                    &parsed.rows,
                    round,
                    live_start_tick,
                    round_end_tick,
                ),
            },
        );
        previous_round_end_tick = round_end_tick;
    }
    Ok(completed)
}

fn score_total_at_round_start(
    rows: &[ParsedPlayerTick],
    round: u32,
    live_start_tick: i32,
    round_end_tick: i32,
) -> Option<u32> {
    let mut by_tick = BTreeMap::<i32, [BTreeMap<u32, usize>; 2]>::new();
    for row in rows.iter().filter(|row| {
        row.round == round
            && row.tick >= live_start_tick
            && row.tick <= round_end_tick
            && row.round_in_progress
            && !row.is_freeze_period
            && matches!(row.team_num, 2 | 3)
    }) {
        let Some(score) = row.team_rounds_total else {
            continue;
        };
        *by_tick.entry(row.tick).or_default()[usize::from(row.team_num - 2)]
            .entry(score)
            .or_default() += 1;
    }
    let mut totals = BTreeMap::<u32, usize>::new();
    for sides in by_tick.into_values() {
        let Some(total) = mode_score(&sides[0])
            .and_then(|t| mode_score(&sides[1]).and_then(|ct| t.checked_add(ct)))
        else {
            continue;
        };
        *totals.entry(total).or_default() += 1;
    }
    mode_score(&totals)
}

fn mode_score(values: &BTreeMap<u32, usize>) -> Option<u32> {
    values
        .iter()
        .max_by_key(|(score, count)| (**count, std::cmp::Reverse(**score)))
        .map(|(score, _)| *score)
}

fn predecessor_round(
    rounds_by_tick: &BTreeMap<i32, BTreeMap<u32, usize>>,
    tick: i32,
) -> Option<u32> {
    rounds_by_tick
        .range(..tick)
        .next_back()
        .and_then(|(_, rounds)| {
            rounds
                .iter()
                .max_by_key(|(round, count)| (**count, std::cmp::Reverse(**round)))
                .map(|(round, _)| *round)
        })
}

fn modal_round_at_tick(
    rounds_by_tick: &BTreeMap<i32, BTreeMap<u32, usize>>,
    tick: i32,
) -> Option<u32> {
    rounds_by_tick.get(&tick).and_then(|rounds| {
        rounds
            .iter()
            .max_by_key(|(round, count)| (**count, std::cmp::Reverse(**round)))
            .map(|(round, _)| *round)
    })
}

fn validate_round_continuity(
    parts: &[ParsedDemo],
    completed: &[BTreeMap<u32, CompletedRound>],
) -> Result<()> {
    let first_round = completed
        .first()
        .and_then(|rounds| rounds.keys().next().copied())
        .ok_or_else(|| Error::InvalidDemo("p1 contains no completed match rounds".into()))?;
    if first_round != 0 {
        return Err(Error::InvalidDemo(format!(
            "segmented match starts at round {first_round}, expected round 0"
        )));
    }
    if !parts[0]
        .events
        .iter()
        .any(|event| event.name == "round_announce_match_start")
    {
        return Err(Error::InvalidDemo(
            "p1 has no formal match-start event".into(),
        ));
    }
    if !parts.last().is_some_and(|part| {
        part.events
            .iter()
            .any(|event| event.name == "cs_win_panel_match")
    }) {
        return Err(Error::InvalidDemo(
            "last demo segment has no match-end event; another part may be missing".into(),
        ));
    }

    let mut previous_last = None::<u32>;
    for (index, rounds) in completed.iter().enumerate() {
        let first = rounds.keys().next().copied().ok_or_else(|| {
            Error::InvalidDemo(format!(
                "demo segment p{} has no completed rounds",
                index + 1
            ))
        })?;
        let last = rounds.keys().next_back().copied().unwrap_or(first);
        for round in first..=last {
            if !rounds.contains_key(&round) {
                return Err(Error::InvalidDemo(format!(
                    "demo segment p{} is missing completed round {round}",
                    index + 1
                )));
            }
        }
        if let Some(previous) = previous_last {
            let expected = previous.saturating_add(1);
            if first != expected {
                return Err(Error::InvalidDemo(format!(
                    "demo segment boundary is not continuous: p{} ends at round {previous}, p{} starts at round {first}",
                    index,
                    index + 1
                )));
            }
            validate_boundary_scores(
                index,
                &parts[index - 1],
                completed[index - 1]
                    .get(&previous)
                    .expect("previous completed round exists"),
                &parts[index],
                rounds.get(&first).expect("first completed round exists"),
            )?;
        }
        if let Some(score_total) = rounds.get(&first).and_then(|round| round.start_score_total) {
            if score_total != first {
                return Err(Error::InvalidDemo(format!(
                    "demo segment p{} starts round {first} from score total {score_total}",
                    index + 1
                )));
            }
        }
        previous_last = Some(last);
    }
    Ok(())
}

fn validate_boundary_scores(
    next_index: usize,
    previous_part: &ParsedDemo,
    previous_round: &CompletedRound,
    next_part: &ParsedDemo,
    next_round: &CompletedRound,
) -> Result<()> {
    let expected = score_by_player(previous_part, previous_round, true);
    let actual = score_by_player(next_part, next_round, false);
    let shared = expected
        .iter()
        .filter_map(|(steam_id, expected_score)| {
            actual
                .get(steam_id)
                .map(|actual_score| (*steam_id, *expected_score, *actual_score))
        })
        .collect::<Vec<_>>();
    if shared.len() < 8 {
        return Err(Error::InvalidDemo(format!(
            "demo segment boundary p{} -> p{} has insufficient score evidence",
            next_index,
            next_index + 1
        )));
    }
    if let Some((steam_id, expected_score, actual_score)) = shared
        .into_iter()
        .find(|(_, expected_score, actual_score)| expected_score != actual_score)
    {
        return Err(Error::InvalidDemo(format!(
            "demo segment boundary p{} -> p{} score mismatch for SteamID {steam_id}: expected {expected_score}, got {actual_score}",
            next_index,
            next_index + 1
        )));
    }
    Ok(())
}

fn score_by_player(
    parsed: &ParsedDemo,
    round: &CompletedRound,
    include_round_win: bool,
) -> BTreeMap<u64, u32> {
    let mut observations = BTreeMap::<u64, BTreeMap<(u8, u32), usize>>::new();
    for row in parsed.rows.iter().filter(|row| {
        row.steam_id != 0
            && matches!(row.team_num, 2 | 3)
            && row.round_in_progress
            && !row.is_freeze_period
            && row.tick >= round.freeze_end_tick
            && row.tick <= round.round_end_tick
    }) {
        let Some(score) = row.team_rounds_total else {
            continue;
        };
        *observations
            .entry(row.steam_id)
            .or_default()
            .entry((row.team_num, score))
            .or_default() += 1;
    }
    observations
        .into_iter()
        .filter_map(|(steam_id, values)| {
            values
                .into_iter()
                .max_by_key(|((team_num, score), count)| {
                    (*count, std::cmp::Reverse((*team_num, *score)))
                })
                .and_then(|((team_num, score), _)| {
                    let won = include_round_win && team_num == round.winner_side;
                    score.checked_add(u32::from(won))
                })
                .map(|score| (steam_id, score))
        })
        .collect()
}

fn canonical_match_start(
    part: &ParsedDemo,
    rounds: &BTreeMap<u32, CompletedRound>,
) -> Result<ParsedGameEvent> {
    let last_tick = rounds
        .values()
        .map(|round| round.round_end_tick)
        .max()
        .unwrap_or(i32::MAX);
    part.events
        .iter()
        .filter(|event| event.name == "round_announce_match_start" && event.tick <= last_tick)
        .max_by_key(|event| event.tick)
        .cloned()
        .ok_or_else(|| Error::InvalidDemo("p1 has no usable match-start event".into()))
}

fn canonical_match_end(
    part: &ParsedDemo,
    rounds: &BTreeMap<u32, CompletedRound>,
) -> Result<ParsedGameEvent> {
    let final_round_end = rounds
        .values()
        .map(|round| round.round_end_tick)
        .max()
        .unwrap_or(i32::MIN);
    part.events
        .iter()
        .filter(|event| event.name == "cs_win_panel_match" && event.tick >= final_round_end)
        .min_by_key(|event| event.tick)
        .cloned()
        .ok_or_else(|| Error::InvalidDemo("last segment has no usable match-end event".into()))
}

fn retained_events(
    source: Vec<ParsedGameEvent>,
    rounds: &BTreeMap<u32, CompletedRound>,
    match_start: Option<&ParsedGameEvent>,
    match_end: Option<&ParsedGameEvent>,
) -> Vec<ParsedGameEvent> {
    let mut events = source
        .into_iter()
        .filter(|event| {
            if matches!(
                event.name.as_str(),
                "round_announce_match_start" | "cs_win_panel_match"
            ) {
                return false;
            }
            if event.name == "round_end" {
                return rounds.values().any(|round| {
                    event.tick == round.round_end_tick
                        && event.winner_side == Some(round.winner_side)
                });
            }
            round_for_tick(event.tick, rounds).is_some()
        })
        .collect::<Vec<_>>();
    if let Some(event) = match_start {
        events.push(event.clone());
    }
    if let Some(event) = match_end {
        events.push(event.clone());
    }
    events
}

fn tick_in_completed_round(tick: i32, rounds: &BTreeMap<u32, CompletedRound>) -> bool {
    round_for_tick(tick, rounds).is_some()
}

fn round_for_tick(tick: i32, rounds: &BTreeMap<u32, CompletedRound>) -> Option<&CompletedRound> {
    rounds
        .values()
        .find(|round| tick >= round.row_min_tick && tick <= round.row_max_tick)
}

fn retained_tick_range(part: &ParsedDemo) -> Option<(i32, i32)> {
    part.rows
        .iter()
        .map(|row| row.tick)
        .chain(part.events.iter().map(|event| event.tick))
        .chain(part.projectiles.iter().flat_map(|projectile| {
            [Some(projectile.tick), projectile.effect_tick]
                .into_iter()
                .flatten()
        }))
        .chain(part.voice_frames.iter().map(|frame| frame.tick))
        .chain(part.round_freeze_end_ticks.iter().copied())
        .chain(part.bomb_beginplant_ticks.iter().copied())
        .chain(part.bomb_planted_ticks.iter().copied())
        .fold(None, |range, tick| {
            Some(match range {
                Some((min_tick, max_tick)) => (min_tick.min(tick), max_tick.max(tick)),
                None => (tick, tick),
            })
        })
}

fn shift_part_ticks(part: &mut ParsedDemo, offset: i64, part_number: u32) -> Result<()> {
    for row in &mut part.rows {
        row.tick = checked_shift_tick(row.tick, offset, part_number)?;
    }
    for projectile in &mut part.projectiles {
        projectile.tick = checked_shift_tick(projectile.tick, offset, part_number)?;
        if let Some(tick) = projectile.effect_tick.as_mut() {
            *tick = checked_shift_tick(*tick, offset, part_number)?;
        }
    }
    for frame in &mut part.voice_frames {
        frame.tick = checked_shift_tick(frame.tick, offset, part_number)?;
    }
    for event in &mut part.events {
        event.tick = checked_shift_tick(event.tick, offset, part_number)?;
    }
    for tick in &mut part.round_freeze_end_ticks {
        *tick = checked_shift_tick(*tick, offset, part_number)?;
    }
    for tick in &mut part.bomb_beginplant_ticks {
        *tick = checked_shift_tick(*tick, offset, part_number)?;
    }
    for tick in &mut part.bomb_planted_ticks {
        *tick = checked_shift_tick(*tick, offset, part_number)?;
    }
    Ok(())
}

fn checked_shift_tick(tick: i32, offset: i64, part_number: u32) -> Result<i32> {
    i32::try_from(i64::from(tick) + offset).map_err(|_| {
        Error::InvalidDemo(format!(
            "demo segment p{part_number} tick rebasing exceeds the supported range"
        ))
    })
}

#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct EconItemKey {
    steam_id: Option<u64>,
    item_def_index: Option<u32>,
    paint_kit: Option<u32>,
    paint_seed: Option<u32>,
    paint_wear_raw: Option<u32>,
    paint_wear_bits: Option<u32>,
    item_name: Option<String>,
    skin_name: Option<String>,
}

impl From<&ParsedEconItem> for EconItemKey {
    fn from(item: &ParsedEconItem) -> Self {
        Self {
            steam_id: item.steam_id,
            item_def_index: item.item_def_index,
            paint_kit: item.paint_kit,
            paint_seed: item.paint_seed,
            paint_wear_raw: item.paint_wear_raw,
            paint_wear_bits: item.paint_wear.map(f32::to_bits),
            item_name: item.item_name.clone(),
            skin_name: item.skin_name.clone(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::analysis::browser::analyze_browser_demo;
    use crate::analysis::quality::AnalysisOptions;
    use crate::demo_reader::demo_content_sha256;
    use crate::model::{ParsedProjectile, ParsedVoiceFrame};

    fn source(root: &Path) -> DemoSourceSet {
        DemoSourceSet {
            logical_stem: "match".to_string(),
            parts: vec![
                DemoSourcePart {
                    part: 1,
                    path: root.join("match-p1.dem"),
                },
                DemoSourcePart {
                    part: 2,
                    path: root.join("match-p2.dem"),
                },
            ],
        }
    }

    fn row(round: u32, tick: i32, steam_id: u64, team_num: u8, score: u32) -> ParsedPlayerTick {
        ParsedPlayerTick {
            tick,
            steam_id,
            name: format!("player-{steam_id}"),
            team_num,
            is_alive: true,
            round,
            round_in_progress: tick >= 20,
            is_freeze_period: tick < 20,
            team_rounds_total: Some(score),
            scoreboard_score: Some((round * 10 + score) as i32),
            ..ParsedPlayerTick::default()
        }
    }

    fn completed_round_rows(round: u32, side_scores: [u32; 2]) -> Vec<ParsedPlayerTick> {
        [10, 20, 40]
            .into_iter()
            .flat_map(|tick| {
                (0..10).map(move |index| {
                    let team_num = if index < 5 { 2 } else { 3 };
                    let score = side_scores[usize::from(team_num - 2)];
                    row(round, tick, 100 + index, team_num, score)
                })
            })
            .collect()
    }

    fn part_one() -> ParsedDemo {
        let mut rows = completed_round_rows(0, [0, 0]);
        rows.extend((0..10).map(|index| {
            let team_num = if index < 5 { 2 } else { 3 };
            row(
                1,
                60,
                100 + index,
                team_num,
                if team_num == 2 { 1 } else { 0 },
            )
        }));
        ParsedDemo {
            path: "match-p1.dem".to_string(),
            stem: "match-p1".to_string(),
            demo_sha256: "11".repeat(32),
            map: "de_mirage".to_string(),
            demo_patch_version: Some(14_165),
            demo_version_name: Some("valve_demo_2".to_string()),
            server_name: Some("ESL Match Server #1".to_string()),
            playback_time_seconds: Some(100.0),
            tick_rate: 64.0,
            round_freeze_end_ticks: vec![15, 65],
            rows,
            events: vec![
                ParsedGameEvent {
                    tick: 5,
                    name: "round_announce_match_start".to_string(),
                    ..ParsedGameEvent::default()
                },
                ParsedGameEvent {
                    // HLTV segments commonly put round_end one tick after the
                    // last live row, at the same tick as the next shell.
                    tick: 60,
                    name: "round_end".to_string(),
                    winner_side: Some(2),
                    ..ParsedGameEvent::default()
                },
            ],
            ..ParsedDemo::default()
        }
    }

    fn part_two() -> ParsedDemo {
        let mut rows = completed_round_rows(1, [1, 0]);
        for row in &mut rows {
            row.scoreboard_score = Some(99);
        }
        rows.extend((0..2).map(|index| row(2, 60, 100 + index, 2, 2)));
        ParsedDemo {
            path: "match-p2.dem".to_string(),
            stem: "match-p2".to_string(),
            demo_sha256: "22".repeat(32),
            map: "de_mirage".to_string(),
            demo_patch_version: Some(14_165),
            demo_version_name: Some("valve_demo_2".to_string()),
            server_name: Some("ESL Match Server #1".to_string()),
            playback_time_seconds: Some(200.0),
            tick_rate: 64.0,
            round_freeze_end_ticks: vec![15, 65],
            bomb_planted_ticks: vec![30],
            rows,
            projectiles: vec![ParsedProjectile {
                tick: 25,
                effect_tick: Some(30),
                ..ParsedProjectile::default()
            }],
            voice_frames: vec![ParsedVoiceFrame {
                tick: 25,
                xuid: 100,
                ..ParsedVoiceFrame::default()
            }],
            events: vec![
                ParsedGameEvent {
                    tick: 1,
                    name: "round_end".to_string(),
                    winner_side: None,
                    ..ParsedGameEvent::default()
                },
                ParsedGameEvent {
                    tick: 60,
                    name: "round_end".to_string(),
                    winner_side: Some(3),
                    ..ParsedGameEvent::default()
                },
                ParsedGameEvent {
                    tick: 65,
                    name: "cs_win_panel_match".to_string(),
                    ..ParsedGameEvent::default()
                },
            ],
            ..ParsedDemo::default()
        }
    }

    #[test]
    fn resolves_any_part_to_one_ordered_source_set() {
        let temp = tempfile::tempdir().unwrap();
        fs::write(temp.path().join("Match-p1.dem"), b"one").unwrap();
        fs::write(temp.path().join("match-p2.dem"), b"two").unwrap();

        let from_p1 = resolve_demo_source(&temp.path().join("Match-p1.dem")).unwrap();
        let from_p2 = resolve_demo_source(&temp.path().join("match-p2.dem")).unwrap();

        assert_eq!(from_p1, from_p2);
        assert_eq!(from_p1.logical_stem, "Match");
        assert_eq!(from_p1.parts.len(), 2);
        assert_eq!(from_p1.parts[0].part, 1);
        assert_eq!(from_p1.parts[1].part, 2);
    }

    #[test]
    fn single_demo_hash_remains_the_content_hash() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("ordinary.dem");
        fs::write(&path, b"ordinary demo bytes").unwrap();
        let source = resolve_demo_source(&path).unwrap();

        assert!(!source.is_segmented());
        assert_eq!(
            demo_source_sha256(&source).unwrap(),
            demo_content_sha256(&path).unwrap()
        );
    }

    #[test]
    fn compound_hash_is_order_sensitive_and_path_independent() {
        let first = compound_demo_sha256(&["11".repeat(32), "22".repeat(32)]).unwrap();
        let second = compound_demo_sha256(&["22".repeat(32), "11".repeat(32)]).unwrap();
        assert_ne!(first, second);
        assert_eq!(first.len(), 64);
    }

    #[test]
    fn expected_raw_part_hash_preserves_legacy_singleton_identity() {
        let temp = tempfile::tempdir().unwrap();
        let first = temp.path().join("match-p1.dem");
        let second = temp.path().join("match-p2.dem");
        fs::write(&first, b"one").unwrap();
        fs::write(&second, b"two").unwrap();

        let expected = demo_content_sha256(&second).unwrap();
        let resolved = resolve_demo_source_for_sha256(&second, &expected)
            .unwrap()
            .unwrap();

        assert_eq!(resolved.parts.len(), 1);
        assert_eq!(resolved.primary_path(), second);

        fs::remove_file(first).unwrap();
        let resolved = resolve_demo_source_for_sha256(&second, &expected)
            .unwrap()
            .unwrap();
        assert_eq!(resolved.parts.len(), 1);
        assert_eq!(resolved.primary_path(), second);
    }

    #[test]
    fn rejects_gapped_or_ambiguous_segment_sets() {
        let gap = tempfile::tempdir().unwrap();
        fs::write(gap.path().join("match-p1.dem"), b"one").unwrap();
        fs::write(gap.path().join("match-p3.dem"), b"three").unwrap();
        assert!(resolve_demo_source(&gap.path().join("match-p1.dem"))
            .unwrap_err()
            .to_string()
            .contains("missing p2"));

        let ambiguous = tempfile::tempdir().unwrap();
        fs::write(ambiguous.path().join("match-p1.dem"), b"one").unwrap();
        fs::write(ambiguous.path().join("match-p1.dem.zst"), b"one compressed").unwrap();
        fs::write(ambiguous.path().join("match-p2.dem"), b"two").unwrap();
        assert!(resolve_demo_source(&ambiguous.path().join("match-p1.dem"))
            .unwrap_err()
            .to_string()
            .contains("ambiguous"));
    }

    #[test]
    fn merge_keeps_only_completed_rounds_and_rebases_every_tick_lane() {
        let temp = tempfile::tempdir().unwrap();
        let merged =
            merge_parsed_demo_parts(&source(temp.path()), vec![part_one(), part_two()]).unwrap();

        assert_eq!(merged.stem, "match");
        assert_eq!(
            merged.path,
            temp.path().join("match-p1.dem").display().to_string()
        );
        assert_eq!(merged.playback_time_seconds, None);
        assert_eq!(
            merged
                .rows
                .iter()
                .map(|row| row.round)
                .collect::<BTreeSet<_>>(),
            BTreeSet::from([0, 1])
        );
        assert_eq!(merged.round_freeze_end_ticks.len(), 2);
        assert_eq!(merged.bomb_planted_ticks.len(), 1);
        assert_eq!(merged.projectiles.len(), 1);
        assert_eq!(merged.voice_frames.len(), 1);
        assert_eq!(
            merged
                .events
                .iter()
                .filter(|event| event.name == "round_end")
                .count(),
            2
        );
        assert!(!merged
            .events
            .iter()
            .any(|event| event.name == "round_end" && event.winner_side.is_none()));
        assert_eq!(
            merged
                .events
                .iter()
                .filter(|event| event.name == "round_announce_match_start")
                .count(),
            1
        );
        assert_eq!(
            merged
                .events
                .iter()
                .filter(|event| event.name == "cs_win_panel_match")
                .count(),
            1
        );

        let part_one_max = merged
            .rows
            .iter()
            .filter(|row| row.round == 0)
            .map(|row| row.tick)
            .max()
            .unwrap();
        let part_two_min = merged
            .rows
            .iter()
            .filter(|row| row.round == 1)
            .map(|row| row.tick)
            .min()
            .unwrap();
        let part_one_round_end = merged
            .events
            .iter()
            .filter(|event| event.name == "round_end")
            .map(|event| event.tick)
            .min()
            .unwrap();
        assert_eq!(part_two_min, part_one_round_end + 1);
        assert!(part_one_round_end > part_one_max);
        assert!(merged.projectiles[0].tick > part_one_max);
        assert!(merged.projectiles[0].effect_tick.unwrap() > part_one_max);
        assert!(merged.voice_frames[0].tick > part_one_max);
        assert!(merged.bomb_planted_ticks[0] > part_one_max);
        assert_eq!(
            merged
                .rows
                .iter()
                .filter_map(|row| row.scoreboard_score)
                .max(),
            Some(99)
        );

        let browser = analyze_browser_demo(&merged, AnalysisOptions::default());
        let score = browser
            .score
            .expect("merged match should have a final score");
        assert_eq!(score.status, "final");
        assert_eq!(score.team_a.score + score.team_b.score, 2);
        assert_eq!(browser.players.len(), 10);
    }

    #[test]
    fn merge_rejects_non_contiguous_rounds_and_header_mismatch() {
        let temp = tempfile::tempdir().unwrap();
        let mut second = part_two();
        for row in &mut second.rows {
            row.round = row.round.saturating_add(1);
        }
        assert!(
            merge_parsed_demo_parts(&source(temp.path()), vec![part_one(), second])
                .unwrap_err()
                .to_string()
                .contains("not continuous")
        );

        let mut second = part_two();
        second.map = "de_anubis".to_string();
        assert!(
            merge_parsed_demo_parts(&source(temp.path()), vec![part_one(), second])
                .unwrap_err()
                .to_string()
                .contains("map mismatch")
        );

        let mut second = part_two();
        for row in &mut second.rows {
            if row.round == 1 {
                row.team_rounds_total = Some(if row.team_num == 2 { 0 } else { 1 });
            }
        }
        assert!(
            merge_parsed_demo_parts(&source(temp.path()), vec![part_one(), second])
                .unwrap_err()
                .to_string()
                .contains("score mismatch")
        );

        let mut second = part_two();
        second.rows.retain(|row| row.steam_id < 107);
        assert!(
            merge_parsed_demo_parts(&source(temp.path()), vec![part_one(), second])
                .unwrap_err()
                .to_string()
                .contains("insufficient roster evidence")
        );
    }
}
