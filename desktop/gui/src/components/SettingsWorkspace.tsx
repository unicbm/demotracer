/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useEffect, useMemo, useRef, useState } from "react";
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
import {
  isThemeColor,
  isThemeFontFamily,
  themePalette,
  UI_FONT_SIZE_MAX,
  UI_FONT_SIZE_MIN,
  type CustomCssProfile,
  type ResolvedTheme,
  type ThemeCustomization,
  type ThemePalette,
} from "../appearance";
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
  PlaybackInstallProgress,
  PlaybackReleaseStatus,
  PlaybackUpdateStatus,
  ServerConfigDocument,
  ServerConfigValidation,
  Theme,
} from "../types";
import { releaseNotesForLanguage } from "../releaseNotes";
import { SERVER_CONFIG_GUIDE, type ServerConfigGuideGroup } from "../serverConfigGuide";
import type { PlaybackHandoffMode, PlaybackPresetOptions } from "./PlaybackCommandBuilder";
import { DialogPrimitive } from "./Dialog";
import { SelectControl, type SelectControlOption } from "./SelectControl";
import { SwitchControl } from "./SwitchControl";
import "./settings-workspace.css";

type SettingsModal =
  | "desktopUpdate"
  | "playbackInstall"
  | "environment"
  | "storage"
  | "conversion"
  | "playback"
  | "serverConfig"
  | "about"
  | "theme"
  | "customCss"
  | null;

type ThemeColorKey = keyof ThemePalette;

interface ThemeEditorDraft extends ThemePalette {
  fontFamily: string;
  monoFontFamily: string;
}

const THEME_COLOR_KEYS: readonly ThemeColorKey[] = [
  "primary",
  "secondary",
  "textPrimary",
  "textSecondary",
  "info",
  "warning",
  "danger",
  "success",
];

function themeEditorDraft(customization: ThemeCustomization, theme: ResolvedTheme): ThemeEditorDraft {
  return {
    ...themePalette(customization, theme),
    fontFamily: customization.fontFamily ?? "",
    monoFontFamily: customization.monoFontFamily ?? "",
  };
}

