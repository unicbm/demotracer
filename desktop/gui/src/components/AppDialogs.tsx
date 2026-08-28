/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { Badge, Button, Group, Paper, Progress, Stack, Text } from "@mantine/core";
import type { RefObject } from "react";
import {
  consentIsValid,
  fileName,
  type DuplicateDemoConflictState,
} from "../appSupport";
import { AlertIcon, ArrowIcon, CheckIcon, CloseIcon, CopyIcon, FolderIcon } from "../icons";
import { COSMETIC_PHRASE, TEXT } from "../i18n";
import { releaseNotesForLanguage } from "../releaseNotes";
import type {
  DemoLibraryEntry,
  GuiUpdateStatus,
  Language,
  OutputPreflight,
  PlaybackReleaseStatus,
  PlaybackUpdateStatus,
} from "../types";
import { DialogPrimitive } from "./Dialog";

type AppWords = (typeof TEXT)[Language];

interface UpdateDialogProps {
  words: AppWords;
  language: Language;
  busy: boolean;
  status: string;
  playbackInstallBlockedByCs2: boolean;
  availableUpdateCount: number;
  guiUpdateOffered: boolean;
  playbackUpdateOffered: boolean;
  guiUpdate: GuiUpdateStatus;
  playbackRelease: PlaybackReleaseStatus | null;
  playbackUpdate: PlaybackUpdateStatus;
  progressActive: boolean;
  progress: number | null;
  playbackInstallStatus: string;
  playbackReleaseError: string;
  releaseAction: "installingOnline" | "installingFile" | "rollingBack" | null;
  guiUpdateRetryRequired: boolean;
  guiUpdateAvailable: boolean;
  initialFocusRef: RefObject<HTMLButtonElement | null>;
  onDismiss: () => void;
  onIgnore: () => void;
  onInstall: () => void;
}

