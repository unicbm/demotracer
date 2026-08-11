/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use quick_xml::{de::from_str, events::Event, Reader};
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use std::fs;
use std::io::Read;
use std::ops::Range;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};
use tauri::http::{header, Method, Request, Response, StatusCode};

const CACHE_DIRECTORY: &str = "steam-profiles";
const LEGACY_CACHE_DIRECTORIES: [&str; 3] = [
    "steam-profiles-v3",
    "steam-profiles-v2",
    "steam-profiles-v1",
];
const ASSET_DIRECTORY: &str = "assets";
const CACHE_SCHEMA_VERSION: u32 = 1;
const CACHE_TTL_MS: u64 = 24 * 60 * 60 * 1_000;
const MISSING_ASSET_RETRY_MS: u64 = 5 * 60 * 1_000;
const MAX_PROFILES: usize = 32;
const MAX_PARALLEL_REQUESTS: usize = 4;
const MAX_PROFILE_XML_BYTES: usize = 512 * 1024;
const MAX_PROFILE_HTML_BYTES: usize = 1024 * 1024;
const MAX_PROFILE_IMAGE_BYTES: usize = 4 * 1024 * 1024;

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SteamProfileDto {
    pub steam_id: String,
    pub persona_name: String,
    pub avatar_url: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub avatar_frame_url: Option<String>,
    pub profile_url: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
struct CachedSteamProfile {
    #[serde(default)]
    schema_version: u32,
    fetched_at_ms: u64,
    profile: SteamProfileDto,
    #[serde(default)]
    assets_checked_at_ms: u64,
    #[serde(default)]
    avatar_asset: Option<CachedProfileAsset>,
    #[serde(default)]
    avatar_frame_asset: Option<CachedProfileAsset>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
struct CachedProfileAsset {
    file_name: String,
    mime_type: String,
    source_url: String,
    fetched_at_ms: u64,
}

#[derive(Clone, Debug)]
struct FetchedSteamProfile {
    profile: SteamProfileDto,
    enhanced_assets_complete: bool,
}

#[derive(Debug)]
struct ProfileCacheLayout {
    directory: PathBuf,
    asset_directory: PathBuf,
    legacy_directories: Vec<PathBuf>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ProfileAssetKind {
    Avatar,
    Frame,
}

impl ProfileAssetKind {
    fn path_segment(self) -> &'static str {
        match self {
            Self::Avatar => "avatar",
            Self::Frame => "frame",
        }
    }
}

#[derive(Debug, Deserialize)]
struct SteamCommunityProfileXml {
    #[serde(rename = "steamID64")]
    steam_id: String,
    #[serde(rename = "steamID")]
    persona_name: String,
    #[serde(rename = "avatarMedium")]
    avatar_medium_url: String,
    #[serde(default, rename = "avatarFull")]
    avatar_full_url: Option<String>,
}

#[derive(Debug, Default, Eq, PartialEq)]
struct ProfileAvatarAssets {
    animated_avatar_url: Option<String>,
    avatar_frame_url: Option<String>,
}

pub(crate) fn resolve_profiles(
    local_data_root: Option<PathBuf>,
    steam_ids: Vec<String>,
) -> Vec<SteamProfileDto> {
    let mut seen = BTreeSet::new();
    let steam_ids = steam_ids
        .into_iter()
        .map(|value| value.trim().to_string())
        .filter(|value| valid_steam_id(value) && seen.insert(value.clone()))
        .take(MAX_PROFILES)
        .collect::<Vec<_>>();
    let cache_layout = local_data_root.clone().and_then(ProfileCacheLayout::new);

    let mut profiles = Vec::with_capacity(steam_ids.len());
    for chunk in steam_ids.chunks(MAX_PARALLEL_REQUESTS) {
        let chunk_profiles = std::thread::scope(|scope| {
            chunk
                .iter()
                .map(|steam_id| {
                    let cache_layout = cache_layout.as_ref();
                    scope.spawn(move || resolve_profile(cache_layout, steam_id, false))
                })
                .collect::<Vec<_>>()
                .into_iter()
                .filter_map(|worker| worker.join().ok().flatten())
                .collect::<Vec<_>>()
        });
        profiles.extend(chunk_profiles);
    }
    if let Some(local_data_root) = local_data_root {
        let hydration_ids = profiles
            .iter()
            .map(|profile| profile.steam_id.clone())
            .collect::<Vec<_>>();
        if !hydration_ids.is_empty() {
            std::thread::spawn(move || hydrate_profile_assets(local_data_root, hydration_ids));
        }
    }
    profiles
}

impl ProfileCacheLayout {
    fn new(local_data_root: PathBuf) -> Option<Self> {
        let directory = local_data_root.join(CACHE_DIRECTORY);
        let asset_directory = directory.join(ASSET_DIRECTORY);
        fs::create_dir_all(&asset_directory).ok()?;
        Some(Self {
            directory,
            asset_directory,
            legacy_directories: LEGACY_CACHE_DIRECTORIES
                .iter()
                .map(|name| local_data_root.join(name))
                .collect(),
        })
    }
}

fn resolve_profile(
    cache_layout: Option<&ProfileCacheLayout>,
    steam_id: &str,
    hydrate_assets: bool,
) -> Option<SteamProfileDto> {
    resolve_profile_with(
        cache_layout,
        steam_id,
        now_ms(),
        hydrate_assets,
        &fetch_profile,
        &fetch_profile_asset,
    )
}

fn hydrate_profile_assets(local_data_root: PathBuf, steam_ids: Vec<String>) {
    let Some(cache_layout) = ProfileCacheLayout::new(local_data_root) else {
        return;
    };
    for chunk in steam_ids.chunks(MAX_PARALLEL_REQUESTS) {
        std::thread::scope(|scope| {
            for steam_id in chunk {
                let cache_layout = &cache_layout;
                scope.spawn(move || {
                    let _ = resolve_profile(Some(cache_layout), steam_id, true);
                });
            }
        });
    }
}

fn resolve_profile_with<PF, AF>(
    cache_layout: Option<&ProfileCacheLayout>,
    steam_id: &str,
    now: u64,
    hydrate_assets: bool,
    profile_fetcher: &PF,
    asset_fetcher: &AF,
) -> Option<SteamProfileDto>
where
    PF: Fn(&str) -> Option<FetchedSteamProfile>,
    AF: Fn(&str) -> Option<Vec<u8>>,
{
    let (mut cached, migrated) = cache_layout
        .map(|layout| read_or_migrate_cache(layout, steam_id))
        .unwrap_or((None, false));
    let mut cache_changed = migrated;
    let metadata_fresh = cached
        .as_ref()
        .is_some_and(|entry| now.saturating_sub(entry.fetched_at_ms) <= CACHE_TTL_MS);

    // A cached profile must remain immediately usable even when its metadata TTL has expired.
    // Refresh stale metadata only from the hydration pass; otherwise an offline Steam request
    // can make the GUI look as if the persistent cache disappeared until every timeout finishes.
    let should_fetch_metadata = cached.is_none() || (hydrate_assets && !metadata_fresh);
    if should_fetch_metadata {
        if let Some(mut fetched) = profile_fetcher(steam_id) {
            if !fetched.enhanced_assets_complete {
                if let Some(previous) = cached.as_ref() {
                    if trusted_animated_avatar_url(&previous.profile.avatar_url) {
                        fetched.profile.avatar_url = previous.profile.avatar_url.clone();
                    }
                    if fetched.profile.avatar_frame_url.is_none() {
                        fetched.profile.avatar_frame_url =
                            previous.profile.avatar_frame_url.clone();
                    }
                }
            }
            let previous = cached.as_ref();
            let keep_frame_asset = fetched.profile.avatar_frame_url.is_some();
            cached = Some(CachedSteamProfile {
                schema_version: CACHE_SCHEMA_VERSION,
                fetched_at_ms: now,
                profile: fetched.profile,
                assets_checked_at_ms: 0,
                avatar_asset: previous.and_then(|entry| entry.avatar_asset.clone()),
                avatar_frame_asset: keep_frame_asset
                    .then(|| previous.and_then(|entry| entry.avatar_frame_asset.clone()))
                    .flatten(),
            });
            cache_changed = true;
        }
    }

    let mut cached = cached?;
    let assets_need_refresh = cache_layout.is_some_and(|layout| {
        profile_asset_needs_refresh(
            layout,
            steam_id,
            ProfileAssetKind::Avatar,
            Some(&cached.profile.avatar_url),
            cached.avatar_asset.as_ref(),
        ) || profile_asset_needs_refresh(
            layout,
            steam_id,
            ProfileAssetKind::Frame,
            cached.profile.avatar_frame_url.as_deref(),
            cached.avatar_frame_asset.as_ref(),
        )
    });
    let may_retry_missing_assets = cached.assets_checked_at_ms == 0
        || now.saturating_sub(cached.assets_checked_at_ms) >= MISSING_ASSET_RETRY_MS;
    if hydrate_assets && assets_need_refresh && may_retry_missing_assets {
        if let Some(layout) = cache_layout {
            refresh_profile_asset(
                layout,
                steam_id,
                ProfileAssetKind::Avatar,
                Some(&cached.profile.avatar_url),
                &mut cached.avatar_asset,
                now,
                asset_fetcher,
            );
            refresh_profile_asset(
                layout,
                steam_id,
                ProfileAssetKind::Frame,
                cached.profile.avatar_frame_url.as_deref(),
                &mut cached.avatar_frame_asset,
                now,
                asset_fetcher,
            );
            cached.assets_checked_at_ms = now;
            cache_changed = true;
        }
    }

    if cache_changed {
        if let Some(layout) = cache_layout {
            let _ = write_cache(&layout.directory, steam_id, &cached);
        }
    }
    Some(materialize_cached_profile(cache_layout, steam_id, &cached))
}

fn read_or_migrate_cache(
    layout: &ProfileCacheLayout,
    steam_id: &str,
) -> (Option<CachedSteamProfile>, bool) {
    if let Some(cached) = read_cache(&layout.directory, steam_id) {
        return (Some(cached), false);
    }
    let cached = layout
        .legacy_directories
        .iter()
        .filter_map(|directory| read_cache(directory, steam_id))
        .max_by_key(|entry| entry.fetched_at_ms);
    (cached, true)
}

fn profile_asset_needs_refresh(
    layout: &ProfileCacheLayout,
    steam_id: &str,
    kind: ProfileAssetKind,
    source_url: Option<&str>,
    asset: Option<&CachedProfileAsset>,
) -> bool {
    let Some(source_url) = source_url else {
        return false;
    };
    !asset.is_some_and(|asset| {
        asset.source_url == source_url && cached_asset_available(layout, steam_id, kind, asset)
    })
}

fn refresh_profile_asset<AF>(
    layout: &ProfileCacheLayout,
    steam_id: &str,
    kind: ProfileAssetKind,
    source_url: Option<&str>,
    current: &mut Option<CachedProfileAsset>,
    now: u64,
    asset_fetcher: &AF,
) where
    AF: Fn(&str) -> Option<Vec<u8>>,
{
    let Some(source_url) = source_url else {
        *current = None;
        return;
    };
    if current.as_ref().is_some_and(|asset| {
        asset.source_url == source_url && cached_asset_available(layout, steam_id, kind, asset)
    }) {
        return;
    }
    let Some(bytes) = asset_fetcher(source_url) else {
        return;
    };
    let Some((mime_type, extension)) = image_format(&bytes) else {
        return;
    };
    let file_name = format!("{steam_id}-{}.{}", kind.path_segment(), extension);
    if write_replacing(&layout.asset_directory.join(&file_name), &bytes).is_ok() {
        *current = Some(CachedProfileAsset {
            file_name,
            mime_type: mime_type.to_string(),
            source_url: source_url.to_string(),
            fetched_at_ms: now,
        });
    }
}

fn materialize_cached_profile(
    cache_layout: Option<&ProfileCacheLayout>,
    steam_id: &str,
    cached: &CachedSteamProfile,
) -> SteamProfileDto {
    let mut profile = cached.profile.clone();
    if let Some(layout) = cache_layout {
        if let Some(asset) = cached.avatar_asset.as_ref().filter(|asset| {
            cached_asset_available(layout, steam_id, ProfileAssetKind::Avatar, asset)
        }) {
            profile.avatar_url = cached_asset_url(steam_id, ProfileAssetKind::Avatar, asset);
        }
        if let Some(asset) = cached.avatar_frame_asset.as_ref().filter(|asset| {
            cached_asset_available(layout, steam_id, ProfileAssetKind::Frame, asset)
        }) {
            profile.avatar_frame_url =
                Some(cached_asset_url(steam_id, ProfileAssetKind::Frame, asset));
        }
    }
    profile
}

fn cached_asset_available(
    layout: &ProfileCacheLayout,
    steam_id: &str,
    kind: ProfileAssetKind,
    asset: &CachedProfileAsset,
) -> bool {
    if !valid_cached_asset_metadata(steam_id, kind, asset) {
        return false;
    }
    let path = layout.asset_directory.join(&asset.file_name);
    let Ok(metadata) = fs::metadata(&path) else {
        return false;
    };
    if !metadata.is_file() || metadata.len() == 0 || metadata.len() > MAX_PROFILE_IMAGE_BYTES as u64
    {
        return false;
    }
    let Ok(mut file) = fs::File::open(path) else {
        return false;
    };
    let mut signature = [0_u8; 12];
    let Ok(read) = file.read(&mut signature) else {
        return false;
    };
    image_format(&signature[..read]).map(|(mime_type, _)| mime_type)
        == Some(asset.mime_type.as_str())
}

fn cached_asset_url(steam_id: &str, kind: ProfileAssetKind, asset: &CachedProfileAsset) -> String {
    #[cfg(windows)]
    let base = "http://steam-avatar.localhost";
    #[cfg(not(windows))]
    let base = "steam-avatar://localhost";
    format!(
        "{base}/{steam_id}/{}?v={}",
        kind.path_segment(),
        asset.fetched_at_ms
    )
}

fn read_cache(directory: &Path, steam_id: &str) -> Option<CachedSteamProfile> {
    let text = fs::read_to_string(directory.join(format!("{steam_id}.json"))).ok()?;
    let mut cached: CachedSteamProfile = serde_json::from_str(&text).ok()?;
    if cached.schema_version > CACHE_SCHEMA_VERSION
        || cached.profile.steam_id != steam_id
        || !valid_cached_profile(&cached.profile)
    {
        return None;
    }
    cached.avatar_asset = cached
        .avatar_asset
        .filter(|asset| valid_cached_asset_metadata(steam_id, ProfileAssetKind::Avatar, asset));
    cached.avatar_frame_asset = cached
        .avatar_frame_asset
        .filter(|asset| valid_cached_asset_metadata(steam_id, ProfileAssetKind::Frame, asset));
    Some(cached)
}

fn valid_cached_profile(profile: &SteamProfileDto) -> bool {
    valid_steam_id(&profile.steam_id)
        && trusted_profile_avatar_url(&profile.avatar_url)
        && profile
            .avatar_frame_url
            .as_deref()
            .is_none_or(trusted_avatar_frame_url)
        && profile.profile_url
            == format!("https://steamcommunity.com/profiles/{}", profile.steam_id)
}

fn valid_cached_asset_metadata(
    steam_id: &str,
    kind: ProfileAssetKind,
    asset: &CachedProfileAsset,
) -> bool {
    let Some(extension) = mime_extension(&asset.mime_type) else {
        return false;
    };
    asset.file_name == format!("{steam_id}-{}.{}", kind.path_segment(), extension)
        && match kind {
            ProfileAssetKind::Avatar => trusted_profile_avatar_url(&asset.source_url),
            ProfileAssetKind::Frame => trusted_avatar_frame_url(&asset.source_url),
        }
}

fn write_cache(
    directory: &Path,
    steam_id: &str,
    cached: &CachedSteamProfile,
) -> std::io::Result<()> {
    let mut cached = cached.clone();
    cached.schema_version = CACHE_SCHEMA_VERSION;
    write_replacing(
        &directory.join(format!("{steam_id}.json")),
        &serde_json::to_vec(&cached)?,
    )
}

fn write_replacing(path: &Path, bytes: &[u8]) -> std::io::Result<()> {
    let temporary = path.with_extension(format!(
        "{}.tmp-{}",
        path.extension()
            .and_then(|value| value.to_str())
            .unwrap_or("cache"),
        std::process::id()
    ));
    fs::write(&temporary, bytes)?;
    if fs::rename(&temporary, path).is_ok() {
        return Ok(());
    }
    if path.is_file() {
        fs::remove_file(path)?;
    }
    fs::rename(temporary, path)
}

fn image_format(bytes: &[u8]) -> Option<(&'static str, &'static str)> {
    if bytes.starts_with(&[0xff, 0xd8, 0xff]) {
        Some(("image/jpeg", "jpg"))
    } else if bytes.starts_with(b"\x89PNG\r\n\x1a\n") {
        Some(("image/png", "png"))
    } else if bytes.starts_with(b"GIF87a") || bytes.starts_with(b"GIF89a") {
        Some(("image/gif", "gif"))
    } else if bytes.len() >= 12 && bytes.starts_with(b"RIFF") && &bytes[8..12] == b"WEBP" {
        Some(("image/webp", "webp"))
    } else {
        None
    }
}

fn mime_extension(mime_type: &str) -> Option<&'static str> {
    match mime_type {
        "image/jpeg" => Some("jpg"),
        "image/png" => Some("png"),
        "image/gif" => Some("gif"),
        "image/webp" => Some("webp"),
        _ => None,
    }
}

fn parse_profile_xml(steam_id: &str, xml: &str) -> Option<SteamProfileDto> {
    let profile: SteamCommunityProfileXml = from_str(xml).ok()?;
    if profile.steam_id.trim() != steam_id {
        return None;
    }
    let persona_name = profile.persona_name.trim();
    let avatar_url = profile
        .avatar_full_url
        .as_deref()
        .map(str::trim)
        .filter(|value| trusted_avatar_url(value))
        .or_else(|| {
            let value = profile.avatar_medium_url.trim();
            trusted_avatar_url(value).then_some(value)
        })?;
    if persona_name.is_empty() {
        return None;
    }
    Some(SteamProfileDto {
        steam_id: steam_id.to_string(),
        persona_name: persona_name.to_string(),
        avatar_url: avatar_url.to_string(),
        avatar_frame_url: None,
        profile_url: format!("https://steamcommunity.com/profiles/{steam_id}"),
    })
}

fn valid_steam_id(value: &str) -> bool {
    value.len() == 17
        && value.as_bytes().first().is_some_and(u8::is_ascii_digit)
        && !value.starts_with('0')
        && value.bytes().all(|byte| byte.is_ascii_digit())
}

fn trusted_avatar_url(value: &str) -> bool {
    const PREFIXES: [&str; 3] = [
        "https://avatars.akamai.steamstatic.com/",
        "https://avatars.fastly.steamstatic.com/",
        "https://steamcdn-a.akamaihd.net/steamcommunity/public/images/avatars/",
    ];
    value.len() <= 512
        && !value.contains(['?', '#'])
        && PREFIXES.iter().any(|prefix| value.starts_with(prefix))
}

fn trusted_profile_avatar_url(value: &str) -> bool {
    trusted_avatar_url(value) || trusted_animated_avatar_url(value)
}

fn parse_profile_avatar_assets(html: &str) -> ProfileAvatarAssets {
    let avatar_start = [
        "profile_small_header_avatar",
        "playerAvatar profile_header_size",
    ]
    .into_iter()
    .filter_map(|marker| html.find(marker))
    .min();
    let Some(avatar_start) = avatar_start else {
        return ProfileAvatarAssets::default();
    };
    let mut avatar_tail_end = html.len().min(avatar_start.saturating_add(8 * 1024));
    while !html.is_char_boundary(avatar_tail_end) {
        avatar_tail_end = avatar_tail_end.saturating_sub(1);
    }
    let avatar_tail = &html[avatar_start..avatar_tail_end];
    let avatar_end = [
        "profile_header_centered_col",
        "profile_small_header_persona",
    ]
    .into_iter()
    .filter_map(|marker| avatar_tail.find(marker))
    .min()
    .unwrap_or(avatar_tail.len());
    let avatar_html = &avatar_tail[..avatar_end];
    let frame_range = div_element_range(avatar_html, "profile_avatar_frame");
    let avatar_frame_url = frame_range
        .as_ref()
        .and_then(|range| parse_image_url(&avatar_html[range.clone()], trusted_avatar_frame_url));
    let animated_avatar_url = match frame_range {
        Some(range) => parse_image_url(&avatar_html[range.end..], trusted_animated_avatar_url)
            .or_else(|| parse_image_url(&avatar_html[..range.start], trusted_animated_avatar_url)),
        None => parse_image_url(avatar_html, trusted_animated_avatar_url),
    };

    ProfileAvatarAssets {
        animated_avatar_url,
        avatar_frame_url,
    }
}

fn div_element_range(html: &str, class_marker: &str) -> Option<Range<usize>> {
    let marker = html.find(class_marker)?;
    let start = html[..marker].rfind("<div")?;
    let mut cursor = start;
    let mut depth = 0_u32;
    loop {
        let next_open = html[cursor..].find("<div").map(|offset| cursor + offset);
        let next_close = html[cursor..].find("</div>").map(|offset| cursor + offset);
        match (next_open, next_close) {
            (Some(open), Some(close)) if open < close => {
                depth = depth.saturating_add(1);
                cursor = open.saturating_add(4);
            }
            (_, Some(close)) => {
                depth = depth.checked_sub(1)?;
                cursor = close.saturating_add("</div>".len());
                if depth == 0 {
                    return Some(start..cursor);
                }
            }
            _ => return None,
        }
    }
}

fn parse_image_url(html: &str, trusted_url: fn(&str) -> bool) -> Option<String> {
    let mut cursor = 0;
    while let Some(image_offset) = html[cursor..].find("<img") {
        let image_start = cursor.saturating_add(image_offset);
        let image_end = image_start.saturating_add(html[image_start..].find('>')?);
        let mut reader = Reader::from_str(&html[image_start..=image_end]);
        let image = match reader.read_event().ok()? {
            Event::Start(image) | Event::Empty(image) => image,
            _ => return None,
        };

        for expected_name in ["srcset", "data-srcset", "src", "data-src"] {
            for attribute in image.attributes().flatten() {
                if attribute
                    .key
                    .as_ref()
                    .eq_ignore_ascii_case(expected_name.as_bytes())
                {
                    let Ok(value) = attribute.unescape_value() else {
                        continue;
                    };
                    if let Some(candidate) = value
                        .split(',')
                        .filter_map(|candidate| candidate.split_ascii_whitespace().next())
                        .find(|candidate| trusted_url(candidate))
                    {
                        return Some(candidate.to_string());
                    }
                }
            }
        }
        cursor = image_end.saturating_add(1);
    }
    None
}

fn trusted_animated_avatar_url(value: &str) -> bool {
    const PREFIX: &str = "https://shared.fastly.steamstatic.com/community_assets/images/items/";
    value.len() <= 512
        && !value.contains(['?', '#'])
        && value.starts_with(PREFIX)
        && value.ends_with(".gif")
}

fn trusted_avatar_frame_url(value: &str) -> bool {
    const PREFIX: &str = "https://shared.fastly.steamstatic.com/community_assets/images/items/";
    value.len() <= 512
        && !value.contains(['?', '#'])
        && value.starts_with(PREFIX)
        && (value.ends_with(".gif") || value.ends_with(".png"))
}

pub(crate) fn serve_cached_asset(
    local_data_root: Option<PathBuf>,
    request: &Request<Vec<u8>>,
) -> Response<Vec<u8>> {
    if request.method() != Method::GET {
        return empty_asset_response(StatusCode::METHOD_NOT_ALLOWED);
    }
    let Some(layout) = local_data_root.and_then(ProfileCacheLayout::new) else {
        return empty_asset_response(StatusCode::NOT_FOUND);
    };
    let mut segments = request.uri().path().trim_matches('/').split('/');
    let (Some(steam_id), Some(kind), None) = (segments.next(), segments.next(), segments.next())
    else {
        return empty_asset_response(StatusCode::NOT_FOUND);
    };
    if !valid_steam_id(steam_id) {
        return empty_asset_response(StatusCode::NOT_FOUND);
    }
    let kind = match kind {
        "avatar" => ProfileAssetKind::Avatar,
        "frame" => ProfileAssetKind::Frame,
        _ => return empty_asset_response(StatusCode::NOT_FOUND),
    };
    let Some(cached) = read_cache(&layout.directory, steam_id) else {
        return empty_asset_response(StatusCode::NOT_FOUND);
    };
    let asset = match kind {
        ProfileAssetKind::Avatar => cached.avatar_asset.as_ref(),
        ProfileAssetKind::Frame => cached.avatar_frame_asset.as_ref(),
    };
    let Some(asset) = asset.filter(|asset| {
        valid_cached_asset_metadata(steam_id, kind, asset)
            && cached_asset_available(&layout, steam_id, kind, asset)
    }) else {
        return empty_asset_response(StatusCode::NOT_FOUND);
    };
    let Ok(bytes) = fs::read(layout.asset_directory.join(&asset.file_name)) else {
        return empty_asset_response(StatusCode::NOT_FOUND);
    };
    if image_format(&bytes).map(|(mime_type, _)| mime_type) != Some(asset.mime_type.as_str()) {
        return empty_asset_response(StatusCode::UNSUPPORTED_MEDIA_TYPE);
    }

    Response::builder()
        .status(StatusCode::OK)
        .header(header::CONTENT_TYPE, &asset.mime_type)
        .header(header::CACHE_CONTROL, "public, max-age=31536000, immutable")
        .header(header::ACCESS_CONTROL_ALLOW_ORIGIN, "*")
        .header("X-Content-Type-Options", "nosniff")
        .body(bytes)
        .unwrap_or_else(|_| empty_asset_response(StatusCode::INTERNAL_SERVER_ERROR))
}

fn empty_asset_response(status: StatusCode) -> Response<Vec<u8>> {
    Response::builder()
        .status(status)
        .header(header::CACHE_CONTROL, "no-store")
        .header("X-Content-Type-Options", "nosniff")
        .body(Vec::new())
        .unwrap_or_default()
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| u64::try_from(duration.as_millis()).unwrap_or(u64::MAX))
        .unwrap_or_default()
}

