/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { createPortal } from "react-dom";
import { type CSSProperties, type MouseEvent as ReactMouseEvent, useLayoutEffect, useRef, useState } from "react";
import {
  buildCosmeticInventorySimulatorItem,
  buildCosmeticViewerUrl,
  buildMusicKitInventorySimulatorItem,
  isInventorySimulatorItemCraftable,
  resolveCharmCatalog,
  resolveCosmeticCatalog,
  resolveMusicKitCatalog,
  resolveStickerCatalog,
  type CosmeticCatalogEntry,
} from "../cosmeticCatalog";
import { CheckIcon, CloseIcon, CopyIcon, ExternalLinkIcon, PlusIcon, ReplayIcon } from "../icons";
import type { TextDictionary } from "../i18n";
import {
  INVENTORY_SIMULATOR_BATCH_LIMIT,
  inventorySimulatorSelectionKey,
  type InventorySimulatorItem,
} from "../inventorySimulator";
import {
  type InventorySimulatorSelectionController,
} from "../inventorySimulatorSelection";
import type { CosmeticEvidence, Language, PlayerDetails, ViewmodelEvidence } from "../types";
import type { CopyTarget } from "./TaskViews";
import { CrosshairPreview } from "./CrosshairPreview";
import { ContextMenu, type ContextMenuState } from "./ContextMenu";
import { DialogPrimitive } from "./Dialog";
import { demoPlayerColorValue, SteamPlayerIdentity, type SteamProfileMap } from "./SteamProfile";

export interface RosterPlayer {
  name: string;
  steamId: string;
  playerColor?: string | null;
  score?: number | null;
  kills?: number | null;
  deaths?: number | null;
  assists?: number | null;
  mvps?: number | null;
  details?: PlayerDetails | null;
}

export interface PlayerSelection {
  teamId: string;
  playerIndex: number;
}

export function playerSelectionKey(selection: PlayerSelection): string {
  return `${selection.teamId}:${selection.playerIndex}`;
}

interface RosterTeamProps<T extends RosterPlayer> {
  teamId: string;
  name: string;
  players: T[];
  words: TextDictionary;
  metaLabel?: string;
  matchRounds?: number | null;
  className?: string;
  steamProfiles?: SteamProfileMap;
  retentionPriority?: boolean;
  onSetPlayerPriority?: (playerIndex: number, priority: number) => void;
  onSelectPlayer: (selection: PlayerSelection) => void;
  onCopy?: (value: string, target: CopyTarget) => void;
  onOpenExternal?: (url: string) => void;
}

export function steamProfileUrl(steamId: string): string {
  return `https://steamcommunity.com/profiles/${steamId}`;
}

export function hasSteamProfile(steamId: string): boolean {
  return /^[1-9]\d{16}$/.test(steamId);
}

function targetFor(
  playerKey: string,
  kind: "steam" | "crosshair" | "viewmodel" | "inspect",
  index = 0,
): CopyTarget {
  return `player:${playerKey}:${kind}:${index}`;
}

function formatConfigNumber(value: number): string {
  if (Object.is(value, -0) || value === 0) return "0";
  return value.toFixed(4).replace(/\.?(?:0+)$/, "");
}

function viewmodelCommand(viewmodel: ViewmodelEvidence): string {
  const commands: string[] = [];
  if (viewmodel.fov !== null && viewmodel.fov !== undefined) commands.push(`viewmodel_fov ${formatConfigNumber(viewmodel.fov)}`);
  if (viewmodel.offsetX !== null && viewmodel.offsetX !== undefined) commands.push(`viewmodel_offset_x ${formatConfigNumber(viewmodel.offsetX)}`);
  if (viewmodel.offsetY !== null && viewmodel.offsetY !== undefined) commands.push(`viewmodel_offset_y ${formatConfigNumber(viewmodel.offsetY)}`);
  if (viewmodel.offsetZ !== null && viewmodel.offsetZ !== undefined) commands.push(`viewmodel_offset_z ${formatConfigNumber(viewmodel.offsetZ)}`);
  return commands.length > 0 ? `${commands.join("; ")};` : "";
}

function wearLabel(wear: number, words: TextDictionary): string {
  if (wear <= 0.07) return words.wearFactoryNew;
  if (wear <= 0.15) return words.wearMinimalWear;
  if (wear <= 0.37) return words.wearFieldTested;
  if (wear <= 0.44) return words.wearWellWorn;
  return words.wearBattleScarred;
}

function isDisplayableCosmeticEvidence(cosmetic: CosmeticEvidence): boolean {
  if (cosmetic.kind !== "agent") return true;
  const name = cosmetic.itemName?.trim().toLowerCase();
  return cosmetic.itemDefIndex !== 5036
    && cosmetic.itemDefIndex !== 5037
    && name !== "customplayer_t_map_based"
    && name !== "customplayer_ct_map_based";
}

function markedCatalogName(name: string, marker: string, words: TextDictionary): string {
  if (!marker) return name;
  const separator = name.indexOf(" | ");
  const mark = words.playerCatalogMarker.replace("{marker}", marker);
  return separator < 0
    ? `${name}${mark}`
    : `${name.slice(0, separator)}${mark}${name.slice(separator)}`;
}

