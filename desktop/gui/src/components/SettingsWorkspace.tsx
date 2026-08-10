/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import {
  AlertIcon,
  ArrowIcon,
  CheckIcon,
  ChevronIcon,
  CloseIcon,
  ExternalLinkIcon,
  FolderIcon,
  LibraryIcon,
  RefreshIcon,
  ReplayIcon,
  SearchIcon,
  SlidersIcon,
  TraceMark,
} from "../icons";
import type { ResolvedTheme, UiScale } from "../appearance";
import { DEMOTRACER_CREDITS } from "../credits";
import { LANGUAGE_OPTIONS, type TextDictionary } from "../i18n";
import type {
  Cs2InstallCandidate,
  ConverterSettings,
  EnvironmentCheckStatus,
  EnvironmentDiagnosticReport,
  EnvironmentOverallStatus,
  EnvironmentPluginClassification,
  GuiUpdateStatus,
  Language,
  LocalEnvironmentSettings,
  PlaybackReleaseStatus,
  PlaybackUpdateStatus,
  ServerConfigDocument,
  ServerConfigValidation,
} from "../types";
import { releaseNotesForLanguage } from "../releaseNotes";
import { SERVER_CONFIG_GUIDE, type ServerConfigGuideGroup } from "../serverConfigGuide";
import type { PlaybackHandoffMode, PlaybackPresetOptions } from "./PlaybackCommandBuilder";
import { DialogPrimitive } from "./Dialog";
import "./settings-workspace.css";

type SettingsModal = "desktopUpdate" | "playbackInstall" | "advanced" | "about" | "customCss" | null;

interface SettingsWorkspaceProps {
  words: TextDictionary;
  language: Language;
  resolvedTheme: ResolvedTheme;
  uiScale: UiScale;
  customCss: string;
  environment: LocalEnvironmentSettings;
  exportRoot: string;
  archiveRoots: string[];
  converter: ConverterSettings;
  cosmeticConsentAccepted: boolean;
  playback: PlaybackPresetOptions;
  candidates: Cs2InstallCandidate[];
  report: EnvironmentDiagnosticReport | null;
  serverConfigDocument: ServerConfigDocument | null;
  serverConfigDraft: string;
  serverConfigValidation: ServerConfigValidation | null;
  loadingServerConfig: boolean;
  savingServerConfig: boolean;
  detecting: boolean;
  detectionCompleted: boolean;
  inspecting: boolean;
  appVersion: string;
  guiUpdate: GuiUpdateStatus;
  playbackRelease: PlaybackReleaseStatus | null;
  playbackUpdate: PlaybackUpdateStatus;
  playbackReleaseError: string;
  releaseAction: "installingOnline" | "installingFile" | "rollingBack" | null;
  releaseNotice: string;
  onUiScaleChange: (scale: UiScale) => void;
  onCustomCssChange: (css: string) => void;
  onLanguageChange: (language: Language) => void;
  onToggleTheme: () => void;
  onCs2PathChange: (path: string) => void;
  onBrowseCs2: () => void;
  onDetectCs2: () => void;
  onUseCandidate: (candidate: Cs2InstallCandidate) => void;
  onInspectEnvironment: () => void;
  onCheckGuiUpdate: () => void;
  onInstallGuiUpdate: () => void;
  onCheckPlaybackUpdate: () => void;
  onInstallLatestPlayback: () => void;
  onInstallPlaybackBundle: () => void;
  onRollbackPlayback: () => void;
  onLoadServerConfig: () => Promise<boolean>;
  onServerConfigDraftChange: (json: string) => void;
  onValidateServerConfig: () => Promise<ServerConfigValidation | null>;
  onSaveServerConfig: () => Promise<boolean>;
  onChooseExportRoot: () => void;
  onAddArchiveRoot: () => void;
  onRemoveArchiveRoot: (root: string) => void;
  onAddDemoRoot: () => void;
  onRemoveDemoRoot: (root: string) => void;
  onOpenPath: (path: string) => void;
  onOpenExternal: (url: string) => void;
  onEnvironmentChange: (patch: Partial<LocalEnvironmentSettings>) => void;
  onConverterChange: (patch: Partial<ConverterSettings>) => void;
  onRequestCosmetics: () => void;
  onPlaybackChange: (patch: Partial<PlaybackPresetOptions>) => void;
}

function SwitchControl({
  checked,
  disabled = false,
  label,
  onChange,
}: {
  checked: boolean;
  disabled?: boolean;
  label: string;
  onChange: (checked: boolean) => void;
}) {
  return (
    <button
      className="switch-control"
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
    >
      <span />
    </button>
  );
}

function StatusMark({ status }: { status: EnvironmentCheckStatus }) {
  if (status === "pass") return <CheckIcon size={14} />;
  if (status === "warning" || status === "error") return <AlertIcon size={14} />;
  return <span aria-hidden="true">—</span>;
}

function statusLabel(words: TextDictionary, status: EnvironmentCheckStatus): string {
  if (status === "pass") return words.diagnosticStatusPass;
  if (status === "warning") return words.diagnosticStatusWarning;
  if (status === "error") return words.diagnosticStatusError;
  if (status === "notApplicable") return words.diagnosticStatusNotApplicable;
  return words.diagnosticStatusUnverified;
}

function overallCopy(words: TextDictionary, status: EnvironmentOverallStatus) {
  if (status === "pass") return [words.environmentReadyTitle, words.environmentReadyBody] as const;
  if (status === "warning") return [words.environmentWarningTitle, words.environmentWarningBody] as const;
  if (status === "error") return [words.environmentErrorTitle, words.environmentErrorBody] as const;
  return [words.environmentUnverifiedTitle, words.environmentUnverifiedBody] as const;
}

function pluginClassification(words: TextDictionary, classification: EnvironmentPluginClassification): string {
  if (classification === "demotracer") return words.pluginClassDemoTracer;
  if (classification === "dependency") return words.pluginClassDependency;
  if (classification === "potentialConflict") return words.pluginClassPotentialConflict;
  return words.pluginClassUnknown;
}

function pluginRuntimeState(words: TextDictionary, state: "loaded" | "notLoaded" | "unknown"): string {
  if (state === "loaded") return words.runtimePluginLoaded;
  if (state === "notLoaded") return words.runtimePluginNotLoaded;
  return words.runtimePluginUnknown;
}

function diagnosticGroupLabel(words: TextDictionary, group: string): string {
  if (group === "cs2") return "CS2";
  if (group === "dependencies") return words.diagnosticGroupDependencies;
  if (group === "demotracer") return "DemoTracer";
  if (group === "plugins") return words.diagnosticGroupPlugins;
  if (group === "compatibility") return words.diagnosticGroupCompatibility;
  if (group === "runtime") return words.diagnosticGroupRuntime;
  return group;
}

function confidenceLabel(words: TextDictionary, confidence: string): string {
  if (confidence === "high" || confidence === "certain") return words.confidenceHigh;
  if (confidence === "medium") return words.confidenceMedium;
  if (confidence === "low") return words.confidenceLow;
  return confidence;
}

function SettingLine({
  title,
  description,
  tone,
  checked,
  disabled,
  onChange,
}: {
  title: string;
  description?: string;
  tone?: "warning";
  checked: boolean;
  disabled?: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <div className={`settings-toggle-line${disabled ? " is-disabled" : ""}${tone === "warning" ? " is-warning" : ""}`}>
      <div>
        <strong>{title}</strong>
        {description ? <small>{description}</small> : null}
      </div>
      <SwitchControl checked={checked} disabled={disabled} label={title} onChange={onChange} />
    </div>
  );
}

