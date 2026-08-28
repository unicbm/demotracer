/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useMemo, useState } from "react";
import { ChevronIcon, SearchIcon, TraceMark } from "../icons";
import type { Language } from "../types";
import "./faq-workspace.css";

type FaqCategory = "basics" | "data" | "runtime";

interface FaqEntry {
  id: string;
  category: FaqCategory;
  question: string;
  summary: string;
  paragraphs: string[];
  keywords: string[];
}

interface FaqCopy {
  title: string;
  searchPlaceholder: string;
  searchLabel: string;
  allCategories: string;
  categoryLabel: string;
  categories: Record<FaqCategory, string>;
  questions: string;
  backToQuestions: string;
  noResultsTitle: string;
  clearSearch: string;
  entries: FaqEntry[];
}

const FAQ_COPY: Record<Language, FaqCopy> = {
  zh: {
    title: "常见问题",
    searchPlaceholder: "搜索问题",
    searchLabel: "搜索常见问题",
    allCategories: "全部",
    categoryLabel: "问题分类",
    categories: {
      basics: "解析与转换",
      data: "比赛信息",
      runtime: "回放环境",
    },
    questions: "问题",
    backToQuestions: "返回问题列表",
    noResultsTitle: "没有匹配的问题",
    clearSearch: "清除搜索",
    entries: [
      {
        id: "full-parse",
        category: "basics",
        question: "为什么只选一个回合，也要读取完整 Demo？",
        summary: "回合边界、玩家身份和状态必须沿完整时间线还原，因此仍需解析整场 Demo。",
        paragraphs: [
          "少选回合只会减少生成的 .dtr 文件，通常不会等比例缩短首次解析。",
        ],
        keywords: ["完整解析", "一个回合", "慢", "parse", "round"],
      },
      {
        id: "parse-estimate",
        category: "basics",
        question: "预计耗时是怎么计算的？",
        summary: "根据本机已完成任务的文件大小和实际耗时估算。",
        paragraphs: [
          ".dem.zst 解压前只能按压缩包大小估算；Demo 版本、语音与饰品数据、磁盘负载都会造成偏差。",
        ],
        keywords: ["耗时", "预计", "ETA", "CPU", "速度", "时间"],
      },
      {
        id: "parse-vs-convert",
        category: "basics",
        question: "解析和转换有什么区别？",
        summary: "解析建立比赛和回合索引；转换把所选回合写成回放归档。",
        paragraphs: [
          "转换会生成 .dtr、manifest.json 和所选附加内容，并在完成前校验输出。",
        ],
        keywords: ["解析", "转换", "分析", "入库", "manifest", "dtr"],
      },
      {
        id: "batch-folder-scan",
        category: "basics",
        question: "文件夹扫描会对原始 Demo 做什么？",
        summary: "扫描只查找本地 .dem 和 .dem.zst，不修改、移动或上传原文件。",
        paragraphs: [
          "输出只写入所选 DemoTracer 库目录；原始 Demo 保留在原位置。",
        ],
        keywords: ["文件夹", "扫描", "原文件", "上传", "隐私", "重复"],
      },
      {
        id: "hltv-parts",
        category: "basics",
        question: "HLTV 的 P1 + P2 会怎样处理？",
        summary: "选择任一分段时，DemoTracer 会查找同目录的连续分段并作为一场比赛解析。",
        paragraphs: [
          "分段缺失、重名或比赛数据不连续时，解析会报错，不会猜测合并。",
        ],
        keywords: ["HLTV", "P1", "P2", "超长", "合并", "两个 demo"],
      },
      {
        id: "match-metadata",
        category: "data",
        question: "比分、KDA、SteamID 和服务器信息来自哪里？",
        summary: "这些信息直接来自 Demo，不会联网补全玩家资料。",
        paragraphs: [
          "Demo 提前结束或缺少字段时，相应位置会显示为未知。",
        ],
        keywords: ["比分", "KDA", "SteamID", "服务器", "server id", "玩家", "metadata"],
      },
      {
        id: "runtime-vendors",
        category: "runtime",
        question: "为什么不能混用不同插件附带的 BotController / BotHider？",
        summary: "同名文件不等于接口兼容。",
        paragraphs: [
          "环境检测发现混装时，请安装完整且版本匹配的 DemoTracer 回放组件，不要单独替换 DLL。",
        ],
        keywords: ["BotController", "BotHider", "Bot Improver", "冲突", "ABI", "vendor", "依赖"],
      },
      {
        id: "local-config",
        category: "runtime",
        question: "GUI 设置和 demotracer.config.json 是什么关系？",
        summary: "GUI 管理桌面端默认值；demotracer.config.json 管理服务器插件默认值。",
        paragraphs: [
          "编辑器会保留无法识别的字段；保存后需重载 DemoTracer 才会影响运行态。",
        ],
        keywords: ["设置", "json", "demotracer.config.json", "服务器", "handoff", "配置"],
      },
    ],
  },
  en: {
    title: "Frequently asked questions",
    searchPlaceholder: "Search questions",
    searchLabel: "Search frequently asked questions",
    allCategories: "All",
    categoryLabel: "Question categories",
    categories: {
      basics: "Parsing & conversion",
      data: "Match information",
      runtime: "Playback environment",
    },
    questions: "Questions",
    backToQuestions: "Back to questions",
    noResultsTitle: "No matching questions",
    clearSearch: "Clear search",
    entries: [
      {
        id: "full-parse",
        category: "basics",
        question: "Why is the complete demo read when I select only one round?",
        summary: "Round boundaries, player identity, and state must be reconstructed across the full timeline.",
        paragraphs: [
          "Selecting fewer rounds reduces the number of .dtr files written, but usually does not reduce the initial parse time by the same proportion.",
        ],
        keywords: ["full parse", "one round", "slow", "parse", "round"],
      },
      {
        id: "parse-estimate",
        category: "basics",
        question: "How is the time estimate calculated?",
        summary: "It uses the file size and elapsed time of completed jobs on this computer.",
        paragraphs: [
          "Before decompression, .dem.zst estimates use compressed size. Demo version, voice and cosmetic data, and storage load can all change the result.",
        ],
        keywords: ["estimate", "ETA", "CPU", "speed", "time"],
      },
      {
        id: "parse-vs-convert",
        category: "basics",
        question: "What is the difference between parsing and conversion?",
        summary: "Parsing builds the match and round index; conversion writes selected rounds into a replay archive.",
        paragraphs: [
          "Conversion writes .dtr files, manifest.json, and selected extras, then validates the output.",
        ],
        keywords: ["parse", "convert", "analysis", "library", "manifest", "dtr"],
      },
      {
        id: "batch-folder-scan",
        category: "basics",
        question: "What does a folder scan do to the source demos?",
        summary: "It finds local .dem and .dem.zst files without modifying, moving, or uploading them.",
        paragraphs: [
          "Output is written only to the selected DemoTracer library directory. Source demos remain where they are.",
        ],
        keywords: ["folder", "scan", "source", "upload", "privacy", "duplicate"],
      },
      {
        id: "hltv-parts",
        category: "basics",
        question: "How are HLTV P1 + P2 demos handled?",
        summary: "Selecting either part makes DemoTracer collect consecutive parts with the same name from that folder and parse them as one match.",
        paragraphs: [
          "Missing, ambiguous, or discontinuous parts produce an error instead of a guessed merge.",
        ],
        keywords: ["HLTV", "P1", "P2", "long", "merge", "two demos"],
      },
      {
        id: "match-metadata",
        category: "data",
        question: "Where do the score, KDA, SteamIDs, and server details come from?",
        summary: "They come directly from the demo; player profiles are not completed through online lookups.",
        paragraphs: [
          "When a demo ends early or omits a field, the corresponding value remains unknown.",
        ],
        keywords: ["score", "KDA", "SteamID", "server", "players", "metadata"],
      },
      {
        id: "runtime-vendors",
        category: "runtime",
        question: "Why can’t BotController / BotHider builds from different plugins be mixed?",
        summary: "Matching filenames do not prove interface compatibility.",
        paragraphs: [
          "If environment inspection detects a mixed install, install a complete matching DemoTracer playback bundle instead of replacing one DLL.",
        ],
        keywords: ["BotController", "BotHider", "Bot Improver", "conflict", "ABI", "vendor", "dependency"],
      },
      {
        id: "local-config",
        category: "runtime",
        question: "How do GUI settings relate to demotracer.config.json?",
        summary: "The GUI controls desktop defaults; demotracer.config.json controls server-plugin defaults.",
        paragraphs: [
          "The editor preserves unrecognized fields. Reload DemoTracer after saving to update the runtime configuration.",
        ],
        keywords: ["settings", "json", "demotracer.config.json", "server", "handoff", "configuration"],
      },
    ],
  },
};