function cosmeticTitle(
  cosmetic: CosmeticEvidence,
  words: TextDictionary,
  catalogEntry: CosmeticCatalogEntry | null,
): string {
  const fallback = cosmetic.itemDefIndex !== null && cosmetic.itemDefIndex !== undefined
    ? `#${cosmetic.itemDefIndex}`
    : "—";
  const marker = cosmetic.kind === "knife"
    ? "★"
    : cosmetic.quality === 9 || (cosmetic.stattrakCounter !== null && cosmetic.stattrakCounter !== undefined)
      ? "StatTrak™"
      : "";
  if (catalogEntry) {
    const wear = cosmetic.wear !== null && cosmetic.wear !== undefined ? ` (${wearLabel(cosmetic.wear, words)})` : "";
    return `${markedCatalogName(catalogEntry.name, marker, words)}${wear}`;
  }
  const item = markedCatalogName(cosmetic.itemName || fallback, marker, words);
  const finish = cosmetic.finishName
    || (cosmetic.paintKit !== null && cosmetic.paintKit !== undefined ? `${words.paintKit} #${cosmetic.paintKit}` : "");
  const wear = cosmetic.wear !== null && cosmetic.wear !== undefined ? ` (${wearLabel(cosmetic.wear, words)})` : "";
  return `${item}${finish ? ` | ${finish}` : ""}${wear}`;
}

function cosmeticDisplayName(
  cosmetic: CosmeticEvidence,
  words: TextDictionary,
  catalogEntry: CosmeticCatalogEntry | null,
): { primary: string; secondary: string; full: string } {
  const title = cosmeticTitle(cosmetic, words, catalogEntry);
  const wear = cosmetic.wear !== null && cosmetic.wear !== undefined ? ` (${wearLabel(cosmetic.wear, words)})` : "";
  const withoutWear = wear && title.endsWith(wear) ? title.slice(0, -wear.length) : title;
  const separator = withoutWear.indexOf(" | ");
  const primary = separator >= 0 ? withoutWear.slice(0, separator) : withoutWear;
  const secondary = separator >= 0 ? withoutWear.slice(separator + 3) : "";
  return { primary, secondary, full: title };
}

function CatalogImage({
  entry,
  className,
}: {
  entry: CosmeticCatalogEntry;
  className: string;
}) {
  const [source, setSource] = useState<string | null>(entry.imageUrl);
  if (!source) return null;
  return (
    <img
      className={className}
      src={source}
      alt=""
      loading="lazy"
      decoding="async"
      onError={() => {
        if (entry.fallbackImageUrl && source !== entry.fallbackImageUrl) {
          setSource(entry.fallbackImageUrl);
        } else {
          setSource(null);
        }
      }}
    />
  );
}

function catalogCardStyle(entry: CosmeticCatalogEntry | null): CSSProperties {
  return { "--item-rarity": entry?.rarity ?? "#9ba4ae" } as CSSProperties;
}

function SideMarkers({ side, words }: { side: CosmeticEvidence["side"]; words: TextDictionary }) {
  const sides = side === "t" ? ["t"] : side === "ct" ? ["ct"] : ["ct", "t"];
  return (
    <span className="roster-side-markers" aria-label={sides.map((value) => value === "t" ? words.sideT : words.sideCt).join(" / ")}>
      {sides.map((value) => <i className={`is-${value}`} title={value === "t" ? words.sideT : words.sideCt} key={value} />)}
    </span>
  );
}

function CosmeticViewerDialog({ title, url, words, onClose }: {
  title: string;
  url: string;
  words: TextDictionary;
  onClose: () => void;
}) {
  return (
    <DialogPrimitive
      labelledBy="cosmetic-viewer-title"
      onDismiss={onClose}
      scrimClassName="cosmetic-viewer-scrim"
      className="cosmetic-viewer-dialog"
    >
      <header>
        <div>
          <span>{words.preview360}</span>
          <strong id="cosmetic-viewer-title">{title}</strong>
        </div>
        <button className="icon-button" type="button" onClick={onClose} aria-label={words.close} title={words.close}>
          <CloseIcon size={18} />
        </button>
      </header>
      <iframe src={url} title={`${words.preview360}: ${title}`} referrerPolicy="no-referrer" allowFullScreen />
    </DialogPrimitive>
  );
}