function newCustomCssProfileId(): string {
  if (typeof crypto.randomUUID === "function") return `custom-css-${crypto.randomUUID()}`;
  return `custom-css-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

interface SettingsWorkspaceProps {
  words: TextDictionary;
  language: Language;
  theme: Theme;
  resolvedTheme: ResolvedTheme;
  uiFontSize: number;
  themeCustomization: ThemeCustomization;
  customCssProfiles: readonly CustomCssProfile[];
  activeCustomCssProfileId: string | null;
  environment: LocalEnvironmentSettings;
  aggregateTelemetryEnabled: boolean;
  presenceTelemetryEnabled: boolean;
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
  updateAvailable: boolean;
  playbackRelease: PlaybackReleaseStatus | null;
  playbackUpdate: PlaybackUpdateStatus;
  playbackReleaseError: string;
  releaseAction: "installingOnline" | "installingFile" | "rollingBack" | null;
  playbackInstallProgress: PlaybackInstallProgress | null;
  releaseNotice: string;
  onUiFontSizeChange: (fontSize: number) => void;
  onThemeCustomizationChange: (customization: ThemeCustomization) => void;
  onSaveCustomCssProfile: (profile: CustomCssProfile) => void;
  onActivateCustomCssProfile: (profileId: string | null) => void;
  onDeleteCustomCssProfile: (profileId: string) => void;
  onLanguageChange: (language: Language) => void;
  onThemeChange: (theme: Theme) => void;
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
  onOpenLogDirectory: () => void;
  onOpenExternal: (url: string) => void;
  onEnvironmentChange: (patch: Partial<LocalEnvironmentSettings>) => void;
  onAggregateTelemetryEnabledChange: (enabled: boolean) => void;
  onPresenceTelemetryEnabledChange: (enabled: boolean) => void;
  onConverterChange: (patch: Partial<ConverterSettings>) => void;
  onRequestCosmetics: () => void;
  onPlaybackChange: (patch: Partial<PlaybackPresetOptions>) => void;
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
  options,
  onChange,
}: {
  title: string;
  description?: string;
  value: string;
  options: readonly SelectControlOption[];
  onChange: (value: string) => void;
}) {
  return (
    <div className="settings-select-line">
      <span><strong>{title}</strong>{description ? <small>{description}</small> : null}</span>
      <SelectControl value={value} options={options} label={title} onChange={onChange} />
    </div>
  );
}

function EditableNumberInput({
  value,
  min,
  max,
  step,
  onChange,
}: {
  value: number;
  min: number;
  max: number;
  step: number;
  onChange: (value: number) => void;
}) {
  const [draft, setDraft] = useState(String(value));

  useEffect(() => {
    setDraft(String(value));
  }, [value]);

  const parsedDraft = Number(draft);
  const draftInvalid = draft.trim() !== ""
    && (!Number.isFinite(parsedDraft) || parsedDraft < min || parsedDraft > max);

  const updateDraft = (nextDraft: string) => {
    setDraft(nextDraft);
    if (!nextDraft.trim()) return;
    const nextValue = Number(nextDraft);
    if (Number.isFinite(nextValue) && nextValue >= min && nextValue <= max) onChange(nextValue);
  };

  const finalizeDraft = (nextDraft: string) => {
    if (!nextDraft.trim()) {
      setDraft(String(value));
      return;
    }
    const nextValue = Number(nextDraft);
    if (!Number.isFinite(nextValue)) {
      setDraft(String(value));
      return;
    }
    const validatedValue = Math.min(max, Math.max(min, nextValue));
    setDraft(String(validatedValue));
    if (validatedValue !== value) onChange(validatedValue);
  };

  return (
    <input
      type="number"
      inputMode="decimal"
      min={min}
      max={max}
      step={step}
      value={draft}
      aria-invalid={draftInvalid || undefined}
      onChange={(event) => updateDraft(event.target.value)}
      onBlur={(event) => finalizeDraft(event.currentTarget.value)}
      onKeyDown={(event) => {
        if (event.key === "Enter") event.currentTarget.blur();
      }}
    />
  );
}

function SettingsSubpageRow({
  title,
  status,
  kind = "subpage",
  disabled = false,
  onClick,
}: {
  title: string;
  status?: string;
  kind?: "subpage" | "folder" | "external";
  disabled?: boolean;
  onClick: () => void;
}) {
  return (
    <button className={`settings-subpage-row is-${kind}`} type="button" disabled={disabled} onClick={onClick}>
      <span><strong>{title}</strong></span>
      {status ? <em>{status}</em> : null}
      {kind === "folder" ? <FolderIcon size={14} /> : kind === "external" ? <ExternalLinkIcon size={14} /> : <ChevronIcon size={15} />}
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
  theme,
  resolvedTheme,
  uiFontSize,
  themeCustomization,
  customCssProfiles,
  activeCustomCssProfileId,
  environment,
  aggregateTelemetryEnabled,
  presenceTelemetryEnabled,
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
  updateAvailable,
  playbackRelease,
  playbackUpdate,
  playbackReleaseError,
  releaseAction,
  playbackInstallProgress,
  releaseNotice,
  onUiFontSizeChange,
  onThemeCustomizationChange,
  onSaveCustomCssProfile,
  onActivateCustomCssProfile,
  onDeleteCustomCssProfile,
  onLanguageChange,
  onThemeChange,
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
  onOpenLogDirectory,
  onOpenExternal,
  onEnvironmentChange,
  onAggregateTelemetryEnabledChange,
  onPresenceTelemetryEnabledChange,
  onConverterChange,
  onRequestCosmetics,
  onPlaybackChange,
}: SettingsWorkspaceProps) {
  const [settingsModal, setSettingsModal] = useState<SettingsModal>(null);
  const [themeDraft, setThemeDraft] = useState<ThemeEditorDraft>(() => themeEditorDraft(themeCustomization, resolvedTheme));
  const [customCssDraft, setCustomCssDraft] = useState("");
  const [customCssNameDraft, setCustomCssNameDraft] = useState("");
  const [editingCustomCssProfileId, setEditingCustomCssProfileId] = useState<string | null>(null);
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
    if (settingsModal !== "serverConfig" || !path || serverConfigDocument || loadingServerConfig) return;
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

  const themeColorFields: ReadonlyArray<{ key: ThemeColorKey; label: string }> = [
    { key: "primary", label: words.themePrimaryColor },
    { key: "secondary", label: words.themeSecondaryColor },
    { key: "textPrimary", label: words.themeTextPrimaryColor },
    { key: "textSecondary", label: words.themeTextSecondaryColor },
    { key: "info", label: words.themeInfoColor },
    { key: "warning", label: words.themeWarningColor },
    { key: "danger", label: words.themeErrorColor },
    { key: "success", label: words.themeSuccessColor },
  ];
  const themeDraftValid = THEME_COLOR_KEYS.every((key) => isThemeColor(themeDraft[key]))
    && isThemeFontFamily(themeDraft.fontFamily)
    && isThemeFontFamily(themeDraft.monoFontFamily);
  const customCssProfileLabel = (profile: CustomCssProfile): string => {
    if (profile.id === "starter-hanbaiyu") return words.customCssPresetWhiteJade;
    if (profile.id === "starter-chinese-new-year") return words.customCssPresetChineseNewYear;
    if (profile.id === "starter-black-gold") return words.customCssPresetBlackGold;
    if (profile.id === "starter-ultraviolet") return words.customCssPresetUltraviolet;
    if (profile.id === "starter-monet") return words.customCssPresetMonet;
    return profile.name;
  };
  const activeCustomCssProfile = customCssProfiles.find((profile) => profile.id === activeCustomCssProfileId);
  const themeStatus = activeCustomCssProfile
    ? customCssProfileLabel(activeCustomCssProfile)
    : (Object.keys(themeCustomization).length > 0 ? words.themeCustomized : words.themeDefault);

  const openThemeEditor = () => {
    setThemeDraft(themeEditorDraft(themeCustomization, resolvedTheme));
    setSettingsModal("theme");
  };

  const openCustomCssEditor = (profile?: CustomCssProfile) => {
    setEditingCustomCssProfileId(profile?.id ?? null);
    setCustomCssNameDraft(profile ? customCssProfileLabel(profile) : "");
    setCustomCssDraft(profile?.css ?? "");
    setSettingsModal("customCss");
  };

  const saveCustomCssProfile = () => {
    const name = customCssNameDraft.trim().slice(0, 64);
    if (!name || !customCssDraft.trim()) return;
    onSaveCustomCssProfile({
      id: editingCustomCssProfileId ?? newCustomCssProfileId(),
      name,
      css: customCssDraft,
    });
    setSettingsModal("theme");
  };

  const saveTheme = () => {
    if (!themeDraftValid) return;
    const palette: ThemePalette = {
      primary: themeDraft.primary.trim().toUpperCase(),
      secondary: themeDraft.secondary.trim().toUpperCase(),
      textPrimary: themeDraft.textPrimary.trim().toUpperCase(),
      textSecondary: themeDraft.textSecondary.trim().toUpperCase(),
      info: themeDraft.info.trim().toUpperCase(),
      warning: themeDraft.warning.trim().toUpperCase(),
      danger: themeDraft.danger.trim().toUpperCase(),
      success: themeDraft.success.trim().toUpperCase(),
    };
    const next: ThemeCustomization = { ...themeCustomization, [resolvedTheme]: palette };
    const fontFamily = themeDraft.fontFamily.trim();
    if (fontFamily) next.fontFamily = fontFamily;
    else delete next.fontFamily;
    const monoFontFamily = themeDraft.monoFontFamily.trim();
    if (monoFontFamily) next.monoFontFamily = monoFontFamily;
    else delete next.monoFontFamily;
    onThemeCustomizationChange(next);
    setSettingsModal(null);
  };

  const appearanceView = (
    <div className="settings-pane settings-appearance-pane">
      <section className="settings-card settings-form-card" aria-label={words.settingsNavAppearance}>
        <SettingSelectLine
          title={words.language}
          value={language}
          options={(["zh", "en"] as const).map((option) => ({ value: option, label: LANGUAGE_OPTIONS[option].label }))}
          onChange={(value) => onLanguageChange(value as Language)}
        />
        <div className="settings-choice-row">
          <div><strong>{words.theme}</strong></div>
          <div className="segmented-control" role="group" aria-label={words.theme}>
            {(["light", "dark", "system"] as const).map((option) => (
              <button
                className={theme === option ? "is-selected" : ""}
                type="button"
                aria-pressed={theme === option}
                key={option}
                onClick={() => onThemeChange(option)}
              >
                {option === "light" ? words.lightTheme : option === "dark" ? words.darkTheme : words.systemTheme}
              </button>
            ))}
          </div>
        </div>
        <div className="settings-number-row">
          <div><strong>{words.uiFontSize}</strong><small>{words.uiFontSizeHelp}</small></div>
          <label>
            <EditableNumberInput
              value={uiFontSize}
              min={UI_FONT_SIZE_MIN}
              max={UI_FONT_SIZE_MAX}
              step={1}
              onChange={onUiFontSizeChange}
            />
            <em>px</em>
          </label>
        </div>
        <SettingLine
          title={words.soundNotifications}
          checked={environment.soundNotifications}
          onChange={(soundNotifications) => onEnvironmentChange({ soundNotifications })}
        />
        <SettingLine
          title={words.aggregateTelemetry}
          description={words.aggregateTelemetryHelp}
          checked={aggregateTelemetryEnabled}
          onChange={onAggregateTelemetryEnabledChange}
        />
        <SettingLine
          title={words.presenceTelemetry}
          description={words.presenceTelemetryHelp}
          checked={presenceTelemetryEnabled}
          onChange={onPresenceTelemetryEnabledChange}
        />
        <button
          className="settings-subpage-row"
          type="button"
          onClick={openThemeEditor}
        >
          <span><strong>{words.themeSettingsTitle}</strong></span>
          <em>{themeStatus}</em>
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
  const playbackInstallLabel = playbackInstallProgress?.phase === "downloading" ? words.releaseDownloading
    : playbackInstallProgress?.phase === "verifying" ? words.releaseVerifying
      : playbackInstallProgress?.phase === "installing" ? words.releaseInstalling
        : words.releaseChecking;
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
                  <ReplayIcon size={15} />{releaseAction === "installingOnline" ? playbackInstallLabel : words.releaseInstallNow}
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

        <div className="playback-settings-advanced conversion-settings-advanced">
          <div className="playback-settings-advanced-heading">{words.compatibilityOptions}</div>
          <div className="playback-settings-advanced-body">
            <div className="settings-number-row settings-readonly-row">
              <div><strong>{words.freezePreroll}</strong><small>{words.freezePrerollDefaultHelp}</small></div>
              <span className="setting-value-badge">{words.freezePrerollAutoValue}</span>
            </div>
            <SettingLine title={words.subtickCapture} description={words.subtickCaptureHelp} checked={converter.subtickMode === "auto"} onChange={(enabled) => onConverterChange({ subtickMode: enabled ? "auto" : "off" })} />
            <div className="settings-number-row">
              <div><strong>{words.maxRoundDuration}</strong><small>{words.maxRoundDurationHelp}</small></div>
              <label>
                <EditableNumberInput
                  min={30}
                  max={1800}
                  step={10}
                  value={converter.maxRoundSeconds}
                  onChange={(maxRoundSeconds) => onConverterChange({ maxRoundSeconds })}
                />
                <span>{words.seconds}</span>
              </label>
            </div>
          </div>
        </div>
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
        <div className="playback-settings-advanced">
          <div className="playback-settings-advanced-heading">{words.playbackAdvancedOverrides}</div>
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
            <SettingSelectLine
              title={words.handoffMode}
              description={words.handoffModeHelp}
              value={playback.handoffMode}
              options={[
                { value: "death_contact_c4", label: words.handoffDeathContactC4 },
                { value: "death_or_contact", label: words.handoffDeathOrContact },
                { value: "death", label: words.handoffDeath },
                { value: "contact", label: words.handoffContact },
                { value: "off", label: words.disabled },
              ]}
              onChange={(value) => onPlaybackChange({ handoffMode: value as PlaybackHandoffMode })}
            />
            <SettingSelectLine
              title={words.handoffScope}
              description={words.handoffScopeHelp}
              value={playback.handoffScope}
              options={[
                { value: "slot", label: words.handoffScopeSlot },
                { value: "all", label: words.handoffScopeAll },
              ]}
              onChange={(value) => onPlaybackChange({ handoffScope: value as "slot" | "all" })}
            />
            <SettingLine title={words.threat360} description={words.threat360Help} checked={playback.threat360 === "on"} onChange={(checked) => onPlaybackChange({ threat360: checked ? "on" : "off" })} />
            {playback.threat360 === "on" ? (
              <div className="settings-advanced-inline">
                <label>
                  <span><strong>{words.threat360Range}</strong><small>150–800</small></span>
                  <EditableNumberInput
                    min={150}
                    max={800}
                    step={10}
                    value={playback.threat360Range}
                    onChange={(threat360Range) => onPlaybackChange({ threat360Range })}
                  />
                </label>
                <SettingLine title={words.threat360RequireLos} checked={playback.threat360Los} onChange={(threat360Los) => onPlaybackChange({ threat360Los })} />
              </div>
            ) : null}
          </div>
        </div>
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
      <header className="credits-product-hero">
        <TraceMark size={36} />
        <span className="credits-product-copy">
          <strong>{words.appName}</strong>
          <code className="credits-version">v{aboutVersion}</code>
        </span>
      </header>

      <section className="credits-section is-contributors" aria-labelledby="credits-contributors-title">
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
              {person.githubHandle === DEMOTRACER_CREDITS.creator.githubHandle ? <span className="credits-contribution">{words.creditsCreatorRole}</span> : null}
            </button>
          ))}
        </div>
      </section>

      <section className="credits-section is-foundations" aria-labelledby="credits-foundations-title">
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

  const themeView = (
    <div className="settings-theme-form">
      <div className="settings-theme-profile-row">
        <strong>{words.customCssStyles}</strong>
        <SelectControl
          value={activeCustomCssProfileId ?? ""}
          options={[
            { value: "", label: words.customCssDefaultStyle },
            ...customCssProfiles.map((profile) => ({ value: profile.id, label: customCssProfileLabel(profile) })),
          ]}
          label={words.customCssStyles}
          onChange={(profileId) => onActivateCustomCssProfile(profileId || null)}
        />
      </div>
      {themeColorFields.map(({ key, label }) => {
        const color = themeDraft[key];
        const valid = isThemeColor(color);
        return (
          <label className={`settings-theme-color-row${valid ? "" : " is-invalid"}`} key={key}>
            <strong>{label}</strong>
            <span className="settings-theme-color-control">
              <span className="settings-theme-color-swatch" style={{ backgroundColor: valid ? color : "transparent" }}>
                <input
                  type="color"
                  value={valid ? color.slice(0, 7) : "#000000"}
                  aria-label={`${label} · ${words.themeChooseColor}`}
                  onChange={(event) => setThemeDraft((current) => ({ ...current, [key]: event.target.value.toUpperCase() }))}
                />
              </span>
              <input
                className="settings-theme-color-value"
                value={color}
                maxLength={9}
                spellCheck={false}
                aria-label={label}
                aria-invalid={!valid}
                onBlur={() => {
                  if (valid) setThemeDraft((current) => ({ ...current, [key]: current[key].toUpperCase() }));
                }}
                onChange={(event) => setThemeDraft((current) => ({ ...current, [key]: event.target.value }))}
              />
            </span>
          </label>
        );
      })}
      <label className={`settings-theme-font-row${isThemeFontFamily(themeDraft.fontFamily) ? "" : " is-invalid"}`}>
        <strong>{words.themeFontFamily}</strong>
        <input
          value={themeDraft.fontFamily}
          maxLength={200}
          spellCheck={false}
          placeholder={words.themeFontPlaceholder}
          aria-invalid={!isThemeFontFamily(themeDraft.fontFamily)}
          onChange={(event) => setThemeDraft((current) => ({ ...current, fontFamily: event.target.value }))}
        />
      </label>
      <label className={`settings-theme-font-row${isThemeFontFamily(themeDraft.monoFontFamily) ? "" : " is-invalid"}`}>
        <strong>{words.themeMonoFontFamily}</strong>
        <input
          value={themeDraft.monoFontFamily}
          maxLength={200}
          spellCheck={false}
          placeholder={words.themeMonoFontPlaceholder}
          aria-invalid={!isThemeFontFamily(themeDraft.monoFontFamily)}
          onChange={(event) => setThemeDraft((current) => ({ ...current, monoFontFamily: event.target.value }))}
        />
      </label>
      <div className="settings-theme-css-row">
        <strong>{words.themeCssInjection}</strong>
        <span className="settings-theme-css-actions">
          {activeCustomCssProfile ? (
            <button className="secondary-button" type="button" onClick={() => openCustomCssEditor(activeCustomCssProfile)}>
              {words.themeEditCss}
            </button>
          ) : null}
          <button className="secondary-button" type="button" onClick={() => openCustomCssEditor()}>
            {words.customCssCreate}
          </button>
        </span>
      </div>
    </div>
  );

  const compactPathStatus = (path: string) => {
    const normalized = path.trim().replace(/[\\/]+$/, "");
    return normalized.split(/[\\/]/).at(-1) || words.notSelected;
  };
  const playbackReleaseStatus = !environment.cs2Path.trim()
    ? words.releaseUnverified
    : playbackRelease?.currentVersion ? `v${playbackRelease.currentVersion}` : words.releaseMissingLegacy;
  const modalTitle = settingsModal === "desktopUpdate" ? words.releaseDesktopApp
    : settingsModal === "playbackInstall" ? words.releasePlayback
      : settingsModal === "environment" ? words.advancedEnvironmentDiagnostics
        : settingsModal === "storage" ? words.advancedStorage
          : settingsModal === "conversion" ? words.advancedConversion
            : settingsModal === "playback" ? words.advancedPlayback
              : settingsModal === "serverConfig" ? words.serverConfigTitle
                : settingsModal === "about" ? words.settingsNavAbout
                  : settingsModal === "theme" ? words.themeSettingsTitle
                    : words.customCssEditorTitle;
  const modalContent = settingsModal === "desktopUpdate" ? desktopUpdateView
    : settingsModal === "playbackInstall" ? playbackInstallView
      : settingsModal === "environment" ? environmentView
        : settingsModal === "storage" ? pathsView
          : settingsModal === "conversion" ? exportView
            : settingsModal === "playback" ? playbackView
              : settingsModal === "serverConfig" ? serverConfigView
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
            <section className="settings-dashboard-panel">
              <header><strong>CS2</strong></header>
              <div className="settings-subpage-list">
                <SettingsSubpageRow
                  title={words.releaseCs2Directory}
                  status={compactPathStatus(environment.cs2Path)}
                  kind="folder"
                  disabled={!environment.cs2Path.trim()}
                  onClick={() => onOpenPath(environment.cs2Path)}
                />
                <SettingsSubpageRow
                  title={words.advancedEnvironmentDiagnostics}
                  status={reportCopy?.[0] ?? (environment.cs2Path.trim() ? words.releaseUnverified : words.notSelected)}
                  onClick={() => setSettingsModal("environment")}
                />
              </div>
            </section>
            <section className="settings-dashboard-panel">
              <header><strong>{words.settingsNavPaths}</strong></header>
              <div className="settings-subpage-list">
                <SettingsSubpageRow
                  title={words.defaultOutputDirectory}
                  status={compactPathStatus(exportRoot)}
                  kind="folder"
                  disabled={!exportRoot.trim()}
                  onClick={() => onOpenPath(exportRoot)}
                />
                <SettingsSubpageRow
                  title={words.archiveLibraryDirectories}
                  status={words.advancedConfiguredFolders.replace("{count}", String(archiveRoots.length))}
                  onClick={() => setSettingsModal("storage")}
                />
                <SettingsSubpageRow
                  title={words.rawDemoDirectories}
                  status={words.advancedConfiguredFolders.replace("{count}", String(environment.demoRoots.length))}
                  onClick={() => setSettingsModal("storage")}
                />
              </div>
            </section>
          </div>
          <div className="settings-dashboard-column">
            <section className="settings-dashboard-panel is-environment">
              <header>
                <strong>{words.settingsNavEnvironment}</strong>
                {updateAvailable ? <i className="settings-nav-update-dot" title={words.releaseUpdateAvailable} aria-hidden="true" /> : null}
              </header>
              <div className="settings-subpage-list">
                <SettingsSubpageRow title={`${words.releaseDesktopApp} · v${guiUpdate.currentVersion || appVersion || "—"}`} status={guiStatus} onClick={() => setSettingsModal("desktopUpdate")} />
                <SettingsSubpageRow title={words.releasePlayback} status={playbackReleaseStatus} onClick={() => setSettingsModal("playbackInstall")} />
              </div>
            </section>
            <section className="settings-dashboard-panel" aria-label={words.demoTracerAdvancedSettings}>
              <header><strong>{words.demoTracerAdvancedSettings}</strong></header>
              <div className="settings-subpage-list">
                <SettingsSubpageRow title={words.advancedServerConfig} onClick={() => setSettingsModal("serverConfig")} />
                <SettingsSubpageRow title={words.advancedConversion} onClick={() => setSettingsModal("conversion")} />
                <SettingsSubpageRow title={words.advancedPlayback} onClick={() => setSettingsModal("playback")} />
                <SettingsSubpageRow title={words.advancedLogDirectory} kind="folder" onClick={onOpenLogDirectory} />
              </div>
            </section>
            <section className="settings-dashboard-panel" aria-label={words.settingsNavAbout}>
              <header><strong>{words.settingsNavAbout}</strong></header>
              <div className="settings-subpage-list">
                <SettingsSubpageRow title={words.aboutTitle} status={`v${aboutVersion}`} onClick={() => setSettingsModal("about")} />
                <SettingsSubpageRow title="GitHub" kind="external" onClick={() => onOpenExternal("https://github.com/unicbm/demotracer")} />
              </div>
            </section>
          </div>
        </div>
      </div>

      {settingsModal && settingsModal !== "theme" && settingsModal !== "customCss" ? (
        <DialogPrimitive labelledBy="settings-modal-title" onDismiss={() => setSettingsModal(null)} className={`dialog-surface settings-modal is-${settingsModal}${settingsModal === "serverConfig" ? " is-advanced" : ""}`}>
          <header className="settings-modal-header">
            <h2 id="settings-modal-title">{modalTitle}</h2>
            <button className="icon-button" type="button" onClick={() => setSettingsModal(null)} aria-label={words.close} title={words.close}><CloseIcon size={16} /></button>
          </header>
          <div className="settings-modal-body">{modalContent}</div>
          {settingsModal === "playbackInstall" || settingsModal === "about" ? null : (
            <footer className="settings-modal-footer"><button className="secondary-button" type="button" onClick={() => setSettingsModal(null)}>{words.close}</button></footer>
          )}
        </DialogPrimitive>
      ) : null}

      {settingsModal === "theme" ? (
        <DialogPrimitive labelledBy="theme-settings-modal-title" onDismiss={() => setSettingsModal(null)} className="dialog-surface settings-modal settings-theme-modal">
          <header className="settings-modal-header">
            <h2 id="theme-settings-modal-title">{words.themeSettingsTitle}</h2>
            <button className="icon-button" type="button" onClick={() => setSettingsModal(null)} aria-label={words.close} title={words.close}><CloseIcon size={16} /></button>
          </header>
          {themeView}
          <footer className="settings-modal-footer">
            <button className="secondary-button" type="button" onClick={() => setSettingsModal(null)}>{words.cancel}</button>
            <button className="primary-button" type="button" disabled={!themeDraftValid} onClick={saveTheme}>{words.save}</button>
          </footer>
        </DialogPrimitive>
      ) : null}

      {settingsModal === "customCss" ? (
        <DialogPrimitive labelledBy="custom-css-modal-title" onDismiss={() => setSettingsModal("theme")} className="dialog-surface settings-modal settings-css-modal">
          <header className="settings-modal-header">
            <h2 id="custom-css-modal-title">{words.customCssEditorTitle}</h2>
            <button className="icon-button" type="button" onClick={() => setSettingsModal("theme")} aria-label={words.close} title={words.close}><CloseIcon size={16} /></button>
          </header>
          <div className="settings-css-editor">
            <p>{words.customCssHelp}</p>
            <label className="settings-css-name-field">
              <strong>{words.customCssName}</strong>
              <input
                value={customCssNameDraft}
                maxLength={64}
                autoFocus
                placeholder={words.customCssNamePlaceholder}
                onChange={(event) => setCustomCssNameDraft(event.target.value)}
              />
            </label>
            <textarea value={customCssDraft} spellCheck={false} maxLength={65_536} placeholder={words.customCssPlaceholder} onChange={(event) => setCustomCssDraft(event.target.value)} />
          </div>
          <footer className="settings-modal-footer">
            {editingCustomCssProfileId ? (
              <button
                className="danger-button"
                type="button"
                onClick={() => {
                  onDeleteCustomCssProfile(editingCustomCssProfileId);
                  setSettingsModal("theme");
                }}
              >
                {words.customCssDelete}
              </button>
            ) : null}
            <button className="text-button" type="button" onClick={() => setCustomCssDraft("")}>{words.customCssClear}</button>
            <span />
            <button className="secondary-button" type="button" onClick={() => setSettingsModal("theme")}>{words.cancel}</button>
            <button className="primary-button" type="button" disabled={!customCssNameDraft.trim() || !customCssDraft.trim()} onClick={saveCustomCssProfile}>{words.customCssSave}</button>
          </footer>
        </DialogPrimitive>
      ) : null}
    </section>
  );
}
