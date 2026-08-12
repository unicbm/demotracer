/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use super::{AppState, CommandErrorDto, CommandResult, CosmeticConsentDto, TaskEvent, TaskPhase};
use cs2_demotracer::browser_analysis::BrowserDemoSource;
use cs2_demotracer::demo_id::sha256_hex;
use cs2_demotracer::demo_series::{group_demo_sources, resolve_demo_source, DemoSourceSet};
use cs2_demotracer::export::DEFAULT_FREEZE_PREROLL_SECONDS;
use cs2_demotracer::model::{Side, SubtickMode};
use cs2_demotracer::quality::AnalysisOptions;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use tauri::ipc::Channel;
use tauri::{AppHandle, Manager, State};

const BATCH_SCHEMA_VERSION: u32 = 2;
const DEFAULT_SCAN_LIMIT: usize = 512;
const MAX_SCAN_RESULTS: usize = 4096;
const MAX_SCAN_DEPTH: usize = 8;
pub(crate) const MAX_BATCH_ITEMS: usize = 8;
pub(crate) const MAX_BATCH_CONCURRENCY: usize = 8;
const MIN_BATCH_CONCURRENCY: usize = 1;
const MIN_AUTO_BATCH_CONCURRENCY: usize = 2;
const MAX_PERSISTED_BATCHES: usize = 100;
const ESTIMATED_PARALLEL_EFFICIENCY: f64 = 0.65;
/// Normalize compressed on-disk sizes into a rough logical demo size so mixed
/// `.dem` and `.dem.zst` queues share a useful ETA scale before EWMA refinement.
const ESTIMATED_ZSTD_EXPANSION: u64 = 4;

static NEXT_BATCH_NONCE: AtomicU64 = AtomicU64::new(1);
static NEXT_LEDGER_NONCE: AtomicU64 = AtomicU64::new(1);

#[derive(Default)]
pub(crate) struct BatchState {
    runtimes: Mutex<BTreeMap<String, Arc<BatchRuntime>>>,
}

struct BatchRuntime {
    ledger_path: PathBuf,
    ledger: Mutex<BatchLedgerDto>,
    cancel_requested: AtomicBool,
    running: AtomicBool,
}

impl BatchState {
    fn runtime(&self, batch_id: &str) -> CommandResult<Option<Arc<BatchRuntime>>> {
        Ok(self
            .runtimes
            .lock()
            .map_err(|_| {
                CommandErrorDto::new("batch_state_poisoned", "The batch registry is unavailable.")
            })?
            .get(batch_id)
            .cloned())
    }

    fn insert_runtime(&self, runtime: Arc<BatchRuntime>) -> CommandResult<()> {
        let batch_id = runtime.lock_ledger()?.batch_id.clone();
        self.runtimes
            .lock()
            .map_err(|_| {
                CommandErrorDto::new("batch_state_poisoned", "The batch registry is unavailable.")
            })?
            .insert(batch_id, runtime);
        Ok(())
    }
}

impl BatchRuntime {
    fn from_ledger(path: PathBuf, ledger: BatchLedgerDto) -> Self {
        Self {
            ledger_path: path,
            cancel_requested: AtomicBool::new(ledger.cancel_requested),
            ledger: Mutex::new(ledger),
            running: AtomicBool::new(false),
        }
    }

    fn lock_ledger(&self) -> CommandResult<MutexGuard<'_, BatchLedgerDto>> {
        self.ledger.lock().map_err(|_| {
            CommandErrorDto::new("batch_state_poisoned", "The batch state is unavailable.")
        })
    }

    fn snapshot(&self) -> CommandResult<BatchLedgerDto> {
        Ok(self.lock_ledger()?.clone())
    }

    fn update<T>(
        &self,
        mutate: impl FnOnce(&mut BatchLedgerDto) -> CommandResult<T>,
    ) -> CommandResult<T> {
        let mut ledger = self.lock_ledger()?;
        let value = mutate(&mut ledger)?;
        ledger.revision = ledger.revision.saturating_add(1);
        ledger.updated_at_ms = unix_time_ms(SystemTime::now());
        persist_ledger_atomic(&self.ledger_path, &ledger)?;
        Ok(value)
    }
}

struct RuntimeRunGuard {
    runtime: Arc<BatchRuntime>,
    _process_lock: BatchProcessLock,
}

impl RuntimeRunGuard {
    fn acquire(runtime: Arc<BatchRuntime>) -> CommandResult<Self> {
        runtime
            .running
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .map_err(|_| {
                CommandErrorDto::new("batch_already_running", "This batch is already running.")
            })?;
        let process_lock = match BatchProcessLock::acquire(&runtime.ledger_path) {
            Ok(lock) => lock,
            Err(error) => {
                runtime.running.store(false, Ordering::Release);
                return Err(error);
            }
        };
        Ok(Self {
            runtime,
            _process_lock: process_lock,
        })
    }
}

impl Drop for RuntimeRunGuard {
    fn drop(&mut self) {
        self.runtime.running.store(false, Ordering::Release);
    }
}

struct BatchProcessLock {
    path: PathBuf,
    #[cfg(windows)]
    handle: *mut std::ffi::c_void,
    #[cfg(not(windows))]
    _file: fs::File,
}

impl BatchProcessLock {
    fn acquire(ledger_path: &Path) -> CommandResult<Self> {
        let name = ledger_path
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_else(|| "batch.json".to_string());
        let path = ledger_path.with_file_name(format!("{name}.lock"));

        #[cfg(windows)]
        {
            use std::os::windows::ffi::OsStrExt;

            const GENERIC_READ: u32 = 0x8000_0000;
            const GENERIC_WRITE: u32 = 0x4000_0000;
            const OPEN_ALWAYS: u32 = 4;
            const FILE_ATTRIBUTE_NORMAL: u32 = 0x80;

            #[link(name = "Kernel32")]
            extern "system" {
                fn CreateFileW(
                    file_name: *const u16,
                    desired_access: u32,
                    share_mode: u32,
                    security_attributes: *mut std::ffi::c_void,
                    creation_disposition: u32,
                    flags_and_attributes: u32,
                    template_file: *mut std::ffi::c_void,
                ) -> *mut std::ffi::c_void;
            }

            let wide = path
                .as_os_str()
                .encode_wide()
                .chain(std::iter::once(0))
                .collect::<Vec<_>>();
            let handle = unsafe {
                CreateFileW(
                    wide.as_ptr(),
                    GENERIC_READ | GENERIC_WRITE,
                    0,
                    std::ptr::null_mut(),
                    OPEN_ALWAYS,
                    FILE_ATTRIBUTE_NORMAL,
                    std::ptr::null_mut(),
                )
            };
            if handle as isize == -1 {
                let error = std::io::Error::last_os_error();
                let code = if matches!(error.raw_os_error(), Some(32) | Some(33)) {
                    "batch_locked"
                } else {
                    "batch_lock_failed"
                };
                return Err(CommandErrorDto::at_path(
                    code,
                    format!("Another DemoTracer window may already be running this batch: {error}"),
                    &path,
                ));
            }
            return Ok(Self { path, handle });
        }

        #[cfg(not(windows))]
        {
            let file = fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&path)
                .map_err(|error| {
                    CommandErrorDto::at_path(
                        "batch_locked",
                        format!(
                            "Another DemoTracer process may already be running this batch: {error}"
                        ),
                        &path,
                    )
                })?;
            Ok(Self { path, _file: file })
        }
    }
}

