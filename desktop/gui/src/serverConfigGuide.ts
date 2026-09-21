/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import type { Language } from "./types";

export type ServerConfigGuideGroup = "general" | "handoff" | "fidelity" | "match" | "cosmetics";

export interface ServerConfigGuideField {
  path: string;
  group: ServerConfigGuideGroup;
  type: "boolean" | "number" | "enum";
  accepted?: readonly string[];
  defaultValue?: string;
  description: Record<Language, string>;
}

const booleanField = (
  path: string,
  group: ServerConfigGuideGroup,
  defaultValue: boolean | undefined,
  zh: string,
  en: string,
): ServerConfigGuideField => ({
  path,
  group,
  type: "boolean",
  defaultValue: defaultValue === undefined ? undefined : String(defaultValue),
  description: { zh, en },
});

const enumField = (
  path: string,
  group: ServerConfigGuideGroup,
  accepted: readonly string[],
  defaultValue: string,
  zh: string,
  en: string,
): ServerConfigGuideField => ({ path, group, type: "enum", accepted, defaultValue, description: { zh, en } });

export const SERVER_CONFIG_GUIDE: readonly ServerConfigGuideField[] = [
  enumField("identity", "general", ["off", "name", "steam", "avatar", "full"], "steam", "回放选手身份同步级别。", "Replay player identity sync level."),
  booleanField("allow_partial", "general", true, "安全 Bot 数量不足时允许部分回放。", "Allow a partial replay when too few safe bot slots exist."),
  booleanField("playoff", "general", false, "序列耗尽后按录像开局主武器选择长枪池续播，不依赖余额或经济同步。", "Continue from recorded rifle/sniper openings after a sequence is exhausted, independently of cash or balance alignment."),
  booleanField("chat_auto", "general", true, "按 Demo 时间线自动回放文字聊天。", "Replay demo chat on the recorded timeline."),
  booleanField("round_banner", "general", true, "回合开始时显示 Demo 回合提示。", "Show the demo round banner when playback starts."),

  enumField("handoff.mode", "handoff", ["off", "death", "contact", "death_or_contact", "death_contact_c4"], "death_contact_c4", "何时把 Bot 控制权交回原生 AI。", "When replay control returns to the native bot AI."),
  enumField("handoff.scope", "handoff", ["slot", "all"], "slot", "接触或死亡时释放当前槽位，或释放全部回放槽位。", "Release the triggering slot or every replay slot on contact/death."),
  booleanField("handoff.threat_360", "handoff", true, "回放期间允许原生 360 度威胁感知。", "Enable native 360-degree threat perception during replay."),
  {
    path: "handoff.threat_360_range",
    group: "handoff",
    type: "number",
    accepted: ["150–800"],
    defaultValue: "420",
    description: { zh: "兼容回退检测器的威胁半径；超出范围会被夹取。", en: "Threat radius for the compatibility fallback detector; values are clamped." },
  },
  booleanField("handoff.threat_360_los", "handoff", true, "兼容回退检测器要求视线可达。", "Require line of sight in the compatibility fallback detector."),
  enumField("handoff.viewmodel_continuity", "handoff", ["round", "release"], "round", "交接后保留持枪视角到回合边界，或立即恢复。", "Keep the replay viewmodel until the round boundary or restore it immediately."),

  enumField("fidelity.preset", "fidelity", ["default", "full", "handoff_safe", "off"], "default", "回放保真预设；下列布尔字段可覆盖单项。", "Replay fidelity preset; the booleans below can override individual parts."),
  booleanField("fidelity.weapons", "fidelity", undefined, "武器、装备与当前槽位对齐。", "Align weapons, loadout, and active slot."),
  booleanField("fidelity.projectiles", "fidelity", undefined, "投掷物与 Demo 证据对齐。", "Align projectiles to demo evidence."),
  booleanField("fidelity.crosshair", "fidelity", true, "观战时同步并在结束后恢复准星。", "Sync the spectator crosshair and restore it afterwards."),
  booleanField("fidelity.left_hand_desired", "fidelity", undefined, "写入 Demo 记录的左右手 desired 状态。", "Write the demo-recorded left-hand desired state."),
  booleanField("fidelity.balance", "fidelity", false, "在对应 DTR 回合开始时把 replay Bot 余额同步为 Demo 记录值。", "Set each replay bot's balance from demo evidence when its DTR round starts."),

  enumField("match.preset", "match", ["off", "scoreboard", "full"], "off", "本地赛事展示预设，不改变回放移动。", "Local match-presentation preset; it does not change replay movement."),
  booleanField("match.scoreboard", "match", undefined, "尽力同步 KDA、MVP、队名与比分。", "Best-effort KDA, MVP, team-name, and score synchronization."),

  enumField("cosmetics.preset", "cosmetics", ["off", "weapons", "basic", "full"], "off", "只消费显式导出的 Demo 饰品证据。", "Consume only explicitly exported demo cosmetic evidence."),
  booleanField("cosmetics.weapons", "cosmetics", undefined, "应用武器皮肤证据。", "Apply weapon finish evidence."),
  booleanField("cosmetics.knives", "cosmetics", undefined, "应用刀具证据。", "Apply knife evidence."),
  booleanField("cosmetics.gloves", "cosmetics", undefined, "应用手套证据。", "Apply glove evidence."),
  booleanField("cosmetics.names", "cosmetics", undefined, "应用武器自定义名称。", "Apply custom weapon names."),
  booleanField("cosmetics.agents", "cosmetics", false, "应用非默认探员模型证据。", "Apply non-default agent-model evidence."),
  booleanField("cosmetics.stickers", "cosmetics", undefined, "应用已导出的贴纸证据。", "Apply exported sticker evidence."),
  booleanField("cosmetics.charms", "cosmetics", undefined, "应用已导出的挂件证据。", "Apply exported charm evidence."),
  booleanField("cosmetics.preserve_native", "cosmetics", false, "缺少对应证据时保留 Bot 原生饰品。", "Keep native bot cosmetics when matching evidence is absent."),
] as const;