#[cfg(windows)]
fn fetch_profile(steam_id: &str) -> Option<FetchedSteamProfile> {
    let bytes = crate::http_client::get_https(
        &format!("https://steamcommunity.com/profiles/{steam_id}?xml=1"),
        MAX_PROFILE_XML_BYTES,
        5_000,
    )
    .ok()?;
    let xml = String::from_utf8(bytes).ok()?;
    let mut profile = parse_profile_xml(steam_id, &xml)?;
    let mut enhanced_assets_complete = false;
    if let Ok(bytes) =
        crate::http_client::get_https(&profile.profile_url, MAX_PROFILE_HTML_BYTES, 5_000)
    {
        if let Ok(html) = String::from_utf8(bytes) {
            enhanced_assets_complete = true;
            let assets = parse_profile_avatar_assets(&html);
            if let Some(avatar_url) = assets.animated_avatar_url {
                profile.avatar_url = avatar_url;
            }
            profile.avatar_frame_url = assets.avatar_frame_url;
        }
    }
    Some(FetchedSteamProfile {
        profile,
        enhanced_assets_complete,
    })
}

#[cfg(not(windows))]
fn fetch_profile(_steam_id: &str) -> Option<FetchedSteamProfile> {
    None
}

#[cfg(windows)]
fn fetch_profile_asset(url: &str) -> Option<Vec<u8>> {
    let trusted = trusted_profile_avatar_url(url) || trusted_avatar_frame_url(url);
    if !trusted {
        return None;
    }
    crate::http_client::get_https(url, MAX_PROFILE_IMAGE_BYTES, 5_000).ok()
}