function CosmeticEvidencePopover({
  anchor,
  cosmetic,
  language,
  words,
}: {
  anchor: DOMRect;
  cosmetic: CosmeticEvidence;
  language: Language;
  words: TextDictionary;
}) {
  const popoverRef = useRef<HTMLDivElement | null>(null);
  const [position, setPosition] = useState({ left: anchor.right + 10, top: anchor.top });
  const stickers = cosmetic.stickers ?? [];
  const charms = cosmetic.charms ?? [];
  const catalogEntry = resolveCosmeticCatalog(cosmetic, language);
  const displayName = cosmeticDisplayName(cosmetic, words, catalogEntry);
  const attachmentEntries = [
    ...stickers.map((sticker) => resolveStickerCatalog(sticker.stickerId, language)),
    ...charms.map((charm) => resolveCharmCatalog(charm.charmId, charm.stickerId, language)),
  ].filter((entry): entry is CosmeticCatalogEntry => entry !== null).slice(0, 5);
  const wear = cosmetic.wear;

  useLayoutEffect(() => {
    const popover = popoverRef.current;
    if (!popover) return;
    const gap = 10;
    const margin = 10;
    const bounds = popover.getBoundingClientRect();
    const right = anchor.right + gap;
    const left = anchor.left - bounds.width - gap;
    setPosition({
      left: right + bounds.width <= window.innerWidth - margin ? right : Math.max(margin, left),
      top: Math.max(margin, Math.min(anchor.top, window.innerHeight - bounds.height - margin)),
    });
  }, [anchor]);

  return createPortal(
    <div
      ref={popoverRef}
      className="cosmetic-evidence-popover"
      role="tooltip"
      style={{ ...catalogCardStyle(catalogEntry), left: position.left, top: position.top }}
    >
      <div className="cosmetic-popover-hero">
        {catalogEntry ? <CatalogImage key={catalogEntry.imageUrl} entry={catalogEntry} className="cosmetic-popover-preview" /> : <span className="cosmetic-popover-placeholder" />}
        {attachmentEntries.length > 0 ? (
          <span className="cosmetic-popover-attachment-strip" aria-hidden="true">
            {attachmentEntries.map((entry, index) => (
              <CatalogImage key={`${entry.imageUrl}-${index}`} entry={entry} className="cosmetic-popover-attachment-chip" />
            ))}
          </span>
        ) : null}
        <SideMarkers side={cosmetic.side} words={words} />
        <i className="roster-rarity-bar" aria-hidden="true" />
      </div>
      <header className="cosmetic-popover-heading">
        <strong>{displayName.primary}</strong>
        {displayName.secondary ? <span>{displayName.secondary}</span> : null}
      </header>
      {cosmetic.customName ? (
        <div className="cosmetic-popover-custom-name">
          <small>{words.customName}</small>
          <span>“{cosmetic.customName}”</span>
        </div>
      ) : null}
      <dl className="cosmetic-popover-facts">
        {cosmetic.itemDefIndex !== null && cosmetic.itemDefIndex !== undefined ? <div><dt>{words.itemDefinition}</dt><dd>{cosmetic.itemDefIndex}</dd></div> : null}
        {cosmetic.paintKit !== null && cosmetic.paintKit !== undefined ? <div><dt>{words.paintKit}</dt><dd>{cosmetic.paintKit}</dd></div> : null}
        {cosmetic.seed !== null && cosmetic.seed !== undefined ? (
          <div><dt>{words.patternTemplate}</dt><dd>{cosmetic.seed}</dd></div>
        ) : cosmetic.kind === "glove" ? (
          <div><dt>{words.patternTemplate}</dt><dd>{words.unresolvedCosmeticSeed}</dd></div>
        ) : null}
        {wear !== null && wear !== undefined ? (
          <div className="cosmetic-popover-wear is-wide">
            <dt>{words.wearRating}</dt>
            <dd><span>{wearLabel(wear, words)}</span><strong>{String(wear)}</strong></dd>
            <span className="cosmetic-wear-scale" aria-hidden="true">
              <i style={{ left: `${Math.min(100, Math.max(0, wear * 100))}%` }} />
            </span>
          </div>
        ) : null}
        {cosmetic.stattrakCounter !== null && cosmetic.stattrakCounter !== undefined ? <div><dt>{words.stattrakCount}</dt><dd>{cosmetic.stattrakCounter}</dd></div> : null}
        {cosmetic.itemId ? <div className="is-wide"><dt>{words.itemId}</dt><dd>{cosmetic.itemId}</dd></div> : null}
      </dl>
      {stickers.length > 0 ? (
        <section className="cosmetic-popover-attachments">
          <h4>{words.stickers.replace("{count}", String(stickers.length))}</h4>
          {stickers.map((sticker) => {
            const entry = resolveStickerCatalog(sticker.stickerId, language);
            return (
              <div className="cosmetic-popover-attachment" key={`${sticker.slot}-${sticker.stickerId}`}>
                {entry ? <CatalogImage entry={entry} className="cosmetic-popover-attachment-image" /> : <span className="cosmetic-popover-attachment-placeholder" />}
                <span>
                  <strong>{entry?.name ?? `#${sticker.stickerId}`}</strong>
                  <small>{words.stickerSlot.replace("{slot}", String(sticker.slot + 1))} · #{sticker.stickerId} · {words.wearRating} {formatConfigNumber(sticker.wear)}</small>
                  <small>X {formatConfigNumber(sticker.offsetX)} · Y {formatConfigNumber(sticker.offsetY)}{sticker.scale !== null && sticker.scale !== undefined ? ` · ×${formatConfigNumber(sticker.scale)}` : ""}{sticker.rotation !== null && sticker.rotation !== undefined ? ` · ${formatConfigNumber(sticker.rotation)}°` : ""}</small>
                </span>
              </div>
            );
          })}
        </section>
      ) : null}
      {charms.length > 0 ? (
        <section className="cosmetic-popover-attachments">
          <h4>{words.charms.replace("{count}", String(charms.length))}</h4>
          {charms.map((charm) => {
            const entry = resolveCharmCatalog(charm.charmId, charm.stickerId, language);
            return (
              <div className="cosmetic-popover-attachment" key={`${charm.slot}-${charm.charmId}`}>
                {entry ? <CatalogImage entry={entry} className="cosmetic-popover-attachment-image" /> : <span className="cosmetic-popover-attachment-placeholder" />}
                <span>
                  <strong>{entry?.name ?? `#${charm.charmId}`}</strong>
                  <small>{words.charmSlot.replace("{slot}", String(charm.slot + 1))} · #{charm.charmId}{charm.seed !== null && charm.seed !== undefined ? ` · ${words.patternTemplate} ${charm.seed}` : ""}</small>
                  <small>X {formatConfigNumber(charm.offsetX)} · Y {formatConfigNumber(charm.offsetY)} · Z {formatConfigNumber(charm.offsetZ)}{charm.highlight !== null && charm.highlight !== undefined ? ` · H ${formatConfigNumber(charm.highlight)}` : ""}</small>
                </span>
              </div>
            );
          })}
        </section>
      ) : null}
    </div>,
    document.body,
  );
}

