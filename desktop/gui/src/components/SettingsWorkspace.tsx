/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useEffect, useRef, useState } from "react";
import { Tooltip } from "@mantine/core";
import {
  AlertIcon,
  CheckIcon,
  ChevronIcon,
  CloseIcon,
  ExternalLinkIcon,
  FolderIcon,
  HelpIcon,
  RefreshIcon,
  ReplayIcon,
  SearchIcon,
  SlidersIcon,
} from "../icons";
import {
  isThemeColor,
  isThemeFontFamily,
  normalizeSidebarOpacity,
  normalizePanelOpacity,
  BACKGROUND_FILTERS,
  BACKGROUND_FILTER_KEYS,
  normalizeBackgroundFilter,
  type BackgroundFilterKey,
  type BackgroundMaterial,
  SIDEBAR_OPACITY_DEFAULT,
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
  GuiUpdateStatus,
  Language,
  LocalEnvironmentSettings,
  PlaybackInstallProgress,
  PlaybackReleaseStatus,
  PlaybackUpdateStatus,
  ServerConfigDocument,
  ServerConfigValidation,
  Theme,
  WorkspaceBackground,
} from "../types";
import type { PlaybackHandoffMode, PlaybackPresetOptions } from "../playbackCommand";
import { DialogPrimitive } from "./Dialog";
import { WorkspaceBackgroundEditor } from "./WorkspaceBackground";
import { DEFAULT_BACKGROUND_CROP, type BackgroundCrop } from "../workspaceBackground";
import { SelectControl, type SelectControlOption } from "./SelectControl";
import { SwitchControl } from "./SwitchControl";
import "./settings-workspace.css";

type SettingsModal = "theme" | "background" | "customCss" | "serverConfig" | "credits" | null;

type ThemeColorKey = keyof ThemePalette;

interface ThemeEditorDraft extends ThemePalette, Record<BackgroundFilterKey, number> {
  fontFamily: string;
  monoFontFamily: string;
  sidebarOpacity: number;
  sidebarFollowPanels: boolean;
  panelOpacity: number;
  backgroundMaterial: BackgroundMaterial;
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
    sidebarOpacity: normalizeSidebarOpacity(customization.sidebarOpacity ?? SIDEBAR_OPACITY_DEFAULT),
    sidebarFollowPanels: customization.sidebarFollowPanels ?? true,
    panelOpacity: normalizePanelOpacity(customization.panelOpacity),
    backgroundMaterial: customization.backgroundMaterial ?? "glass",
    backgroundBlur: normalizeBackgroundFilter("backgroundBlur", customization.backgroundBlur),
    backgroundBrightness: normalizeBackgroundFilter("backgroundBrightness", customization.backgroundBrightness),
    backgroundSaturation: normalizeBackgroundFilter("backgroundSaturation", customization.backgroundSaturation),
    backgroundContrast: normalizeBackgroundFilter("backgroundContrast", customization.backgroundContrast),
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
  workspaceBackground: WorkspaceBackground | null;
  backgroundCrop: BackgroundCrop;
  onSaveWorkspaceBackground: (image: WorkspaceBackground, crop: BackgroundCrop) => Promise<void>;
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
  serverConfigDocument: ServerConfigDocument | null;
  serverConfigDraft: string;
  serverConfigValidation: ServerConfigValidation | null;
  loadingServerConfig: boolean;
  savingServerConfig: boolean;
  detecting: boolean;
  detectionCompleted: boolean;
  guiUpdate: GuiUpdateStatus;
  playbackRelease: PlaybackReleaseStatus | null;
  playbackUpdate: PlaybackUpdateStatus;
  playbackReleaseError: string;
  releaseAction: "installingOnline" | "installingFile" | "rollingBack" | null;
  playbackInstallProgress: PlaybackInstallProgress | null;
  releaseNotice: string;
  onUiFontSizeChange: (fontSize: number) => void;
  onThemeCustomizationChange: (customization: ThemeCustomization) => void;
  onChooseWorkspaceBackground: () => Promise<WorkspaceBackground | null>;
  onClearWorkspaceBackground: () => void;
  onSaveCustomCssProfile: (profile: CustomCssProfile) => void;
  onActivateCustomCssProfile: (profileId: string | null) => void;
  onDeleteCustomCssProfile: (profileId: string) => void;
  onLanguageChange: (language: Language) => void;
  onThemeChange: (theme: Theme) => void;
  onCs2PathChange: (path: string) => void;
  onBrowseCs2: () => void;
  onDetectCs2: () => void;
  onUseCandidate: (candidate: Cs2InstallCandidate) => void;
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
  onAggregateTelemetryEnabledChange: (enabled: boolean) => void;
  onPresenceTelemetryEnabledChange: (enabled: boolean) => void;
  onConverterChange: (patch: Partial<ConverterSettings>) => void;
  onRequestCosmetics: () => void;
  onPlaybackChange: (patch: Partial<PlaybackPresetOptions>) => void;
}

