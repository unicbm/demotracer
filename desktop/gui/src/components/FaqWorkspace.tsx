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
  points?: string[];
  keywords: string[];
}

interface FaqCopy {
  title: string;
  searchPlaceholder: string;
  searchLabel: string;
  allCategories: string;
  categories: Record<FaqCategory, string>;
  questions: string;
  answer: string;
  backToQuestions: string;
  noResultsTitle: string;
  noResultsBody: string;
  clearSearch: string;
  entries: FaqEntry[];
}

const FAQ_COPY: Record<Language, FaqCopy> = {
  zh: {
    title: "常见问题",
    searchPlaceholder: "搜索问题，例如“为什么要完整解析”",
    searchLabel: "搜索常见问题",
    allCategories: "全部",
    categories: {
      basics: "解析与转换",
      data: "比赛信息",
      runtime: "回放环境",
    },
    questions: "问题",
    answer: "说明",
    backToQuestions: "返回问题列表",
    noResultsTitle: "没有找到相关问题",
    noResultsBody: "换一个关键词，或切回“全部”分类。",
    clearSearch: "清除搜索",
    entries: [
      {
        id: "full-parse",
        category: "basics",
        question: "为什么只选一个回合，也要读取完整 Demo？",
        summary: "回合选择发生在完整解析之后，不是从文件中随机截取一段。",
        paragraphs: [
          "CS2 Demo 的玩家身份、回合边界、比分和事件上下文需要沿时间线还原。即使最终只导出一个回合，解析器仍要先读完整场比赛，才能可靠判断这个回合是否完整、是否可回放。",
          "因此“选择回合”会减少写出的 .dtr 数量，但通常不会按比例缩短第一次解析时间。",
        ],
        keywords: ["完整解析", "一个回合", "慢", "parse", "round"],
      },
      {
        id: "parse-estimate",
        category: "basics",
        question: "预计耗时是怎么计算的？",
        summary: "首个 Demo 会成为这台电脑的本地速度样本，后续估算仍是近似值。",
        paragraphs: [
          "完成第一个 Demo 的解析后，DemoTracer 会根据文件大小与实际解析耗时建立本机基线，再为队列中的文件显示大致剩余解析时间。这个基线只保存在本地；.dem.zst 在解压前只能按压缩包大小做初始粗略估计。",
          "不同版本 Demo、语音和饰品数据量、磁盘速度以及同时运行的程序都会改变实际耗时，所以估算应理解为计划参考，而不是完成时间承诺。",
        ],
        keywords: ["耗时", "预计", "ETA", "CPU", "速度", "时间"],
      },
      {
        id: "parse-vs-convert",
        category: "basics",
        question: "解析和转换有什么区别？",
        summary: "解析读取整场比赛并建立索引；转换只把选中的回合写成回放档案。",
        paragraphs: [
          "解析阶段产生地图、回合、阵容、比分和质量判断。进入回合选择页时，这些信息已经可用。",
          "转换阶段根据你的回合和导出选项生成 .dtr、manifest.json 及可选附加内容，并在完成后验证输出。",
        ],
        keywords: ["解析", "转换", "分析", "入库", "manifest", "dtr"],
      },
      {
        id: "batch-folder-scan",
        category: "basics",
        question: "文件夹扫描会对原始 Demo 做什么？",
        summary: "它只查找本地 .dem 和 .dem.zst 并建立任务清单，不修改、移动或上传原文件。",
        paragraphs: [
          "扫描结果会先作为待处理队列展示，由你确认后再开始。已知源路径会在扫描页标记；入库解析后还会用完整内容标识识别已有档案，避免无意覆盖。",
          "输出只写入你选择的 DemoTracer 库目录；原始 Demo 仍留在原位置。",
        ],
        keywords: ["文件夹", "扫描", "原文件", "上传", "隐私", "重复"],
      },
      {
        id: "batch-limit",
        category: "basics",
        question: "为什么一次不能加入无限多个 Demo？",
        summary: "队列上限用于控制磁盘占用、错误可读性和预计完成时间。",
        paragraphs: [
          "批量解析适合有限并发，而不是同时启动所有文件。DemoTracer 会让少量任务并行，其余任务排队，避免内存和磁盘争用反而拖慢整批工作。",
          "如果扫描结果很多，建议分批确认。上限只限制单次任务，不限制本地 Demo 库的总规模。",
        ],
        keywords: ["上限", "太多", "并发", "队列", "内存", "批量"],
      },
      {
        id: "batch-stop-resume",
        category: "basics",
        question: "中途停止会丢掉已经完成的内容吗？",
        summary: "停止只应终止未完成工作；已验证入库的档案必须保留。",
        paragraphs: [
          "请求停止后，队列不再派发新任务。已经完成并通过验证的 Demo 会继续留在库中，失败、取消和未开始的任务会保留明确状态，之后可以单独重试。",
          "不要把临时写入目录当作成功结果。只有完成 manifest 和文件验证后，任务才应显示为“已入库”。",
        ],
        points: ["已完成：保留并可立即打开", "失败：显示原因并允许重试", "未开始：保留在队列或由你移除"],
        keywords: ["停止", "取消", "恢复", "进度", "丢失", "重试"],
      },
      {
        id: "hltv-parts",
        category: "basics",
        question: "HLTV 的 P1 + P2 超长 Demo 会自动合并吗？",
        summary: "当前版本不合并，它们会被识别为两个独立 Demo。",
        paragraphs: [
          "部分 HLTV 比赛会把同一场超长比赛拆成 P1 和 P2。自动判断两段的连续关系并合并回合编号需要额外的比赛级校验，本版本暂不处理。",
          "可以分别入库和转换，但界面不应把它们描述成一份连续档案。",
        ],
        keywords: ["HLTV", "P1", "P2", "超长", "合并", "两个 demo"],
      },
      {
        id: "match-metadata",
        category: "data",
        question: "比分、KDA、SteamID 和服务器信息来自哪里？",
        summary: "它们直接来自 Demo 解析结果，不需要联网查询玩家资料。",
        paragraphs: [
          "解析完成后，DemoTracer 已拥有地图、服务器名称、平台线索、队伍比分、玩家名称、SteamID64 以及 Demo 中最后可靠的 K/D/A 快照。回合选择页和转换后的档案页可以复用同一份比赛摘要。",
          "如果 Demo 在比赛结束前停止、缺少相关字段或来源格式特殊，部分值可能显示为未知。界面不应凭空补全。",
        ],
        keywords: ["比分", "KDA", "SteamID", "服务器", "server id", "玩家", "metadata"],
      },
      {
        id: "runtime-vendors",
        category: "runtime",
        question: "为什么不能混用不同插件附带的 BotController / BotHider？",
        summary: "同名依赖可能由不同项目按各自接口版本构建，文件存在不代表兼容。",
        paragraphs: [
          "DemoTracer 和 CS2 Bot Improver 都可能附带 BotController 或 BotHider，但它们的 vendor 版本、导出能力和调用约定可能不同。混装后最常见的问题是插件看似加载，实际在回放开始、换图或卸载时出错。",
          "环境检测会核对安装收据、ABI/API 与运行中的 heartbeat。发现混装时，应安装一整套匹配的 DemoTracer playback bundle，而不是只替换某一个 DLL。",
        ],
        keywords: ["BotController", "BotHider", "Bot Improver", "冲突", "ABI", "vendor", "依赖"],
      },
      {
        id: "local-config",
        category: "runtime",
        question: "GUI 设置和 demotracer.config.json 是什么关系？",
        summary: "GUI 管理本机转换默认值；服务器 JSON 管理 DemoTracer 的运行时默认值。",
        paragraphs: [
          "输出位置、Demo 库、转换选项和通知偏好属于桌面端设置。服务器旁的 demotracer.config.json 则影响 CSS 插件加载后的回放、handoff 和对齐行为。",
          "GUI 可以为常用且安全的 JSON 字段提供编辑入口，但不应静默覆盖未知字段。修改前应读取现有文件，保存时保留当前界面不认识的配置。",
        ],
        keywords: ["设置", "json", "demotracer.config.json", "服务器", "handoff", "配置"],
      },
      {
        id: "notifications",
        category: "runtime",
        question: "什么时候会有提示音？",
        summary: "批量任务完成、需要处理错误或被中断时，提示音最有价值。",
        paragraphs: [
          "长任务不应要求一直盯着窗口。整批完成和出现阻塞性错误应使用不同声音，并同时保留可见状态，避免静音用户错过结果。",
          "提示音应可以在设置中关闭，也不应为队列里每个正常完成的 Demo 连续播放。单个任务完成可用轻量视觉反馈，整批完成再播放一次声音。",
        ],
        keywords: ["提示音", "通知", "完成", "报错", "静音", "声音"],
      },
    ],
  },
  en: {
    title: "Frequently asked questions",
    searchPlaceholder: "Search, for example “why is a full parse required?”",
    searchLabel: "Search frequently asked questions",
    allCategories: "All",
    categories: {
      basics: "Parsing & conversion",
      data: "Match information",
      runtime: "Playback environment",
    },
    questions: "Questions",
    answer: "Answer",
    backToQuestions: "Back to questions",
    noResultsTitle: "No matching questions",
    noResultsBody: "Try another term or switch back to the All category.",
    clearSearch: "Clear search",
    entries: [
      {
        id: "full-parse",
        category: "basics",
        question: "Why is the complete demo read when I select only one round?",
        summary: "Round selection happens after a full parse; it is not a random byte-range extraction.",
        paragraphs: [
          "Player identity, round boundaries, scores, and event context must be reconstructed along the CS2 demo timeline. The parser reads the match before it can determine whether a round is complete and replayable.",
          "Selecting fewer rounds reduces the number of .dtr files written, but normally does not reduce the first parse time by the same proportion.",
        ],
        keywords: ["full parse", "one round", "slow", "parse", "round"],
      },
      {
        id: "parse-estimate",
        category: "basics",
        question: "How is the time estimate calculated?",
        summary: "The first completed demo becomes a local speed sample for this computer; later estimates remain approximate.",
        paragraphs: [
          "After the first demo is parsed, DemoTracer combines file size and actual parse time into a machine-local baseline, then shows an approximate remaining parse time for queued files. The baseline stays on this computer; before decompression, a .dem.zst archive size is only an initial rough estimate.",
          "Demo versions, voice and cosmetic data, storage speed, and other running software all affect throughput. Treat the estimate as planning guidance, not a completion-time promise.",
        ],
        keywords: ["estimate", "ETA", "CPU", "speed", "time"],
      },
      {
        id: "parse-vs-convert",
        category: "basics",
        question: "What is the difference between parsing and conversion?",
        summary: "Parsing reads the match and builds its index; conversion writes the selected rounds into a replay archive.",
        paragraphs: [
          "Parsing produces the map, rounds, roster, score, and quality assessment. That information is already available when the round selection page opens.",
          "Conversion applies your round and export choices, writes .dtr files, manifest.json, and optional sidecars, then validates the result.",
        ],
        keywords: ["parse", "convert", "analysis", "library", "manifest", "dtr"],
      },
      {
        id: "batch-folder-scan",
        category: "basics",
        question: "What does a folder scan do to the source demos?",
        summary: "It finds local .dem and .dem.zst files and builds a job list; it does not modify, move, or upload them.",
        paragraphs: [
          "The scan is shown as a pending queue for confirmation. Known source paths are marked immediately; after parsing, full content identity also reconciles an existing archive without overwriting it.",
          "Output is written only to the DemoTracer library directory you choose. The source demos remain where they are.",
        ],
        keywords: ["folder", "scan", "source", "upload", "privacy", "duplicate"],
      },
      {
        id: "batch-limit",
        category: "basics",
        question: "Why can’t I add an unlimited number of demos at once?",
        summary: "A queue limit keeps disk use, errors, and the expected completion time understandable.",
        paragraphs: [
          "Batch parsing benefits from bounded concurrency, not launching every file simultaneously. DemoTracer can run a small number of jobs in parallel while the rest wait, avoiding memory and disk contention that slows the entire batch.",
          "For a very large scan, confirm it in several batches. The limit applies to one job queue, not to the total size of the local library.",
        ],
        keywords: ["limit", "too many", "parallel", "queue", "memory", "batch"],
      },
      {
        id: "batch-stop-resume",
        category: "basics",
        question: "Will stopping a batch discard demos that already finished?",
        summary: "Stopping should end unfinished work only; archives that passed validation must remain available.",
        paragraphs: [
          "After a stop request, the queue should stop dispatching new jobs. Completed and validated demos remain in the library, while failed, cancelled, and pending jobs keep explicit states and can be retried individually.",
          "A temporary output directory is not a successful result. A job should read Imported only after its manifest and output files pass validation.",
        ],
        points: ["Completed: retained and ready to open", "Failed: reason shown with a retry action", "Pending: kept in the queue until you remove it"],
        keywords: ["stop", "cancel", "resume", "progress", "lost", "retry"],
      },
      {
        id: "hltv-parts",
        category: "basics",
        question: "Are extra-long HLTV P1 + P2 demos merged automatically?",
        summary: "Not in the current version; they are treated as two independent demos.",
        paragraphs: [
          "Some HLTV matches split one extra-long match into P1 and P2. Determining continuity and merging round numbering requires additional match-level validation, which this version does not perform.",
          "Both parts can be imported and converted separately, but the UI should not describe them as one continuous archive.",
        ],
        keywords: ["HLTV", "P1", "P2", "long", "merge", "two demos"],
      },
      {
        id: "match-metadata",
        category: "data",
        question: "Where do the score, KDA, SteamIDs, and server details come from?",
        summary: "They come directly from the parsed demo and do not require an online player lookup.",
        paragraphs: [
          "After parsing, DemoTracer has the map, server name, platform hint, team score, player names, SteamID64 values, and the last reliable K/D/A snapshot present in the demo. The round selection and converted archive views can use the same match summary.",
          "Some values may remain unknown when a demo ends early, omits the relevant fields, or uses an unusual source format. The UI should never invent missing values.",
        ],
        keywords: ["score", "KDA", "SteamID", "server", "players", "metadata"],
      },
      {
        id: "runtime-vendors",
        category: "runtime",
        question: "Why can’t BotController / BotHider builds from different plugins be mixed?",
        summary: "Dependencies with the same name may target different interfaces; a present file is not proof of compatibility.",
        paragraphs: [
          "DemoTracer and CS2 Bot Improver may both ship BotController or BotHider, but their vendor versions, exported capabilities, and calling contracts can differ. A mixed install may appear to load and still fail when playback starts, the map changes, or plugins unload.",
          "Environment inspection checks the install receipt, ABI/API contracts, and the live heartbeat. When mixing is detected, install one complete matching DemoTracer playback bundle instead of replacing a single DLL.",
        ],
        keywords: ["BotController", "BotHider", "Bot Improver", "conflict", "ABI", "vendor", "dependency"],
      },
      {
        id: "local-config",
        category: "runtime",
        question: "How do GUI settings relate to demotracer.config.json?",
        summary: "The GUI owns local conversion defaults; the server JSON owns DemoTracer runtime defaults.",
        paragraphs: [
          "Output locations, demo libraries, conversion choices, and notification preferences are desktop settings. demotracer.config.json beside the server plugin controls playback, handoff, and alignment behavior after the CSS plugin loads.",
          "The GUI can expose common safe JSON fields, but it must not silently discard unknown fields. It should read the existing file and preserve configuration it does not understand when saving.",
        ],
        keywords: ["settings", "json", "demotracer.config.json", "server", "handoff", "configuration"],
      },
      {
        id: "notifications",
        category: "runtime",
        question: "When should DemoTracer play a notification sound?",
        summary: "Sounds are most useful when a batch completes, an error needs attention, or work is interrupted.",
        paragraphs: [
          "Long jobs should not require watching the window. Batch completion and blocking errors should use distinct sounds alongside visible status, so muted users still receive feedback.",
          "Sounds should be optional, and every normally completed demo in a queue should not trigger a loud alert. Use lightweight visual feedback per item and play one sound when the whole batch finishes.",
        ],
        keywords: ["sound", "notification", "complete", "error", "mute", "audio"],
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
        <div className="faq-category-list" aria-label={copy.categories.basics}>
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
              <span className="faq-pane-label">{copy.answer}</span>
              <h2>{selectedEntry.question}</h2>
              <p className="faq-answer-summary">{selectedEntry.summary}</p>
              <div className="faq-answer-body">
                {selectedEntry.paragraphs.map((paragraph) => <p key={paragraph}>{paragraph}</p>)}
                {selectedEntry.points ? (
                  <ul>
                    {selectedEntry.points.map((point) => <li key={point}>{point}</li>)}
                  </ul>
                ) : null}
              </div>
            </article>
          </>
        ) : (
          <div className="faq-empty-state">
            <SearchIcon size={24} />
            <strong>{copy.noResultsTitle}</strong>
            <p>{copy.noResultsBody}</p>
            <button className="secondary-button" type="button" onClick={() => { setQuery(""); setCategory("all"); setMobileAnswerOpen(false); }}>{copy.clearSearch}</button>
          </div>
        )}
      </div>
    </section>
  );
}