export function UpdateDialog({
  words,
  language,
  busy,
  status,
  playbackInstallBlockedByCs2,
  availableUpdateCount,
  guiUpdateOffered,
  playbackUpdateOffered,
  guiUpdate,
  playbackRelease,
  playbackUpdate,
  progressActive,
  progress,
  playbackInstallStatus,
  playbackReleaseError,
  releaseAction,
  guiUpdateRetryRequired,
  guiUpdateAvailable,
  initialFocusRef,
  onDismiss,
  onIgnore,
  onInstall,
}: UpdateDialogProps) {
  return (
    <DialogPrimitive
      labelledBy="update-dialog-title"
      describedBy="update-dialog-description"
      onDismiss={() => { if (!busy) onDismiss(); }}
      initialFocusRef={initialFocusRef}
      dismissOnScrimClick={false}
      className="dialog-surface gui-update-dialog"
    >
      <header className="update-dialog-header">
        <div className="update-dialog-heading">
          <div>
            <span className="dialog-eyebrow">{words.releaseUpdateStatus}</span>
            <h2 id="update-dialog-title">{words.releaseUpdateTitle}</h2>
          </div>
        </div>
        <Group className="update-dialog-header-actions" gap="sm" wrap="nowrap">
          <Badge
            className="update-dialog-state"
            color={playbackInstallBlockedByCs2 ? "orange" : "blue"}
            variant="light"
            size="lg"
            radius="xl"
            leftSection={<span className="update-dialog-state-dot" aria-hidden="true" />}
          >
            <span role="status">
              {busy ? status : words.releaseUpdateComponentsCount.replace("{count}", String(availableUpdateCount))}
            </span>
          </Badge>
          <button className="icon-button" type="button" disabled={busy} onClick={onDismiss} aria-label={words.close}>
            <CloseIcon size={16} />
          </button>
        </Group>
      </header>

      <Stack className="update-dialog-content" gap="md">
        {guiUpdateOffered ? (
          <Paper className="update-dialog-component" component="section" withBorder radius="md" p="lg" aria-labelledby="gui-update-component-title">
            <div className="update-dialog-component-header">
              <div>
                <Text id="gui-update-component-title" fw={700}>DemoTracer</Text>
                <Text c="dimmed" size="xs">{words.releaseDesktopApp}</Text>
              </div>
              <div className="update-dialog-version-route" aria-label={words.releaseUpdateStatus}>
                <code>v{guiUpdate.currentVersion || "—"}</code>
                <ArrowIcon size={16} aria-hidden="true" />
                <code>v{guiUpdate.availableVersion || "—"}</code>
              </div>
            </div>
            <Text className="update-dialog-component-notes" component="p" mt="md" size="sm">
              {releaseNotesForLanguage(guiUpdate.notes, language) || words.releaseGenericNotes}
            </Text>
          </Paper>
        ) : null}

        {playbackUpdateOffered ? (
          <Paper className="update-dialog-component" component="section" withBorder radius="md" p="lg" aria-labelledby="playback-update-component-title">
            <div className="update-dialog-component-header">
              <div>
                <Text id="playback-update-component-title" fw={700}>{words.releasePlayback}</Text>
              </div>
              <div className="update-dialog-version-route" aria-label={words.releaseUpdateStatus}>
                <code>{playbackRelease?.currentVersion ? `v${playbackRelease.currentVersion}` : words.releaseMissingLegacy}</code>
                <ArrowIcon size={16} aria-hidden="true" />
                <code>v{playbackUpdate.latestVersion || "—"}</code>
              </div>
            </div>
            <Text className="update-dialog-component-notes" component="p" mt="md" size="sm">
              {releaseNotesForLanguage(playbackUpdate.notes, language) || words.releasePlaybackGenericNotes}
            </Text>
          </Paper>
        ) : null}

        <div id="update-dialog-description" className="update-dialog-guidance">
          <Text className="update-dialog-scope" c="dimmed" size="xs">{words.releaseUpdateScope}</Text>
          {guiUpdateOffered && playbackUpdateOffered ? (
            <Text className="update-dialog-scope" c="dimmed" size="xs">{words.releaseUpdateSequence}</Text>
          ) : null}
        </div>

        {progressActive ? (
          <Paper className="update-dialog-progress" withBorder radius="md" p="sm" role="status" aria-live="polite">
            <Group justify="space-between" mb={7}>
              <Text c="dimmed" size="xs">
                {guiUpdate.phase === "installing"
                  ? words.releaseInstalling
                  : guiUpdate.phase === "downloading" ? words.releaseDownloading : playbackInstallStatus}
              </Text>
              <Text className="update-dialog-progress-value" size="xs" fw={700}>{progress != null ? `${progress}%` : "…"}</Text>
            </Group>
            <Progress value={progress ?? 36} animated={progress == null} size="sm" radius="xl" />
          </Paper>
        ) : null}

        {guiUpdate.phase === "installing" ? (
          <Text className="update-dialog-status" c="dimmed" size="xs" role="status">{words.releaseInstallingDesktop}</Text>
        ) : null}
        {guiUpdate.phase === "error" ? (
          <Text className="release-error update-dialog-error" c="red" size="sm"><AlertIcon size={15} />{words.releaseCheckUnavailable}</Text>
        ) : null}
        {playbackReleaseError && playbackUpdateOffered ? (
          <Text className={`release-error update-dialog-error${playbackInstallBlockedByCs2 ? " is-warning" : ""}`} c={playbackInstallBlockedByCs2 ? "orange" : "red"} size="sm">
            <AlertIcon size={15} />
            {playbackInstallBlockedByCs2 ? words.releaseCloseCs2ToContinue : playbackReleaseError}
          </Text>
        ) : null}
      </Stack>

      <footer className="update-dialog-footer">
        <Button variant="subtle" color="gray" disabled={busy} onClick={onIgnore}>{words.releaseIgnoreVersion}</Button>
        <Group gap="sm">
          <Button ref={initialFocusRef} variant="default" disabled={busy} onClick={onDismiss}>{words.releaseLater}</Button>
          <Button disabled={busy || availableUpdateCount === 0} onClick={onInstall}>
            {guiUpdate.phase === "downloading" ? words.releaseDownloading
              : guiUpdate.phase === "installing" ? words.releaseInstalling
                : releaseAction === "installingOnline" ? playbackInstallStatus
                  : guiUpdateRetryRequired ? words.releaseCheckNow
                    : guiUpdateAvailable && playbackUpdateOffered ? words.releaseUpdateAll
                      : guiUpdateAvailable ? words.releaseUpdateAndRestart
                        : words.releaseInstallPlaybackUpdate}
          </Button>
        </Group>
      </footer>
    </DialogPrimitive>
  );
}

interface OverwriteDialogProps {
  words: AppWords;
  conflict: OutputPreflight;
  conversionStartPending: boolean;
  initialFocusRef: RefObject<HTMLButtonElement | null>;
  onDismiss: () => void;
  onOpenExisting: () => void;
  onChooseAnother: () => void;
  onReplace: () => void;
}