function SettingLabel({ title, description }: { title: string; description?: string }) {
  return <span className="settings-label">
    <strong>{title}</strong>
    {description ? <Tooltip label={description} multiline w={280} withArrow events={{ hover: true, focus: true, touch: true }}>
      <button className="settings-help" type="button" aria-label={description}><HelpIcon size={14} /></button>
    </Tooltip> : null}
  </span>;
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
        <SettingLabel title={title} description={tone === "warning" ? undefined : description} />
        {tone === "warning" ? <small>{description}</small> : null}
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
      <SettingLabel title={title} description={description} />
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

function PathRow({
  path,
  removeLabel,
  openLabel,
  onOpen,
  onRemove,
}: {
  path: string;
  removeLabel: string;
  openLabel: string;
  onOpen: () => void;
  onRemove: () => void;
}) {
  return (
    <div className="settings-path-row">
      <button className="settings-path-open-target" type="button" onClick={onOpen} aria-label={`${openLabel}: ${path}`} title={path}>
        <FolderIcon size={16} />
        <code>{path}</code>
      </button>
      <button className="text-button" type="button" onClick={onRemove} aria-label={`${removeLabel}: ${path}`}>{removeLabel}</button>
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
  workspaceBackground,
  backgroundCrop,
  onSaveWorkspaceBackground,
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
  serverConfigDocument,
  serverConfigDraft,
  serverConfigValidation,
  loadingServerConfig,
  savingServerConfig,
  detecting,
  detectionCompleted,
  guiUpdate,
  playbackRelease,
  playbackUpdate,
  playbackReleaseError,
  releaseAction,
  playbackInstallProgress,
  releaseNotice,
  onUiFontSizeChange,
  onThemeCustomizationChange,
  onChooseWorkspaceBackground,
  onClearWorkspaceBackground,
  onSaveCustomCssProfile,
  onActivateCustomCssProfile,
  onDeleteCustomCssProfile,
  onLanguageChange,
  onThemeChange,
  onCs2PathChange,
  onBrowseCs2,
  onDetectCs2,
  onUseCandidate,
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
  onAggregateTelemetryEnabledChange,
  onPresenceTelemetryEnabledChange,
  onConverterChange,
  onRequestCosmetics,
  onPlaybackChange,
}: SettingsWorkspaceProps) {
  const [settingsModal, setSettingsModal] = useState<SettingsModal>(null);
  const [backgroundDraft, setBackgroundDraft] = useState<{ image: WorkspaceBackground; crop: BackgroundCrop } | null>(null);
  const chooseBackground = async () => {
    const image = await onChooseWorkspaceBackground();
    if (image) {
      setBackgroundDraft({ image, crop: DEFAULT_BACKGROUND_CROP });
      setSettingsModal("background");
    }
  };
  const [themeDraft, setThemeDraft] = useState<ThemeEditorDraft>(() => themeEditorDraft(themeCustomization, resolvedTheme));
  const [customCssDraft, setCustomCssDraft] = useState("");
  const [customCssNameDraft, setCustomCssNameDraft] = useState("");
  const [editingCustomCssProfileId, setEditingCustomCssProfileId] = useState<string | null>(null);
  const [validatingServerConfig, setValidatingServerConfig] = useState(false);
  const [serverConfigFeedback, setServerConfigFeedback] = useState<{ tone: "progress" | "success" | "error"; message: string } | null>(null);
  const autoLoadedConfigPath = useRef("");
  const defaultRootKey = exportRoot.replace(/\\/g, "/").toLocaleLowerCase();
  const additionalArchiveRoots = archiveRoots.filter((root) => root.replace(/\\/g, "/").toLocaleLowerCase() !== defaultRootKey);
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
    setServerConfigFeedback(null);
    await onValidateServerConfig();
    setValidatingServerConfig(false);
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
    next.sidebarOpacity = normalizeSidebarOpacity(themeDraft.sidebarOpacity);
    next.sidebarFollowPanels = themeDraft.sidebarFollowPanels;
    for (const key of BACKGROUND_FILTER_KEYS) next[key] = normalizeBackgroundFilter(key, themeDraft[key]);
    next.panelOpacity = normalizePanelOpacity(themeDraft.panelOpacity);
    next.backgroundMaterial = themeDraft.backgroundMaterial;
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
          <SettingLabel title={words.uiFontSize} description={words.uiFontSizeHelp} />
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
          className="settings-theme-entry"
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

  const releaseBusy = releaseAction !== null;
  const playbackUpdateBusy = releaseBusy || playbackUpdate.phase === "checking"
    || guiUpdate.phase === "checking" || guiUpdate.phase === "downloading" || guiUpdate.phase === "installing";
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
  const playbackInstallView = (
    <section className="settings-card playback-install-card" aria-label={words.releasePlayback}>
      <div className="settings-card-heading">
        <h3>{words.releasePlayback}</h3>
        {environment.cs2Path.trim() ? <span className="settings-version" title={words.releaseInstalledBundle}>
          {playbackRelease?.currentVersion ? `v${playbackRelease.currentVersion}` : words.releaseMissingLegacy}
        </span> : null}
      </div>
      {releaseNotice ? <div className="release-notice" role="status"><CheckIcon size={16} /><span>{releaseNotice}</span></div> : null}
      <div className="playback-settings-list">
        {!environment.cs2Path.trim() ? (
          <p className="settings-inline-note">{words.releaseChooseCs2Folder}</p>
        ) : (
          <>
            <div className={`playback-settings-row is-action is-update-status${playbackUpdate.phase === "available" ? " has-update" : ""}${playbackUpdate.error ? " has-error" : ""}`}>
              <div>
                <span>{playbackUpdateLabel}</span>
                {playbackUpdate.phase === "available" && playbackUpdate.latestVersion ? (
                  <small className="playback-update-route">
                    {playbackRelease?.currentVersion ? `v${playbackRelease.currentVersion}` : words.releaseMissingLegacy} → v{playbackUpdate.latestVersion}
                  </small>
                ) : playbackUpdate.error ? <small>{playbackUpdate.error}</small> : null}
              </div>
              {playbackUpdate.phase === "available" ? (
                <button className="primary-button" type="button" disabled={playbackUpdateBusy} onClick={onInstallLatestPlayback}>
                  <ReplayIcon size={15} />{releaseAction === "installingOnline" ? playbackInstallLabel
                    : guiUpdate.phase === "available" ? words.releaseUpdateAll : words.releaseInstallPlaybackUpdate}
                </button>
              ) : (
                <button className="secondary-button" type="button" disabled={playbackUpdateBusy} onClick={onCheckPlaybackUpdate}>
                  <RefreshIcon className={playbackUpdate.phase === "checking" ? "release-spin" : undefined} size={15} />
                  {playbackUpdate.phase === "checking" ? words.releaseChecking : words.releaseCheckNow}
                </button>
              )}
            </div>
            {playbackReleaseError ? (
              <div className="playback-settings-row has-error" role="alert">
                <div><span>{words.errorPlaybackTitle}</span><small>{playbackReleaseError}</small></div>
              </div>
            ) : null}
            <details className="playback-maintenance">
              <summary>{words.playbackMaintenance}<ChevronIcon size={15} /></summary>
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
            </details>
          </>
        )}
      </div>
    </section>
  );

  const environmentView = (
    <div className="settings-pane settings-environment-pane">
      <section className="settings-card cs2-location-card" aria-label={words.cs2Location}>
        <div className="settings-path-input">
          <input
            key={environment.cs2Path}
            defaultValue={environment.cs2Path}
            disabled={detecting || releaseBusy}
            spellCheck={false}
            placeholder={words.cs2PathPlaceholder}
            aria-label={words.cs2Location}
            onBlur={(event) => onCs2PathChange(event.currentTarget.value)}
            onKeyDown={(event) => { if (event.key === "Enter") event.currentTarget.blur(); }}
          />
          <button className="text-button" type="button" disabled={detecting || releaseBusy} onClick={onBrowseCs2}>
            <FolderIcon size={15} />{words.browseFolder}
          </button>
          <button className="icon-button" type="button" disabled={detecting || releaseBusy} onClick={onDetectCs2} aria-label={words.autoDetectCs2} title={detecting ? words.detectingCs2 : words.autoDetectCs2}>
            <SearchIcon size={16} />
          </button>
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
                disabled={detecting || releaseBusy}
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
      </section>

      {playbackInstallView}
    </div>
  );

  const pathsView = (
    <div className="settings-pane settings-paths-pane">
      <section className="settings-card" aria-labelledby="default-output-title">
        <div className="settings-card-heading">
          <div>
            <h3 id="default-output-title">{words.defaultOutputDirectory}</h3>
            {exportRoot ? (
              <button className="primary-path-readout" type="button" onClick={() => onOpenPath(exportRoot)} aria-label={`${words.openFolder}: ${exportRoot}`} title={exportRoot}>
                <FolderIcon size={14} /><code>{exportRoot}</code>
              </button>
            ) : <small className="settings-directory-status">{words.notSelected}</small>}
          </div>
          <button className="text-button" type="button" onClick={onChooseExportRoot}>
            <FolderIcon size={15} />{words.changeFolder}
          </button>
        </div>
      </section>

      <section className="settings-card" aria-labelledby="archive-roots-title">
        <div className="settings-card-heading">
          <div className="settings-directory-heading">
            <h3 id="archive-roots-title">{words.archiveLibraryDirectories}</h3>
            {additionalArchiveRoots.length === 0 ? <small className="settings-directory-status">{words.noDemoDirectories}</small> : null}
          </div>
          <button className="text-button" type="button" onClick={onAddArchiveRoot}>
            <FolderIcon size={15} />{words.addFolder}
          </button>
        </div>
        {additionalArchiveRoots.length > 0 ? (
          <div className="settings-path-list">
            {additionalArchiveRoots.map((root) => (
              <PathRow key={root} path={root} removeLabel={words.removeFolder} openLabel={words.openFolder} onOpen={() => onOpenPath(root)} onRemove={() => onRemoveArchiveRoot(root)} />
            ))}
          </div>
        ) : null}
      </section>

      <section className="settings-card" aria-labelledby="demo-roots-title">
        <div className="settings-card-heading">
          <div className="settings-directory-heading">
            <h3 id="demo-roots-title">{words.rawDemoDirectories}</h3>
            {environment.demoRoots.length === 0 ? <small className="settings-directory-status">{words.noDemoDirectories}</small> : null}
          </div>
          <button className="text-button" type="button" onClick={onAddDemoRoot}>
            <FolderIcon size={15} />{words.addDemoDirectory}
          </button>
        </div>
        {environment.demoRoots.length > 0 ? (
          <div className="settings-path-list">
            {environment.demoRoots.map((root) => (
              <PathRow key={root} path={root} removeLabel={words.removeFolder} openLabel={words.openFolder} onOpen={() => onOpenPath(root)} onRemove={() => onRemoveDemoRoot(root)} />
            ))}
          </div>
        ) : null}
      </section>

    </div>
  );

  const exportView = (
    <div className="settings-pane settings-export-pane">
      <section className="settings-card settings-form-card">
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

        <div className="settings-number-row">
          <SettingLabel title={words.maxRoundDuration} description={words.maxRoundDurationHelp} />
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
      </section>

    </div>
  );

  const playbackView = (
    <div className="settings-pane settings-playback-pane">
      <section className="settings-card settings-form-card playback-defaults-card">
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
      </section>

    </div>
  );

  const serverConfigView = (
    <div className="settings-pane server-config-pane">
      {environment.cs2Path.trim() ? <header className="settings-card settings-pane-toolbar">
        <div className="settings-header-actions">
          <button className="secondary-button" type="button" disabled={!environment.cs2Path.trim() || loadingServerConfig || savingServerConfig || validatingServerConfig} onClick={() => void handleLoadServerConfig()}>
            <RefreshIcon size={16} />{loadingServerConfig ? words.loadingServerConfig : words.loadServerConfig}
          </button>
          <button className="secondary-button" type="button" disabled={!serverConfigDraft.trim() || loadingServerConfig || savingServerConfig || validatingServerConfig} onClick={() => void handleValidateServerConfig()}>
            <CheckIcon size={16} />{validatingServerConfig ? words.validatingServerConfig : words.validateServerConfig}
          </button>
          <button className="primary-button" type="button" disabled={!serverConfigDocument || !serverConfigDraft.trim() || loadingServerConfig || savingServerConfig || validatingServerConfig || serverConfigValidation?.valid === false} onClick={() => void handleSaveServerConfig()}>
            <SlidersIcon size={16} />{savingServerConfig ? words.savingServerConfig : words.saveServerConfig}
          </button>
        </div>
      </header> : null}
      {serverConfigFeedback ? <div className={`server-config-action-feedback is-${serverConfigFeedback.tone}`} role="status" aria-live="polite">{serverConfigFeedback.message}</div> : null}

      {!environment.cs2Path.trim() ? (
        <section className="settings-card settings-empty">
          <span><FolderIcon size={22} /></span>
          <div>
            <h3>{words.serverConfigNeedsPath}</h3>
            <button className="secondary-button server-config-choose-path" type="button" onClick={onBrowseCs2}>
              <FolderIcon size={15} />{words.browseFolder}
            </button>
          </div>
        </section>
      ) : !serverConfigDocument ? (
        <section className="settings-card settings-empty">
          <span><SlidersIcon size={22} /></span>
          <div><h3>{words.serverConfigNotLoaded}</h3></div>
        </section>
      ) : (
        <>
          <section className="settings-card server-config-editor-card">
            <div className="settings-card-heading">
              <div>
                <button className="text-button" type="button" onClick={() => onOpenExternal("https://github.com/unicbm/demotracer/blob/main/docs/COMMANDS.md")}>{words.documentation}</button>
              </div>
              <span className={`count-badge${serverConfigDocument.source === "installed" ? "" : " is-warning"}`}>
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
                disabled={loadingServerConfig || savingServerConfig}
                aria-label={words.serverConfigEditor}
                onChange={(event) => {
                  setServerConfigFeedback(null);
                  onServerConfigDraftChange(event.target.value);
                }}
              />
            </div>
          </section>

          {serverConfigValidation ? (
            <section className={`settings-card server-config-validation is-${serverConfigValidation.valid ? "valid" : "invalid"}`}>
              <div className="settings-card-heading">
                <div>
                  <h3>{serverConfigValidation.valid ? words.serverConfigValid : words.serverConfigInvalid}</h3>
                  <p>{words.serverConfigValidationHelp}</p>
                </div>
                <span className={`count-badge${serverConfigValidation.valid ? "" : " is-warning"}`}>
                  {serverConfigValidation.errors.length} / {serverConfigValidation.warnings.length}
                </span>
              </div>
              {[...serverConfigValidation.errors, ...serverConfigValidation.warnings].length > 0 ? (
                <ul className="server-config-issues">
                  {[...serverConfigValidation.errors, ...serverConfigValidation.warnings].map((issue) => (
                    <li key={`${issue.code}:${issue.path}:${issue.message}`}>
                      <AlertIcon size={15} /><div><code>{issue.path || "$"}</code><span>{issue.message}</span></div>
                    </li>
                  ))}
                </ul>
              ) : <p className="settings-empty-list">{words.serverConfigNoIssues}</p>}
              {serverConfigValidation.unknownPaths.length > 0 ? (
                <details className="server-config-unknown">
                  <summary>{words.serverConfigUnknownFields.replace("{count}", String(serverConfigValidation.unknownPaths.length))}</summary>
                  <p>{words.serverConfigUnknownFieldsHelp}</p>
                  <div>{serverConfigValidation.unknownPaths.map((path) => <code key={path}>{path}</code>)}</div>
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

  const creditedPeople = [
    DEMOTRACER_CREDITS.creator,
    ...DEMOTRACER_CREDITS.contributors,
  ];
  const aboutView = (
    <div className="settings-pane settings-about-pane">
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
      <div className="settings-theme-background-row">
        <span
          className={`settings-theme-background-preview${workspaceBackground ? " has-image" : ""}`}
          style={workspaceBackground ? { backgroundImage: `url(${workspaceBackground.dataUrl})` } : undefined}
          aria-hidden="true"
        />
        <span className="settings-theme-background-copy">
          <strong>{words.workspaceBackground}</strong>
          <small>{workspaceBackground
            ? words.workspaceBackgroundConfigured
              .replace("{width}", String(workspaceBackground.width))
              .replace("{height}", String(workspaceBackground.height))
            : words.workspaceBackgroundNotConfigured}</small>
        </span>
        <span className="settings-theme-css-actions">
          {workspaceBackground ? <button className="secondary-button" type="button" onClick={onClearWorkspaceBackground}>{words.workspaceBackgroundRemove}</button> : null}
          {workspaceBackground ? <button className="secondary-button" type="button" onClick={() => {
            setBackgroundDraft({ image: workspaceBackground, crop: backgroundCrop });
            setSettingsModal("background");
          }}>{words.workspaceBackgroundEdit}</button> : null}
          <button className="secondary-button" type="button" onClick={() => void chooseBackground()}>{words.workspaceBackgroundChoose}</button>
        </span>
      </div>
      <div className="settings-theme-material-row">
        <span><strong>{words.backgroundMaterial}</strong><small>{words.backgroundMaterialHelp}</small></span>
        <div className="segmented-control" role="group" aria-label={words.backgroundMaterial}>
          {(["glass", "transparent"] as const).map((material) => <button key={material} type="button"
            className={themeDraft.backgroundMaterial === material ? "is-selected" : ""}
            aria-pressed={themeDraft.backgroundMaterial === material}
            onClick={() => setThemeDraft((current) => ({ ...current, backgroundMaterial: material }))}
          >{material === "glass" ? words.backgroundMaterialGlass : words.backgroundMaterialTransparent}</button>)}
        </div>
      </div>
      <label className="settings-theme-opacity-row">
        <span><strong>{words.panelTransparency}</strong><small>{words.panelTransparencyHelp}</small></span>
        <input type="range" min={0} max={100} step={1} value={Math.round((1 - themeDraft.panelOpacity) * 100)}
          onChange={(event) => setThemeDraft((current) => ({ ...current, panelOpacity: normalizePanelOpacity(1 - Number(event.target.value) / 100) }))} />
        <output>{Math.round((1 - themeDraft.panelOpacity) * 100)}%</output>
      </label>
      <div className="settings-theme-material-row">
        <span><strong>{words.sidebarAppearance}</strong><small>{words.sidebarOpacityHelp}</small></span>
        <div className="segmented-control" role="group" aria-label={words.sidebarOpacity}>
          {[true, false].map((follow) => <button key={String(follow)} type="button"
            className={themeDraft.sidebarFollowPanels === follow ? "is-selected" : ""}
            aria-pressed={themeDraft.sidebarFollowPanels === follow}
            onClick={() => setThemeDraft((current) => ({ ...current, sidebarFollowPanels: follow }))}
          >{follow ? words.sidebarFollowPanels : words.sidebarIndependent}</button>)}
        </div>
      </div>
      <label className="settings-theme-opacity-row">
        <span>
          <strong>{words.sidebarOpacity}</strong>
        </span>
        <input
          type="range"
          min={0}
          max={100}
          step={1}
          disabled={themeDraft.sidebarFollowPanels}
          value={Math.round((1 - (themeDraft.sidebarFollowPanels ? themeDraft.panelOpacity : themeDraft.sidebarOpacity)) * 100)}
          onChange={(event) => setThemeDraft((current) => ({
            ...current,
            sidebarOpacity: normalizeSidebarOpacity(1 - Number(event.target.value) / 100),
          }))}
        />
        <output>{Math.round((1 - (themeDraft.sidebarFollowPanels ? themeDraft.panelOpacity : themeDraft.sidebarOpacity)) * 100)}%</output>
      </label>
      {BACKGROUND_FILTER_KEYS.map((key) => {
        const range = BACKGROUND_FILTERS[key];
        const disabled = key === "backgroundBlur" && themeDraft.backgroundMaterial === "transparent";
        return <label className="settings-theme-opacity-row" key={key}>
          <span><strong>{words[key]}</strong>{key === "backgroundBlur" ? <small>{words.backgroundBlurHelp}</small> : null}</span>
          <input type="range" min={range.min} max={range.max} step={1} disabled={disabled} value={disabled ? 0 : themeDraft[key]}
            onChange={(event) => setThemeDraft((current) => ({ ...current, [key]: normalizeBackgroundFilter(key, Number(event.target.value)) }))} />
          <output>{disabled ? 0 : themeDraft[key]}{range.unit}</output>
        </label>;
      })}
      <div className="settings-theme-css-row">
        <strong>{words.backgroundAdjustmentsReset}</strong>
        <button className="secondary-button" type="button" onClick={() => setThemeDraft((current) => ({
          ...current, sidebarFollowPanels: true, sidebarOpacity: SIDEBAR_OPACITY_DEFAULT, panelOpacity: 0.5,
          backgroundMaterial: "glass", backgroundBlur: BACKGROUND_FILTERS.backgroundBlur.initial,
          backgroundBrightness: 100, backgroundSaturation: 100, backgroundContrast: 100,
        }))}>{words.backgroundAdjustmentsResetButton}</button>
      </div>
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

  return (
    <section className="settings-workspace" aria-label={words.settingsTitle}>
      <div className="settings-content">
        <header className="settings-section-heading">
          <h1>{words.settingsTitle}</h1>
        </header>
        <div className="settings-columns">
          <div className="settings-column">
            <section className="settings-group" aria-labelledby="settings-general-title">
              <h2 id="settings-general-title">{words.settingsNavAppearance}</h2>
              {appearanceView}
              <button className="settings-theme-entry" type="button" disabled={guiUpdateBusy} onClick={guiUpdate.phase === "available" ? onInstallGuiUpdate : onCheckGuiUpdate}>
                <strong>{guiUpdate.phase === "available" ? words.releaseInstallNow : words.releaseCheckNow}</strong>
                <em aria-live="polite">{guiStatus}</em><ChevronIcon size={15} />
              </button>
              <button className="settings-theme-entry" type="button" onClick={() => setSettingsModal("credits")}>
                <strong>{words.creditsTitle}</strong><span /><ChevronIcon size={15} />
              </button>
            </section>
            <section className="settings-group" aria-labelledby="settings-conversion-title">
              <h2 id="settings-conversion-title">{words.settingsNavExport}</h2>
              {exportView}
            </section>
            <section className="settings-group" aria-labelledby="settings-storage-title">
              <h2 id="settings-storage-title">{words.settingsNavPaths}</h2>
              {pathsView}
            </section>
          </div>
          <div className="settings-column">
            <section className="settings-group" aria-labelledby="settings-playback-title">
              <h2 id="settings-playback-title">{words.settingsNavPlayback}</h2>
              {playbackView}
              <button className="settings-theme-entry" type="button" onClick={() => setSettingsModal("serverConfig")}>
                <strong>{words.serverConfigTitle}</strong><span /><ChevronIcon size={15} />
              </button>
            </section>
            <section className="settings-group" aria-labelledby="settings-cs2-title">
              <h2 id="settings-cs2-title">{words.settingsNavCs2}</h2>
              {environmentView}
            </section>
          </div>
        </div>
      </div>

      {settingsModal === "serverConfig" || settingsModal === "credits" ? (
        <DialogPrimitive labelledBy="settings-detail-title" onDismiss={() => setSettingsModal(null)} className={`dialog-surface settings-modal settings-detail-modal settings-${settingsModal}-modal`}>
          <header className="settings-modal-header">
            <h2 id="settings-detail-title">{settingsModal === "serverConfig" ? words.serverConfigTitle : words.creditsTitle}</h2>
            <button className="icon-button" type="button" onClick={() => setSettingsModal(null)} aria-label={words.close} title={words.close}><CloseIcon size={16} /></button>
          </header>
          <div className="settings-detail-body">{settingsModal === "serverConfig" ? serverConfigView : aboutView}</div>
        </DialogPrimitive>
      ) : null}

      {settingsModal === "background" && backgroundDraft ? <WorkspaceBackgroundEditor
        image={backgroundDraft.image} initialCrop={backgroundDraft.crop} words={words}
        onCancel={() => { setBackgroundDraft(null); setSettingsModal("theme"); }}
        onSave={async (crop) => {
          await onSaveWorkspaceBackground(backgroundDraft.image, crop);
          setBackgroundDraft(null);
          setSettingsModal("theme");
        }}
      /> : null}

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