function metricValues(player: RosterPlayer) {
  const kills = player.kills;
  const deaths = player.deaths;
  const headshots = player.details?.headshotKills;
  const totalDamage = player.details?.totalDamage;
  const rounds = player.details?.statsRounds;
  return {
    kd: kills !== null && kills !== undefined && deaths !== null && deaths !== undefined && deaths > 0
      ? (kills / deaths).toFixed(2)
      : null,
    adr: totalDamage !== null && totalDamage !== undefined && rounds !== null && rounds !== undefined && rounds > 0
      ? (totalDamage / rounds).toFixed(1)
      : null,
    hs: kills !== null && kills !== undefined && kills > 0 && headshots !== null && headshots !== undefined && headshots <= kills
      ? `${(headshots / kills * 100).toFixed(1)}% · ${headshots}`
      : null,
  };
}

function CopyAction({
  value,
  target,
  copiedTarget,
  label,
  copiedLabel,
  onCopy,
}: {
  value: string;
  target: CopyTarget;
  copiedTarget: CopyTarget | null;
  label: string;
  copiedLabel: string;
  onCopy: (value: string, target: CopyTarget) => void;
}) {
  const copied = copiedTarget === target;
  return (
    <button className="roster-copy-action" type="button" onClick={() => onCopy(value, target)} title={label}>
      {copied ? <CheckIcon size={13} /> : <CopyIcon size={13} />}
      <span>{copied ? copiedLabel : label}</span>
    </button>
  );
}

function ViewmodelEvidenceList({
  commands,
  playerKey,
  words,
  copiedTarget,
  onCopy,
}: {
  commands: string[];
  playerKey: string;
  words: TextDictionary;
  copiedTarget: CopyTarget | null;
  onCopy: (value: string, target: CopyTarget) => void;
}) {
  return (
    <section className="roster-evidence-section player-viewmodel-section">
      <header><strong>{words.viewmodelProfiles}</strong></header>
      <ul className="player-config-command-list viewmodel-config-list">
        {commands.map((command, index) => (
          <li key={`${command}:${index}`}>
            <span>
              <code>{command}</code>
            </span>
            <CopyAction value={command} target={targetFor(playerKey, "viewmodel", index)} copiedTarget={copiedTarget} label={words.copyCommand} copiedLabel={words.copied} onCopy={onCopy} />
          </li>
        ))}
      </ul>
    </section>
  );
}

function CosmeticCard({
  cosmetic,
  index,
  playerKey,
  language,
  words,
  onCopy,
  onOpenExternal,
  onPreview,
  inventoryItem,
  inventorySelected,
  inventorySelectionDisabled,
  onToggleInventorySelection,
}: {
  cosmetic: CosmeticEvidence;
  index: number;
  playerKey: string;
  language: Language;
  words: TextDictionary;
  onCopy: (value: string, target: CopyTarget) => void;
  onOpenExternal: (url: string) => void;
  onPreview: (title: string, url: string) => void;
  inventoryItem: InventorySimulatorItem | null;
  inventorySelected: boolean;
  inventorySelectionDisabled: boolean;
  onToggleInventorySelection: () => void;
}) {
  const cardRef = useRef<HTMLElement | null>(null);
  const [hoverAnchor, setHoverAnchor] = useState<DOMRect | null>(null);
  const [menu, setMenu] = useState<ContextMenuState | null>(null);
  const stickers = cosmetic.stickers ?? [];
  const charms = cosmetic.charms ?? [];
  const catalogEntry = resolveCosmeticCatalog(cosmetic, language);
  const displayName = cosmeticDisplayName(cosmetic, words, catalogEntry);
  const title = displayName.full;
  const viewerUrl = buildCosmeticViewerUrl(cosmetic, language);
  const attachmentEntries = [
    ...stickers.map((sticker) => resolveStickerCatalog(sticker.stickerId, language)),
    ...charms.map((charm) => resolveCharmCatalog(charm.charmId, charm.stickerId, language)),
  ].filter((entry): entry is CosmeticCatalogEntry => entry !== null).slice(0, 5);
  const openMenu = (event: ReactMouseEvent) => {
    event.preventDefault();
    setHoverAnchor(null);
    const items = [
      cosmetic.inspectUrl ? {
        label: words.inspectInGame,
        icon: <ExternalLinkIcon size={15} />,
        onSelect: () => onOpenExternal(cosmetic.inspectUrl!),
      } : null,
      cosmetic.inspectUrl ? {
        label: words.copyInspectUrl,
        icon: <CopyIcon size={15} />,
        onSelect: () => onCopy(cosmetic.inspectUrl!, targetFor(playerKey, "inspect", index)),
      } : cosmetic.inspectCommand ? {
        label: words.copyInspectCommand,
        icon: <CopyIcon size={15} />,
        onSelect: () => onCopy(cosmetic.inspectCommand!, targetFor(playerKey, "inspect", index)),
      } : null,
      viewerUrl ? {
        label: words.openPreview360,
        icon: <ReplayIcon size={15} />,
        dividerBefore: Boolean(cosmetic.inspectUrl || cosmetic.inspectCommand),
        onSelect: () => onPreview(title, viewerUrl),
      } : null,
      inventoryItem && !inventorySelectionDisabled ? {
        label: inventorySelected ? words.removeFromInventorySimulatorSelection : words.selectForInventorySimulator,
        icon: inventorySelected ? <CheckIcon size={15} /> : <PlusIcon size={15} />,
        dividerBefore: Boolean(cosmetic.inspectUrl || cosmetic.inspectCommand || viewerUrl),
        onSelect: onToggleInventorySelection,
      } : null,
    ].filter((item): item is NonNullable<typeof item> => item !== null);
    if (items.length === 0) return;
    setMenu({ x: event.clientX, y: event.clientY, items, label: title });
  };
  const showEvidence = () => {
    if (!menu && cardRef.current) setHoverAnchor(cardRef.current.getBoundingClientRect());
  };
  return (
    <>
      <article
        ref={cardRef}
        className={`roster-cosmetic-card${inventorySelected ? " is-inventory-selected" : ""}`}
        style={catalogCardStyle(catalogEntry)}
        tabIndex={0}
        aria-label={title}
        onPointerEnter={showEvidence}
        onPointerLeave={() => setHoverAnchor(null)}
        onFocus={showEvidence}
        onBlur={() => setHoverAnchor(null)}
        onContextMenu={openMenu}
      >
        <div className="roster-cosmetic-visual">
          {catalogEntry ? <CatalogImage key={catalogEntry.imageUrl} entry={catalogEntry} className="roster-cosmetic-preview" /> : <span className="roster-cosmetic-placeholder" />}
          {attachmentEntries.length > 0 ? (
            <span className="roster-cosmetic-attachment-strip" aria-hidden="true">
              {attachmentEntries.map((entry, attachmentIndex) => (
                <CatalogImage key={`${entry.imageUrl}-${attachmentIndex}`} entry={entry} className="roster-cosmetic-attachment-chip" />
              ))}
            </span>
          ) : null}
          <SideMarkers side={cosmetic.side} words={words} />
          {inventoryItem ? (
            <button
              className={`roster-inventory-simulator-action${inventorySelected ? " is-selected" : ""}`}
              type="button"
              disabled={inventorySelectionDisabled}
              aria-pressed={inventorySelected}
              aria-label={`${inventorySelected ? words.removeFromInventorySimulatorSelection : words.selectForInventorySimulator}: ${title}`}
              title={inventorySelected ? words.removeFromInventorySimulatorSelection : words.selectForInventorySimulator}
              onClick={(event) => {
                event.stopPropagation();
                setHoverAnchor(null);
                onToggleInventorySelection();
              }}
            >
              {inventorySelected ? <CheckIcon size={15} /> : <PlusIcon size={15} />}
            </button>
          ) : null}
          <i className="roster-rarity-bar" aria-hidden="true" />
        </div>
        <span className="roster-cosmetic-title" title={title}>
          <strong>{displayName.primary}</strong>
          {displayName.secondary ? <small>{displayName.secondary}</small> : null}
        </span>
      </article>
      {hoverAnchor ? <CosmeticEvidencePopover anchor={hoverAnchor} cosmetic={cosmetic} language={language} words={words} /> : null}
      {menu ? <ContextMenu menu={menu} onClose={() => setMenu(null)} /> : null}
    </>
  );
}