export function OverwriteDialog({ words, conflict, conversionStartPending, initialFocusRef, onDismiss, onOpenExisting, onChooseAnother, onReplace }: OverwriteDialogProps) {
  return (
    <DialogPrimitive labelledBy="overwrite-title" describedBy="overwrite-description" onDismiss={onDismiss} initialFocusRef={initialFocusRef} dismissOnScrimClick={false}>
      <header className="dialog-header">
        <h2 id="overwrite-title">{words.overwriteTitle}</h2>
        <button className="icon-button" type="button" onClick={onDismiss} aria-label={words.close}><CloseIcon size={16} /></button>
      </header>
      <p id="overwrite-description" className="dialog-description">{words.overwriteBody}</p>
      <code className="dialog-path">{conflict.root}</code>
      <button className="text-button dialog-inline-action" type="button" onClick={onOpenExisting}><FolderIcon size={15} />{words.openExisting}</button>
      <footer className="dialog-actions three-actions">
        <button className="secondary-button" type="button" onClick={onDismiss}>{words.cancel}</button>
        <button ref={initialFocusRef} className="secondary-button" type="button" onClick={onChooseAnother}>{words.chooseAnotherOutput}</button>
        <button className="danger-button" type="button" disabled={conversionStartPending} onClick={onReplace}>{words.replaceAndConvert}</button>
      </footer>
    </DialogPrimitive>
  );
}

interface DuplicateDemoDialogProps {
  words: AppWords;
  conflict: DuplicateDemoConflictState;
  initialFocusRef: RefObject<HTMLButtonElement | null>;
  onDismiss: () => void;
  onAnalyzeAgain: (conflict: DuplicateDemoConflictState) => void;
  onOpenExisting: (manifestPath: string) => void;
}

export function DuplicateDemoDialog({ words, conflict, initialFocusRef, onDismiss, onAnalyzeAgain, onOpenExisting }: DuplicateDemoDialogProps) {
  const existing = conflict.primary.matches[0];
  return (
    <DialogPrimitive labelledBy="duplicate-demo-title" describedBy="duplicate-demo-description" onDismiss={onDismiss} initialFocusRef={initialFocusRef} dismissOnScrimClick={false}>
      <header className="dialog-header">
        <h2 id="duplicate-demo-title">{words.duplicateDemoTitle}</h2>
        <button className="icon-button" type="button" onClick={onDismiss} aria-label={words.close}><CloseIcon size={16} /></button>
      </header>
      <p id="duplicate-demo-description" className="dialog-description">
        {conflict.batch
          ? words.duplicateBatchBody.replace("{existing}", String(conflict.batch.replaceSourceIds.length)).replace("{total}", String(conflict.batch.selections.length))
          : words.duplicateDemoBody}
        {!conflict.batch && conflict.primary.matches.length > 1
          ? ` ${words.duplicateDemoMatchCount.replace("{count}", String(conflict.primary.matches.length))}`
          : ""}
      </p>
      <strong className="dialog-target-name">{existing.displayName || fileName(existing.root) || existing.demoId}</strong>
      <code className="dialog-path">{existing.root}</code>
      <footer className="dialog-actions three-actions">
        <button className="secondary-button" type="button" onClick={onDismiss}>{words.cancel}</button>
        <button className={conflict.batch ? "danger-button" : "secondary-button"} type="button" onClick={() => onAnalyzeAgain(conflict)}>
          {conflict.batch ? words.analyzeBatchAgain.replace("{count}", String(conflict.batch.selections.length)) : words.analyzeAgain}
        </button>
        <button ref={initialFocusRef} className="primary-button" type="button" onClick={() => onOpenExisting(existing.manifestPath)}>
          {words.openExistingArchive}<ArrowIcon size={15} />
        </button>
      </footer>
    </DialogPrimitive>
  );
}

interface DeleteArchiveDialogProps {
  words: AppWords;
  target: DemoLibraryEntry;
  deleting: boolean;
  initialFocusRef: RefObject<HTMLButtonElement | null>;
  onDismiss: () => void;
  onDelete: () => void;
}

export function DeleteArchiveDialog({ words, target, deleting, initialFocusRef, onDismiss, onDelete }: DeleteArchiveDialogProps) {
  return (
    <DialogPrimitive labelledBy="delete-archive-title" describedBy="delete-archive-description" onDismiss={() => { if (!deleting) onDismiss(); }} initialFocusRef={initialFocusRef} dismissOnScrimClick={false}>
      <header className="dialog-header warning-header">
        <span><AlertIcon size={18} /></span>
        <h2 id="delete-archive-title">{words.deleteArchiveTitle}</h2>
        <button className="icon-button" type="button" disabled={deleting} onClick={onDismiss} aria-label={words.close}><CloseIcon size={16} /></button>
      </header>
      <p id="delete-archive-description" className="dialog-description">{words.deleteArchiveBody}</p>
      <strong className="dialog-target-name">{target.displayName || fileName(target.root) || target.demoId}</strong>
      <footer className="dialog-actions">
        <button ref={initialFocusRef} className="secondary-button" type="button" disabled={deleting} onClick={onDismiss}>{words.cancel}</button>
        <button className="danger-button" type="button" disabled={deleting} onClick={onDelete}>{deleting ? words.deletingArchive : words.deleteArchive}</button>
      </footer>
    </DialogPrimitive>
  );
}