function SettingSelectLine({
  title,
  description,
  value,
  children,
  onChange,
}: {
  title: string;
  description?: string;
  value: string;
  children: ReactNode;
  onChange: (value: string) => void;
}) {
  return (
    <label className="settings-select-line">
      <span><strong>{title}</strong>{description ? <small>{description}</small> : null}</span>
      <select value={value} onChange={(event) => onChange(event.target.value)}>{children}</select>
    </label>
  );
}

function SettingsSubpageRow({
  title,
  status,
  onClick,
}: {
  title: string;
  status?: string;
  onClick: () => void;
}) {
  return (
    <button className="settings-subpage-row" type="button" onClick={onClick}>
      <span><strong>{title}</strong></span>
      {status ? <em>{status}</em> : null}
      <ChevronIcon size={15} />
    </button>
  );
}

function PathRow({
  path,
  badge,
  removeLabel,
  openLabel,
  removable,
  onOpen,
  onRemove,
}: {
  path: string;
  badge?: string;
  removeLabel: string;
  openLabel: string;
  removable: boolean;
  onOpen: () => void;
  onRemove: () => void;
}) {
  return (
    <div className="settings-path-row">
      <button className="settings-path-open-target" type="button" onClick={onOpen} aria-label={`${openLabel}: ${path}`} title={path}>
        <FolderIcon size={16} />
        <code>{path}</code>
        {badge ? <span>{badge}</span> : null}
      </button>
      {removable ? (
        <button className="text-button" type="button" onClick={onRemove}>{removeLabel}</button>
      ) : null}
    </div>
  );
}