function MusicKitCard({ musicKitId, language, words, inventoryItem, inventorySelected, inventorySelectionDisabled, onToggleInventorySelection }: {
  musicKitId: number;
  language: Language;
  words: TextDictionary;
  inventoryItem: InventorySimulatorItem | null;
  inventorySelected: boolean;
  inventorySelectionDisabled: boolean;
  onToggleInventorySelection: () => void;
}) {
  const entry = resolveMusicKitCatalog(musicKitId, language);
  const title = entry?.name ?? `${words.musicKit} #${musicKitId}`;
  const separator = title.indexOf(" | ");
  const primary = separator >= 0 ? title.slice(0, separator) : words.musicKit;
  const secondary = separator >= 0 ? title.slice(separator + 3) : title;
  return (
    <article className={`roster-cosmetic-card roster-music-kit-card${inventorySelected ? " is-inventory-selected" : ""}`} style={catalogCardStyle(entry)}>
      <div className="roster-cosmetic-visual">
        {entry ? <CatalogImage key={entry.imageUrl} entry={entry} className="roster-cosmetic-preview" /> : <span className="roster-cosmetic-placeholder" />}
        {inventoryItem ? (
          <button
            className={`roster-inventory-simulator-action${inventorySelected ? " is-selected" : ""}`}
            type="button"
            disabled={inventorySelectionDisabled}
            aria-pressed={inventorySelected}
            aria-label={`${inventorySelected ? words.removeFromInventorySimulatorSelection : words.selectForInventorySimulator}: ${title}`}
            title={inventorySelected ? words.removeFromInventorySimulatorSelection : words.selectForInventorySimulator}
            onClick={onToggleInventorySelection}
          >
            {inventorySelected ? <CheckIcon size={15} /> : <PlusIcon size={15} />}
          </button>
        ) : null}
        <i className="roster-rarity-bar" aria-hidden="true" />
      </div>
      <span className="roster-cosmetic-title" title={title}><strong>{primary}</strong><small>{secondary}</small></span>
    </article>
  );
}