export interface FaqWorkspaceProps {
  language: Language;
}

export function FaqWorkspace({ language }: FaqWorkspaceProps) {
  const copy = FAQ_COPY[language];
  const [query, setQuery] = useState("");
  const [category, setCategory] = useState<FaqCategory | "all">("all");
  const [selectedId, setSelectedId] = useState(copy.entries[0].id);
  const [mobileAnswerOpen, setMobileAnswerOpen] = useState(false);

  const filteredEntries = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase(language === "zh" ? "zh-CN" : "en-US");
    return copy.entries.filter((entry) => {
      if (category !== "all" && entry.category !== category) return false;
      if (!needle) return true;
      return [entry.question, entry.summary, ...entry.paragraphs, ...entry.keywords]
        .join(" ")
        .toLocaleLowerCase(language === "zh" ? "zh-CN" : "en-US")
        .includes(needle);
    });
  }, [category, copy.entries, language, query]);

  const selectedEntry = filteredEntries.find((entry) => entry.id === selectedId) ?? filteredEntries[0] ?? null;
  const categories = Object.entries(copy.categories) as [FaqCategory, string][];

  return (
    <section className="faq-workspace" aria-labelledby="faq-workspace-title">
      <header className="faq-hero">
        <div className="faq-hero-mark" aria-hidden="true"><TraceMark size={30} /></div>
        <div className="faq-hero-copy">
          <h1 id="faq-workspace-title">{copy.title}</h1>
        </div>
      </header>

      <div className="faq-controls">
        <label className="faq-search">
          <SearchIcon size={17} />
          <span className="sr-only">{copy.searchLabel}</span>
          <input
            type="search"
            value={query}
            onChange={(event) => { setQuery(event.target.value); setMobileAnswerOpen(false); }}
            placeholder={copy.searchPlaceholder}
          />
        </label>
        <div className="faq-category-list" aria-label={copy.categoryLabel}>
          <button className={category === "all" ? "is-active" : ""} type="button" onClick={() => { setCategory("all"); setMobileAnswerOpen(false); }}>{copy.allCategories}</button>
          {categories.map(([value, label]) => (
            <button className={category === value ? "is-active" : ""} type="button" key={value} onClick={() => { setCategory(value); setMobileAnswerOpen(false); }}>{label}</button>
          ))}
        </div>
      </div>

      <div className={`faq-content${mobileAnswerOpen ? " is-answer-open" : ""}`}>
        {filteredEntries.length > 0 && selectedEntry ? (
          <>
            <nav className="faq-question-pane" aria-label={copy.questions}>
              <span className="faq-pane-label">{copy.questions}<b>{filteredEntries.length}</b></span>
              <div className="faq-question-list">
                {filteredEntries.map((entry) => (
                  <button
                    className={entry.id === selectedEntry.id ? "is-active" : ""}
                    type="button"
                    key={entry.id}
                    aria-current={entry.id === selectedEntry.id ? "page" : undefined}
                    onClick={() => { setSelectedId(entry.id); setMobileAnswerOpen(true); }}
                  >
                    <span>{entry.question}</span>
                    {category === "all" ? <small>{copy.categories[entry.category]}</small> : null}
                    <ChevronIcon size={15} />
                  </button>
                ))}
              </div>
            </nav>

            <article className="faq-answer-pane" key={selectedEntry.id}>
              <button className="faq-mobile-back" type="button" onClick={() => setMobileAnswerOpen(false)}>
                <ChevronIcon size={15} />
                <span>{copy.backToQuestions}</span>
              </button>
              <h2>{selectedEntry.question}</h2>
              <p className="faq-answer-summary">{selectedEntry.summary}</p>
              <div className="faq-answer-body">
                {selectedEntry.paragraphs.map((paragraph) => <p key={paragraph}>{paragraph}</p>)}
              </div>
            </article>
          </>
        ) : (
          <div className="faq-empty-state">
            <SearchIcon size={24} />
            <strong>{copy.noResultsTitle}</strong>
            <button className="secondary-button" type="button" onClick={() => { setQuery(""); setCategory("all"); setMobileAnswerOpen(false); }}>{copy.clearSearch}</button>
          </div>
        )}
      </div>
    </section>
  );
}