export function SettingsWorkspace({
  words,
  language,
  resolvedTheme,
  uiScale,
  customCss,
  environment,
  exportRoot,
  archiveRoots,
  converter,
  cosmeticConsentAccepted,
  playback,
  candidates,
  report,
  serverConfigDocument,
  serverConfigDraft,
  serverConfigValidation,
  loadingServerConfig,
  savingServerConfig,
  detecting,
  detectionCompleted,
  inspecting,
  appVersion,
  guiUpdate,
  playbackRelease,
  playbackUpdate,
  playbackReleaseError,
  releaseAction,
  releaseNotice,
  onUiScaleChange,
  onCustomCssChange,
  onLanguageChange,
  onToggleTheme,
  onCs2PathChange,
  onBrowseCs2,
  onDetectCs2,
  onUseCandidate,
  onInspectEnvironment,
  onCheckGuiUpdate,
  onInstallGuiUpdate,
  onCheckPlaybackUpdate,
  onInstallLatestPlayback,
  onInstallPlaybackBundle,
  onRollbackPlayback,
  onLoadServerConfig,
  onServerConfigDraftChange,
  onValidateServerConfig,
  onSaveServerConfig,
  onChooseExportRoot,
  onAddArchiveRoot,
  onRemoveArchiveRoot,
  onAddDemoRoot,
  onRemoveDemoRoot,
  onOpenPath,
  onOpenExternal,
  onEnvironmentChange,
  onConverterChange,
  onRequestCosmetics,
  onPlaybackChange,
}: SettingsWorkspaceProps) {
  const [settingsModal, setSettingsModal] = useState<SettingsModal>(null);
  const [customCssDraft, setCustomCssDraft] = useState(customCss);
  const [serverGuideQuery, setServerGuideQuery] = useState("");
  const [validatingServerConfig, setValidatingServerConfig] = useState(false);
  const [serverConfigFeedback, setServerConfigFeedback] = useState<{ tone: "progress" | "success" | "error"; message: string } | null>(null);
  const autoLoadedConfigPath = useRef("");
  const reportCopy = report ? overallCopy(words, report.overall) : null;
  const defaultRootKey = exportRoot.replace(/\\/g, "/").toLocaleLowerCase();
  const normalizedGuideQuery = serverGuideQuery.trim().toLocaleLowerCase();
  const serverGuideGroups = useMemo(() => {
    const groups = new Map<ServerConfigGuideGroup, Array<(typeof SERVER_CONFIG_GUIDE)[number]>>();
    for (const field of SERVER_CONFIG_GUIDE) {
      const searchText = `${field.path} ${field.description[language]} ${field.accepted?.join(" ") ?? ""}`.toLocaleLowerCase();
      if (normalizedGuideQuery && !searchText.includes(normalizedGuideQuery)) continue;
      const fields = groups.get(field.group) ?? [];
      fields.push(field);
      groups.set(field.group, fields);
    }
    return groups;
  }, [language, normalizedGuideQuery]);

  const handleLoadServerConfig = async () => {
    setServerConfigFeedback({ tone: "progress", message: words.loadingServerConfig });
    const succeeded = await onLoadServerConfig();
    setServerConfigFeedback({
      tone: succeeded ? "success" : "error",
      message: succeeded ? words.serverConfigLoadSucceeded : words.serverConfigLoadFailed,
    });
  };

  const handleValidateServerConfig = async () => {
    setValidatingServerConfig(true);
    setServerConfigFeedback({ tone: "progress", message: words.validatingServerConfig });
    const validation = await onValidateServerConfig();
    setValidatingServerConfig(false);
    setServerConfigFeedback({
      tone: validation ? (validation.valid ? "success" : "error") : "error",
      message: validation ? (validation.valid ? words.serverConfigValid : words.serverConfigInvalid) : words.serverConfigValidationFailed,
    });
  };

  const handleSaveServerConfig = async () => {
    setServerConfigFeedback({ tone: "progress", message: words.savingServerConfig });
    const succeeded = await onSaveServerConfig();
    setServerConfigFeedback({
      tone: succeeded ? "success" : "error",
      message: succeeded ? words.serverConfigSaveSucceeded : words.serverConfigSaveFailed,
    });
  };

  useEffect(() => {
    const path = environment.cs2Path.trim();
    if (settingsModal !== "advanced" || !path || serverConfigDocument || loadingServerConfig) return;
    if (autoLoadedConfigPath.current === path) return;
    autoLoadedConfigPath.current = path;
    void handleLoadServerConfig();
  }, [environment.cs2Path, loadingServerConfig, onLoadServerConfig, serverConfigDocument, settingsModal]);

  const serverGuideGroupLabel = (group: ServerConfigGuideGroup): string => {
    if (group === "general") return words.serverConfigGroupGeneral;
    if (group === "handoff") return words.serverConfigGroupHandoff;
    if (group === "fidelity") return words.serverConfigGroupFidelity;
    if (group === "match") return words.serverConfigGroupMatch;
    return words.serverConfigGroupCosmetics;
  };

  const appearanceView = (
    <div className="settings-pane settings-appearance-pane">
      <section className="settings-card settings-form-card" aria-label={words.settingsNavAppearance}>
        <div className="settings-choice-row">
          <div><strong>{words.language}</strong></div>
          <div className="segmented-control" role="group" aria-label={words.language}>
            {(["zh", "en"] as const).map((option) => (
              <button className={language === option ? "is-selected" : ""} type="button" aria-pressed={language === option} key={option} onClick={() => onLanguageChange(option)}>
                {LANGUAGE_OPTIONS[option].label}
              </button>
            ))}
          </div>
        </div>
        <div className="settings-choice-row">
          <div><strong>{words.theme}</strong></div>
          <div className="segmented-control" role="group" aria-label={words.theme}>
            <button className={resolvedTheme === "light" ? "is-selected" : ""} type="button" aria-pressed={resolvedTheme === "light"} onClick={resolvedTheme === "light" ? undefined : onToggleTheme}>{words.lightTheme}</button>
            <button className={resolvedTheme === "dark" ? "is-selected" : ""} type="button" aria-pressed={resolvedTheme === "dark"} onClick={resolvedTheme === "dark" ? undefined : onToggleTheme}>{words.darkTheme}</button>
          </div>
        </div>
        <div className="settings-choice-row">
          <div><strong>{words.uiScale}</strong></div>
          <div className="segmented-control" role="group" aria-label={words.uiScale}>
            {([1, 1.1] as const).map((scale) => (
              <button
                className={uiScale === scale ? "is-selected" : ""}
                type="button"
                aria-pressed={uiScale === scale}
                key={scale}
                onClick={() => onUiScaleChange(scale)}
              >
                {scale === 1 ? words.uiScaleStandard : words.uiScaleLarge}
              </button>
            ))}
          </div>
        </div>
        <SettingLine
          title={words.soundNotifications}
          checked={environment.soundNotifications}
          onChange={(soundNotifications) => onEnvironmentChange({ soundNotifications })}
        />
        <button
          className="settings-subpage-row"
          type="button"
          onClick={() => {
            setCustomCssDraft(customCss);
            setSettingsModal("customCss");
          }}
        >
          <span><strong>{words.customCssTitle}</strong></span>
          <em>{customCss.trim() ? words.customCssConfigured : words.customCssNotConfigured}</em>
          <ChevronIcon size={15} />
        </button>
      </section>
    </div>
  );

  const environmentView = (
    <div className="settings-pane settings-environment-pane">
      <section className="settings-card cs2-location-card" aria-label={words.cs2Location}>
        <div className="settings-environment-path-row">
          <strong>{words.cs2Location}</strong>
          <div className="settings-path-input">
            <input
              value={environment.cs2Path}
              disabled={detecting || inspecting}
              spellCheck={false}
              placeholder={words.cs2PathPlaceholder}
              aria-label={words.cs2Location}
              onChange={(event) => onCs2PathChange(event.target.value)}
            />
            <button className="secondary-button" type="button" disabled={detecting || inspecting} onClick={onBrowseCs2}>
              <FolderIcon size={15} />{words.browseFolder}
            </button>
          </div>
        </div>
        {candidates.length > 0 ? (
          <div className="detected-install-list">
            <div className="detected-install-heading">
              <strong>{words.detectedCs2Installs}</strong>
              <small>{words.detectedCs2InstallsHelp}</small>
            </div>
            {candidates.map((candidate) => (
              <button
                className="detected-install-option"
                key={`${candidate.source}:${candidate.gameCsgoPath}`}
                type="button"
                disabled={detecting || inspecting}
                onClick={() => onUseCandidate(candidate)}
              >
                <span><FolderIcon size={16} /></span>
                <span>
                  <strong>{candidate.label}</strong>
                  <code>{candidate.path}</code>
                </span>
                <small>{candidate.source}</small>
                <b>{words.useDetectedInstall}</b>
              </button>
            ))}
          </div>
        ) : detectionCompleted && !detecting ? (
          <div className="detected-install-empty">
            <strong>{words.noDetectedCs2Title}</strong>
            <small>{words.noDetectedCs2Help}</small>
          </div>
        ) : null}
        <div className="settings-card-actions">
          <button className="secondary-button" type="button" disabled={detecting || inspecting} onClick={onDetectCs2}>
            <SearchIcon size={16} />{detecting ? words.detectingCs2 : words.autoDetectCs2}
          </button>
          <button className="primary-button" type="button" disabled={!environment.cs2Path.trim() || detecting || inspecting} onClick={onInspectEnvironment}>
            <RefreshIcon size={16} />{inspecting ? words.inspectingEnvironment : words.inspectEnvironment}
          </button>
        </div>
      </section>

      {report ? (
        <>
          <details className={`diagnostic-detail-bundle is-${report.overall}`}>
            <summary>
              <span className="diagnostic-detail-mark"><StatusMark status={report.overall} /></span>
              <span className="diagnostic-detail-title">
                <strong>{reportCopy?.[0]}</strong>
                {report.cached ? <small>{words.cachedDiagnosticBadge}</small> : null}
              </span>
              <b>{words.environmentDetailCount
                .replace("{checks}", String(report.checks.length))
                .replace("{plugins}", String(report.plugins.length))}</b>
              <ChevronIcon size={15} />
            </summary>
            <div className="diagnostic-detail-content">
          <section className="settings-card install-receipt" aria-labelledby="install-receipt-title">
            <div className="settings-card-heading">
              <div>
                <h3 id="install-receipt-title">{words.installReceiptTitle}</h3>
                <p>{words.installReceiptHelp}</p>
              </div>
              <span className={`count-badge${report.receipt.found && report.receipt.verified ? "" : " is-warning"}`}>
                {!report.receipt.found
                  ? words.installReceiptMissing
                  : report.receipt.verified
                    ? words.installReceiptVerified
                    : words.installReceiptUnverified}
              </span>
            </div>
            <div className="receipt-contract-grid">
              <div>
                <span>{words.bundleVersionLabel}</span>
                <strong>{report.receipt.bundleVersion ?? "—"}</strong>
              </div>
              <div>
                <span>{words.nativeContractLabel}</span>
                <strong>{report.receipt.botControllerAbi == null
                  ? "—"
                  : `ABI ${report.receipt.botControllerAbi}.${report.receipt.botControllerMinor ?? "?"}`}</strong>
              </div>
              <div>
                <span>{words.apiContractLabel}</span>
                <strong>{report.receipt.botHiderApi == null && report.receipt.demoTracerApi == null
                  ? "—"
                  : `BotHider ${report.receipt.botHiderApi ?? "?"} · DemoTracer ${report.receipt.demoTracerApi ?? "?"}`}</strong>
              </div>
              <div>
                <span>{words.receiptFilesLabel}</span>
                <strong>{words.receiptFilesValue
                  .replace("{checked}", String(report.receipt.filesChecked))
                  .replace("{mismatched}", String(report.receipt.filesMismatched))}</strong>
              </div>
            </div>
            {report.receipt.path ? <code className="receipt-path">{report.receipt.path}</code> : null}
          </section>

          {report.conflicts.length > 0 ? (
            <section className="settings-card diagnostic-conflicts" aria-labelledby="diagnostic-conflicts-title">
              <div className="settings-card-heading">
                <div>
                  <h3 id="diagnostic-conflicts-title">{words.conflictsTitle}</h3>
                  <p>{words.conflictsHelp}</p>
                </div>
                <span className="count-badge is-warning">{report.conflicts.length}</span>
              </div>
              <div className="conflict-list">
                {report.conflicts.map((conflict) => (
                  <article className={`conflict-item is-${conflict.severity}`} key={`${conflict.ruleId}:${conflict.evidencePath}`}>
                    <span><AlertIcon size={16} /></span>
                    <div>
                      <div><strong>{conflict.title}</strong><small>{confidenceLabel(words, conflict.confidence)}</small></div>
                      <p>{conflict.summary}</p>
                      {conflict.evidencePath ? <code>{conflict.evidencePath}</code> : null}
                      {conflict.affectedFeatures.length > 0 ? (
                        <footer>{conflict.affectedFeatures.map((feature) => <span key={feature}>{feature}</span>)}</footer>
                      ) : null}
                    </div>
                  </article>
                ))}
              </div>
            </section>
          ) : null}

          <section className="settings-card diagnostic-checks" aria-labelledby="diagnostic-checks-title">
            <div className="settings-card-heading">
              <div>
                <h3 id="diagnostic-checks-title">{words.diagnosticChecks}</h3>
                <p>{words.diagnosticChecksHelp}</p>
              </div>
              <span className="count-badge">{report.checks.length}</span>
            </div>
            <div className="diagnostic-check-list">
              {report.checks.map((check) => (
                <details className={`diagnostic-check is-${check.status}`} key={check.id}>
                  <summary>
                    <span className="diagnostic-check-mark"><StatusMark status={check.status} /></span>
                    <span>
                      <strong>{check.title}</strong>
                      <small>{check.summary}</small>
                    </span>
                    <b>{diagnosticGroupLabel(words, check.group)}</b>
                    <em>{statusLabel(words, check.status)}</em>
                  </summary>
                  {(check.expected || check.actual || check.evidencePath || check.action) ? (
                    <div className="diagnostic-check-detail">
                      {check.expected ? <div><span>{words.expectedValue}</span><code>{check.expected}</code></div> : null}
                      {check.actual ? <div><span>{words.actualValue}</span><code>{check.actual}</code></div> : null}
                      {check.evidencePath ? <div><span>{words.evidencePath}</span><code>{check.evidencePath}</code></div> : null}
                      {check.action ? <p><strong>{words.suggestedAction}</strong>{check.action}</p> : null}
                    </div>
                  ) : null}
                </details>
              ))}
            </div>
          </section>

          <section className="settings-card plugin-inventory" aria-labelledby="plugin-inventory-title">
            <div className="settings-card-heading">
              <div>
                <h3 id="plugin-inventory-title">{words.pluginInventory}</h3>
                <p>{words.pluginInventoryHelp}</p>
              </div>
              <span className="count-badge">{report.plugins.length}</span>
            </div>
            {report.plugins.length > 0 ? (
              <div className="plugin-list">
                {report.plugins.map((plugin) => (
                  <div className={`plugin-row is-${plugin.classification}`} key={`${plugin.directory}:${plugin.name}`}>
                    <span><LibraryIcon size={15} /></span>
                    <div>
                      <strong>{plugin.name}</strong>
                      <code>{plugin.directory}</code>
                    </div>
                    <small title={plugin.assemblyFiles.join("\n")}>
                      {words.assemblyCount.replace("{count}", String(plugin.assemblyFiles.length))} · {pluginRuntimeState(words, plugin.runtimeState)}
                    </small>
                    <b>{pluginClassification(words, plugin.classification)}</b>
                  </div>
                ))}
              </div>
            ) : <p className="settings-empty-list">{words.noCssPluginsFound}</p>}
          </section>
            </div>
          </details>
        </>
      ) : null}
    </div>
  );

  const releaseBusy = releaseAction !== null;
  const playbackUpdateBusy = releaseBusy || playbackUpdate.phase === "checking";
  const playbackUpdateLabel = playbackUpdate.phase === "checking" ? words.releaseChecking
    : playbackUpdate.phase === "current" ? words.releaseUpToDate
      : playbackUpdate.phase === "available" ? words.releaseUpdateAvailable
        : playbackUpdate.phase === "unavailable" ? words.releasePlaybackUnavailable
          : playbackUpdate.phase === "error" ? words.releaseCheckUnavailable
            : words.releaseNotChecked;
  const guiUpdateBusy = guiUpdate.phase === "checking"
    || guiUpdate.phase === "downloading"
    || guiUpdate.phase === "installing";
  const guiStatus = guiUpdate.phase === "checking" ? words.releaseChecking
    : guiUpdate.phase === "current" ? words.releaseUpToDate
      : guiUpdate.phase === "available" ? words.releaseUpdateAvailable
        : guiUpdate.phase === "downloading" ? words.releaseDownloading
          : guiUpdate.phase === "installing" ? words.releaseInstalling
            : guiUpdate.phase === "error" ? words.releaseCheckUnavailable
              : words.releaseNotChecked;
  const guiReleaseNotes = releaseNotesForLanguage(guiUpdate.notes, language);
  const guiProgressPercent = guiUpdate.totalBytes && guiUpdate.downloadedBytes != null
    ? Math.min(100, Math.round((guiUpdate.downloadedBytes / guiUpdate.totalBytes) * 100))
    : null;
  const desktopUpdateView = (
    <div className="settings-pane release-manager-pane">
      {releaseNotice ? <div className="release-notice" role="status"><CheckIcon size={16} /><span>{releaseNotice}</span></div> : null}

      <section
        className={`settings-card release-card desktop-release-card is-${guiUpdate.phase}`}
        data-update-phase={guiUpdate.phase}
        aria-labelledby="desktop-release-title"
      >
        <div className="release-product-hero">
          <span className="release-product-mark" aria-hidden="true"><TraceMark size={27} /></span>
          <div className="release-product-copy">
            <span>{words.releaseDesktopApp}</span>
            <h3 id="desktop-release-title">DemoTracer <code>v{guiUpdate.currentVersion || appVersion || playbackRelease?.appVersion || "1.0.0"}</code></h3>
            <p>{words.releaseAutomaticUpdates}</p>
          </div>
          <span className={`release-status-pill is-${guiUpdate.phase}`} role="status">
            <i aria-hidden="true" />{guiStatus}
          </span>
        </div>

        <div className="release-version-route" aria-label={words.releaseUpdateStatus}>
          <div><span>{words.releaseCurrentVersion}</span><strong>v{guiUpdate.currentVersion || appVersion || "—"}</strong></div>
          <span className="release-version-arrow" aria-hidden="true"><ArrowIcon size={17} /></span>
          <div><span>{words.releaseLatestVersion}</span><strong>{guiUpdate.availableVersion ? `v${guiUpdate.availableVersion}` : "—"}</strong></div>
        </div>

        {guiReleaseNotes ? (
          <section className="release-notes-panel" aria-label={words.releaseUpdateNotes}>
            <strong>{words.releaseUpdateNotes}</strong>
            <p>{guiReleaseNotes}</p>
          </section>
        ) : null}
        {guiUpdate.phase === "downloading" || guiUpdate.phase === "installing" ? (
          <div className="release-download-feedback" role="status" aria-live="polite">
            <div>
              <span>{guiUpdate.phase === "installing" ? words.releaseInstalling : words.releaseDownloading}</span>
              <strong>{guiProgressPercent != null ? `${guiProgressPercent}%` : "…"}</strong>
            </div>
            <div className={`release-progress${guiProgressPercent == null ? " is-indeterminate" : ""}`}>
              <span style={{ width: `${guiProgressPercent ?? 36}%` }} />
            </div>
          </div>
        ) : null}
        {guiUpdate.phase === "error" ? <p className="release-error"><AlertIcon size={15} />{words.releaseCheckUnavailable}</p> : null}
        <footer className="release-actions">
          <button className="secondary-button" type="button" disabled={guiUpdateBusy} onClick={onCheckGuiUpdate}>
            <RefreshIcon className={guiUpdate.phase === "checking" ? "release-spin" : undefined} size={15} />
            {guiUpdate.phase === "checking" ? words.releaseChecking : words.releaseCheckNow}
          </button>
          {guiUpdate.phase === "available" ? (
            <button className="primary-button" type="button" onClick={onInstallGuiUpdate}>
              <ReplayIcon size={15} />{words.releaseInstallNow}
            </button>
          ) : (
            <button className="text-button" type="button" onClick={() => onOpenExternal("https://github.com/unicbm/demotracer/releases")}>
              <ExternalLinkIcon size={15} />{words.releaseOpenGithub}
            </button>
          )}
        </footer>
      </section>
    </div>
  );

  const playbackInstallView = (
    <div className="settings-pane release-manager-pane">
      <section className="playback-settings-list" aria-label={words.releasePlayback}>
        {!environment.cs2Path.trim() ? (
          <div className="release-callout"><FolderIcon size={18} /><span>{words.releaseChooseCs2Folder}</span></div>
        ) : (
          <>
            <div className="playback-settings-row is-path">
              <span>{words.releaseCs2Directory}</span>
              <code title={environment.cs2Path}>{environment.cs2Path}</code>
            </div>
            <div className="playback-settings-row">
              <span>{words.releaseInstalledBundle}</span>
              <strong>{playbackRelease?.currentVersion ? `v${playbackRelease.currentVersion}` : words.releaseMissingLegacy}</strong>
            </div>
            <div className={`playback-settings-row${playbackReleaseError ? " has-error" : ""}`}>
              <div>
                <span>{words.releaseLoadedPlugin}</span>
                {playbackReleaseError ? <small>{playbackReleaseError}</small> : null}
              </div>
              <strong>{playbackRelease?.loadedPluginVersion ? `v${playbackRelease.loadedPluginVersion}` : words.releaseNotRunning}</strong>
            </div>
            <div className="playback-settings-row">
              <span>{words.releaseLatestVersion}</span>
              <strong>{playbackUpdate.latestVersion ? `v${playbackUpdate.latestVersion}` : "—"}</strong>
            </div>
            <div className={`playback-settings-row is-action${playbackUpdate.error ? " has-error" : ""}`}>
              <div>
                <span>{words.releaseUpdateStatus}</span>
                {playbackUpdate.error ? <small>{playbackUpdate.error}</small> : null}
              </div>
              <div className="playback-row-action">
                <span className={`release-status-pill is-${playbackUpdate.phase}`} role="status">
                  <i aria-hidden="true" />{playbackUpdateLabel}
                </span>
                <button className="secondary-button" type="button" disabled={playbackUpdateBusy} onClick={onCheckPlaybackUpdate}>
                  <RefreshIcon className={playbackUpdate.phase === "checking" ? "release-spin" : undefined} size={15} />
                  {playbackUpdate.phase === "checking" ? words.releaseChecking : words.releaseCheckNow}
                </button>
              </div>
            </div>
            {playbackUpdate.phase === "available" ? (
              <div className="playback-settings-row is-action">
                <span>{words.releaseInstallLatestPlayback}</span>
                <button className="primary-button" type="button" disabled={playbackUpdateBusy} onClick={onInstallLatestPlayback}>
                  <ReplayIcon size={15} />{releaseAction === "installingOnline" ? words.releaseInstalling : words.releaseInstallNow}
                </button>
              </div>
            ) : null}
            <div className="playback-settings-row is-action">
              <span>{words.releaseLocalPackage}</span>
              <button className="secondary-button" type="button" disabled={releaseBusy} onClick={onInstallPlaybackBundle}>
                <FolderIcon size={15} />{releaseAction === "installingFile" ? words.releaseInstalling : words.releaseInstallFromZip}
              </button>
            </div>
            <div className="playback-settings-row is-action">
              <span>{words.releaseRollback}</span>
              <button className="secondary-button" type="button" disabled={releaseBusy || !playbackRelease?.canRollback} onClick={onRollbackPlayback}>
                {releaseAction === "rollingBack" ? words.releaseRollingBack : words.releaseRollbackAction}
              </button>
            </div>
          </>
        )}
      </section>
    </div>
  );

  const pathsView = (
    <div className="settings-pane settings-paths-pane">
      <section className="settings-card" aria-labelledby="default-output-title">
        <div className="settings-card-heading">
          <div>
            <h3 id="default-output-title">{words.defaultOutputDirectory}</h3>
          </div>
          <button className="secondary-button" type="button" onClick={onChooseExportRoot}>
            <FolderIcon size={15} />{words.changeFolder}
          </button>
        </div>
        {exportRoot ? (
          <button className="primary-path-readout" type="button" onClick={() => onOpenPath(exportRoot)} aria-label={`${words.openFolder}: ${exportRoot}`} title={exportRoot}>
            <FolderIcon size={16} /><code>{exportRoot}</code>
          </button>
        ) : <div className="primary-path-readout is-empty"><code>{words.notSelected}</code></div>}
      </section>

      <section className="settings-card" aria-labelledby="archive-roots-title">
        <div className="settings-card-heading">
          <div>
            <h3 id="archive-roots-title">{words.archiveLibraryDirectories}</h3>
          </div>
          <button className="secondary-button" type="button" onClick={onAddArchiveRoot}>
            <FolderIcon size={15} />{words.addFolder}
          </button>
        </div>
        <div className="settings-path-list">
          {archiveRoots.map((root) => {
            const isDefault = root.replace(/\\/g, "/").toLocaleLowerCase() === defaultRootKey;
            return (
              <PathRow
                key={root}
                path={root}
                 badge={isDefault ? words.defaultExport : undefined}
                 removeLabel={words.removeFolder}
                 openLabel={words.openFolder}
                 removable={!isDefault}
                 onOpen={() => onOpenPath(root)}
                 onRemove={() => onRemoveArchiveRoot(root)}
              />
            );
          })}
        </div>
      </section>

      <section className="settings-card" aria-labelledby="demo-roots-title">
        <div className="settings-card-heading">
          <div>
            <h3 id="demo-roots-title">{words.rawDemoDirectories}</h3>
          </div>
          <button className="secondary-button" type="button" onClick={onAddDemoRoot}>
            <FolderIcon size={15} />{words.addDemoDirectory}
          </button>
        </div>
        {environment.demoRoots.length > 0 ? (
          <div className="settings-path-list">
            {environment.demoRoots.map((root) => (
              <PathRow key={root} path={root} removeLabel={words.removeFolder} openLabel={words.openFolder} removable onOpen={() => onOpenPath(root)} onRemove={() => onRemoveDemoRoot(root)} />
            ))}
          </div>
        ) : <p className="settings-empty-list">{words.noDemoDirectories}</p>}
      </section>

    </div>
  );

  const exportView = (
    <div className="settings-pane settings-export-pane">
      <section className="settings-card settings-form-card">
        <div className="settings-card-heading settings-inline-card-heading"><h3>{words.settingsNavExport}</h3></div>
        <div className="settings-choice-row">
          <div><strong>{words.side}</strong></div>
          <div className="segmented-control" role="group" aria-label={words.side}>
            {(["both", "t", "ct"] as const).map((side) => (
              <button key={side} className={converter.side === side ? "is-selected" : ""} type="button" aria-pressed={converter.side === side} onClick={() => onConverterChange({ side })}>
                {side === "both" ? words.both : side === "t" ? words.t : words.ct}
              </button>
            ))}
          </div>
        </div>

        <div className="settings-choice-row">
          <div><strong>{words.playbackRange}</strong></div>
          <div className="segmented-control" role="group" aria-label={words.playbackRange}>
            <button className={!converter.fullRound ? "is-selected" : ""} type="button" aria-pressed={!converter.fullRound} onClick={() => onConverterChange({ fullRound: false })}>{words.cutBeforePlant}</button>
            <button className={converter.fullRound ? "is-selected" : ""} type="button" aria-pressed={converter.fullRound} onClick={() => onConverterChange({ fullRound: true })}>{words.fullRoundLabel}</button>
          </div>
        </div>

        <SettingLine title={words.exportVoice} checked={converter.exportVoice} onChange={(exportVoice) => onConverterChange({ exportVoice })} />

        <SettingLine
          title={words.exportCosmetics}
          description={cosmeticConsentAccepted ? words.cosmeticDefaultAcceptedHelp : words.cosmeticDefaultHelp}
          tone={cosmeticConsentAccepted ? undefined : "warning"}
          checked={converter.exportCosmetics}
          onChange={(exportCosmetics) => {
            if (exportCosmetics && !cosmeticConsentAccepted) onRequestCosmetics();
            else onConverterChange({ exportCosmetics });
          }}
        />

        {converter.exportCosmetics ? (
          <div className="settings-dependent-options">
            <SettingLine title={words.exportStickers} checked={converter.exportStickers} onChange={(exportStickers) => onConverterChange({ exportStickers })} />
            <SettingLine title={words.exportCharms} checked={converter.exportCharms} onChange={(exportCharms) => onConverterChange({ exportCharms })} />
          </div>
        ) : null}

        <details className="playback-settings-advanced conversion-settings-advanced">
          <summary>
            <strong>{words.compatibilityOptions}</strong>
            <ChevronIcon size={15} />
          </summary>
          <div className="playback-settings-advanced-body">
            <div className="settings-number-row">
              <div><strong>{words.freezePreroll}</strong><small>{words.freezePrerollDefaultHelp}</small></div>
              <label>
                <input
                  type="number"
                  min={0}
                  max={120}
                  step={1}
                  value={converter.freezePrerollSeconds}
                  onChange={(event) => {
                    const value = Number(event.target.value);
                    if (Number.isFinite(value) && value >= 0 && value <= 120) onConverterChange({ freezePrerollSeconds: value });
                  }}
                />
                <span>{words.seconds}</span>
              </label>
            </div>
            <SettingLine title={words.subtickCapture} description={words.subtickCaptureHelp} checked={converter.subtickMode === "auto"} onChange={(enabled) => onConverterChange({ subtickMode: enabled ? "auto" : "off" })} />
            <div className="settings-number-row">
              <div><strong>{words.maxRoundDuration}</strong><small>{words.maxRoundDurationHelp}</small></div>
              <label>
                <input
                  type="number"
                  min={30}
                  max={1800}
                  step={10}
                  value={converter.maxRoundSeconds}
                  onChange={(event) => {
                    const value = Number(event.target.value);
                    if (Number.isFinite(value) && value >= 30 && value <= 1800) onConverterChange({ maxRoundSeconds: value });
                  }}
                />
                <span>{words.seconds}</span>
              </label>
            </div>
          </div>
        </details>
      </section>

    </div>
  );

  const playbackView = (
    <div className="settings-pane settings-playback-pane">
      <section className="settings-card settings-form-card playback-defaults-card">
        <div className="settings-card-heading settings-inline-card-heading"><h3>{words.settingsNavPlayback}</h3></div>
        <SettingLine
          title={words.syncWeapons}
          checked={playback.weapons || playback.cosmetics}
          onChange={(weapons) => onPlaybackChange(weapons ? { weapons: true } : { weapons: false, cosmetics: false })}
        />
        <SettingLine
          title={words.syncSteamIdentity}
          checked={playback.steamIdentity || playback.avatar}
          onChange={(steamIdentity) => onPlaybackChange(steamIdentity ? { steamIdentity: true } : { steamIdentity: false, avatar: false })}
        />
        <SettingLine title={words.syncVoice} checked={playback.voice} onChange={(voice) => onPlaybackChange({ voice })} />
        <SettingLine
          title={words.syncCosmetics}
          description={words.playbackCosmeticsDefaultHelp}
          checked={playback.cosmetics}
          onChange={(cosmetics) => onPlaybackChange(cosmetics ? { cosmetics: true, weapons: true } : { cosmetics: false })}
        />
        <details className="playback-settings-advanced">
          <summary>
            <strong>{words.playbackAdvancedOverrides}</strong>
            <ChevronIcon size={15} />
          </summary>
          <div className="playback-settings-advanced-body">
            <SettingLine
              title={words.syncAvatar}
              description={words.syncAvatarHelp}
              checked={playback.avatar}
              onChange={(avatar) => onPlaybackChange(avatar ? { avatar: true, steamIdentity: true } : { avatar: false })}
            />
            <SettingLine title={words.playoffBeta} description={words.playoffHelp} checked={playback.playoff} onChange={(playoff) => onPlaybackChange({ playoff })} />
            <SettingLine title={words.projectileAlignment} description={words.projectileAlignmentHelp} checked={playback.projectileAlignment === "on"} onChange={(checked) => onPlaybackChange({ projectileAlignment: checked ? "on" : "off" })} />
            <SettingLine title={words.crosshairAlignment} description={words.crosshairAlignmentHelp} checked={playback.crosshairAlignment === "on"} onChange={(checked) => onPlaybackChange({ crosshairAlignment: checked ? "on" : "off" })} />
            <SettingLine title={words.leftHandAlignment} description={words.leftHandAlignmentHelp} checked={playback.leftHandAlignment === "on"} onChange={(checked) => onPlaybackChange({ leftHandAlignment: checked ? "on" : "off" })} />
            <SettingLine title={words.matchPresentation} description={words.matchPresentationHelp} checked={playback.matchPresentation === "scoreboard"} onChange={(checked) => onPlaybackChange({ matchPresentation: checked ? "scoreboard" : "off" })} />
            <SettingLine title={words.partialReplay} description={words.partialReplayHelp} checked={playback.allowPartial === "on"} onChange={(checked) => onPlaybackChange({ allowPartial: checked ? "on" : "off" })} />
            <SettingSelectLine title={words.handoffMode} description={words.handoffModeHelp} value={playback.handoffMode} onChange={(value) => onPlaybackChange({ handoffMode: value as PlaybackHandoffMode })}>
              <option value="death_contact_c4">{words.handoffDeathContactC4}</option>
              <option value="death_or_contact">{words.handoffDeathOrContact}</option>
              <option value="death">{words.handoffDeath}</option>
              <option value="contact">{words.handoffContact}</option>
              <option value="off">{words.disabled}</option>
            </SettingSelectLine>
            <SettingSelectLine title={words.handoffScope} description={words.handoffScopeHelp} value={playback.handoffScope} onChange={(value) => onPlaybackChange({ handoffScope: value as "slot" | "all" })}>
              <option value="slot">{words.handoffScopeSlot}</option><option value="all">{words.handoffScopeAll}</option>
            </SettingSelectLine>
            <SettingLine title={words.threat360} description={words.threat360Help} checked={playback.threat360 === "on"} onChange={(checked) => onPlaybackChange({ threat360: checked ? "on" : "off" })} />
            {playback.threat360 === "on" ? (
              <div className="settings-advanced-inline">
                <label>
                  <span><strong>{words.threat360Range}</strong><small>150–800</small></span>
                  <input
                    type="number"
                    min={150}
                    max={800}
                    step={10}
                    value={playback.threat360Range}
                    onChange={(event) => {
                      const value = Number(event.target.value);
                      if (Number.isFinite(value) && value >= 150 && value <= 800) onPlaybackChange({ threat360Range: value });
                    }}
                  />
                </label>
                <SettingLine title={words.threat360RequireLos} checked={playback.threat360Los} onChange={(threat360Los) => onPlaybackChange({ threat360Los })} />
              </div>
            ) : null}
          </div>
        </details>
      </section>

    </div>
  );

  const effectiveServerValidation = serverConfigValidation ?? serverConfigDocument?.validation ?? null;
  const serverConfigView = (
    <div className="settings-pane server-config-pane">
      <header className="settings-card settings-pane-toolbar">
        <div className="settings-header-actions">
          <button className="secondary-button" type="button" disabled={!environment.cs2Path.trim() || loadingServerConfig || savingServerConfig || validatingServerConfig} onClick={() => void handleLoadServerConfig()}>
            <RefreshIcon size={16} />{loadingServerConfig ? words.loadingServerConfig : words.loadServerConfig}
          </button>
          <button className="secondary-button" type="button" disabled={!serverConfigDraft.trim() || loadingServerConfig || savingServerConfig || validatingServerConfig} onClick={() => void handleValidateServerConfig()}>
            <CheckIcon size={16} />{validatingServerConfig ? words.validatingServerConfig : words.validateServerConfig}
          </button>
          <button className="primary-button" type="button" disabled={!serverConfigDocument || !serverConfigDraft.trim() || loadingServerConfig || savingServerConfig || validatingServerConfig || effectiveServerValidation?.valid === false} onClick={() => void handleSaveServerConfig()}>
            <SlidersIcon size={16} />{savingServerConfig ? words.savingServerConfig : words.saveServerConfig}
          </button>
        </div>
      </header>
      {serverConfigFeedback ? <div className={`server-config-action-feedback is-${serverConfigFeedback.tone}`} role="status" aria-live="polite">{serverConfigFeedback.message}</div> : null}

      {!environment.cs2Path.trim() ? (
        <section className="settings-card diagnostic-empty">
          <span><FolderIcon size={22} /></span>
          <div>
            <h3>{words.serverConfigNeedsPath}</h3>
            <p>{words.serverConfigNeedsPathHelp}</p>
            <button className="secondary-button server-config-choose-path" type="button" onClick={onBrowseCs2}>
              <FolderIcon size={15} />{words.browseFolder}
            </button>
          </div>
        </section>
      ) : !serverConfigDocument ? (
        <section className="settings-card diagnostic-empty">
          <span><SlidersIcon size={22} /></span>
          <div><h3>{words.serverConfigNotLoaded}</h3><p>{words.serverConfigNotLoadedHelp}</p></div>
        </section>
      ) : (
        <>
          <section className="settings-card server-config-editor-card">
            <div className="settings-card-heading">
              <div>
                <h3>{words.serverConfigEditor}</h3>
                <p>{words.serverConfigEditorHelp}</p>
              </div>
              <span className={`count-badge${serverConfigDocument.exists ? "" : " is-warning"}`}>
                {serverConfigDocument.source === "installed"
                  ? words.serverConfigInstalled
                  : serverConfigDocument.source === "example"
                    ? words.serverConfigExample
                    : words.serverConfigBuiltIn}
              </span>
            </div>
            <code className="server-config-path">{serverConfigDocument.configPath}</code>
            <div className="server-config-workbench">
              <textarea
                className="server-config-editor"
                value={serverConfigDraft}
                spellCheck={false}
                aria-label={words.serverConfigEditor}
                onChange={(event) => onServerConfigDraftChange(event.target.value)}
              />
              <aside className="server-config-guide" aria-label={words.serverConfigFieldReference}>
                <header>
                  <div><strong>{words.serverConfigFieldReference}</strong><small>{words.serverConfigFieldReferenceHelp}</small></div>
                  <label>
                    <SearchIcon size={14} />
                    <input value={serverGuideQuery} onChange={(event) => setServerGuideQuery(event.target.value)} placeholder={words.serverConfigSearchFields} />
                  </label>
                </header>
                <div className="server-config-guide-groups">
                  {[...serverGuideGroups.entries()].map(([group, fields]) => (
                    <details key={group} open={Boolean(normalizedGuideQuery) || group === "general"}>
                      <summary><strong>{serverGuideGroupLabel(group)}</strong><span>{fields.length}</span><ChevronIcon size={13} /></summary>
                      <ul>
                        {fields.map((field) => (
                          <li key={field.path}>
                            <div><code>{field.path}</code><span>{field.type === "boolean" ? words.serverConfigTypeBoolean : field.type === "number" ? words.serverConfigTypeNumber : words.serverConfigTypeEnum}</span></div>
                            <p>{field.description[language]}</p>
                            <small>
                              {field.accepted?.length ? <span>{words.serverConfigAllowed}: <code>{field.accepted.join(" · ")}</code></span> : <span>{words.serverConfigAllowed}: <code>true · false · null</code></span>}
                              {field.defaultValue !== undefined ? <span>{words.serverConfigDefault}: <code>{field.defaultValue}</code></span> : null}
                            </small>
                          </li>
                        ))}
                      </ul>
                    </details>
                  ))}
                  {serverGuideGroups.size === 0 ? <p>{words.serverConfigNoMatchingFields}</p> : null}
                </div>
              </aside>
            </div>
          </section>

          {effectiveServerValidation ? (
            <section className={`settings-card server-config-validation is-${effectiveServerValidation.valid ? "valid" : "invalid"}`}>
              <div className="settings-card-heading">
                <div>
                  <h3>{effectiveServerValidation.valid ? words.serverConfigValid : words.serverConfigInvalid}</h3>
                  <p>{words.serverConfigValidationHelp}</p>
                </div>
                <span className={`count-badge${effectiveServerValidation.valid ? "" : " is-warning"}`}>
                  {effectiveServerValidation.errors.length} / {effectiveServerValidation.warnings.length}
                </span>
              </div>
              {[...effectiveServerValidation.errors, ...effectiveServerValidation.warnings].length > 0 ? (
                <ul className="server-config-issues">
                  {[...effectiveServerValidation.errors, ...effectiveServerValidation.warnings].map((issue) => (
                    <li key={`${issue.code}:${issue.path}:${issue.message}`}>
                      <AlertIcon size={15} /><div><code>{issue.path || "$"}</code><span>{words.serverConfigFieldIssue}</span></div>
                    </li>
                  ))}
                </ul>
              ) : <p className="settings-empty-list">{words.serverConfigNoIssues}</p>}
              {effectiveServerValidation.unknownPaths.length > 0 ? (
                <details className="server-config-unknown">
                  <summary>{words.serverConfigUnknownFields.replace("{count}", String(effectiveServerValidation.unknownPaths.length))}</summary>
                  <p>{words.serverConfigUnknownFieldsHelp}</p>
                  <div>{effectiveServerValidation.unknownPaths.map((path) => <code key={path}>{path}</code>)}</div>
                </details>
              ) : null}
            </section>
          ) : null}

          <aside className="safe-defaults-note server-config-reload-note">
            <span><AlertIcon size={17} /></span>
            <div><strong>{words.serverConfigReloadTitle}</strong><p>{words.serverConfigReloadHelp}</p></div>
            <code>{serverConfigDocument.reloadCommand}</code>
          </aside>
        </>
      )}
    </div>
  );

  const aboutVersion = appVersion || playbackRelease?.appVersion || "1.0.0";
  const creditedPeople = [
    DEMOTRACER_CREDITS.creator,
    ...DEMOTRACER_CREDITS.contributors,
  ];
  const aboutView = (
    <div className="settings-pane settings-about-pane">
      <header className="settings-pane-header credits-page-header">
        <h2>{words.aboutTitle}</h2>
        <code className="credits-version">v{aboutVersion}</code>
      </header>

      <section className="credits-section" aria-labelledby="credits-contributors-title">
        <header className="credits-section-heading">
          <h3 id="credits-contributors-title">{words.creditsContributorsTitle}</h3>
        </header>
        <div className="credits-list credits-contributor-list">
          {creditedPeople.map((person) => (
            <button
              className="credits-person-row"
              type="button"
              key={person.githubHandle}
              title={`GitHub · ${person.githubHandle}`}
              aria-label={`GitHub: ${person.githubHandle}`}
              onClick={() => onOpenExternal(person.profileUrl)}
            >
              <span className="credits-avatar" aria-hidden="true">
                {person.githubHandle.slice(0, 2).toUpperCase()}
                <img
                  src={person.avatarUrl}
                  alt=""
                  loading="lazy"
                  decoding="async"
                  onError={(event) => { event.currentTarget.hidden = true; }}
                />
              </span>
              <span className="credits-person-identity"><strong>{person.name}</strong><small>@{person.githubHandle}</small></span>
              <span className="credits-contribution">{person.githubHandle === DEMOTRACER_CREDITS.creator.githubHandle ? words.creditsCreatorRole : ""}</span>
              <ExternalLinkIcon className="credits-external-icon" size={14} />
            </button>
          ))}
        </div>
      </section>

      <section className="credits-section" aria-labelledby="credits-foundations-title">
        <header className="credits-section-heading">
          <h3 id="credits-foundations-title">{words.creditsFoundationsTitle}</h3>
        </header>
        <div className="credits-list credits-foundation-list">
          {DEMOTRACER_CREDITS.foundations.map((foundation) => (
            <article className="credits-foundation-row" key={foundation.id}>
              <button
                className="credits-foundation-profile"
                type="button"
                title={`GitHub · ${foundation.githubHandle}`}
                aria-label={`GitHub: ${foundation.githubHandle}`}
                onClick={() => onOpenExternal(foundation.profileUrl)}
              >
                <span className="credits-avatar" aria-hidden="true">
                  {foundation.githubHandle.slice(0, 2).toUpperCase()}
                  <img
                    src={foundation.avatarUrl}
                    alt=""
                    loading="lazy"
                    decoding="async"
                    onError={(event) => { event.currentTarget.hidden = true; }}
                  />
                </span>
                <span><strong>{foundation.author}</strong><small>@{foundation.githubHandle}</small></span>
              </button>
              <div className="credits-project-links">
                {foundation.projects.map((project) => (
                  <button
                    type="button"
                    key={project.repository}
                    title={`GitHub · ${project.repository}`}
                    onClick={() => onOpenExternal(project.url)}
                  >
                    <span>{project.name}</span><ExternalLinkIcon size={11} />
                  </button>
                ))}
              </div>
            </article>
          ))}
        </div>
      </section>
    </div>
  );

  const playbackReleaseStatus = !environment.cs2Path.trim()
    ? words.releaseUnverified
    : playbackRelease?.currentVersion ? `v${playbackRelease.currentVersion}` : words.releaseMissingLegacy;
  const modalTitle = settingsModal === "desktopUpdate" ? words.releaseDesktopApp
    : settingsModal === "playbackInstall" ? words.releasePlayback
    : settingsModal === "advanced" ? words.serverConfigTitle
      : settingsModal === "about" ? words.settingsNavAbout
        : words.customCssTitle;
  const modalContent = settingsModal === "desktopUpdate" ? desktopUpdateView
    : settingsModal === "playbackInstall" ? playbackInstallView
    : settingsModal === "advanced" ? serverConfigView
      : settingsModal === "about" ? aboutView
        : null;

  return (
    <section className="settings-workspace" aria-label={words.settingsTitle}>
      <div className="settings-content">
        <div className="settings-dashboard">
          <div className="settings-dashboard-column">
            <section className="settings-dashboard-panel is-general">
              <header><strong>{words.settingsNavAppearance}</strong></header>
              <div className="settings-dashboard-panel-body">{appearanceView}</div>
            </section>
          </div>
          <div className="settings-dashboard-column">
            <section className="settings-dashboard-panel is-environment">
              <header>
                <strong>{words.settingsNavEnvironment}</strong>
                {guiUpdate.phase === "available" ? <i className="settings-nav-update-dot" title={words.releaseUpdateAvailable} aria-hidden="true" /> : null}
              </header>
              <div className="settings-subpage-list">
                <SettingsSubpageRow title={`${words.releaseDesktopApp} · v${guiUpdate.currentVersion || appVersion || "—"}`} status={guiStatus} onClick={() => setSettingsModal("desktopUpdate")} />
                <SettingsSubpageRow title={words.releasePlayback} status={playbackReleaseStatus} onClick={() => setSettingsModal("playbackInstall")} />
              </div>
            </section>
            <section className="settings-dashboard-panel settings-subpage-list" aria-label={words.settingsSections}>
              <SettingsSubpageRow title={words.settingsNavServerConfig} onClick={() => setSettingsModal("advanced")} />
              <SettingsSubpageRow title={words.settingsNavAbout} onClick={() => setSettingsModal("about")} />
            </section>
          </div>
          <div className="settings-dashboard-inline settings-dashboard-environment">{environmentView}</div>
          <div className="settings-dashboard-inline settings-dashboard-storage">{pathsView}</div>
          <div className="settings-dashboard-inline">{exportView}</div>
          <div className="settings-dashboard-inline">{playbackView}</div>
        </div>
      </div>

      {settingsModal && settingsModal !== "customCss" ? (
        <DialogPrimitive labelledBy="settings-modal-title" onDismiss={() => setSettingsModal(null)} className={`dialog-surface settings-modal is-${settingsModal}`}>
          <header className="settings-modal-header">
            <h2 id="settings-modal-title">{modalTitle}</h2>
            <button className="icon-button" type="button" onClick={() => setSettingsModal(null)} aria-label={words.close} title={words.close}><CloseIcon size={16} /></button>
          </header>
          <div className="settings-modal-body">{modalContent}</div>
          {settingsModal === "playbackInstall" ? null : (
            <footer className="settings-modal-footer"><button className="secondary-button" type="button" onClick={() => setSettingsModal(null)}>{words.close}</button></footer>
          )}
        </DialogPrimitive>
      ) : null}

      {settingsModal === "customCss" ? (
        <DialogPrimitive labelledBy="custom-css-modal-title" onDismiss={() => setSettingsModal(null)} className="dialog-surface settings-modal settings-css-modal">
          <header className="settings-modal-header">
            <h2 id="custom-css-modal-title">{words.customCssTitle}</h2>
            <button className="icon-button" type="button" onClick={() => setSettingsModal(null)} aria-label={words.close} title={words.close}><CloseIcon size={16} /></button>
          </header>
          <div className="settings-css-editor">
            <p>{words.customCssHelp}</p>
            <textarea value={customCssDraft} spellCheck={false} maxLength={65_536} placeholder={words.customCssPlaceholder} onChange={(event) => setCustomCssDraft(event.target.value)} />
          </div>
          <footer className="settings-modal-footer">
            <button className="text-button" type="button" onClick={() => setCustomCssDraft("")}>{words.customCssReset}</button>
            <span />
            <button className="secondary-button" type="button" onClick={() => setSettingsModal(null)}>{words.cancel}</button>
            <button className="primary-button" type="button" onClick={() => { onCustomCssChange(customCssDraft); setSettingsModal(null); }}>{words.customCssSave}</button>
          </footer>
        </DialogPrimitive>
      ) : null}
    </section>
  );
}