function CrosshairEvidence({
  codes,
  viewmodels,
  playerKey,
  words,
  copiedTarget,
  onCopy,
}: {
  codes: string[];
  viewmodels: string[];
  playerKey: string;
  words: TextDictionary;
  copiedTarget: CopyTarget | null;
  onCopy: (value: string, target: CopyTarget) => void;
}) {
  const [previewCode, setPreviewCode] = useState(codes[0]);
  return (
    <div className="player-setup-grid">
      <section className="roster-evidence-section player-crosshair-preview-section">
        <header><strong>{words.crosshairPreview}</strong></header>
        <CrosshairPreview code={previewCode} label={words.crosshairPreview} unavailableLabel={words.crosshairPreviewUnavailable} words={words} />
      </section>
      <div className="player-configuration-commands">
        <section className="roster-evidence-section player-crosshair-section">
          <header><strong>{words.crosshairCodes}</strong></header>
          <ul className="player-config-command-list crosshair-config-list">
            {codes.map((code, index) => (
              <li className={previewCode === code ? "is-selected" : ""} key={`${code}-${index}`}>
                <button className="crosshair-code-select" type="button" onClick={() => setPreviewCode(code)}>
                  <code>{code}</code>
                </button>
                <CopyAction value={code} target={targetFor(playerKey, "crosshair", index)} copiedTarget={copiedTarget} label={words.copyCommand} copiedLabel={words.copied} onCopy={onCopy} />
              </li>
            ))}
          </ul>
        </section>
        {viewmodels.length > 0 ? <ViewmodelEvidenceList commands={viewmodels} playerKey={playerKey} words={words} copiedTarget={copiedTarget} onCopy={onCopy} /> : null}
      </div>
    </div>
  );
}

export function PlayerDossier({
  playerKey,
  player,
  language,
  words,
  copiedTarget,
  onCopy,
  onOpenExternal,
  inventorySelection,
  view = "all",
}: {
  playerKey: string;
  player: RosterPlayer;
  language: Language;
  words: TextDictionary;
  copiedTarget: CopyTarget | null;
  onCopy: (value: string, target: CopyTarget) => void;
  onOpenExternal: (url: string) => void;
  inventorySelection: InventorySimulatorSelectionController;
  view?: "all" | "configuration" | "cosmetics" | "evidence";
}) {
  const [viewer, setViewer] = useState<{ title: string; url: string } | null>(null);
  const details = player.details;
  const metrics = metricValues(player);
  const crosshairCodes = details?.crosshairCodes ?? [];
  const viewmodels = (details?.viewmodels ?? []).map(viewmodelCommand).filter(Boolean);
  const cosmetics = (details?.cosmetics ?? []).filter(isDisplayableCosmeticEvidence);
  const musicKitIds = (details?.musicKitIds ?? []).filter((musicKitId) => (
    resolveMusicKitCatalog(musicKitId, language) !== null
  ));
  const evidenceCount = cosmetics.length + musicKitIds.length;
  const cosmeticInventoryEntries = cosmetics.map((cosmetic, index) => {
    const item = buildCosmeticInventorySimulatorItem(cosmetic, language);
    return {
      key: inventorySimulatorSelectionKey(
        player.steamId || playerKey,
        `cosmetic-${cosmetic.kind}-${cosmetic.itemId || cosmetic.itemDefIndex}-${cosmetic.paintKit}-${cosmetic.side || "both"}-${index}`,
      ),
      item: item && isInventorySimulatorItemCraftable(item) ? item : null,
    };
  });
  const musicKitInventoryEntries = musicKitIds.map((musicKitId) => ({
    key: inventorySimulatorSelectionKey(player.steamId || playerKey, `music-kit-${musicKitId}`),
    item: buildMusicKitInventorySimulatorItem(musicKitId, language),
  }));
  const inventoryEntries = [...cosmeticInventoryEntries, ...musicKitInventoryEntries]
    .filter((entry): entry is { key: string; item: InventorySimulatorItem } => entry.item !== null)
    .slice(0, INVENTORY_SIMULATOR_BATCH_LIMIT);
  const selectedInventoryCount = inventorySelection.items.size;
  const selectedCurrentPlayerCount = inventoryEntries.filter(({ key }) => inventorySelection.items.has(key)).length;
  const steamProfileAvailable = hasSteamProfile(player.steamId);
  const showProfile = view === "all";
  const showConfiguration = view === "all" || view === "configuration" || view === "evidence";
  const showCosmetics = view === "all" || view === "cosmetics" || view === "evidence";
  return (
    <div className="player-dossier">
      {showProfile ? <div className="roster-profile-line">
        <div className="roster-profile-facts">
          <span><small>{words.steamId}</small><code>{player.steamId || "—"}</code></span>
          {metrics.kd ? <span className="roster-profile-stat"><small>{words.kd}</small><strong>{metrics.kd}</strong></span> : null}
          {metrics.adr ? <span className="roster-profile-stat"><small>{words.adr}</small><strong>{metrics.adr}</strong></span> : null}
          {metrics.hs ? <span className="roster-profile-stat"><small>{words.headshotRate}</small><strong>{metrics.hs}</strong></span> : null}
        </div>
        <div className="roster-profile-actions">
          {steamProfileAvailable ? (
            <>
              <CopyAction
                value={player.steamId}
                target={targetFor(playerKey, "steam")}
                copiedTarget={copiedTarget}
                label={words.copySteamId}
                copiedLabel={words.copied}
                onCopy={onCopy}
              />
              <button className="roster-external-action" type="button" onClick={() => onOpenExternal(steamProfileUrl(player.steamId))}>
                <ExternalLinkIcon size={13} />{words.openSteamProfile}
              </button>
            </>
          ) : null}
        </div>
      </div> : null}

      {showConfiguration && crosshairCodes.length > 0 ? (
        <CrosshairEvidence
          key={playerKey}
          codes={crosshairCodes}
          viewmodels={viewmodels}
          playerKey={playerKey}
          words={words}
          copiedTarget={copiedTarget}
          onCopy={onCopy}
        />
      ) : showConfiguration && viewmodels.length > 0 ? (
        <div className="player-setup-grid is-commands-only">
          <div className="player-configuration-commands">
            <ViewmodelEvidenceList commands={viewmodels} playerKey={playerKey} words={words} copiedTarget={copiedTarget} onCopy={onCopy} />
          </div>
        </div>
      ) : showConfiguration ? <p className="roster-evidence-empty">{words.playerConfigurationEmpty}</p> : null}

      {showCosmetics ? <section className="roster-evidence-section roster-cosmetics">
        <header className="roster-cosmetic-toolbar">
          <strong>{words.cosmeticEvidence}{words.cosmeticEvidenceCount.replace("{count}", String(evidenceCount))}</strong>
          {inventoryEntries.length > 0 ? <div className="roster-inventory-batch-actions">
            <span>{words.inventorySimulatorSelectedCount.replace("{count}", String(selectedInventoryCount))}</span>
            <button type="button" disabled={inventorySelection.busy || inventorySelection.full || selectedCurrentPlayerCount === inventoryEntries.length} onClick={() => inventorySelection.select(inventoryEntries)}>
              {words.selectAllInventorySimulator}
            </button>
            <button type="button" disabled={inventorySelection.busy || selectedInventoryCount === 0} onClick={inventorySelection.clear}>
              {words.clearInventorySimulatorSelection}
            </button>
            <button className="is-primary" type="button" disabled={inventorySelection.busy || selectedInventoryCount === 0} onClick={() => void inventorySelection.sync(language)}>
              <PlusIcon size={14} />{inventorySelection.busy ? words.openingInventorySimulator : words.addSelectedToInventorySimulator}
            </button>
          </div> : null}
        </header>
        {evidenceCount > 0 ? (
          <div className="roster-cosmetic-list">
            {cosmetics.map((cosmetic, index) => (
              <CosmeticCard
                cosmetic={cosmetic}
                index={index}
                playerKey={playerKey}
                language={language}
                words={words}
                onCopy={onCopy}
                onOpenExternal={onOpenExternal}
                onPreview={(title, url) => setViewer({ title, url })}
                inventoryItem={inventoryEntries.find(({ key }) => key === cosmeticInventoryEntries[index]?.key)?.item ?? null}
                inventorySelected={inventorySelection.items.has(cosmeticInventoryEntries[index]?.key ?? "")}
                inventorySelectionDisabled={inventorySelection.full && !inventorySelection.items.has(cosmeticInventoryEntries[index]?.key ?? "")}
                onToggleInventorySelection={() => {
                  const entry = cosmeticInventoryEntries[index];
                  if (entry.item) inventorySelection.toggle({ key: entry.key, item: entry.item });
                }}
                key={`${cosmetic.kind}-${cosmetic.itemId || cosmetic.itemDefIndex}-${cosmetic.paintKit}-${cosmetic.side || "both"}-${index}`}
              />
            ))}
            {musicKitIds.map((musicKitId, index) => (
              <MusicKitCard
                musicKitId={musicKitId}
                language={language}
                words={words}
                inventoryItem={inventoryEntries.find(({ key }) => key === musicKitInventoryEntries[index]?.key)?.item ?? null}
                inventorySelected={inventorySelection.items.has(musicKitInventoryEntries[index]?.key ?? "")}
                inventorySelectionDisabled={inventorySelection.full && !inventorySelection.items.has(musicKitInventoryEntries[index]?.key ?? "")}
                onToggleInventorySelection={() => {
                  const entry = musicKitInventoryEntries[index];
                  if (entry.item) inventorySelection.toggle({ key: entry.key, item: entry.item });
                }}
                key={`music-kit-${musicKitId}`}
              />
            ))}
          </div>
        ) : <p className="roster-evidence-empty">{words.cosmeticEvidenceEmpty}</p>}
      </section> : null}

      {viewer ? <CosmeticViewerDialog title={viewer.title} url={viewer.url} words={words} onClose={() => setViewer(null)} /> : null}

    </div>
  );
}