#[cfg(not(windows))]
fn fetch_profile_asset(_url: &str) -> Option<Vec<u8>> {
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};

    const STEAM_ID: &str = "76561198147750283";
    const AVATAR_URL: &str = "https://avatars.fastly.steamstatic.com/cached_full.jpg";
    const FRAME_URL: &str =
        "https://shared.fastly.steamstatic.com/community_assets/images/items/1/frame.png";
    const JPEG_BYTES: &[u8] = &[0xff, 0xd8, 0xff, 0xd9];
    const PNG_BYTES: &[u8] = b"\x89PNG\r\n\x1a\n";

    static NEXT_TEMP_ROOT: AtomicU64 = AtomicU64::new(1);

    struct TestCacheRoot {
        path: PathBuf,
    }

    impl TestCacheRoot {
        fn new(label: &str) -> Self {
            let path = std::env::temp_dir().join(format!(
                "demotracer-steam-profile-{label}-{}-{}",
                std::process::id(),
                NEXT_TEMP_ROOT.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir_all(&path).unwrap();
            Self { path }
        }
    }

    impl Drop for TestCacheRoot {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.path);
        }
    }

    fn cached_test_profile(persona_name: &str) -> SteamProfileDto {
        SteamProfileDto {
            steam_id: STEAM_ID.to_string(),
            persona_name: persona_name.to_string(),
            avatar_url: AVATAR_URL.to_string(),
            avatar_frame_url: Some(FRAME_URL.to_string()),
            profile_url: format!("https://steamcommunity.com/profiles/{STEAM_ID}"),
        }
    }

    fn uncached_entry(persona_name: &str, fetched_at_ms: u64) -> CachedSteamProfile {
        CachedSteamProfile {
            schema_version: 0,
            fetched_at_ms,
            profile: cached_test_profile(persona_name),
            assets_checked_at_ms: 0,
            avatar_asset: None,
            avatar_frame_asset: None,
        }
    }

    #[test]
    fn parses_public_profile_identity_and_avatar() {
        let xml = r#"<?xml version="1.0"?><profile>
            <steamID64>76561198147750283</steamID64>
            <steamID><![CDATA[21baz]]></steamID>
            <avatarMedium><![CDATA[https://avatars.akamai.steamstatic.com/abc_medium.jpg]]></avatarMedium>
            <avatarFull><![CDATA[https://avatars.akamai.steamstatic.com/abc_full.jpg]]></avatarFull>
        </profile>"#;
        let profile = parse_profile_xml(STEAM_ID, xml).unwrap();
        assert_eq!(profile.persona_name, "21baz");
        assert_eq!(profile.steam_id, STEAM_ID);
        assert_eq!(
            profile.avatar_url,
            "https://avatars.akamai.steamstatic.com/abc_full.jpg"
        );
        assert_eq!(profile.avatar_frame_url, None);
        assert_eq!(
            profile.profile_url,
            "https://steamcommunity.com/profiles/76561198147750283"
        );
    }

    #[test]
    fn rejects_mismatched_identity_or_untrusted_avatar_host() {
        let mismatch = r#"<profile><steamID64>76561198000000000</steamID64><steamID>x</steamID><avatarMedium>https://avatars.akamai.steamstatic.com/a.jpg</avatarMedium></profile>"#;
        let untrusted = r#"<profile><steamID64>76561198147750283</steamID64><steamID>x</steamID><avatarMedium>https://example.com/a.jpg</avatarMedium></profile>"#;
        assert!(parse_profile_xml(STEAM_ID, mismatch).is_none());
        assert!(parse_profile_xml(STEAM_ID, untrusted).is_none());
    }

    #[test]
    fn parses_pr_animated_avatar_from_real_profile_html_structure() {
        let html = r#"
            <div class="profile_header_content" data-panel="{&quot;flow-children&quot;:&quot;row&quot;}">
                <div class="playerAvatar profile_header_size online" data-miniprofile="350295751">
                    <div class="playerAvatarAutoSizeInner">
                        <picture>
                            <source media="(prefers-reduced-motion: reduce)" srcset="https://shared.fastly.steamstatic.com/community_assets/images/items/2928650/af644c31a4591126ff4faf2564b88891359cbb48.jpg"></source>
                            <img srcset="https://shared.fastly.steamstatic.com/community_assets/images/items/2928650/119373dde20ed21e9e784e98323cfd6ee4ef264d.gif" >
                        </picture>
                    </div>
                </div>
            </div>
        "#;
        let assets = parse_profile_avatar_assets(html);
        assert_eq!(
            assets.animated_avatar_url.as_deref(),
            Some("https://shared.fastly.steamstatic.com/community_assets/images/items/2928650/119373dde20ed21e9e784e98323cfd6ee4ef264d.gif")
        );
        assert_eq!(assets.avatar_frame_url, None);
    }

    #[test]
    fn separates_profile_frame_from_static_avatar() {
        let html = r#"
            <div class="profile_header_content">
                <div class="playerAvatar profile_header_size online">
                    <div class="playerAvatarAutoSizeInner">
                        <div class="profile_avatar_frame">
                            <picture>
                                <source media="(prefers-reduced-motion: reduce)" srcset="https://shared.fastly.steamstatic.com/community_assets/images/items/212070/static-frame.png"></source>
                                <img src="https://shared.fastly.steamstatic.com/community_assets/images/items/212070/animated-frame.gif">
                            </picture>
                        </div>
                        <picture>
                            <img srcset="https://avatars.fastly.steamstatic.com/static_full.jpg">
                        </picture>
                    </div>
                </div>
                <div class="profile_header_centered_col"></div>
            </div>
        "#;
        let assets = parse_profile_avatar_assets(html);
        assert_eq!(assets.animated_avatar_url, None);
        assert_eq!(
            assets.avatar_frame_url.as_deref(),
            Some("https://shared.fastly.steamstatic.com/community_assets/images/items/212070/animated-frame.gif")
        );
    }

    #[test]
    fn supports_lazy_data_srcset_before_static_image_attributes() {
        let html = r#"
            <div class="profile_small_header_avatar">
                <img src="https://avatars.fastly.steamstatic.com/static.jpg"
                     data-src="https://avatars.fastly.steamstatic.com/lazy.jpg"
                     data-srcset="https://shared.fastly.steamstatic.com/community_assets/images/items/1/animated.gif 1x">
            </div>
        "#;
        let assets = parse_profile_avatar_assets(html);
        assert_eq!(
            assets.animated_avatar_url.as_deref(),
            Some("https://shared.fastly.steamstatic.com/community_assets/images/items/1/animated.gif")
        );
    }

    #[test]
    fn rejects_untrusted_or_non_gif_profile_images() {
        let untrusted = r#"<div class="profile_small_header_avatar"><img srcset="https://example.com/avatar.gif"></div>"#;
        let static_image = r#"<div class="profile_small_header_avatar"><img srcset="https://shared.fastly.steamstatic.com/community_assets/images/items/1/avatar.jpg"></div>"#;
        assert_eq!(
            parse_profile_avatar_assets(untrusted),
            ProfileAvatarAssets::default()
        );
        assert_eq!(
            parse_profile_avatar_assets(static_image),
            ProfileAvatarAssets::default()
        );
    }

    #[test]
    fn truncates_profile_html_at_a_utf8_boundary() {
        const MARKER: &str = "profile_small_header_avatar";
        let mut html = format!(r#"<div class="{MARKER}">"#);
        let avatar_start = html.find(MARKER).unwrap();
        let truncation_point = avatar_start + 8 * 1024;
        html.push_str(&"x".repeat(truncation_point - html.len() - 1));
        html.push('界');
        html.push_str("</div>");

        assert_eq!(
            parse_profile_avatar_assets(&html),
            ProfileAvatarAssets::default()
        );
    }

    #[test]
    fn validates_only_steam_id64_shaped_values() {
        assert!(valid_steam_id(STEAM_ID));
        assert!(!valid_steam_id("0"));
        assert!(!valid_steam_id("7656119814775028x"));
    }

    #[test]
    fn migrates_legacy_metadata_and_keeps_downloaded_assets_offline() {
        let temp = TestCacheRoot::new("legacy-offline");
        let legacy_directory = temp.path.join("steam-profiles-v3");
        fs::create_dir_all(&legacy_directory).unwrap();
        fs::write(
            legacy_directory.join(format!("{STEAM_ID}.json")),
            serde_json::to_vec(&uncached_entry("legacy", 1_000)).unwrap(),
        )
        .unwrap();
        let layout = ProfileCacheLayout::new(temp.path.clone()).unwrap();

        let migrated = resolve_profile_with(
            Some(&layout),
            STEAM_ID,
            2_000,
            true,
            &|_| -> Option<FetchedSteamProfile> {
                panic!("fresh legacy metadata must not refetch the Steam profile")
            },
            &|url| {
                if url == AVATAR_URL {
                    Some(JPEG_BYTES.to_vec())
                } else if url == FRAME_URL {
                    Some(PNG_BYTES.to_vec())
                } else {
                    None
                }
            },
        )
        .unwrap();

        assert!(migrated.avatar_url.contains("steam-avatar"));
        assert!(migrated
            .avatar_frame_url
            .as_deref()
            .unwrap()
            .contains("steam-avatar"));
        assert!(layout.directory.join(format!("{STEAM_ID}.json")).is_file());
        assert!(layout
            .asset_directory
            .join(format!("{STEAM_ID}-avatar.jpg"))
            .is_file());
        assert!(layout
            .asset_directory
            .join(format!("{STEAM_ID}-frame.png"))
            .is_file());

        let offline = resolve_profile_with(
            Some(&layout),
            STEAM_ID,
            CACHE_TTL_MS + 3_000,
            true,
            &|_| None,
            &|_| -> Option<Vec<u8>> { panic!("complete local assets must not be refetched") },
        )
        .unwrap();
        assert_eq!(offline.avatar_url, migrated.avatar_url);
        assert_eq!(offline.avatar_frame_url, migrated.avatar_frame_url);

        let request = Request::builder()
            .uri(format!("http://steam-avatar.localhost/{STEAM_ID}/avatar"))
            .body(Vec::new())
            .unwrap();
        let response = serve_cached_asset(Some(temp.path.clone()), &request);
        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(response.headers()[header::CONTENT_TYPE], "image/jpeg");
        assert_eq!(response.body(), JPEG_BYTES);

        fs::write(
            layout
                .asset_directory
                .join(format!("{STEAM_ID}-avatar.jpg")),
            b"corrupt",
        )
        .unwrap();
        let corrupt_fallback = resolve_profile_with(
            Some(&layout),
            STEAM_ID,
            2_500,
            false,
            &|_| -> Option<FetchedSteamProfile> { panic!("metadata is still fresh") },
            &|_| -> Option<Vec<u8>> { panic!("foreground resolution must not fetch images") },
        )
        .unwrap();
        assert_eq!(corrupt_fallback.avatar_url, AVATAR_URL);
    }

    #[test]
    fn invalid_download_never_replaces_the_remote_last_known_url() {
        let temp = TestCacheRoot::new("invalid-image");
        let layout = ProfileCacheLayout::new(temp.path.clone()).unwrap();
        let profile = cached_test_profile("fresh");

        let resolved = resolve_profile_with(
            Some(&layout),
            STEAM_ID,
            10_000,
            true,
            &|_| {
                Some(FetchedSteamProfile {
                    profile: profile.clone(),
                    enhanced_assets_complete: true,
                })
            },
            &|_| Some(b"not an image".to_vec()),
        )
        .unwrap();

        assert_eq!(resolved.avatar_url, AVATAR_URL);
        assert_eq!(resolved.avatar_frame_url.as_deref(), Some(FRAME_URL));
        let cached = read_cache(&layout.directory, STEAM_ID).unwrap();
        assert_eq!(cached.avatar_asset, None);
        assert_eq!(cached.avatar_frame_asset, None);

        let request = Request::builder()
            .uri(format!(
                "http://steam-avatar.localhost/{STEAM_ID}/../avatar"
            ))
            .body(Vec::new())
            .unwrap();
        assert_eq!(
            serve_cached_asset(Some(temp.path.clone()), &request).status(),
            StatusCode::NOT_FOUND
        );
    }

    #[test]
    fn foreground_metadata_resolution_never_waits_for_image_downloads() {
        let temp = TestCacheRoot::new("foreground");
        let layout = ProfileCacheLayout::new(temp.path.clone()).unwrap();
        let profile = cached_test_profile("foreground");

        let resolved = resolve_profile_with(
            Some(&layout),
            STEAM_ID,
            10_000,
            false,
            &|_| {
                Some(FetchedSteamProfile {
                    profile: profile.clone(),
                    enhanced_assets_complete: true,
                })
            },
            &|_| -> Option<Vec<u8>> { panic!("foreground resolution must not fetch images") },
        )
        .unwrap();

        assert_eq!(resolved.avatar_url, AVATAR_URL);
        let cached = read_cache(&layout.directory, STEAM_ID).unwrap();
        assert_eq!(cached.assets_checked_at_ms, 0);
        assert_eq!(cached.avatar_asset, None);
    }

    #[test]
    fn stale_cached_metadata_is_returned_before_background_refresh() {
        let temp = TestCacheRoot::new("stale-foreground");
        let layout = ProfileCacheLayout::new(temp.path.clone()).unwrap();
        write_cache(
            &layout.directory,
            STEAM_ID,
            &uncached_entry("cached", 1_000),
        )
        .unwrap();

        let resolved = resolve_profile_with(
            Some(&layout),
            STEAM_ID,
            CACHE_TTL_MS + 2_000,
            false,
            &|_| -> Option<FetchedSteamProfile> {
                panic!("foreground resolution must not refresh stale cached metadata")
            },
            &|_| -> Option<Vec<u8>> {
                panic!("foreground resolution must not fetch cached profile images")
            },
        )
        .unwrap();

        assert_eq!(resolved.persona_name, "cached");
        assert_eq!(resolved.avatar_url, AVATAR_URL);
    }
}