export function ReparseDialog({ words, onDismiss, onConfirm }: { words: AppWords; onDismiss: () => void; onConfirm: () => void }) {
  return (
    <DialogPrimitive labelledBy="reparse-title" describedBy="reparse-description" onDismiss={onDismiss} dismissOnScrimClick={false}>
      <header className="dialog-header warning-header"><span><AlertIcon size={18} /></span><h2 id="reparse-title">{words.reparseConfirmTitle}</h2></header>
      <p id="reparse-description" className="dialog-description">{words.reparseConfirmBody}</p>
      <footer className="dialog-actions">
        <button className="secondary-button" type="button" onClick={onDismiss}>{words.cancel}</button>
        <button className="danger-button" type="button" onClick={onConfirm}>{words.reparseConfirmAction}</button>
      </footer>
    </DialogPrimitive>
  );
}

interface CosmeticConsentDialogProps {
  words: AppWords;
  phrase: string;
  copied: boolean;
  initialFocusRef: RefObject<HTMLInputElement | null>;
  onDismiss: () => void;
  onPhraseChange: (phrase: string) => void;
  onCopyPhrase: () => void;
  onEnable: () => void;
}

export function CosmeticConsentDialog({ words, phrase, copied, initialFocusRef, onDismiss, onPhraseChange, onCopyPhrase, onEnable }: CosmeticConsentDialogProps) {
  return (
    <DialogPrimitive labelledBy="cosmetic-title" describedBy="cosmetic-description" onDismiss={onDismiss} initialFocusRef={initialFocusRef} dismissOnScrimClick={false} className="dialog-surface cosmetic-dialog">
      <header className="dialog-header warning-header">
        <span><AlertIcon size={18} /></span><h2 id="cosmetic-title">{words.cosmeticTitle}</h2>
        <button className="icon-button" type="button" onClick={onDismiss} aria-label={words.close}><CloseIcon size={16} /></button>
      </header>
      <p id="cosmetic-description" className="dialog-description">{words.cosmeticBody}</p>
      <div className="phrase-field">
        <label htmlFor="cosmetic-confirmation-phrase">{words.typePhrase}</label>
        <button className="phrase-copy-button" type="button" onClick={onCopyPhrase} aria-label={words.copyPhrase}>
          <code>{COSMETIC_PHRASE}</code>
          <span>{copied ? <CheckIcon size={14} /> : <CopyIcon size={14} />}{copied ? words.copied : words.copyPhrase}</span>
        </button>
        <input id="cosmetic-confirmation-phrase" ref={initialFocusRef} autoComplete="off" spellCheck={false} value={phrase} onChange={(event) => onPhraseChange(event.target.value)} />
        <small>{words.phraseCaseSensitive}</small>
      </div>
      <footer className="dialog-actions">
        <button className="secondary-button" type="button" onClick={onDismiss}>{words.cancel}</button>
        <button className="primary-button" type="button" disabled={!consentIsValid(phrase)} onClick={onEnable}>{words.enableCosmetics}<ArrowIcon size={15} /></button>
      </footer>
    </DialogPrimitive>
  );
}

interface CloseTaskDialogProps {
  words: AppWords;
  initialFocusRef: RefObject<HTMLButtonElement | null>;
  onDismiss: () => void;
  onExit: () => void;
}

export function CloseTaskDialog({ words, initialFocusRef, onDismiss, onExit }: CloseTaskDialogProps) {
  return (
    <DialogPrimitive labelledBy="close-task-title" describedBy="close-task-description" onDismiss={onDismiss} initialFocusRef={initialFocusRef} dismissOnScrimClick={false}>
      <header className="dialog-header warning-header"><span><AlertIcon size={18} /></span><h2 id="close-task-title">{words.closeTaskTitle}</h2></header>
      <p id="close-task-description" className="dialog-description">{words.closeTaskBody}</p>
      <footer className="dialog-actions">
        <button ref={initialFocusRef} className="primary-button" type="button" onClick={onDismiss}>{words.keepWorking}</button>
        <button className="danger-button" type="button" onClick={onExit}>{words.closeAnyway}</button>
      </footer>
    </DialogPrimitive>
  );
}