type RosterStatKey = "kills" | "deaths" | "assists" | "adr" | "kd" | "kpr" | "headshots" | "hs" | "mvps";

export function RosterTeam<T extends RosterPlayer>({
  teamId,
  name,
  players,
  words,
  metaLabel,
  matchRounds = null,
  className = "",
  steamProfiles,
  retentionPriority = false,
  onSetPlayerPriority,
  onSelectPlayer,
  onCopy,
  onOpenExternal,
}: RosterTeamProps<T>) {
  const [menu, setMenu] = useState<ContextMenuState | null>(null);
  const canPrioritize = retentionPriority && players.length > 1 && Boolean(onSetPlayerPriority);
  const hasValue = (value: number | null | undefined): value is number => value !== null && value !== undefined;
  const stat = (value: number | null | undefined) => hasValue(value) ? String(value) : "—";
  const playerStats = players.map((player) => {
    const kills = player.kills;
    const deaths = player.deaths;
    const assists = player.assists;
    const headshots = player.details?.headshotKills;
    const totalDamage = player.details?.totalDamage;
    const rounds = player.details?.statsRounds ?? matchRounds;
    const validRounds = rounds !== null && rounds !== undefined && rounds > 0 ? rounds : null;
    return {
      kills: hasValue(kills) ? stat(kills) : null,
      deaths: hasValue(deaths) ? stat(deaths) : null,
      assists: hasValue(assists) ? stat(assists) : null,
      adr: hasValue(totalDamage) && validRounds !== null
        ? (totalDamage / validRounds).toFixed(1)
        : null,
      kd: hasValue(kills) && hasValue(deaths) && deaths > 0
        ? (kills / deaths).toFixed(2)
        : null,
      kpr: hasValue(kills) && validRounds !== null
        ? (kills / validRounds).toFixed(2)
        : null,
      headshots: hasValue(headshots) ? String(headshots) : null,
      hs: hasValue(kills) && kills > 0 && hasValue(headshots) && headshots <= kills
        ? `${(headshots / kills * 100).toFixed(1)}%`
        : null,
      mvps: hasValue(player.mvps) ? String(player.mvps) : null,
    };
  });
  const allColumns: Array<{ key: RosterStatKey; label: string; width: string }> = [
    { key: "kills", label: "K", width: "minmax(24px, .32fr)" },
    { key: "deaths", label: "D", width: "minmax(24px, .32fr)" },
    { key: "assists", label: "A", width: "minmax(24px, .32fr)" },
    { key: "adr", label: words.adr, width: "minmax(36px, .48fr)" },
    { key: "kd", label: words.kd, width: "minmax(36px, .46fr)" },
    { key: "kpr", label: "KPR", width: "minmax(40px, .5fr)" },
    { key: "headshots", label: words.headshotKillsShort, width: "minmax(38px, .46fr)" },
    { key: "hs", label: "HS%", width: "minmax(48px, .58fr)" },
    { key: "mvps", label: "MVP", width: "minmax(34px, .42fr)" },
  ];
  const columns = allColumns.filter((column) => playerStats.some((values) => values[column.key] !== null));
  const gridTemplateColumns = `minmax(160px, 1.45fr) ${columns.map((column) => column.width).join(" ")}`;
  const openPlayerMenu = (
    event: ReactMouseEvent,
    player: T,
    selection: PlayerSelection,
  ) => {
    event.preventDefault();
    event.stopPropagation();
    const selectionKey = playerSelectionKey(selection);
    const steamAvailable = hasSteamProfile(player.steamId);
    setMenu({
      x: event.clientX,
      y: event.clientY,
      label: player.name,
      items: [
        {
          label: words.playerAnalysis,
          icon: <ReplayIcon size={15} />,
          onSelect: () => onSelectPlayer(selection),
        },
        ...(steamAvailable && onCopy ? [{
          label: words.copySteamId,
          icon: <CopyIcon size={15} />,
          dividerBefore: true,
          onSelect: () => onCopy(player.steamId, targetFor(selectionKey, "steam")),
        }] : []),
        ...(steamAvailable && onOpenExternal ? [{
          label: words.openSteamProfile,
          icon: <ExternalLinkIcon size={15} />,
          onSelect: () => onOpenExternal(steamProfileUrl(player.steamId)),
        }] : []),
      ],
    });
  };
  return (
    <>
      <section className={`archive-roster-team ${className}`.trim()} aria-label={name}>
      <header>
        <div>
          <strong title={name}>{name}</strong>
        </div>
        {metaLabel ? <span>{metaLabel}</span> : null}
      </header>
      <div className={`archive-roster-stat-row${canPrioritize ? " has-retention" : ""}`}>
        {canPrioritize ? <span className="roster-retention-head" title={words.retentionPriority}>#</span> : null}
        <div className="archive-roster-stat-head" style={{ gridTemplateColumns }} aria-hidden="true">
          <span>{words.playerColumn}</span>
          {columns.map((column) => <span key={column.key}>{column.label}</span>)}
        </div>
      </div>
      <ul>
        {players.map((player, playerIndex) => {
          const selection = { teamId, playerIndex };
          const selectionKey = playerSelectionKey(selection);
          const values = playerStats[playerIndex];
          return (
            <li
              className={`roster-player${canPrioritize ? " has-retention" : ""}`}
              key={`${teamId}:${player.steamId || playerIndex}`}
              onContextMenu={(event) => openPlayerMenu(event, player, selection)}
              style={demoPlayerColorValue(player.playerColor)
                ? ({ "--roster-player-color": demoPlayerColorValue(player.playerColor) } as CSSProperties)
                : undefined}
            >
              {canPrioritize ? (
                <select
                  className="roster-retention-select"
                  value={playerIndex + 1}
                  aria-label={words.retentionRank.replace("{rank}", String(playerIndex + 1))}
                  title={words.retentionRank.replace("{rank}", String(playerIndex + 1))}
                  onChange={(event) => {
                    onSetPlayerPriority?.(playerIndex, Number(event.currentTarget.value));
                  }}
                >
                  {players.map((_, priorityIndex) => (
                    <option key={priorityIndex + 1} value={priorityIndex + 1}>
                      {priorityIndex + 1}
                    </option>
                  ))}
                </select>
              ) : null}
              <button
                className="roster-player-summary"
                type="button"
                data-player-key={selectionKey}
                style={{ gridTemplateColumns }}
                title={words.rosterPlayerHint}
                onClick={() => onSelectPlayer(selection)}
              >
                <SteamPlayerIdentity
                  className="roster-player-identity"
                  profile={steamProfiles?.get(player.steamId)}
                  demoName={player.name}
                  steamId={player.steamId}
                  playerColor={player.playerColor}
                  size="compact"
                  showAlias={false}
                />
                {columns.map((column) => (
                  <span className={`archive-roster-stat is-${column.key}`} key={column.key}>
                    {values[column.key] ?? "—"}
                  </span>
                ))}
              </button>
            </li>
          );
        })}
      </ul>
      </section>
      {menu ? <ContextMenu menu={menu} onClose={() => setMenu(null)} /> : null}
    </>
  );
}