impl Drop for BatchProcessLock {
    fn drop(&mut self) {
        #[cfg(windows)]
        unsafe {
            #[link(name = "Kernel32")]
            extern "system" {
                fn CloseHandle(object: *mut std::ffi::c_void) -> i32;
            }
            let _ = CloseHandle(self.handle);
        }
        let _ = fs::remove_file(&self.path);
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ChooseDemoFolderRequest {
    #[serde(default)]
    pub initial_path: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ScanDemoFolderRequest {
    pub root: String,
    #[serde(default = "default_true")]
    pub recursive: bool,
    #[serde(default = "default_scan_limit")]
    pub limit: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct DemoScanCandidateDto {
    pub path: String,
    pub relative_path: String,
    pub file_name: String,
    pub size_bytes: String,
    pub compressed: bool,
    pub modified_at_ms: Option<u64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct DemoFolderScanDto {
    pub root: String,
    pub recursive: bool,
    pub limit: usize,
    pub candidates: Vec<DemoScanCandidateDto>,
    pub truncated: bool,
    pub skipped_reparse_points: usize,
    pub warnings: Vec<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct BatchConversionSettingsDto {
    #[serde(default)]
    pub include_suspicious: bool,
    #[serde(default)]
    pub full_round: bool,
    #[serde(default)]
    pub side: Side,
    #[serde(default)]
    pub subtick_mode: SubtickMode,
    #[serde(default = "default_max_round_seconds")]
    pub max_round_seconds: f32,
    #[serde(default = "default_freeze_preroll_seconds")]
    pub freeze_preroll_seconds: f32,
    #[serde(default = "default_true")]
    pub export_voice: bool,
    #[serde(default)]
    pub export_cosmetics: bool,
    #[serde(default)]
    pub export_stickers: bool,
    #[serde(default)]
    pub export_charms: bool,
}

impl Default for BatchConversionSettingsDto {
    fn default() -> Self {
        Self {
            include_suspicious: false,
            full_round: false,
            side: Side::Both,
            subtick_mode: SubtickMode::Auto,
            max_round_seconds: default_max_round_seconds(),
            freeze_preroll_seconds: DEFAULT_FREEZE_PREROLL_SECONDS,
            export_voice: true,
            export_cosmetics: false,
            export_stickers: false,
            export_charms: false,
        }
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct StartBatchImportRequest {
    pub source_root: String,
    pub library_root: String,
    pub demo_paths: Vec<String>,
    /// Existing archives are replaced only for these explicitly confirmed source demos.
    #[serde(default)]
    pub replace_demo_paths: Vec<String>,
    /// `None` selects the CPU-derived default. Explicit values are limited to 1-8.
    #[serde(default)]
    pub concurrency: Option<usize>,
    #[serde(default)]
    pub settings: BatchConversionSettingsDto,
    #[serde(default)]
    pub cosmetic_consent: Option<CosmeticConsentDto>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ResumeBatchImportRequest {
    pub batch_id: String,
    #[serde(default)]
    pub retry_failed: bool,
    #[serde(default)]
    pub item_id: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct BatchIdRequest {
    pub batch_id: String,
}

/// Contract implemented by the parent module. The helper owns one parsed demo at a time,
/// chooses recommended rounds (or the request's explicit suspicious-round policy), and runs
/// the same staged conversion/validation path as the single-demo command.
#[derive(Debug, Clone)]
pub(crate) struct BatchProcessRequest {
    pub batch_id: String,
    pub item_id: String,
    pub source_path: PathBuf,
    pub library_root: PathBuf,
    pub settings: BatchConversionSettingsDto,
    pub replace_existing: bool,
}

#[derive(Debug, Clone)]
pub(crate) struct BatchProcessResult {
    pub archive_root: PathBuf,
    pub manifest_path: PathBuf,
    pub demo_sha256: String,
    pub map: String,
    pub server_name: Option<String>,
    pub demo_source: Option<BrowserDemoSource>,
    pub rounds_exported: usize,
    pub files_written: usize,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) enum BatchStatusDto {
    Pending,
    Running,
    Stopping,
    Paused,
    Completed,
    CompletedWithErrors,
}

#[derive(Debug, Clone, Copy, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) enum BatchItemStatusDto {
    Pending,
    Running,
    Completed,
    Failed,
}

#[derive(Debug, Clone, Copy, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) enum BatchItemPhaseDto {
    Queued,
    Decompressing,
    Parsing,
    Analyzing,
    Converting,
    Voice,
    Validating,
    Complete,
    Failed,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct BatchErrorDto {
    pub code: String,
    pub message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
}

impl From<CommandErrorDto> for BatchErrorDto {
    fn from(value: CommandErrorDto) -> Self {
        Self {
            code: value.code,
            message: value.message,
            path: value.path,
        }
    }
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct BatchCalibrationDto {
    pub samples: usize,
    pub seconds_per_gib: f64,
    pub first_item_id: String,
    pub first_parse_ms: u64,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct BatchItemDto {
    pub item_id: String,
    pub source_path: String,
    pub relative_path: String,
    pub file_name: String,
    pub size_bytes: String,
    pub modified_at_ms: Option<u64>,
    #[serde(default)]
    pub source_parts: Vec<BatchSourcePartDto>,
    #[serde(default)]
    pub replace_existing: bool,
    pub status: BatchItemStatusDto,
    pub phase: BatchItemPhaseDto,
    pub attempts: u32,
    pub parse_ms: Option<u64>,
    pub predicted_parse_ms: Option<u64>,
    pub demo_sha256: Option<String>,
    pub map: Option<String>,
    pub server_name: Option<String>,
    pub archive_root: Option<String>,
    pub manifest_path: Option<String>,
    pub rounds_exported: Option<usize>,
    pub files_written: Option<usize>,
    pub error: Option<BatchErrorDto>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct BatchSourcePartDto {
    pub part: u32,
    pub path: String,
    pub size_bytes: String,
    pub modified_at_ms: Option<u64>,
}

impl BatchItemDto {
    fn size_bytes_u64(&self) -> u64 {
        self.size_bytes.parse().unwrap_or(0)
    }

    fn estimated_parse_bytes(&self) -> u64 {
        let size = self.size_bytes_u64();
        if self
            .source_parts
            .iter()
            .any(|part| is_compressed_demo_path(Path::new(&part.path)))
            || (self.source_parts.is_empty()
                && is_compressed_demo_path(Path::new(&self.source_path)))
        {
            size.saturating_mul(ESTIMATED_ZSTD_EXPANSION)
        } else {
            size
        }
    }
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct BatchLedgerDto {
    pub schema_version: u32,
    pub batch_id: String,
    pub revision: u64,
    pub created_at_ms: u64,
    pub updated_at_ms: u64,
    pub source_root: String,
    pub library_root: String,
    pub settings: BatchConversionSettingsDto,
    pub status: BatchStatusDto,
    pub cancel_requested: bool,
    /// The player's explicit 1-4 choice. `None` means CPU-derived Auto.
    #[serde(default)]
    pub requested_concurrency: Option<usize>,
    pub concurrency: usize,
    pub calibration: Option<BatchCalibrationDto>,
    pub items: Vec<BatchItemDto>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct BatchListDto {
    pub batches: Vec<BatchLedgerDto>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub(crate) enum BatchEvent {
    Started {
        batch_id: String,
        total: usize,
        concurrency: usize,
    },
    ItemPhase {
        batch_id: String,
        item_id: String,
        phase: BatchItemPhaseDto,
        parse_eta_seconds: Option<u64>,
    },
    ItemTask {
        batch_id: String,
        item_id: String,
        task: TaskEvent,
        parse_eta_seconds: Option<u64>,
    },
    EstimateUpdated {
        batch_id: String,
        parse_eta_seconds: u64,
        samples: usize,
    },
    ItemCompleted {
        batch_id: String,
        item_id: String,
        archive_root: String,
        manifest_path: String,
        demo_source: Option<BrowserDemoSource>,
        rounds_exported: usize,
        parse_eta_seconds: Option<u64>,
    },
    ItemFailed {
        batch_id: String,
        item_id: String,
        error: BatchErrorDto,
        parse_eta_seconds: Option<u64>,
    },
    Paused {
        batch_id: String,
        completed: usize,
        failed: usize,
        pending: usize,
    },
    Finished {
        batch_id: String,
        completed: usize,
        failed: usize,
    },
}

fn default_true() -> bool {
    true
}

fn default_scan_limit() -> usize {
    DEFAULT_SCAN_LIMIT
}

fn default_max_round_seconds() -> f32 {
    AnalysisOptions::default().max_round_seconds
}

fn default_freeze_preroll_seconds() -> f32 {
    DEFAULT_FREEZE_PREROLL_SECONDS
}

#[tauri::command]
pub(crate) async fn choose_demo_batch_dir(
    request: ChooseDemoFolderRequest,
) -> CommandResult<Option<String>> {
    tauri::async_runtime::spawn_blocking(move || {
        let mut dialog = rfd::FileDialog::new().set_title("Choose a folder containing CS2 demos");
        if let Some(value) = request
            .initial_path
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
        {
            let hint = Path::new(value);
            if hint.is_dir() {
                dialog = dialog.set_directory(hint);
            } else if let Some(parent) = hint.parent().filter(|parent| parent.is_dir()) {
                dialog = dialog.set_directory(parent);
            }
        }
        dialog.pick_folder().map(|path| path.display().to_string())
    })
    .await
    .map_err(|error| CommandErrorDto::new("dialog_failed", error.to_string()))
}

#[tauri::command]
pub(crate) async fn scan_demo_folder(
    request: ScanDemoFolderRequest,
) -> CommandResult<DemoFolderScanDto> {
    tauri::async_runtime::spawn_blocking(move || scan_demo_folder_for(&request))
        .await
        .map_err(|error| CommandErrorDto::new("demo_scan_worker_failed", error.to_string()))?
}

#[tauri::command]
pub(crate) async fn start_batch_import(
    app: AppHandle,
    request: StartBatchImportRequest,
    events: Channel<BatchEvent>,
    state: State<'_, BatchState>,
    app_state: State<'_, AppState>,
) -> CommandResult<BatchLedgerDto> {
    let _busy = app_state.acquire_busy()?;
    let ledger = build_batch_ledger(&request)?;
    let path = batch_ledger_path(&app, &ledger.batch_id)?;
    persist_ledger_atomic(&path, &ledger)?;
    let runtime = Arc::new(BatchRuntime::from_ledger(path, ledger));
    state.insert_runtime(runtime.clone())?;
    run_runtime_async(runtime, events).await
}

#[tauri::command]
pub(crate) async fn resume_batch_import(
    app: AppHandle,
    request: ResumeBatchImportRequest,
    events: Channel<BatchEvent>,
    state: State<'_, BatchState>,
    app_state: State<'_, AppState>,
) -> CommandResult<BatchLedgerDto> {
    let _busy = app_state.acquire_busy()?;
    validate_batch_id(&request.batch_id)?;
    let runtime = match state.runtime(&request.batch_id)? {
        Some(runtime) => runtime,
        None => {
            let path = batch_ledger_path(&app, &request.batch_id)?;
            let ledger = load_ledger_with_recovery(&path)?;
            let runtime = Arc::new(BatchRuntime::from_ledger(path, ledger));
            state.insert_runtime(runtime.clone())?;
            runtime
        }
    };
    if runtime.running.load(Ordering::Acquire) {
        return Err(CommandErrorDto::new(
            "batch_already_running",
            "This batch is already running.",
        ));
    }
    runtime.cancel_requested.store(false, Ordering::Release);
    runtime.update(|ledger| {
        if let Some(item_id) = request.item_id.as_deref() {
            let retryable = ledger
                .items
                .iter()
                .any(|item| item.item_id == item_id && item.status == BatchItemStatusDto::Failed);
            if !request.retry_failed || !retryable {
                return Err(CommandErrorDto::new(
                    "batch_item_not_retryable",
                    "The selected batch item is not currently failed and retryable.",
                ));
            }
        }
        ledger.cancel_requested = false;
        ledger.status = BatchStatusDto::Pending;
        for item in &mut ledger.items {
            if item.status == BatchItemStatusDto::Running
                || (request.retry_failed
                    && item.status == BatchItemStatusDto::Failed
                    && request
                        .item_id
                        .as_deref()
                        .map_or(true, |item_id| item.item_id == item_id))
            {
                item.status = BatchItemStatusDto::Pending;
                item.phase = BatchItemPhaseDto::Queued;
                item.error = None;
            }
        }
        Ok(())
    })?;
    run_runtime_async(runtime, events).await
}

#[tauri::command]
pub(crate) fn read_batch_import(
    app: AppHandle,
    request: BatchIdRequest,
    state: State<'_, BatchState>,
) -> CommandResult<BatchLedgerDto> {
    validate_batch_id(&request.batch_id)?;
    if let Some(runtime) = state.runtime(&request.batch_id)? {
        return runtime.snapshot();
    }
    let path = batch_ledger_path(&app, &request.batch_id)?;
    load_ledger_with_recovery(&path)
}

#[tauri::command]
pub(crate) fn list_batch_imports(app: AppHandle) -> CommandResult<BatchListDto> {
    let directory = batch_ledger_directory(&app)?;
    let mut ledgers = Vec::new();
    let entries = fs::read_dir(&directory).map_err(|error| {
        CommandErrorDto::at_path("batch_list_failed", error.to_string(), &directory)
    })?;
    let mut seen = BTreeSet::new();
    for entry in entries {
        let entry = match entry {
            Ok(entry) => entry,
            Err(_) => continue,
        };
        let path = entry.path();
        let name = path
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default();
        let primary_path = if name.ends_with(".json") {
            path
        } else if let Some(primary_name) = name.strip_suffix(".json.bak") {
            path.with_file_name(format!("{primary_name}.json"))
        } else {
            continue;
        };
        if let Ok(ledger) = load_ledger_with_recovery(&primary_path) {
            if seen.insert(ledger.batch_id.clone()) {
                ledgers.push(ledger);
            }
        }
    }
    ledgers.sort_by(|left, right| right.updated_at_ms.cmp(&left.updated_at_ms));
    ledgers.truncate(MAX_PERSISTED_BATCHES);
    Ok(BatchListDto { batches: ledgers })
}

#[tauri::command]
pub(crate) fn cancel_batch_import(
    request: BatchIdRequest,
    state: State<'_, BatchState>,
) -> CommandResult<BatchLedgerDto> {
    validate_batch_id(&request.batch_id)?;
    let runtime = state.runtime(&request.batch_id)?.ok_or_else(|| {
        CommandErrorDto::new(
            "batch_not_active",
            "This batch is not active in the current app session.",
        )
    })?;
    runtime.cancel_requested.store(true, Ordering::Release);
    runtime.update(|ledger| {
        ledger.cancel_requested = true;
        if ledger.status == BatchStatusDto::Running {
            ledger.status = BatchStatusDto::Stopping;
        }
        Ok(())
    })?;
    runtime.snapshot()
}

async fn run_runtime_async(
    runtime: Arc<BatchRuntime>,
    events: Channel<BatchEvent>,
) -> CommandResult<BatchLedgerDto> {
    tauri::async_runtime::spawn_blocking(move || run_batch_runtime(runtime, events))
        .await
        .map_err(|error| CommandErrorDto::new("batch_worker_failed", error.to_string()))?
}

fn scan_demo_folder_for(request: &ScanDemoFolderRequest) -> CommandResult<DemoFolderScanDto> {
    let limit = request.limit.clamp(1, MAX_SCAN_RESULTS);
    let root = validate_source_root(Path::new(request.root.trim()))?;
    let mut demo_paths = Vec::new();
    let mut warnings = Vec::new();
    let mut skipped_reparse_points = 0_usize;
    let mut truncated = false;
    let mut pending = vec![(root.clone(), 0_usize)];

    'scan: while let Some((directory, depth)) = pending.pop() {
        let entries = match fs::read_dir(&directory) {
            Ok(entries) => entries,
            Err(error) => {
                warnings.push(format!("{}: {error}", directory.display()));
                continue;
            }
        };
        let mut paths = Vec::new();
        for entry in entries {
            match entry {
                Ok(entry) => paths.push(entry.path()),
                Err(error) => warnings.push(format!("{}: {error}", directory.display())),
            }
        }
        paths.sort();
        let mut child_directories = Vec::new();
        for path in paths {
            let metadata = match fs::symlink_metadata(&path) {
                Ok(metadata) => metadata,
                Err(error) => {
                    warnings.push(format!("{}: {error}", path.display()));
                    continue;
                }
            };
            if super::catalog::is_symlink_or_reparse(&metadata) {
                skipped_reparse_points = skipped_reparse_points.saturating_add(1);
                continue;
            }
            if metadata.is_dir() {
                if request.recursive && depth < MAX_SCAN_DEPTH {
                    child_directories.push(path);
                }
                continue;
            }
            if !metadata.is_file() || !is_demo_path(&path) {
                continue;
            }
            if demo_paths.len() >= MAX_SCAN_RESULTS {
                truncated = true;
                break 'scan;
            }
            demo_paths.push(path);
        }
        for child in child_directories.into_iter().rev() {
            pending.push((child, depth + 1));
        }
    }
    demo_paths.sort();
    let mut grouped = BTreeMap::<String, DemoSourceSet>::new();
    for path in demo_paths {
        match resolve_demo_source(&path) {
            Ok(source) => {
                let key = normalized_path_key(source.primary_path());
                grouped.entry(key).or_insert(source);
            }
            Err(error) => warnings.push(format!("{}: {error}", path.display())),
        }
    }
    let sources = grouped.into_values().collect::<Vec<_>>();
    if sources.len() > limit {
        truncated = true;
    }
    let mut candidates = sources
        .iter()
        .take(limit)
        .map(|source| scan_candidate(&root, source))
        .collect::<CommandResult<Vec<_>>>()?;
    candidates.sort_by(|left, right| left.relative_path.cmp(&right.relative_path));
    Ok(DemoFolderScanDto {
        root: root.display().to_string(),
        recursive: request.recursive,
        limit,
        candidates,
        truncated,
        skipped_reparse_points,
        warnings,
    })
}

fn scan_candidate(root: &Path, source: &DemoSourceSet) -> CommandResult<DemoScanCandidateDto> {
    let path = source.primary_path();
    let metadata = source
        .metadata()
        .map_err(|error| CommandErrorDto::from_core("demo_source_inspect_failed", error))?;
    Ok(DemoScanCandidateDto {
        path: path.display().to_string(),
        relative_path: path
            .strip_prefix(root)
            .unwrap_or(path)
            .to_string_lossy()
            .replace('\\', "/"),
        file_name: path
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_else(|| "demo.dem".to_string()),
        size_bytes: metadata.size_bytes.to_string(),
        compressed: source.paths().any(is_compressed_demo_path),
        modified_at_ms: metadata.modified.map(unix_time_ms),
    })
}

fn build_batch_ledger(request: &StartBatchImportRequest) -> CommandResult<BatchLedgerDto> {
    let settings = validated_batch_settings(&request.settings, request.cosmetic_consent.as_ref())?;
    if request.demo_paths.is_empty() {
        return Err(CommandErrorDto::new(
            "batch_empty",
            "Select at least one demo for batch import.",
        ));
    }
    let source_root = validate_source_root(Path::new(request.source_root.trim()))?;
    let library_root =
        super::canonical_normal_library_root(Path::new(request.library_root.trim()))?;
    let mut canonical_paths = Vec::new();
    let mut seen = BTreeSet::new();
    for value in &request.demo_paths {
        let path = validate_batch_demo_path(&source_root, Path::new(value.trim()))?;
        let key = normalized_path_key(&path);
        if seen.insert(key) {
            canonical_paths.push(path);
        }
    }
    let selected_paths = canonical_paths
        .iter()
        .map(|path| normalized_path_key(path))
        .collect::<BTreeSet<_>>();
    let mut replace_paths = BTreeSet::new();
    for value in &request.replace_demo_paths {
        let path = validate_batch_demo_path(&source_root, Path::new(value.trim()))?;
        let key = normalized_path_key(&path);
        if !selected_paths.contains(&key) {
            return Err(CommandErrorDto::at_path(
                "batch_replace_target_not_selected",
                "Only a demo selected for this batch can replace an existing archive.",
                &path,
            ));
        }
        replace_paths.insert(key);
    }
    canonical_paths.sort();
    let mut sources = group_demo_sources(canonical_paths)
        .map_err(|error| CommandErrorDto::from_core("demo_source_group_failed", error))?;
    for source in &mut sources {
        for part in &mut source.parts {
            part.path = validate_batch_demo_path(&source_root, &part.path)?;
        }
    }
    if sources.is_empty() {
        return Err(CommandErrorDto::new(
            "batch_empty",
            "Select at least one unique demo for batch import.",
        ));
    }
    if sources.len() > MAX_BATCH_ITEMS {
        return Err(CommandErrorDto::new(
            "batch_too_large",
            format!("A parallel conversion can contain at most {MAX_BATCH_ITEMS} demos."),
        ));
    }

    let now = unix_time_ms(SystemTime::now());
    let batch_id = new_batch_id(now);
    let mut items = Vec::with_capacity(sources.len());
    for (index, source) in sources.into_iter().enumerate() {
        let path = source.primary_path();
        let source_parts = source
            .parts
            .iter()
            .map(|part| {
                let metadata = fs::symlink_metadata(&part.path).map_err(|error| {
                    CommandErrorDto::at_path(
                        "batch_demo_inspect_failed",
                        error.to_string(),
                        &part.path,
                    )
                })?;
                Ok(BatchSourcePartDto {
                    part: part.part,
                    path: part.path.display().to_string(),
                    size_bytes: metadata.len().to_string(),
                    modified_at_ms: metadata.modified().ok().map(unix_time_ms),
                })
            })
            .collect::<CommandResult<Vec<_>>>()?;
        let metadata = source
            .metadata()
            .map_err(|error| CommandErrorDto::from_core("batch_demo_inspect_failed", error))?;
        let relative_path = path
            .strip_prefix(&source_root)
            .unwrap_or(&path)
            .to_string_lossy()
            .replace('\\', "/");
        let id_material = format!(
            "{}\0{}\0{}\0{}",
            normalized_path_key(&path),
            metadata.size_bytes,
            metadata.modified.map(unix_time_ms).unwrap_or(0),
            index
        );
        let item_id = format!("{}-{index:02}", &sha256_hex(id_material.as_bytes())[..16]);
        items.push(BatchItemDto {
            item_id,
            source_path: path.display().to_string(),
            relative_path,
            file_name: path
                .file_name()
                .map(|name| name.to_string_lossy().into_owned())
                .unwrap_or_else(|| "demo.dem".to_string()),
            size_bytes: metadata.size_bytes.to_string(),
            modified_at_ms: metadata.modified.map(unix_time_ms),
            source_parts,
            replace_existing: source
                .parts
                .iter()
                .any(|part| replace_paths.contains(&normalized_path_key(&part.path))),
            status: BatchItemStatusDto::Pending,
            phase: BatchItemPhaseDto::Queued,
            attempts: 0,
            parse_ms: None,
            predicted_parse_ms: None,
            demo_sha256: None,
            map: None,
            server_name: None,
            archive_root: None,
            manifest_path: None,
            rounds_exported: None,
            files_written: None,
            error: None,
        });
    }

    Ok(BatchLedgerDto {
        schema_version: BATCH_SCHEMA_VERSION,
        batch_id,
        revision: 1,
        created_at_ms: now,
        updated_at_ms: now,
        source_root: source_root.display().to_string(),
        library_root: library_root.display().to_string(),
        settings,
        status: BatchStatusDto::Pending,
        cancel_requested: false,
        requested_concurrency: request.concurrency,
        concurrency: requested_batch_concurrency(request.concurrency)?,
        calibration: None,
        items,
    })
}

fn validated_batch_settings(
    requested: &BatchConversionSettingsDto,
    cosmetic_consent: Option<&CosmeticConsentDto>,
) -> CommandResult<BatchConversionSettingsDto> {
    validate_batch_settings(requested)?;
    let cosmetics = super::validate_cosmetic_options(
        requested.export_cosmetics,
        requested.export_stickers,
        requested.export_charms,
        cosmetic_consent,
    )?;
    let mut settings = requested.clone();
    settings.export_cosmetics = cosmetics.cosmetics;
    settings.export_stickers = cosmetics.stickers;
    settings.export_charms = cosmetics.charms;
    Ok(settings)
}

fn validate_batch_settings(settings: &BatchConversionSettingsDto) -> CommandResult<()> {
    let max_round = settings.max_round_seconds;
    if !max_round.is_finite() || !(30.0..=1800.0).contains(&max_round) {
        return Err(CommandErrorDto::new(
            "invalid_max_round_seconds",
            "Maximum round duration must be between 30 and 1800 seconds.",
        ));
    }
    let freeze = settings.freeze_preroll_seconds;
    if !freeze.is_finite() || !(0.0..=120.0).contains(&freeze) {
        return Err(CommandErrorDto::new(
            "invalid_freeze_preroll",
            "Freeze pre-roll must be between 0 and 120 seconds.",
        ));
    }
    Ok(())
}

fn run_batch_runtime(
    runtime: Arc<BatchRuntime>,
    events: Channel<BatchEvent>,
) -> CommandResult<BatchLedgerDto> {
    let _run_guard = RuntimeRunGuard::acquire(runtime.clone())?;
    runtime.cancel_requested.store(false, Ordering::Release);
    let (batch_id, total, concurrency) = runtime.update(|ledger| {
        ledger.status = BatchStatusDto::Running;
        ledger.cancel_requested = false;
        Ok((
            ledger.batch_id.clone(),
            ledger.items.len(),
            ledger
                .concurrency
                .clamp(MIN_BATCH_CONCURRENCY, MAX_BATCH_CONCURRENCY),
        ))
    })?;
    emit_batch(
        &events,
        BatchEvent::Started {
            batch_id: batch_id.clone(),
            total,
            concurrency,
        },
    );

    let pending = pending_item_ids(&runtime)?;
    if !pending.is_empty() && !runtime.cancel_requested.load(Ordering::Acquire) {
        let cursor = AtomicUsize::new(0);

        std::thread::scope(|scope| {
            for _ in 0..concurrency.min(pending.len()) {
                let runtime = runtime.clone();
                let events = events.clone();
                let pending = &pending;
                let cursor = &cursor;
                scope.spawn(move || loop {
                    if runtime.cancel_requested.load(Ordering::Acquire) {
                        break;
                    }
                    let index = cursor.fetch_add(1, Ordering::AcqRel);
                    let Some(item_id) = pending.get(index) else {
                        break;
                    };
                    if runtime.cancel_requested.load(Ordering::Acquire) {
                        break;
                    }
                    let _ = process_batch_item(runtime.clone(), &events, item_id);
                });
            }
        });
    }

    finish_batch_runtime(&runtime, &events)
}

fn process_batch_item(
    runtime: Arc<BatchRuntime>,
    events: &Channel<BatchEvent>,
    item_id: &str,
) -> CommandResult<()> {
    let (
        batch_id,
        source_path,
        library_root,
        settings,
        expected_size,
        expected_modified,
        expected_parts,
        replace_existing,
        initial_phase,
    ) = runtime.update(|ledger| {
        let batch_id = ledger.batch_id.clone();
        let library_root = PathBuf::from(&ledger.library_root);
        let settings = ledger.settings.clone();
        let item = ledger
            .items
            .iter_mut()
            .find(|item| item.item_id == item_id)
            .ok_or_else(|| {
                CommandErrorDto::new("batch_item_missing", "The batch item no longer exists.")
            })?;
        if item.status != BatchItemStatusDto::Pending {
            return Err(CommandErrorDto::new(
                "batch_item_not_pending",
                "The batch item is not pending.",
            ));
        }
        let initial_phase = if item
            .source_parts
            .iter()
            .any(|part| is_compressed_demo_path(Path::new(&part.path)))
            || (item.source_parts.is_empty()
                && is_compressed_demo_path(Path::new(&item.source_path)))
        {
            BatchItemPhaseDto::Decompressing
        } else {
            BatchItemPhaseDto::Parsing
        };
        item.status = BatchItemStatusDto::Running;
        item.phase = initial_phase;
        item.attempts = item.attempts.saturating_add(1);
        item.error = None;
        Ok((
            batch_id,
            PathBuf::from(&item.source_path),
            library_root,
            settings,
            item.size_bytes_u64(),
            item.modified_at_ms,
            item.source_parts.clone(),
            item.replace_existing,
            initial_phase,
        ))
    })?;

    let initial_eta = estimated_remaining_parse_seconds(&runtime.snapshot()?);
    emit_batch(
        events,
        BatchEvent::ItemPhase {
            batch_id: batch_id.clone(),
            item_id: item_id.to_string(),
            phase: initial_phase,
            parse_eta_seconds: initial_eta,
        },
    );

    if let Err(error) = validate_source_fingerprint(
        &source_path,
        expected_size,
        expected_modified,
        &expected_parts,
    ) {
        return fail_batch_item(&runtime, events, item_id, error);
    }

    let parse_started = Instant::now();
    let parse_recorded = Arc::new(AtomicBool::new(false));
    let sink_runtime = runtime.clone();
    let sink_events = events.clone();
    let sink_batch_id = batch_id.clone();
    let sink_item_id = item_id.to_string();
    let sink_parse_recorded = parse_recorded.clone();
    let sink: Arc<dyn Fn(TaskEvent) + Send + Sync> = Arc::new(move |task| {
        let phase = match &task {
            TaskEvent::Phase { phase } => Some(batch_phase_from_task(*phase)),
            _ => None,
        };
        if matches!(
            &task,
            TaskEvent::Phase {
                phase: TaskPhase::Analyzing
            }
        ) && !sink_parse_recorded.swap(true, Ordering::AcqRel)
        {
            let parse_ms = duration_ms(parse_started.elapsed());
            if let Ok((eta, samples)) = record_parse_sample(&sink_runtime, &sink_item_id, parse_ms)
            {
                if let Some(eta) = eta {
                    emit_batch(
                        &sink_events,
                        BatchEvent::EstimateUpdated {
                            batch_id: sink_batch_id.clone(),
                            parse_eta_seconds: eta,
                            samples,
                        },
                    );
                }
            }
        }
        if let Some(phase) = phase {
            let _ = update_item_phase(&sink_runtime, &sink_item_id, phase);
            emit_batch(
                &sink_events,
                BatchEvent::ItemPhase {
                    batch_id: sink_batch_id.clone(),
                    item_id: sink_item_id.clone(),
                    phase,
                    parse_eta_seconds: sink_runtime
                        .snapshot()
                        .ok()
                        .and_then(|ledger| estimated_remaining_parse_seconds(&ledger)),
                },
            );
        }
        emit_batch(
            &sink_events,
            BatchEvent::ItemTask {
                batch_id: sink_batch_id.clone(),
                item_id: sink_item_id.clone(),
                task,
                parse_eta_seconds: sink_runtime
                    .snapshot()
                    .ok()
                    .and_then(|ledger| estimated_remaining_parse_seconds(&ledger)),
            },
        );
    });

    let result = super::process_batch_demo(
        BatchProcessRequest {
            batch_id: batch_id.clone(),
            item_id: item_id.to_string(),
            source_path,
            library_root,
            settings,
            replace_existing,
        },
        sink,
    );

    match result {
        Ok(result) => {
            runtime.update(|ledger| {
                let item = find_item_mut(ledger, item_id)?;
                item.status = BatchItemStatusDto::Completed;
                item.phase = BatchItemPhaseDto::Complete;
                item.demo_sha256 = Some(result.demo_sha256.clone());
                item.map = Some(result.map.clone());
                item.server_name = result.server_name.clone();
                item.archive_root = Some(result.archive_root.display().to_string());
                item.manifest_path = Some(result.manifest_path.display().to_string());
                item.rounds_exported = Some(result.rounds_exported);
                item.files_written = Some(result.files_written);
                item.error = None;
                Ok(())
            })?;
            let eta = estimated_remaining_parse_seconds(&runtime.snapshot()?);
            emit_batch(
                events,
                BatchEvent::ItemCompleted {
                    batch_id,
                    item_id: item_id.to_string(),
                    archive_root: result.archive_root.display().to_string(),
                    manifest_path: result.manifest_path.display().to_string(),
                    demo_source: result.demo_source,
                    rounds_exported: result.rounds_exported,
                    parse_eta_seconds: eta,
                },
            );
            Ok(())
        }
        Err(error) => fail_batch_item(&runtime, events, item_id, error),
    }
}

fn fail_batch_item(
    runtime: &Arc<BatchRuntime>,
    events: &Channel<BatchEvent>,
    item_id: &str,
    error: CommandErrorDto,
) -> CommandResult<()> {
    let public_error = BatchErrorDto::from(error);
    let batch_id = runtime.update(|ledger| {
        let batch_id = ledger.batch_id.clone();
        let item = find_item_mut(ledger, item_id)?;
        item.status = BatchItemStatusDto::Failed;
        item.phase = BatchItemPhaseDto::Failed;
        item.error = Some(public_error.clone());
        Ok(batch_id)
    })?;
    let eta = estimated_remaining_parse_seconds(&runtime.snapshot()?);
    emit_batch(
        events,
        BatchEvent::ItemFailed {
            batch_id,
            item_id: item_id.to_string(),
            error: public_error,
            parse_eta_seconds: eta,
        },
    );
    Ok(())
}

fn finish_batch_runtime(
    runtime: &Arc<BatchRuntime>,
    events: &Channel<BatchEvent>,
) -> CommandResult<BatchLedgerDto> {
    let cancelled = runtime.cancel_requested.load(Ordering::Acquire);
    let (batch_id, completed, failed, pending, status) = runtime.update(|ledger| {
        // No worker exists once this function runs. A row left as Running therefore represents
        // an interrupted item, never a completed one; put it back in the resumable queue.
        for item in &mut ledger.items {
            if item.status == BatchItemStatusDto::Running {
                item.status = BatchItemStatusDto::Pending;
                item.phase = BatchItemPhaseDto::Queued;
            }
        }
        let completed = ledger
            .items
            .iter()
            .filter(|item| item.status == BatchItemStatusDto::Completed)
            .count();
        let failed = ledger
            .items
            .iter()
            .filter(|item| item.status == BatchItemStatusDto::Failed)
            .count();
        let pending = ledger
            .items
            .iter()
            .filter(|item| item.status == BatchItemStatusDto::Pending)
            .count();
        let status = if pending > 0 {
            BatchStatusDto::Paused
        } else if failed > 0 {
            BatchStatusDto::CompletedWithErrors
        } else {
            BatchStatusDto::Completed
        };
        ledger.status = status.clone();
        ledger.cancel_requested = cancelled && pending > 0;
        Ok((ledger.batch_id.clone(), completed, failed, pending, status))
    })?;
    if status == BatchStatusDto::Paused {
        emit_batch(
            events,
            BatchEvent::Paused {
                batch_id,
                completed,
                failed,
                pending,
            },
        );
    } else {
        emit_batch(
            events,
            BatchEvent::Finished {
                batch_id,
                completed,
                failed,
            },
        );
    }
    runtime.snapshot()
}

fn pending_item_ids(runtime: &Arc<BatchRuntime>) -> CommandResult<Vec<String>> {
    Ok(runtime
        .lock_ledger()?
        .items
        .iter()
        .filter(|item| item.status == BatchItemStatusDto::Pending)
        .map(|item| item.item_id.clone())
        .collect())
}

fn find_item_mut<'a>(
    ledger: &'a mut BatchLedgerDto,
    item_id: &str,
) -> CommandResult<&'a mut BatchItemDto> {
    ledger
        .items
        .iter_mut()
        .find(|item| item.item_id == item_id)
        .ok_or_else(|| {
            CommandErrorDto::new("batch_item_missing", "The batch item no longer exists.")
        })
}

fn update_item_phase(
    runtime: &Arc<BatchRuntime>,
    item_id: &str,
    phase: BatchItemPhaseDto,
) -> CommandResult<()> {
    runtime.update(|ledger| {
        let item = find_item_mut(ledger, item_id)?;
        if item.status == BatchItemStatusDto::Running && item.phase != phase {
            item.phase = phase;
        }
        Ok(())
    })
}

fn record_parse_sample(
    runtime: &Arc<BatchRuntime>,
    item_id: &str,
    parse_ms: u64,
) -> CommandResult<(Option<u64>, usize)> {
    runtime.update(|ledger| {
        let (size, previous_sample) = {
            let item = ledger
                .items
                .iter()
                .find(|item| item.item_id == item_id)
                .ok_or_else(|| {
                    CommandErrorDto::new("batch_item_missing", "The batch item no longer exists.")
                })?;
            (item.estimated_parse_bytes(), item.parse_ms)
        };
        if previous_sample.is_some() || size == 0 || parse_ms == 0 {
            let samples = ledger
                .calibration
                .as_ref()
                .map(|calibration| calibration.samples)
                .unwrap_or(0);
            return Ok((estimated_remaining_parse_seconds(ledger), samples));
        }
        let observed_seconds_per_gib =
            parse_ms as f64 / 1000.0 / (size as f64 / (1024.0 * 1024.0 * 1024.0));
        match ledger.calibration.as_mut() {
            Some(calibration) => {
                // A light EWMA adapts as concurrent parse samples finish without making the ETA
                // jump wildly when one match is unusually dense or sparse.
                calibration.seconds_per_gib =
                    calibration.seconds_per_gib * 0.70 + observed_seconds_per_gib * 0.30;
                calibration.samples = calibration.samples.saturating_add(1);
            }
            None => {
                ledger.calibration = Some(BatchCalibrationDto {
                    samples: 1,
                    seconds_per_gib: observed_seconds_per_gib,
                    first_item_id: item_id.to_string(),
                    first_parse_ms: parse_ms,
                });
            }
        }
        if let Some(item) = ledger.items.iter_mut().find(|item| item.item_id == item_id) {
            item.parse_ms = Some(parse_ms);
        }
        refresh_parse_predictions(ledger);
        let samples = ledger
            .calibration
            .as_ref()
            .map(|calibration| calibration.samples)
            .unwrap_or(0);
        Ok((estimated_remaining_parse_seconds(ledger), samples))
    })
}

fn refresh_parse_predictions(ledger: &mut BatchLedgerDto) {
    let Some(calibration) = ledger.calibration.as_ref() else {
        return;
    };
    for item in &mut ledger.items {
        if item.parse_ms.is_some() {
            item.predicted_parse_ms = item.parse_ms;
            continue;
        }
        let gib = item.estimated_parse_bytes() as f64 / (1024.0 * 1024.0 * 1024.0);
        let millis = (calibration.seconds_per_gib * gib * 1000.0).max(1_000.0);
        item.predicted_parse_ms = Some(millis.ceil().min(u64::MAX as f64) as u64);
    }
}

fn estimated_remaining_parse_seconds(ledger: &BatchLedgerDto) -> Option<u64> {
    ledger.calibration.as_ref()?;
    let remaining_ms = ledger
        .items
        .iter()
        .filter(|item| {
            matches!(
                item.status,
                BatchItemStatusDto::Pending | BatchItemStatusDto::Running
            ) && item.parse_ms.is_none()
        })
        .filter_map(|item| item.predicted_parse_ms)
        .fold(0_u64, u64::saturating_add);
    let workers = ledger
        .concurrency
        .clamp(MIN_BATCH_CONCURRENCY, MAX_BATCH_CONCURRENCY) as f64;
    let effective_workers = 1.0 + (workers - 1.0) * ESTIMATED_PARALLEL_EFFICIENCY;
    Some(((remaining_ms as f64 / effective_workers) / 1000.0).ceil() as u64)
}

fn batch_phase_from_task(phase: TaskPhase) -> BatchItemPhaseDto {
    match phase {
        TaskPhase::Decompressing => BatchItemPhaseDto::Decompressing,
        TaskPhase::Parsing => BatchItemPhaseDto::Parsing,
        TaskPhase::Analyzing => BatchItemPhaseDto::Analyzing,
        TaskPhase::Exporting => BatchItemPhaseDto::Converting,
        TaskPhase::Voice => BatchItemPhaseDto::Voice,
        TaskPhase::Validating => BatchItemPhaseDto::Validating,
        TaskPhase::Complete => BatchItemPhaseDto::Complete,
    }
}

fn auto_batch_concurrency() -> usize {
    let logical = std::thread::available_parallelism()
        .map(|value| value.get())
        .unwrap_or(MIN_AUTO_BATCH_CONCURRENCY);
    logical
        .div_ceil(4)
        .clamp(MIN_AUTO_BATCH_CONCURRENCY, MAX_BATCH_CONCURRENCY)
}

fn requested_batch_concurrency(value: Option<usize>) -> CommandResult<usize> {
    match value {
        None => Ok(auto_batch_concurrency()),
        Some(value) if (MIN_BATCH_CONCURRENCY..=MAX_BATCH_CONCURRENCY).contains(&value) => {
            Ok(value)
        }
        Some(_) => Err(CommandErrorDto::new(
            "invalid_batch_concurrency",
            "Parallel conversion concurrency must be Auto or a value from 1 to 8.",
        )),
    }
}

fn validate_source_fingerprint(
    path: &Path,
    expected_size: u64,
    expected_modified_at_ms: Option<u64>,
    expected_parts: &[BatchSourcePartDto],
) -> CommandResult<()> {
    let source = resolve_demo_source(path)
        .map_err(|error| CommandErrorDto::from_core("batch_source_missing", error))?;
    for part in &source.parts {
        let metadata = fs::symlink_metadata(&part.path).map_err(|error| {
            CommandErrorDto::at_path("batch_source_missing", error.to_string(), &part.path)
        })?;
        if !metadata.is_file() || super::catalog::is_symlink_or_reparse(&metadata) {
            return Err(CommandErrorDto::at_path(
                "batch_source_invalid",
                "The queued demo is no longer a normal file.",
                &part.path,
            ));
        }
    }
    if source.parts.len() != expected_parts.len() {
        return Err(CommandErrorDto::at_path(
            "batch_source_changed",
            "The demo segment set changed after it was added to the batch. Rescan the folder before retrying it.",
            path,
        ));
    }
    for (part, expected) in source.parts.iter().zip(expected_parts) {
        let metadata = fs::symlink_metadata(&part.path).map_err(|error| {
            CommandErrorDto::at_path("batch_source_missing", error.to_string(), &part.path)
        })?;
        let actual_modified = metadata.modified().ok().map(unix_time_ms);
        if part.part != expected.part
            || normalized_path_key(&part.path) != normalized_path_key(Path::new(&expected.path))
            || metadata.len().to_string() != expected.size_bytes
            || (expected.modified_at_ms.is_some() && actual_modified != expected.modified_at_ms)
        {
            return Err(CommandErrorDto::at_path(
                "batch_source_changed",
                "A demo segment changed after it was added to the batch. Rescan the folder before retrying it.",
                &part.path,
            ));
        }
    }
    let metadata = source
        .metadata()
        .map_err(|error| CommandErrorDto::from_core("batch_source_missing", error))?;
    let actual_modified = metadata.modified.map(unix_time_ms);
    if metadata.size_bytes != expected_size
        || (expected_modified_at_ms.is_some() && actual_modified != expected_modified_at_ms)
    {
        return Err(CommandErrorDto::at_path(
            "batch_source_changed",
            "The demo changed after it was added to the batch. Rescan the folder before retrying it.",
            path,
        ));
    }
    Ok(())
}

fn emit_batch(channel: &Channel<BatchEvent>, event: BatchEvent) {
    let _ = channel.send(event);
}

fn validate_source_root(path: &Path) -> CommandResult<PathBuf> {
    if path.as_os_str().is_empty() {
        return Err(CommandErrorDto::new(
            "demo_source_root_invalid",
            "Choose a folder containing CS2 demos.",
        ));
    }
    let metadata = fs::symlink_metadata(path).map_err(|error| {
        CommandErrorDto::at_path("demo_source_root_invalid", error.to_string(), path)
    })?;
    if !metadata.is_dir() || super::catalog::is_symlink_or_reparse(&metadata) {
        return Err(CommandErrorDto::at_path(
            "demo_source_root_invalid",
            "Choose a normal folder containing CS2 demos, not a link or junction.",
            path,
        ));
    }
    super::canonicalize_public_path(path).map_err(|error| {
        CommandErrorDto::at_path("demo_source_root_invalid", error.to_string(), path)
    })
}

fn validate_batch_demo_path(source_root: &Path, path: &Path) -> CommandResult<PathBuf> {
    if !is_demo_path(path) {
        return Err(CommandErrorDto::at_path(
            "batch_demo_invalid",
            "Batch import only accepts .dem and .dem.zst files.",
            path,
        ));
    }
    let metadata = fs::symlink_metadata(path)
        .map_err(|error| CommandErrorDto::at_path("batch_demo_invalid", error.to_string(), path))?;
    if !metadata.is_file() || super::catalog::is_symlink_or_reparse(&metadata) {
        return Err(CommandErrorDto::at_path(
            "batch_demo_invalid",
            "Batch import only accepts normal .dem or .dem.zst files, not links or junctions.",
            path,
        ));
    }
    let canonical = super::canonicalize_public_path(path)
        .map_err(|error| CommandErrorDto::at_path("batch_demo_invalid", error.to_string(), path))?;
    if !canonical.starts_with(source_root) || canonical == source_root {
        return Err(CommandErrorDto::at_path(
            "batch_demo_outside_root",
            "The selected demo resolves outside the scanned folder.",
            path,
        ));
    }
    Ok(canonical)
}

fn is_demo_path(path: &Path) -> bool {
    path.file_name()
        .and_then(|name| name.to_str())
        .is_some_and(|name| {
            let lower = name.to_ascii_lowercase();
            lower.ends_with(".dem") || lower.ends_with(".dem.zst")
        })
}

fn is_compressed_demo_path(path: &Path) -> bool {
    path.file_name()
        .and_then(|name| name.to_str())
        .is_some_and(|name| name.to_ascii_lowercase().ends_with(".dem.zst"))
}

fn normalized_path_key(path: &Path) -> String {
    path.to_string_lossy()
        .replace('\\', "/")
        .to_ascii_lowercase()
}

fn new_batch_id(now_ms: u64) -> String {
    let nonce = NEXT_BATCH_NONCE.fetch_add(1, Ordering::Relaxed);
    format!("batch-{now_ms:x}-{nonce:x}")
}

fn validate_batch_id(batch_id: &str) -> CommandResult<()> {
    if batch_id.is_empty()
        || batch_id.len() > 80
        || !batch_id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
    {
        return Err(CommandErrorDto::new(
            "invalid_batch_id",
            "The batch identifier is invalid.",
        ));
    }
    Ok(())
}

fn batch_ledger_directory(app: &AppHandle) -> CommandResult<PathBuf> {
    let directory = app
        .path()
        .app_local_data_dir()
        .map_err(|error| CommandErrorDto::new("batch_storage_unavailable", error.to_string()))?
        .join("batches");
    fs::create_dir_all(&directory).map_err(|error| {
        CommandErrorDto::at_path("batch_storage_unavailable", error.to_string(), &directory)
    })?;
    let metadata = fs::symlink_metadata(&directory).map_err(|error| {
        CommandErrorDto::at_path("batch_storage_unavailable", error.to_string(), &directory)
    })?;
    if !metadata.is_dir() || super::catalog::is_symlink_or_reparse(&metadata) {
        return Err(CommandErrorDto::at_path(
            "batch_storage_invalid",
            "Batch storage must be a normal local folder.",
            &directory,
        ));
    }
    Ok(directory)
}

fn batch_ledger_path(app: &AppHandle, batch_id: &str) -> CommandResult<PathBuf> {
    validate_batch_id(batch_id)?;
    Ok(batch_ledger_directory(app)?.join(format!("{batch_id}.json")))
}

fn backup_ledger_path(path: &Path) -> PathBuf {
    let name = path
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_else(|| "batch.json".to_string());
    path.with_file_name(format!("{name}.bak"))
}

fn persist_ledger_atomic(path: &Path, ledger: &BatchLedgerDto) -> CommandResult<()> {
    if ledger.schema_version != BATCH_SCHEMA_VERSION {
        return Err(CommandErrorDto::new(
            "batch_schema_unsupported",
            "Refusing to write an unsupported batch state schema.",
        ));
    }
    let parent = path.parent().ok_or_else(|| {
        CommandErrorDto::at_path(
            "batch_persist_failed",
            "Batch state has no parent folder.",
            path,
        )
    })?;
    fs::create_dir_all(parent).map_err(|error| {
        CommandErrorDto::at_path("batch_persist_failed", error.to_string(), parent)
    })?;
    let bytes = serde_json::to_vec_pretty(ledger)
        .map_err(|error| CommandErrorDto::new("batch_serialize_failed", error.to_string()))?;
    let sequence = NEXT_LEDGER_NONCE.fetch_add(1, Ordering::Relaxed);
    let temp_path = path.with_file_name(format!(
        ".{}.tmp.{}.{}",
        path.file_name()
            .map(|name| name.to_string_lossy())
            .unwrap_or_default(),
        std::process::id(),
        sequence
    ));
    let mut temp = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temp_path)
        .map_err(|error| {
            CommandErrorDto::at_path("batch_persist_failed", error.to_string(), &temp_path)
        })?;
    if let Err(error) = temp.write_all(&bytes).and_then(|_| temp.sync_all()) {
        let _ = fs::remove_file(&temp_path);
        return Err(CommandErrorDto::at_path(
            "batch_persist_failed",
            error.to_string(),
            &temp_path,
        ));
    }
    drop(temp);

    let backup_path = backup_ledger_path(path);
    if backup_path.exists() {
        fs::remove_file(&backup_path).map_err(|error| {
            CommandErrorDto::at_path(
                "batch_persist_failed",
                format!("Could not remove a previous batch-state backup: {error}"),
                &backup_path,
            )
        })?;
    }
    let had_previous = path.exists();
    if had_previous {
        if let Err(error) = fs::rename(path, &backup_path) {
            let _ = fs::remove_file(&temp_path);
            return Err(CommandErrorDto::at_path(
                "batch_persist_failed",
                error.to_string(),
                path,
            ));
        }
    }
    if let Err(error) = fs::rename(&temp_path, path) {
        if had_previous {
            let _ = fs::rename(&backup_path, path);
        }
        let _ = fs::remove_file(&temp_path);
        return Err(CommandErrorDto::at_path(
            "batch_persist_failed",
            format!("Could not promote batch state: {error}"),
            path,
        ));
    }
    if had_previous {
        let _ = fs::remove_file(&backup_path);
    }
    Ok(())
}

fn load_ledger_with_recovery(path: &Path) -> CommandResult<BatchLedgerDto> {
    let backup_path = backup_ledger_path(path);
    let primary = load_ledger_file(path);
    let backup = load_ledger_file(&backup_path);
    match (primary, backup) {
        (Ok(primary), Ok(backup)) => {
            if backup.revision > primary.revision {
                restore_backup_as_primary(path, &backup_path)?;
                Ok(backup)
            } else {
                Ok(primary)
            }
        }
        (Ok(primary), Err(_)) => Ok(primary),
        (Err(_), Ok(backup)) => {
            restore_backup_as_primary(path, &backup_path)?;
            Ok(backup)
        }
        (Err(primary_error), Err(backup_error)) => {
            if path.exists() {
                Err(primary_error)
            } else if backup_path.exists() {
                Err(backup_error)
            } else {
                Err(CommandErrorDto::at_path(
                    "batch_not_found",
                    "No readable batch state was found.",
                    path,
                ))
            }
        }
    }
}

fn restore_backup_as_primary(path: &Path, backup_path: &Path) -> CommandResult<()> {
    if path.exists() {
        fs::remove_file(path).map_err(|error| {
            CommandErrorDto::at_path(
                "batch_recovery_failed",
                format!("Could not remove the unreadable or older batch state: {error}"),
                path,
            )
        })?;
    }
    // If the process stops before this atomic rename, the valid backup is still present. After
    // it succeeds, the same valid bytes are the primary state used by the next journal write.
    fs::rename(backup_path, path).map_err(|error| {
        CommandErrorDto::at_path(
            "batch_recovery_failed",
            format!("Could not restore the valid batch-state backup: {error}"),
            backup_path,
        )
    })
}

fn load_ledger_file(path: &Path) -> CommandResult<BatchLedgerDto> {
    const MAX_LEDGER_BYTES: u64 = 16 * 1024 * 1024;
    let metadata = fs::symlink_metadata(path)
        .map_err(|error| CommandErrorDto::at_path("batch_read_failed", error.to_string(), path))?;
    if !metadata.is_file()
        || super::catalog::is_symlink_or_reparse(&metadata)
        || metadata.len() > MAX_LEDGER_BYTES
    {
        return Err(CommandErrorDto::at_path(
            "batch_read_failed",
            "Batch state is not a normal JSON file of a supported size.",
            path,
        ));
    }
    let bytes = fs::read(path)
        .map_err(|error| CommandErrorDto::at_path("batch_read_failed", error.to_string(), path))?;
    let ledger: BatchLedgerDto = serde_json::from_slice(&bytes)
        .map_err(|error| CommandErrorDto::at_path("batch_read_failed", error.to_string(), path))?;
    if ledger.schema_version != BATCH_SCHEMA_VERSION {
        return Err(CommandErrorDto::at_path(
            "batch_schema_unsupported",
            format!(
                "Batch schema {} is not supported by this app.",
                ledger.schema_version
            ),
            path,
        ));
    }
    validate_batch_id(&ledger.batch_id)?;
    Ok(ledger)
}

fn unix_time_ms(time: SystemTime) -> u64 {
    time.duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .try_into()
        .unwrap_or(u64::MAX)
}

fn duration_ms(duration: Duration) -> u64 {
    duration.as_millis().try_into().unwrap_or(u64::MAX)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn test_directory(label: &str) -> PathBuf {
        let nonce = NEXT_LEDGER_NONCE.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "cs2-demotracer-batch-{label}-{}-{nonce}",
            std::process::id()
        ));
        fs::create_dir(&path).unwrap();
        path
    }

    fn sample_ledger() -> BatchLedgerDto {
        let now = unix_time_ms(SystemTime::now());
        BatchLedgerDto {
            schema_version: BATCH_SCHEMA_VERSION,
            batch_id: "batch-test-1".to_string(),
            revision: 1,
            created_at_ms: now,
            updated_at_ms: now,
            source_root: "C:/demos".to_string(),
            library_root: "C:/library".to_string(),
            settings: BatchConversionSettingsDto::default(),
            status: BatchStatusDto::Pending,
            cancel_requested: false,
            requested_concurrency: Some(2),
            concurrency: 2,
            calibration: Some(BatchCalibrationDto {
                samples: 1,
                seconds_per_gib: 100.0,
                first_item_id: "one".to_string(),
                first_parse_ms: 50_000,
            }),
            items: vec![
                BatchItemDto {
                    item_id: "one".to_string(),
                    source_path: "C:/demos/one.dem".to_string(),
                    relative_path: "one.dem".to_string(),
                    file_name: "one.dem".to_string(),
                    size_bytes: (512_u64 * 1024 * 1024).to_string(),
                    modified_at_ms: Some(now),
                    source_parts: Vec::new(),
                    replace_existing: false,
                    status: BatchItemStatusDto::Completed,
                    phase: BatchItemPhaseDto::Complete,
                    attempts: 1,
                    parse_ms: Some(50_000),
                    predicted_parse_ms: Some(50_000),
                    demo_sha256: Some("abc".to_string()),
                    map: Some("de_mirage".to_string()),
                    server_name: None,
                    archive_root: Some("C:/library/mirage/one".to_string()),
                    manifest_path: Some("C:/library/mirage/one/manifest.json".to_string()),
                    rounds_exported: Some(12),
                    files_written: Some(120),
                    error: None,
                },
                BatchItemDto {
                    item_id: "two".to_string(),
                    source_path: "C:/demos/two.dem.zst".to_string(),
                    relative_path: "two.dem.zst".to_string(),
                    file_name: "two.dem.zst".to_string(),
                    size_bytes: (256_u64 * 1024 * 1024).to_string(),
                    modified_at_ms: Some(now),
                    source_parts: Vec::new(),
                    replace_existing: false,
                    status: BatchItemStatusDto::Pending,
                    phase: BatchItemPhaseDto::Queued,
                    attempts: 0,
                    parse_ms: None,
                    predicted_parse_ms: None,
                    demo_sha256: None,
                    map: None,
                    server_name: None,
                    archive_root: None,
                    manifest_path: None,
                    rounds_exported: None,
                    files_written: None,
                    error: None,
                },
            ],
        }
    }

    #[test]
    fn scan_is_recursive_bounded_and_reports_truncation() {
        let root = test_directory("scan");
        let nested = root.join("nested");
        fs::create_dir(&nested).unwrap();
        fs::write(root.join("a.dem"), b"a").unwrap();
        fs::write(root.join("b.DEM.ZST"), b"bb").unwrap();
        fs::write(root.join("c.dem"), b"ccc").unwrap();
        fs::write(nested.join("bare.zst"), b"no").unwrap();
        fs::write(root.join("ignore.txt"), b"no").unwrap();

        let scan = scan_demo_folder_for(&ScanDemoFolderRequest {
            root: root.display().to_string(),
            recursive: true,
            limit: 2,
        })
        .unwrap();
        assert_eq!(scan.candidates.len(), 2);
        assert!(scan.truncated);
        assert_eq!(scan.candidates[0].relative_path, "a.dem");
        assert!(!scan.candidates[0].compressed);
        assert_eq!(scan.candidates[1].relative_path, "b.DEM.ZST");
        assert!(scan.candidates[1].compressed);

        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn demo_path_filter_accepts_dem_zst_but_not_bare_zst() {
        assert!(is_demo_path(Path::new("match.dem")));
        assert!(is_demo_path(Path::new("match.DEM.ZST")));
        assert!(is_compressed_demo_path(Path::new("match.dem.zst")));
        assert!(!is_demo_path(Path::new("match.zst")));
        assert!(!is_demo_path(Path::new("match.dem.zip")));
    }

    #[test]
    fn scan_groups_numbered_demo_segments_as_one_candidate() {
        let root = test_directory("segment-scan");
        fs::write(root.join("match-p1.dem"), b"one").unwrap();
        fs::write(root.join("match-p2.dem"), b"two-two").unwrap();
        fs::write(root.join("ordinary.dem"), b"plain").unwrap();

        let scan = scan_demo_folder_for(&ScanDemoFolderRequest {
            root: root.display().to_string(),
            recursive: false,
            limit: 10,
        })
        .unwrap();

        assert_eq!(scan.candidates.len(), 2);
        let segmented = scan
            .candidates
            .iter()
            .find(|candidate| candidate.file_name == "match-p1.dem")
            .unwrap();
        assert_eq!(segmented.size_bytes, "10");

        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn scan_skips_a_broken_segment_set_without_hiding_other_demos() {
        let root = test_directory("broken-segment-scan");
        fs::write(root.join("ordinary.dem"), b"plain").unwrap();
        fs::write(root.join("broken-p1.dem"), b"one").unwrap();
        fs::write(root.join("broken-p3.dem"), b"three").unwrap();

        let scan = scan_demo_folder_for(&ScanDemoFolderRequest {
            root: root.display().to_string(),
            recursive: false,
            limit: 10,
        })
        .unwrap();

        assert_eq!(scan.candidates.len(), 1);
        assert_eq!(scan.candidates[0].file_name, "ordinary.dem");
        assert!(scan
            .warnings
            .iter()
            .any(|warning| warning.contains("missing p2")));

        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn non_recursive_scan_does_not_descend() {
        let root = test_directory("flat-scan");
        let nested = root.join("nested");
        fs::create_dir(&nested).unwrap();
        fs::write(root.join("top.dem"), b"top").unwrap();
        fs::write(nested.join("nested.dem"), b"nested").unwrap();

        let scan = scan_demo_folder_for(&ScanDemoFolderRequest {
            root: root.display().to_string(),
            recursive: false,
            limit: 10,
        })
        .unwrap();
        assert_eq!(scan.candidates.len(), 1);
        assert_eq!(scan.candidates[0].relative_path, "top.dem");

        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn predictor_scales_with_file_size_and_parallel_efficiency() {
        let mut ledger = sample_ledger();
        refresh_parse_predictions(&mut ledger);
        // The 256 MiB zstd input is normalized to roughly 1 GiB of demo data.
        assert_eq!(ledger.items[1].predicted_parse_ms, Some(100_000));
        let eta_two_workers = estimated_remaining_parse_seconds(&ledger).unwrap();
        ledger.concurrency = 4;
        let eta_four_workers = estimated_remaining_parse_seconds(&ledger).unwrap();
        assert!(eta_four_workers < eta_two_workers);
        assert!(eta_four_workers > 0);
    }

    #[test]
    fn atomic_ledger_reader_recovers_valid_backup() {
        let root = test_directory("ledger");
        let path = root.join("batch-test-1.json");
        let mut ledger = sample_ledger();
        persist_ledger_atomic(&path, &ledger).unwrap();
        let backup = backup_ledger_path(&path);
        fs::copy(&path, &backup).unwrap();
        ledger.revision = 2;
        fs::write(&path, b"not json").unwrap();

        let recovered = load_ledger_with_recovery(&path).unwrap();
        assert_eq!(recovered.revision, 1);
        assert_eq!(recovered.batch_id, "batch-test-1");

        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn legacy_batch_schema_reports_the_real_upgrade_error() {
        let root = test_directory("legacy-ledger-schema");
        let path = root.join("batch-test-1.json");
        let mut ledger = sample_ledger();
        ledger.schema_version = 1;
        fs::write(&path, serde_json::to_vec(&ledger).unwrap()).unwrap();

        let error = load_ledger_with_recovery(&path).unwrap_err();
        assert_eq!(error.code, "batch_schema_unsupported");

        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn batch_ids_cannot_escape_storage() {
        assert!(validate_batch_id("batch-123_ab").is_ok());
        assert!(validate_batch_id("../batch").is_err());
        assert!(validate_batch_id("batch.json").is_err());
    }

    #[test]
    fn auto_concurrency_stays_inside_public_bounds() {
        let concurrency = auto_batch_concurrency();
        assert!((MIN_AUTO_BATCH_CONCURRENCY..=MAX_BATCH_CONCURRENCY).contains(&concurrency));
        assert_eq!(requested_batch_concurrency(Some(1)).unwrap(), 1);
        assert_eq!(requested_batch_concurrency(Some(8)).unwrap(), 8);
        assert!(requested_batch_concurrency(Some(9)).is_err());
    }

    #[test]
    fn batch_ledger_round_trips_cosmetic_intent_without_the_consent_phrase() {
        let mut ledger = sample_ledger();
        ledger.settings.export_cosmetics = true;
        ledger.settings.export_stickers = true;
        ledger.settings.export_charms = false;
        ledger.items[0].replace_existing = true;

        let json = serde_json::to_string(&ledger).unwrap();
        assert!(!json.contains("phrase"));
        let restored: BatchLedgerDto = serde_json::from_str(&json).unwrap();
        assert!(restored.settings.export_cosmetics);
        assert!(restored.settings.export_stickers);
        assert!(!restored.settings.export_charms);
        assert!(restored.items[0].replace_existing);
        assert!(!restored.items[1].replace_existing);
    }

    #[test]
    fn legacy_batch_items_never_replace_existing_archives() {
        let mut value = serde_json::to_value(sample_ledger()).unwrap();
        for item in value["items"].as_array_mut().unwrap() {
            item.as_object_mut().unwrap().remove("replaceExisting");
        }

        let restored: BatchLedgerDto = serde_json::from_value(value).unwrap();
        assert!(restored.items.iter().all(|item| !item.replace_existing));
    }

    #[test]
    fn batch_replacement_is_limited_to_explicitly_confirmed_demos() {
        let root = test_directory("replace-selected");
        let library = root.join("library");
        fs::create_dir(&library).unwrap();
        let first = root.join("match-m1.dem");
        let second = root.join("match-m2.dem");
        fs::write(&first, b"first").unwrap();
        fs::write(&second, b"second").unwrap();
        let request = StartBatchImportRequest {
            source_root: root.display().to_string(),
            library_root: library.display().to_string(),
            demo_paths: vec![first.display().to_string(), second.display().to_string()],
            replace_demo_paths: vec![second.display().to_string()],
            concurrency: Some(2),
            settings: BatchConversionSettingsDto::default(),
            cosmetic_consent: None,
        };

        let ledger = build_batch_ledger(&request).unwrap();
        let first = crate::canonicalize_public_path(&first)
            .unwrap()
            .display()
            .to_string();
        let second = crate::canonicalize_public_path(&second)
            .unwrap()
            .display()
            .to_string();
        assert_eq!(ledger.items.len(), 2);
        assert!(
            !ledger
                .items
                .iter()
                .find(|item| item.source_path == first)
                .unwrap()
                .replace_existing
        );
        assert!(
            ledger
                .items
                .iter()
                .find(|item| item.source_path == second)
                .unwrap()
                .replace_existing
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn batch_replacement_rejects_an_unselected_demo() {
        let root = test_directory("replace-unselected");
        let library = root.join("library");
        fs::create_dir(&library).unwrap();
        let selected = root.join("selected.dem");
        let unselected = root.join("unselected.dem");
        fs::write(&selected, b"selected").unwrap();
        fs::write(&unselected, b"unselected").unwrap();
        let request = StartBatchImportRequest {
            source_root: root.display().to_string(),
            library_root: library.display().to_string(),
            demo_paths: vec![selected.display().to_string()],
            replace_demo_paths: vec![unselected.display().to_string()],
            concurrency: Some(2),
            settings: BatchConversionSettingsDto::default(),
            cosmetic_consent: None,
        };

        assert_eq!(
            build_batch_ledger(&request).unwrap_err().code,
            "batch_replace_target_not_selected"
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn legacy_batch_settings_default_cosmetic_export_off() {
        let settings: BatchConversionSettingsDto = serde_json::from_value(serde_json::json!({
            "includeSuspicious": false,
            "fullRound": false,
            "side": "both",
            "subtickMode": "auto",
            "maxRoundSeconds": 240.0,
            "freezePrerollSeconds": 10.0,
            "exportVoice": true
        }))
        .unwrap();

        assert!(!settings.export_cosmetics);
        assert!(!settings.export_stickers);
        assert!(!settings.export_charms);
    }

    #[test]
    fn batch_settings_require_consent_once_and_store_normalized_flags() {
        let mut requested = BatchConversionSettingsDto {
            export_cosmetics: false,
            export_stickers: true,
            export_charms: true,
            ..BatchConversionSettingsDto::default()
        };
        let normalized = validated_batch_settings(&requested, None).unwrap();
        assert!(!normalized.export_cosmetics);
        assert!(!normalized.export_stickers);
        assert!(!normalized.export_charms);

        requested.export_cosmetics = true;
        let wrong = CosmeticConsentDto {
            phrase: "close enough".to_string(),
        };
        assert_eq!(
            validated_batch_settings(&requested, Some(&wrong))
                .unwrap_err()
                .code,
            "cosmetic_consent_required"
        );

        let accepted = CosmeticConsentDto {
            phrase: super::super::COSMETIC_CONFIRMATION_PHRASE.to_string(),
        };
        let normalized = validated_batch_settings(&requested, Some(&accepted)).unwrap();
        assert!(normalized.export_cosmetics);
        assert!(normalized.export_stickers);
        assert!(normalized.export_charms);
    }
}
