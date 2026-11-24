// AI Multi Launcher · ADE — Main Application Controller
// Features: Tactile Ripple & Async Feedback, Multi-Platform Headless/Stealth Orchestration,
//           Auto-Synced @ Roster (Codex + Claude + Antigravity), Battery-Style Quota Telemetry.

import { rpc, fireAndForget, onHostEvent } from "./bridge.js";
import { escapeHtml, renderMarkdown } from "./markdown.js";
import { TerminalManager } from "./terminal.js";
import { renderDiffFilesHtml } from "./diffView.js";
import { PORTAL_CONFIG, setUiLanguage, getUiLanguage, installI18nObserver, applyLanguageToRoot } from "./portalConfig.js";
import { icon } from "./icons.js";
import { attachVerticalResizer, attachHorizontalResizer } from "./resizer.js";

const THEMES = [
  { id: "obsidian", label: "深空极客", swatch: "#6366f1" },
  { id: "matrix", label: "赛博矩阵", swatch: "#10b981" },
  { id: "daylight", label: "晨雾亮色", swatch: "#2563eb" },
  { id: "aurora", label: "极光毛玻璃", swatch: "#8b5cf6" },
];

const state = {
  data: null,
  activeView: "command", // 'command' | 'arena' | 'fleet'
  selectedRoomId: null,
  sessionFilter: "",
  roomDrafts: new Map(),
  designCaptures: [],
  mentionMenu: { open: false, query: "", startIdx: 0, endIdx: 0, selectedIdx: 0 },
  inspectedPopoverWsId: null,

  // Terminal Dock
  terminalOpen: false,
  terminalMaximized: false,
  activeTerminalId: null,
  terminalAccountId: "",

  // Diff Review Drawer
  diffOpen: false,
  diffMode: "unified", // 'unified' | 'split'
  diffSelectedFile: null,
  diffSnapshot: null,
  diffComments: [],
  diffActiveDraftKey: null,
  diffTargetAgent: "",

  // Arena Form
  arenaInitialized: false,

  // Clean Fleet View Filter & Search
  fleetFilter: "all", // 'all' | 'direct' | 'codex' | 'claude' | 'antigravity'
  fleetSearch: "",
  cardMenuWsId: null,
  rosterPopoverOpen: false,
  topbarPoolOpen: false,

  // Dedicated Single-Agent Studio & Live Working Status
  studioWsId: null,
  studioRoomId: null,
  studioMode: "chat", // 'chat' | 'tui'
  studioInspectorOpen: true,
  studioDrafts: new Map(),
  studioDiffMap: new Map(),
  studioTuiMap: new Map(),
  studioEnsuringSet: new Set(),

  // Project Tree State
  collapsedProjects: new Set(),

  // Dynamic Runtime Model / Effort / Speed Catalogs per Workspace
  modelCatalogs: new Map(),
  loadingCatalogs: new Set(),
  composerPicker: { kind: null, query: "", left: 24, bottom: 72 },
  lastComposerFooterSig: "",

  // v0.0.2 Layout, Inspector, Command Palette & Arena Upgrades
  sidebarCollapsed: false,
  inspectorTab: "diff", // 'diff' | 'trace'
  diffWide: false,
  themePopoverOpen: false,
  cmdPaletteOpen: false,
  cmdPaletteQuery: "",
  cmdPaletteIdx: 0,
  expandedCmdSteps: new Set(),
  arenaSlotModels: new Map(),
  arenaExtraSlots: [],
};

const terminalMgr = new TerminalManager();

/* ==========================================================================
   0. Tactile Click Ripple & Universal Async Button Feedback Engine
   ========================================================================== */
function installGlobalTactileFeedback() {
  document.addEventListener("pointerdown", (e) => {
    const target = e.target.closest(
      ".btn, .rail-btn, .theme-btn, .theme-picker-btn, .theme-popover-item, .pool-pill, .preset-card, .term-tab, .diff-file-pill, .mode-seg-btn, .onboard-tab, .provider-pill-card, .onboard-provider-item, .fleet-seg-btn, .card-more-btn, .studio-agent-card, .studio-file-row, .composer-picker-btn, .cpp-item, .inspector-tab-btn, .cmd-palette-item"
    );
    if (!target || target.disabled || target.classList.contains("is-busy")) return;

    const rect = target.getBoundingClientRect();
    const size = Math.max(rect.width, rect.height) * 1.15;
    const ripple = document.createElement("span");
    ripple.className = "ade-ripple";
    ripple.style.width = `${size}px`;
    ripple.style.height = `${size}px`;
    ripple.style.left = `${e.clientX - rect.left - size / 2}px`;
    ripple.style.top = `${e.clientY - rect.top - size / 2}px`;
    target.appendChild(ripple);
    setTimeout(() => ripple.remove(), 520);
  });

  // Close popovers & menus when clicking outside
  document.addEventListener("mousedown", (e) => {
    if (state.inspectedPopoverWsId && !e.target.closest("#account-popover, [data-pool-ws]")) {
      closeAccountPopover();
    }
    if (state.topbarPoolOpen && !e.target.closest("#topbar-pool-popover, #topbar-pool-summary")) {
      closeTopbarPoolPopover();
    }
    if (state.rosterPopoverOpen && !e.target.closest("#roster-popover, #btn-roster-popover")) {
      closeRosterPopover();
    }
    if (state.cardMenuWsId && !e.target.closest("#card-context-menu, [data-card-more]")) {
      closeCardContextMenu();
    }
    if (state.themePopoverOpen && !e.target.closest("#theme-popover, #theme-switcher")) {
      closeThemePopover();
    }
    if (state.mentionMenu.open && !e.target.closest("#mention-popup, #btn-open-at-menu, #composer-input")) {
      state.mentionMenu.open = false;
      renderMentionPopup();
    }
    if (state.composerPicker.kind && !e.target.closest("#composer-picker-popover, [data-composer-picker]")) {
      closeComposerPickerPopover();
    }
  });

  document.addEventListener("keydown", (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
      e.preventDefault();
      if (state.cmdPaletteOpen) closeCommandPalette();
      else openCommandPalette();
      return;
    }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "b") {
      e.preventDefault();
      toggleSidebarCollapse();
      return;
    }
    if (e.key === "Escape") {
      if (state.cmdPaletteOpen) {
        e.preventDefault();
        closeCommandPalette();
        return;
      }
      if (state.composerPicker.kind) {
        closeComposerPickerPopover();
        return;
      }
      if (state.themePopoverOpen) {
        closeThemePopover();
        return;
      }
      if (state.rosterPopoverOpen || state.topbarPoolOpen || state.cardMenuWsId || state.inspectedPopoverWsId) {
        closeRosterPopover();
        closeTopbarPoolPopover();
        closeCardContextMenu();
        closeAccountPopover();
        return;
      }
      if (document.getElementById("modal-backdrop")) {
        closeModal();
      }
    }
  });
}

async function runButtonAction(btn, options, actionFn) {
  if (!btn || btn.classList.contains("is-busy")) return;
  const { busyText = "处理中…", doneText = "✓ 已完成", successToast = null } = options || {};
  const originalHtml = btn.innerHTML;
  const wasDisabled = btn.disabled;

  btn.classList.remove("is-done", "is-error");
  btn.classList.add("is-busy");
  btn.disabled = true;
  btn.innerHTML = `<span class="spin-icon"></span> <span>${escapeHtml(busyText)}</span>`;

  try {
    const res = await actionFn();
    if (btn.isConnected) {
      btn.classList.remove("is-busy");
      btn.classList.add("is-done");
      btn.disabled = wasDisabled;
      btn.innerHTML = `<span>${escapeHtml(doneText)}</span>`;
      setTimeout(() => {
        if (btn.isConnected) {
          btn.classList.remove("is-done");
          btn.innerHTML = originalHtml;
        }
      }, 850);
    }
    if (successToast) {
      const msg = typeof successToast === "function" ? successToast(res) : successToast;
      if (msg) showToast("success", msg);
    }
    return res;
  } catch (err) {
    if (btn.isConnected) {
      btn.classList.remove("is-busy");
      btn.classList.add("is-error");
      btn.innerHTML = originalHtml;
      btn.disabled = wasDisabled;
      setTimeout(() => btn.classList.remove("is-error"), 450);
    }
    showToast("error", err?.message || "操作执行失败");
    return undefined;
  }
}

function formatPlatformShort(client, planOrHint) {
  if (client === "Antigravity" || (planOrHint && String(planOrHint).includes("AGY"))) return { code: "AGY", cls: "antigravity" };
  if (client === "DirectApi") return { code: "API", cls: "directapi" };
  if (client === "MarketCli") return { code: "CLI", cls: "marketcli" };
  if (client === "Claude") return { code: "CLD", cls: "claude" };
  return { code: "CDX", cls: "codex" };
}

function formatTokensCompact(num) {
  if (!num || num <= 0) return "0";
  if (num >= 1_000_000) return (num / 1_000_000).toFixed(2) + "M";
  if (num >= 1_000) return (num / 1_000).toFixed(1) + "k";
  return String(Math.round(num));
}

/* ==========================================================================
   1. Persistent Shell Initialization
   ========================================================================== */
function initShell() {
  const root = document.getElementById("app");
  root.innerHTML = `
    <header class="topbar" id="topbar">
      <div class="topbar-brand">
        <button class="btn btn-ghost btn-sm" id="btn-toggle-sidebar" style="padding:4px 6px" title="切换侧边栏 (Ctrl+B)">${icon("sidebar", 14)}</button>
        <div class="topbar-logo"><img src="/assets/AppIcon.png" alt="Logo" /></div>
        <span class="topbar-title">Hyperion</span>
        <div class="topbar-pool" id="topbar-pool"></div>
      </div>
      <div class="topbar-center">
        <button class="topbar-cmd-trigger" id="btn-cmd-palette" title="打开快速命令面板 (Ctrl+K)">
          <span style="display:inline-flex;align-items:center;gap:6px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${icon("search", 12)} <span>搜索项目、对话或命令…</span></span>
          <span class="kbd-hint">Ctrl+K</span>
        </button>
      </div>
      <div class="topbar-actions">
        <button class="btn btn-sm" id="btn-topbar-stealth" title="将所有外部客户端窗口隐藏至后台运行，桌面与任务栏零弹窗"></button>
        <button class="btn btn-sm" id="btn-toggle-failover" title="某个账号额度耗尽或触发 429 时，自动无感切换池内健康账号"></button>
        <button class="btn btn-sm" id="btn-toggle-design" title="打开内置浏览器实时预览与点选 DOM/截图">${icon("globe", 13)} <span>预览</span></button>
        <button class="btn btn-sm" id="btn-toggle-lang" title="Switch UI to English" style="min-width:36px;font-weight:700">EN</button>
        <div id="theme-switcher"></div>
        <div class="win-controls">
          <button class="win-btn" id="win-min" title="最小化">&#x2014;</button>
          <button class="win-btn" id="win-max" title="最大化 / 还原">&#x25A1;</button>
          <button class="win-btn close" id="win-close" title="关闭">&#x2715;</button>
        </div>
      </div>
    </header>

    <div id="popover-root"></div>
    <div id="cmd-palette-root"></div>

    <div id="design-toolbar" style="display:none;height:38px;background:var(--bg-sidebar);border-bottom:1px solid var(--border-subtle);padding:0 14px;align-items:center;gap:8px">
      <span class="tag worktree">Design Mode</span>
      <input id="design-url-input" class="input" style="max-width:380px;padding:4px 8px;font-size:12px" placeholder="http://localhost:3000" />
      <button class="btn btn-sm btn-primary" id="btn-design-go">访问预览</button>
      <button class="btn btn-sm" id="btn-design-inspect">🎯 点选页面元素抓取 DOM+截图</button>
      <button class="btn btn-sm" id="btn-design-shot">📸 截取整页附加至对话</button>
      <span style="font-size:11px;color:var(--text-muted);margin-left:auto">点击右侧实时网页任意组件，自动提取 HTML/CSS 与截图喂给 Agent</span>
    </div>

    <div class="ade-body">
      <nav class="nav-rail" id="nav-rail">
        <div class="nav-group">
          <button class="rail-btn active" data-nav="command" title="项目工作台 · 新建或打开项目并在项目目录内操作（支持单 Agent 直发与 @ 多智能体协同）">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>
            <span>项目</span>
            <div class="rail-badge" id="badge-rooms" style="display:none"></div>
          </button>
          <button class="rail-btn" data-nav="studio" style="display:none" title="独立专舱">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="4" width="18" height="12" rx="2"/><line x1="8" y1="20" x2="16" y2="20"/><line x1="12" y1="16" x2="12" y2="20"/><circle cx="9" cy="10" r="1"/><circle cx="15" cy="10" r="1"/></svg>
            <span>专舱</span>
            <div class="rail-badge" id="badge-studio" style="display:none"></div>
          </button>
          <button class="rail-btn" data-nav="arena" title="赛马竞技场 · 多账号/跨模型独立 Worktree 并行实现与合并">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2"/></svg>
            <span>竞技场</span>
            <div class="rail-badge" id="badge-arena" style="display:none"></div>
          </button>
          <button class="rail-btn" data-nav="fleet" title="账号与额度池 · 极简卡片监控与接入">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M22 12h-4l-3 9L9 3l-3 9H2"/></svg>
            <span>账号池</span>
            <div class="rail-badge" id="badge-instances" style="display:none"></div>
          </button>
        </div>
        <div class="nav-group">
          ${PORTAL_CONFIG.showDocsNavButton ? `
          <button class="rail-btn" id="btn-rail-docs" title="打开官方网站与使用指南">
            <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/></svg>
            <span>官网</span>
          </button>` : ""}
          <button class="rail-btn" id="btn-rail-refresh" title="立即刷新全平台额度与守护状态">
            <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="23 4 23 10 17 10"/><polyline points="1 20 1 14 7 14"/><path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/></svg>
            <span>同步</span>
          </button>
        </div>
      </nav>

      <main class="view-stage">
        <!-- VIEW 1: UNIFIED COMMAND CENTER -->
        <section id="view-command" class="command-layout">
          <aside class="session-sidebar" id="session-sidebar">
            <div class="sidebar-header">
              <div class="sidebar-header-row" style="gap:6px">
                <button class="btn btn-primary btn-sm" id="btn-new-project" style="flex:1">${icon("plus", 13)} <span>新建项目</span></button>
                <button class="btn btn-sm" id="btn-open-project" title="打开电脑上已有的项目文件夹">${icon("folderOpen", 13)} <span>打开</span></button>
              </div>
              <input id="session-filter-input" class="input" style="padding:5px 8px;font-size:11.5px" placeholder="搜索项目或对话…" />
            </div>
            <div class="session-list" id="session-list"></div>
          </aside>
          <div class="pane-resizer-v" id="sidebar-resizer" title="拖拽调整侧栏宽度 · 双击还原"></div>

          <div class="center-pane">
            <div class="room-header" id="room-header"></div>
            <div class="chat-stream-wrap">
              <div class="chat-stream" id="chat-stream"></div>
              <button type="button" class="scroll-bottom-pill" id="btn-scroll-bottom" style="display:none">${icon("arrowDown", 13)} <span>回到底部</span></button>
            </div>

            <div class="composer-area" id="composer-area">
              <div id="mention-popup" class="mention-popup" style="display:none"></div>
              <div id="design-chips" class="design-chips" style="display:none"></div>
              <div class="composer-box">
                <textarea id="composer-input" class="composer-input" placeholder="输入指令，或按 @ 点名任意智能体协同处理（Ctrl+Enter 发送）…"></textarea>
                <div class="composer-footer">
                  <div class="composer-footer-left">
                    <div class="mode-segmented" id="composer-mode-switch"></div>
                    <div class="composer-hints" id="composer-quick-mentions"></div>
                  </div>
                  <div style="display:flex;align-items:center;gap:6px;flex-shrink:0;margin-left:auto">
                    <button class="btn btn-danger btn-sm" id="btn-stop-room" style="display:none;border-radius:999px">${icon("stop", 11)} <span>停止</span></button>
                    <button class="btn btn-primary btn-sm" id="btn-send-msg">${icon("arrowUp", 13)} <span>发送</span></button>
                  </div>
                </div>
              </div>
            </div>

            <div id="terminal-dock" class="terminal-dock" style="display:none">
              <div class="pane-resizer-h" id="term-resizer" title="拖拽调整终端高度 · 双击还原"></div>
              <div class="terminal-topbar">
                <div class="terminal-tabs" id="terminal-tabs"></div>
                <div style="display:flex;align-items:center;gap:5px">
                  <select id="term-account-select" class="select" style="width:auto;padding:2px 6px;font-size:11px"></select>
                  <button class="btn btn-sm" data-spawn-term="shell">＋ PowerShell</button>
                  <button class="btn btn-sm" data-spawn-term="codex">＋ Codex TUI</button>
                  <button class="btn btn-sm" data-spawn-term="claude">＋ Claude TUI</button>
                  <button class="btn btn-ghost btn-sm" id="btn-term-max" title="放大 / 还原终端高度">${icon("maximize", 12)}</button>
                  <button class="btn btn-ghost btn-sm" id="btn-term-hide" title="收起终端面板">${icon("close", 13)}</button>
                </div>
              </div>
              <div class="terminal-stage" id="terminal-stage"></div>
            </div>
          </div>

          <div class="pane-resizer-v" id="diff-resizer" style="display:none" title="拖拽调整审查面板宽度 · 双击还原"></div>
          <aside id="diff-drawer" class="diff-drawer" style="display:none">
            <div class="diff-header">
              <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;flex-wrap:wrap">
                <div style="display:flex;align-items:center;gap:8px;min-width:0">
                  <div class="inspector-tabs">
                    <button type="button" class="inspector-tab-btn active" id="insp-tab-diff">${icon("diff", 12)} <span>Diff 审查</span></button>
                    <button type="button" class="inspector-tab-btn" id="insp-tab-trace">${icon("activity", 12)} <span>实时轨迹</span></button>
                  </div>
                  <div style="font-weight:700;font-size:12px;color:var(--text-secondary);overflow:hidden;text-overflow:ellipsis;white-space:nowrap" id="diff-title">Git Diff 审查</div>
                </div>
                <div style="display:flex;align-items:center;gap:4px">
                  <button class="btn btn-sm" id="btn-diff-unified">Unified</button>
                  <button class="btn btn-sm" id="btn-diff-split">并排 Split</button>
                  <button class="btn btn-ghost btn-sm" id="btn-diff-wide" title="加宽 / 还原面板">${icon("maximize", 12)}</button>
                  <button class="btn btn-sm" id="btn-diff-refresh">↻ 刷新</button>
                  <button class="btn btn-ghost btn-sm" id="btn-diff-close">${icon("close", 13)}</button>
                </div>
              </div>
              <div class="diff-files-strip" id="diff-files-strip"></div>
            </div>
            <div class="diff-body" id="diff-body"></div>
            <div class="diff-review-bar" id="diff-review-bar">
              <div style="display:flex;align-items:center;justify-content:space-between;gap:8px">
                <span style="font-size:11.5px;color:var(--text-secondary)" id="diff-queue-count">点击任意代码行左侧 ＋ 添加行内批注</span>
                <select id="diff-target-agent" class="select" style="width:auto;padding:3px 8px;font-size:11px"></select>
              </div>
              <div style="display:flex;gap:6px">
                <input id="diff-overall-note" class="input" style="padding:5px 8px;font-size:12px" placeholder="补充总体修改要求（可选）…" />
                <button class="btn btn-primary btn-sm" id="btn-submit-diff-review">打包发回修改</button>
              </div>
            </div>
            <div id="cmd-inspector-trace" style="display:none;flex:1;overflow-y:auto;padding:12px;flex-direction:column;gap:12px"></div>
          </aside>
        </section>

        <!-- VIEW 1.5: DEDICATED SINGLE-AGENT STUDIO & LIVE WORKING STATUS -->
        <section id="view-studio" class="studio-layout" style="display:none">
          <aside class="studio-sidebar">
            <div class="studio-section-head">
              <span>独立智能体列表</span>
              <button class="btn btn-ghost btn-sm" id="btn-studio-add-agent" style="padding:2px 6px">＋ 接入</button>
            </div>
            <div class="studio-agent-list" id="studio-agent-list"></div>
            <div class="studio-section-head" style="padding-top:10px">
              <span id="studio-threads-label">专属任务对话</span>
              <button class="btn btn-primary btn-sm" id="btn-studio-new-thread" style="padding:2px 8px">＋ 新对话</button>
            </div>
            <div class="studio-thread-list" id="studio-thread-list"></div>
          </aside>

          <div class="studio-center">
            <div class="room-header" id="studio-header"></div>

            <div id="studio-chat-wrap" style="flex:1;min-height:0;display:flex;flex-direction:column">
              <div class="chat-stream" id="studio-chat-stream"></div>
              <div class="composer-area">
                <div class="composer-box">
                  <textarea id="studio-composer-input" class="composer-input" placeholder="直接向当前智能体发送任务指令（无需输入 @，Ctrl+Enter 发送）…"></textarea>
                  <div class="composer-footer">
                    <div style="display:flex;align-items:center;gap:8px;flex-wrap:nowrap;min-width:0;overflow:hidden">
                      <div class="mode-segmented" id="studio-mode-switch"></div>
                      <span id="studio-composer-hint" style="font-size:11px;color:var(--text-muted);white-space:nowrap;overflow:hidden;text-overflow:ellipsis"></span>
                    </div>
                    <div style="display:flex;align-items:center;gap:6px;flex-shrink:0">
                      <button class="btn btn-danger btn-sm" id="btn-studio-stop" style="display:none">■ 停止</button>
                      <button class="btn btn-primary btn-sm" id="btn-studio-send" style="padding:5px 16px">发送 (Ctrl+Enter)</button>
                    </div>
                  </div>
                </div>
              </div>
            </div>

            <div id="studio-tui-pane" class="studio-tui-pane" style="display:none">
              <div class="studio-tui-bar">
                <span style="font-size:11.5px;color:var(--text-secondary)" id="studio-tui-caption">专属隔离环境原生交互终端</span>
                <div style="display:flex;align-items:center;gap:6px">
                  <button class="btn btn-primary btn-sm" id="btn-studio-tui-native">▶ 启动智能体原生 TUI</button>
                  <button class="btn btn-sm" id="btn-studio-tui-shell">＋ PowerShell</button>
                  <button class="btn btn-ghost btn-sm" id="btn-studio-tui-back">← 返回对话面板</button>
                </div>
              </div>
              <div id="studio-tui-stage" class="studio-tui-stage"></div>
            </div>
          </div>

          <aside id="studio-inspector" class="studio-inspector">
            <div class="studio-hud-card" id="studio-live-hud"></div>
            <div class="studio-hud-card">
              <div class="studio-hud-title">
                <span>实时执行与思考轨迹</span>
                <span id="studio-steps-count" style="font-weight:600"></span>
              </div>
              <div class="studio-step-list" id="studio-steps-list"></div>
            </div>
            <div class="studio-hud-card">
              <div class="studio-hud-title">
                <span>工作目录文件变更</span>
                <button class="btn btn-ghost btn-sm" id="btn-studio-refresh-diff" style="padding:1px 6px">↻ 扫描</button>
              </div>
              <div class="studio-file-list" id="studio-file-list"></div>
            </div>
          </aside>
        </section>

        <!-- VIEW 2: ARENA -->
        <section id="view-arena" class="arena-layout" style="display:none">
          <div class="arena-main">
            <div class="arena-composer-card">
              <div style="display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap">
                <div>
                  <div style="font-size:16px;font-weight:700">⚡ 多智能体后台并行赛马竞技场 (Headless Worktree Arena)</div>
                  <div style="font-size:12px;color:var(--text-secondary);margin-top:2px">为每个选定智能体自动创建隔离 Git Worktree 并行编码，对比 Diff 与测试结果后一键合入主分支</div>
                </div>
              </div>
              <div class="guard-alert-box" style="margin:2px 0;padding:8px 12px;font-size:11.5px">
                🛡 <strong>账号风控安全保护：</strong>高并发并行赛马推荐勾选「直连 API（API Key）」通道；个人 OAuth 订阅账号默认不自动勾选，避免高频自动化并发触发平台风控。
              </div>
              <div style="display:grid;grid-template-columns:1.5fr 1fr;gap:10px">
                <div style="display:flex;gap:6px">
                  <input id="arena-repo-input" class="input" placeholder="选择 Git 仓库根目录…" />
                  <button class="btn" id="btn-arena-pick-repo">选择仓库</button>
                </div>
                <input id="arena-verify-input" class="input" placeholder="可选自动验收命令（如 dotnet test 或 npm test）" />
              </div>
              <textarea id="arena-prompt-input" class="textarea" rows="3" placeholder="输入赛马开发任务：各参赛 Agent 将在后台各自独立的 Git Worktree 分支中同步实现…"></textarea>
              <div style="display:flex;align-items:center;justify-content:space-between;gap:10px;flex-wrap:wrap">
                <div id="arena-contestants-picker" style="display:flex;align-items:center;gap:8px;flex-wrap:wrap"></div>
                <button class="btn btn-primary" id="btn-arena-start">${icon("zap", 13)} <span>启动后台并行赛马</span></button>
              </div>
            </div>
            <div id="arena-sessions-list" style="display:flex;flex-direction:column;gap:18px"></div>
          </div>
        </section>

        <!-- VIEW 3: CLEAN MINIMALIST ACCOUNT & QUOTA POOL -->
        <section id="view-fleet" class="fleet-view" style="display:none">
          <div class="fleet-top-header">
            <div class="fleet-title-area">
              <span class="fleet-page-title">账号池</span>
              <div class="fleet-inline-stats" id="fleet-inline-stats"></div>
            </div>
            <div style="display:flex;align-items:center;gap:8px">
              <button class="btn btn-sm" id="btn-refresh-all-usage">↻ 刷新额度</button>
              <button class="btn btn-primary btn-sm" id="btn-add-profile">＋ 添加账号 / Agent</button>
            </div>
          </div>

          <div class="fleet-toolbar">
            <div class="fleet-seg-tabs" id="fleet-seg-tabs"></div>
            <div style="display:flex;align-items:center;gap:8px">
              <span id="fleet-guard-slot"></span>
              <input id="fleet-search-input" class="input fleet-search" placeholder="筛选名称、平台或模型…" />
            </div>
          </div>

          <div class="accounts-grid" id="accounts-grid"></div>

          <details class="fleet-advanced-drawer" id="fleet-advanced-drawer">
            <summary class="fleet-advanced-summary">
              <span id="fleet-adv-summary-text">▸ 高级运维与底层守护进程</span>
              <span style="font-size:11px;color:var(--text-muted)">低频操作已收纳 · 点击展开</span>
            </summary>
            <div class="fleet-advanced-body">
              <div style="display:flex;align-items:center;justify-content:space-between;gap:10px;flex-wrap:wrap">
                <span style="font-size:12px;color:var(--text-secondary)">默认全部无头运行（0 弹窗）；若外部客户端意外显示窗口或沙箱异常，可在此一键收纳修复。</span>
                <div style="display:flex;gap:6px;flex-wrap:wrap">
                  <button class="btn btn-sm" id="btn-hide-all-windows">👻 隐藏全部外部窗口</button>
                  <button class="btn btn-sm" id="btn-repair-sandboxes">🛡️ 修复多开沙箱</button>
                  <button class="btn btn-sm" id="btn-fleet-discover">↻ 刷新进程快照</button>
                  <button class="btn btn-sm" id="btn-fleet-reveal-all">👁 恢复隐藏窗口</button>
                </div>
              </div>
              <div id="fleet-warnings"></div>
              <table class="data-table">
                <thead>
                  <tr>
                    <th>归属账号</th>
                    <th>类型</th>
                    <th>根 PID</th>
                    <th>状态</th>
                    <th>进程数</th>
                    <th>时长</th>
                    <th>CPU</th>
                    <th>内存</th>
                    <th>窗口</th>
                    <th style="text-align:right">操作</th>
                  </tr>
                </thead>
                <tbody id="instances-tbody"></tbody>
              </table>
            </div>
          </details>
        </section>
      </main>
    </div>
  `;

  installGlobalTactileFeedback();
  bindGlobalListeners();
  attachVerticalResizer(document.getElementById("sidebar-resizer"), document.getElementById("session-sidebar"), {
    direction: 1,
    min: 185,
    maxFn: () => Math.min(440, Math.round(window.innerWidth * 0.36)),
    defaultSize: 248,
    storageKey: "ade_sidebar_width",
    onResize: () => { if (state.terminalOpen) terminalMgr.fitActive(); },
  });
  attachVerticalResizer(document.getElementById("diff-resizer"), document.getElementById("diff-drawer"), {
    direction: -1,
    min: 360,
    maxFn: () => Math.round(window.innerWidth * 0.68),
    defaultSize: 480,
    storageKey: "ade_diff_width",
    onResize: () => { if (state.terminalOpen) terminalMgr.fitActive(); },
  });
  attachHorizontalResizer(document.getElementById("term-resizer"), document.getElementById("terminal-dock"), {
    min: 140,
    maxFn: () => Math.round(window.innerHeight * 0.72),
    defaultSize: 255,
    storageKey: "ade_term_height",
    onResize: () => terminalMgr.fitActive(),
  });
}

function toggleSidebarCollapse() {
  state.sidebarCollapsed = !state.sidebarCollapsed;
  document.getElementById("session-sidebar")?.classList.toggle("is-collapsed", state.sidebarCollapsed);
  const resizer = document.getElementById("sidebar-resizer");
  if (resizer) resizer.style.display = state.sidebarCollapsed ? "none" : "";
  if (state.terminalOpen) setTimeout(() => terminalMgr.fitActive(), 40);
}

/* ==========================================================================
   2. Event Bindings (Every Button Uses Explicit Tactile / Async Feedback!)
   ========================================================================== */
function bindGlobalListeners() {
  const topbar = document.getElementById("topbar");
  topbar.addEventListener("mousedown", (e) => {
    if (e.button !== 0) return;
    if (e.target.closest("button, select, input, .pool-pill, #theme-switcher")) return;
    fireAndForget("window.drag");
  });
  topbar.addEventListener("dblclick", (e) => {
    if (e.target.closest("button, select, input, .pool-pill, #theme-switcher")) return;
    rpc("window.maximizeToggle");
  });

  document.getElementById("btn-toggle-sidebar").onclick = () => toggleSidebarCollapse();
  document.getElementById("btn-cmd-palette").onclick = () => openCommandPalette();

  document.getElementById("win-min").onclick = () => rpc("window.minimize");
  document.getElementById("win-max").onclick = () => rpc("window.maximizeToggle");
  document.getElementById("win-close").onclick = () => rpc("window.close");

  document.getElementById("nav-rail").addEventListener("click", (e) => {
    const navBtn = e.target.closest("[data-nav]");
    if (navBtn) switchView(navBtn.dataset.nav);
  });

  const railDocsBtn = document.getElementById("btn-rail-docs");
  if (railDocsBtn) {
    railDocsBtn.onclick = () => {
      if (PORTAL_CONFIG.websiteUrl) {
        rpc("shell.openUrl", { url: PORTAL_CONFIG.websiteUrl }).catch((err) => showToast("error", err.message));
      }
    };
  }

  const langBtn = document.getElementById("btn-toggle-lang");
  if (langBtn) {
    langBtn.onclick = () => {
      const nextLang = getUiLanguage() === "en" ? "zh" : "en";
      if (state.data?.settings) state.data.settings.language = nextLang;
      setUiLanguage(nextLang);
      rpc("settings.update", { language: nextLang }).catch(() => {});
    };
  }

  const railRefreshBtn = document.getElementById("btn-rail-refresh");
  railRefreshBtn.onclick = async () => {
    railRefreshBtn.classList.add("is-busy");
    try {
      await Promise.all([rpc("client.refreshDiscovery"), rpc("pool.refreshUsage", { all: true, force: true })]);
      showToast("success", "✓ 已同步全部账号额度与后台进程状态");
    } catch (err) {
      showToast("error", err.message);
    } finally {
      railRefreshBtn.classList.remove("is-busy");
    }
  };

  const stealthBtn = document.getElementById("btn-topbar-stealth");
  stealthBtn.onclick = () => {
    const currentlyStealth = state.data?.stealth?.enabled !== false;
    return runButtonAction(
      stealthBtn,
      {
        busyText: currentlyStealth ? "显现窗口中…" : "隐身收纳中…",
        doneText: currentlyStealth ? "👁 窗口已显示" : "👻 窗口已隐身",
      },
      () => rpc(currentlyStealth ? "stealth.revealAll" : "stealth.hideAll")
    );
  };

  const failoverBtn = document.getElementById("btn-toggle-failover");
  failoverBtn.onclick = () => {
    if (!state.data) return;
    const next = !state.data.settings.autoFailoverEnabled;
    return runButtonAction(
      failoverBtn,
      {
        busyText: "切换中…",
        doneText: next ? "⚡ 已开启" : "已关闭",
        successToast: next ? "⚡ 已开启算力池自动无感换号接力" : "已关闭自动换号接力",
      },
      () => rpc("settings.update", { autoFailoverEnabled: next })
    );
  };

  const designToggleBtn = document.getElementById("btn-toggle-design");
  designToggleBtn.onclick = () => {
    const visible = !(state.data?.design?.visible);
    const url = document.getElementById("design-url-input").value || state.data?.settings?.lastPreviewUrl || "http://localhost:3000";
    return runButtonAction(
      designToggleBtn,
      { busyText: "加载预览…", doneText: visible ? "🌐 预览开启" : "已关闭" },
      async () => {
        await rpc("design.setVisible", { visible, url });
        renderDesignBar();
      }
    );
  };

  const designGoBtn = document.getElementById("btn-design-go");
  designGoBtn.onclick = () => {
    const url = document.getElementById("design-url-input").value.trim();
    if (!url) return;
    return runButtonAction(designGoBtn, { busyText: "打开中…", doneText: "✓ 已加载" }, () => rpc("design.navigate", { url }));
  };
  document.getElementById("design-url-input").addEventListener("keydown", (e) => {
    if (e.key === "Enter") designGoBtn.click();
  });

  const designInspectBtn = document.getElementById("btn-design-inspect");
  designInspectBtn.onclick = () => {
    const next = !(state.data?.design?.inspecting);
    return runButtonAction(designInspectBtn, { busyText: "切换…", doneText: next ? "🎯 请点选" : "已取消" }, () =>
      rpc("design.setInspect", { enabled: next })
    );
  };

  const designShotBtn = document.getElementById("btn-design-shot");
  designShotBtn.onclick = () =>
    runButtonAction(designShotBtn, { busyText: "截图中…", doneText: "✓ 已附加", successToast: "📸 页面截图已附加至输入框" }, () =>
      rpc("design.captureViewport")
    );

  document.getElementById("session-filter-input").addEventListener("input", (e) => {
    state.sessionFilter = e.target.value.trim().toLowerCase();
    renderSessionList();
  });
  document.getElementById("btn-new-project").onclick = () => openCreateProjectModal();
  const openProjBtn = document.getElementById("btn-open-project");
  openProjBtn.onclick = () =>
    runButtonAction(openProjBtn, { busyText: "打开…", doneText: "✓" }, () => openExistingProjectPicker());

  // Dedicated Single-Agent Studio Listeners
  document.getElementById("btn-studio-add-agent").onclick = () => openAddProfileModal("api");
  document.getElementById("btn-studio-new-thread").onclick = () => createNewStudioThread();
  const studioInput = document.getElementById("studio-composer-input");
  studioInput.addEventListener("input", () => {
    if (state.studioRoomId) state.studioDrafts.set(state.studioRoomId, studioInput.value);
  });
  studioInput.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
      e.preventDefault();
      sendCurrentStudioMessage();
    }
  });
  document.getElementById("btn-studio-send").onclick = () => sendCurrentStudioMessage();
  const studioStopBtn = document.getElementById("btn-studio-stop");
  studioStopBtn.onclick = () => {
    const sRoom = getCurrentStudioRoom();
    if (sRoom) {
      runButtonAction(studioStopBtn, { busyText: "停止中…", doneText: "■ 已停止" }, () => rpc("room.stop", { roomId: sRoom.id }));
    }
  };
  document.getElementById("btn-studio-tui-native").onclick = () => spawnStudioTui("native");
  document.getElementById("btn-studio-tui-shell").onclick = () => spawnStudioTui("shell");
  document.getElementById("btn-studio-tui-back").onclick = () => {
    state.studioMode = "chat";
    renderStudioView();
  };
  const studioDiffBtn = document.getElementById("btn-studio-refresh-diff");
  studioDiffBtn.onclick = () =>
    runButtonAction(studioDiffBtn, { busyText: "扫描…", doneText: "✓" }, () => refreshStudioDiff(true));

  // Composer, Clipboard Image Paste & @ Autocomplete
  const composer = document.getElementById("composer-input");
  composer.addEventListener("input", () => {
    if (state.selectedRoomId) state.roomDrafts.set(state.selectedRoomId, composer.value);
    updateMentionAutocomplete();
  });
  composer.addEventListener("paste", (e) => {
    const items = Array.from(e.clipboardData?.items || []);
    const imgItems = items.filter((it) => it.type?.startsWith("image/"));
    if (imgItems.length === 0) return;
    for (const it of imgItems) {
      const file = it.getAsFile();
      if (!file) continue;
      const reader = new FileReader();
      reader.onload = () => {
        if (typeof reader.result === "string") {
          state.designCaptures.push({
            selector: "剪贴板图片",
            screenshotDataUrl: reader.result,
          });
          renderDesignChips();
          showToast("info", "已从剪贴板附加图片截图至输入框");
        }
      };
      reader.readAsDataURL(file);
    }
  });

  const chatStreamEl = document.getElementById("chat-stream");
  const scrollBottomBtn = document.getElementById("btn-scroll-bottom");
  chatStreamEl.addEventListener("scroll", () => {
    const dist = chatStreamEl.scrollHeight - chatStreamEl.scrollTop - chatStreamEl.clientHeight;
    scrollBottomBtn.style.display = dist > 160 ? "inline-flex" : "none";
  });
  scrollBottomBtn.onclick = () => {
    chatStreamEl.scrollTo({ top: chatStreamEl.scrollHeight, behavior: "smooth" });
    scrollBottomBtn.style.display = "none";
  };
  composer.addEventListener("keydown", (e) => {
    if (state.mentionMenu.open) {
      const agents = getCurrentRoom()?.agents || [];
      const filtered = filterMentionAgents(agents, state.mentionMenu.query);
      if (filtered.length > 0) {
        if (e.key === "ArrowDown") {
          e.preventDefault();
          state.mentionMenu.selectedIdx = (state.mentionMenu.selectedIdx + 1) % filtered.length;
          renderMentionPopup();
          return;
        }
        if (e.key === "ArrowUp") {
          e.preventDefault();
          state.mentionMenu.selectedIdx = (state.mentionMenu.selectedIdx - 1 + filtered.length) % filtered.length;
          renderMentionPopup();
          return;
        }
        if (e.key === "Enter" || e.key === "Tab") {
          e.preventDefault();
          applyMentionSelection(filtered[state.mentionMenu.selectedIdx]);
          return;
        }
        if (e.key === "Escape") {
          state.mentionMenu.open = false;
          renderMentionPopup();
          return;
        }
      }
    }
    if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
      e.preventDefault();
      sendCurrentRoomMessage();
    }
  });

  document.getElementById("btn-send-msg").onclick = () => sendCurrentRoomMessage();
  const stopBtn = document.getElementById("btn-stop-room");
  stopBtn.onclick = () => {
    const room = getCurrentRoom();
    if (room) {
      runButtonAction(stopBtn, { busyText: "停止中…", doneText: "■ 已停止", successToast: "已取消本会话正在运行的后台任务" }, () =>
        rpc("room.stop", { roomId: room.id })
      );
    }
  };

  // Terminal Dock
  document.querySelectorAll("[data-spawn-term]").forEach((btn) => {
    btn.addEventListener("click", () =>
      runButtonAction(btn, { busyText: "启动终端…", doneText: "✓ 已就绪" }, () => spawnTerminal(btn.dataset.spawnTerm))
    );
  });
  document.getElementById("btn-term-max").onclick = () => {
    state.terminalMaximized = !state.terminalMaximized;
    document.getElementById("terminal-dock").classList.toggle("maximized", state.terminalMaximized);
    setTimeout(() => terminalMgr.fitActive(), 60);
  };
  document.getElementById("btn-term-hide").onclick = () => {
    state.terminalOpen = false;
    renderRoomHeader();
    renderTerminalDock();
  };

  // Diff & Live Trace Inspector Drawer
  document.getElementById("insp-tab-diff").onclick = () => {
    state.inspectorTab = "diff";
    renderDiffDrawerContent();
  };
  document.getElementById("insp-tab-trace").onclick = () => {
    state.inspectorTab = "trace";
    renderDiffDrawerContent();
  };
  document.getElementById("btn-diff-unified").onclick = () => {
    state.diffMode = "unified";
    state.inspectorTab = "diff";
    renderDiffDrawerContent();
  };
  document.getElementById("btn-diff-split").onclick = () => {
    state.diffMode = "split";
    state.inspectorTab = "diff";
    const drawer = document.getElementById("diff-drawer");
    if (drawer && drawer.getBoundingClientRect().width < 640) {
      drawer.style.width = `${Math.min(720, Math.round(window.innerWidth * 0.56))}px`;
    }
    renderDiffDrawerContent();
  };
  document.getElementById("btn-diff-wide").onclick = () => {
    state.diffWide = !state.diffWide;
    const drawer = document.getElementById("diff-drawer");
    if (drawer) {
      drawer.style.width = state.diffWide
        ? `${Math.round(window.innerWidth * 0.62)}px`
        : "480px";
    }
  };
  const diffRefreshBtn = document.getElementById("btn-diff-refresh");
  diffRefreshBtn.onclick = () =>
    runButtonAction(diffRefreshBtn, { busyText: "计算…", doneText: "✓ 最新", successToast: "已重新扫描 Git 代码变更" }, () =>
      refreshRoomDiff()
    );
  document.getElementById("btn-diff-close").onclick = () => {
    state.diffOpen = false;
    renderRoomHeader();
    renderDiffDrawer();
  };
  const submitReviewBtn = document.getElementById("btn-submit-diff-review");
  submitReviewBtn.onclick = () =>
    runButtonAction(submitReviewBtn, { busyText: "发送批注…", doneText: "✓ 已派单" }, () => submitInlineDiffReview());

  document.getElementById("diff-body").addEventListener("click", (e) => {
    const addBtn = e.target.closest("[data-diff-draft]");
    if (addBtn) {
      state.diffActiveDraftKey = addBtn.dataset.diffDraft;
      renderDiffDrawerContent();
      setTimeout(() => document.getElementById("inline-diff-draft-input")?.focus(), 20);
      return;
    }
    if (e.target.closest("[data-cancel-diff-draft]")) {
      state.diffActiveDraftKey = null;
      renderDiffDrawerContent();
      return;
    }
    const saveBtn = e.target.closest("[data-save-diff-draft]");
    if (saveBtn) {
      const val = document.getElementById("inline-diff-draft-input")?.value.trim();
      if (!val) {
        showToast("warning", "请输入针对该代码行的修改说明");
        return;
      }
      state.diffComments.push({
        filePath: decodeURIComponent(saveBtn.dataset.file),
        lineNumber: Number(saveBtn.dataset.line),
        side: saveBtn.dataset.side,
        codeSnippet: decodeURIComponent(saveBtn.dataset.snippet),
        comment: val,
      });
      state.diffActiveDraftKey = null;
      renderDiffDrawerContent();
      showToast("info", `已加入第 ${state.diffComments.length} 条行内代码批注`);
      return;
    }
    const rmBtn = e.target.closest("[data-remove-diff-comment]");
    if (rmBtn) {
      state.diffComments.splice(Number(rmBtn.dataset.removeDiffComment), 1);
      renderDiffDrawerContent();
    }
  });

  document.addEventListener("click", (e) => {
    const extLink = e.target.closest("[data-ext-url]");
    if (extLink) {
      e.preventDefault();
      rpc("shell.openUrl", { url: extLink.dataset.extUrl }).catch((err) => showToast("error", err.message));
      return;
    }
    const copyBtn = e.target.closest("[data-copy-code]");
    if (copyBtn) {
      const code = decodeURIComponent(copyBtn.dataset.copyCode);
      navigator.clipboard?.writeText(code);
      copyBtn.classList.add("is-done");
      copyBtn.textContent = "✓ 已复制";
      showToast("success", "代码片段已复制到剪贴板");
      setTimeout(() => {
        copyBtn.classList.remove("is-done");
        copyBtn.textContent = "复制代码";
      }, 1100);
    }
  });

  // Arena
  document.getElementById("btn-arena-pick-repo").onclick = async () => {
    const cur = document.getElementById("arena-repo-input").value;
    const res = await rpc("dialog.pickFolder", { initialDirectory: cur, title: "选择赛马 Git 仓库目录" });
    if (res?.path) document.getElementById("arena-repo-input").value = res.path;
  };
  const arenaStartBtn = document.getElementById("btn-arena-start");
  arenaStartBtn.onclick = () =>
    runButtonAction(arenaStartBtn, { busyText: "创建分支并派单…", doneText: "🚀 已发车" }, () => startArenaRace());

  // Fleet & Telemetry Actions
  const hideAllBtn = document.getElementById("btn-hide-all-windows");
  hideAllBtn.onclick = () =>
    runButtonAction(hideAllBtn, { busyText: "隐身收纳中…", doneText: "👻 全部已隐身" }, () => rpc("stealth.hideAll"));

  const repairBtn = document.getElementById("btn-repair-sandboxes");
  repairBtn.onclick = () =>
    runButtonAction(repairBtn, { busyText: "检查沙箱…", doneText: "🛡️ 沙箱正常", successToast: "多开沙箱兼容性检查已完成" }, () =>
      rpc("sandbox.repairAll")
    );

  const refreshUsageBtn = document.getElementById("btn-refresh-all-usage");
  refreshUsageBtn.onclick = () =>
    runButtonAction(
      refreshUsageBtn,
      { busyText: "并行查询中…", doneText: "✓ 额度已更新", successToast: "已完成全平台账号额度与认证状态刷新" },
      () => rpc("pool.refreshUsage", { all: true, force: true })
    );

  document.getElementById("btn-add-profile").onclick = () => openAddProfileModal();

  document.getElementById("fleet-search-input").addEventListener("input", (e) => {
    state.fleetSearch = e.target.value.trim().toLowerCase();
    renderFleetView();
  });

  const discoverBtn = document.getElementById("btn-fleet-discover");
  discoverBtn.onclick = () =>
    runButtonAction(discoverBtn, { busyText: "扫描进程…", doneText: "✓ 已刷新", successToast: "进程树快照已更新" }, () =>
      rpc("client.refreshDiscovery")
    );

  const revealAllBtn = document.getElementById("btn-fleet-reveal-all");
  revealAllBtn.onclick = () =>
    runButtonAction(revealAllBtn, { busyText: "恢复中…", doneText: "👁 已显现" }, () => rpc("stealth.revealAll"));
}

/* ==========================================================================
   3. Topbar Battery HUD, Stealth Status & Account Quick-Inspect Popover
   ========================================================================== */
function applyTheme(themeId) {
  const valid = THEMES.some((t) => t.id === themeId) ? themeId : "obsidian";
  if (state.data?.settings) state.data.settings.theme = valid;
  document.documentElement.setAttribute("data-theme", valid);
  terminalMgr.setTheme(valid);
  renderThemeSwitcher(valid);
  fireAndForget("window.syncTheme", { theme: valid });
}

function closeThemePopover() {
  state.themePopoverOpen = false;
  document.getElementById("theme-popover")?.remove();
}

function openThemePopover(anchorEl) {
  if (state.themePopoverOpen) {
    closeThemePopover();
    return;
  }
  closeThemePopover();
  closeTopbarPoolPopover();
  closeRosterPopover();
  closeAccountPopover();
  state.themePopoverOpen = true;

  const activeTheme = document.documentElement.getAttribute("data-theme") || "obsidian";
  const rect = anchorEl.getBoundingClientRect();
  const pop = document.createElement("div");
  pop.id = "theme-popover";
  pop.className = "theme-popover";
  pop.style.top = `${rect.bottom + 6}px`;
  pop.style.left = `${Math.max(12, Math.min(window.innerWidth - 198, rect.right - 185))}px`;
  pop.innerHTML = THEMES.map(
    (t) =>
      `<button type="button" class="theme-popover-item ${t.id === activeTheme ? "active" : ""}" data-pick-theme="${t.id}">` +
        `<span style="display:flex;align-items:center;gap:8px"><span class="theme-swatch" style="background:${t.swatch}"></span><span>${t.label}</span></span>` +
        `<span>${t.id === activeTheme ? "✓" : ""}</span>` +
      `</button>`
  ).join("");
  document.body.appendChild(pop);
  applyLanguageToRoot(pop);

  pop.querySelectorAll("[data-pick-theme]").forEach((btn) => {
    btn.onclick = () => {
      const next = btn.dataset.pickTheme;
      closeThemePopover();
      applyTheme(next);
      rpc("settings.update", { theme: next }).catch(() => {});
      const label = THEMES.find((x) => x.id === next)?.label || next;
      showToast("info", `已切换界面风格：${label}`);
    };
  });
}

function renderThemeSwitcher(activeTheme) {
  const el = document.getElementById("theme-switcher");
  if (!el) return;
  const cur = THEMES.find((t) => t.id === activeTheme) || THEMES[0];
  el.innerHTML =
    `<button type="button" class="theme-picker-btn" id="theme-select-box" title="切换界面风格">` +
      `<span class="theme-swatch" style="background:${cur.swatch}"></span>` +
      `<span>${cur.label}</span>` +
      `<span style="color:var(--text-muted);font-size:10px">▾</span>` +
    `</button>`;
  const btn = document.getElementById("theme-select-box");
  btn.onclick = (ev) => {
    ev.stopPropagation();
    openThemePopover(btn);
  };
  applyLanguageToRoot(el);
}

function closeCommandPalette() {
  state.cmdPaletteOpen = false;
  document.getElementById("cmd-palette-root").innerHTML = "";
}

function openCommandPalette() {
  state.cmdPaletteOpen = true;
  state.cmdPaletteQuery = "";
  state.cmdPaletteIdx = 0;

  const root = document.getElementById("cmd-palette-root");
  root.innerHTML = `
    <div class="cmd-palette-backdrop" id="cmd-palette-backdrop">
      <div class="cmd-palette-panel">
        <div class="cmd-palette-head">
          ${icon("search", 15)}
          <input id="cmd-palette-input" class="cmd-palette-input" placeholder="快速跳转项目、会话、视图或执行命令…" />
          <span class="kbd-hint">ESC</span>
        </div>
        <div class="cmd-palette-list" id="cmd-palette-list"></div>
      </div>
    </div>
  `;
  applyLanguageToRoot(root);

  document.getElementById("cmd-palette-backdrop").onclick = (e) => {
    if (e.target.id === "cmd-palette-backdrop") closeCommandPalette();
  };

  const getItems = () => {
    const en = getUiLanguage() === "en";
    const items = [
      { label: en ? "Switch to Project Workspace" : "前往：项目工作台", sub: "View", run: () => switchView("command") },
      { label: en ? "Switch to Parallel Worktree Arena" : "前往：多智能体赛马竞技场", sub: "View", run: () => switchView("arena") },
      { label: en ? "Switch to Accounts & Quota Pool" : "前往：账号与额度池", sub: "View", run: () => switchView("fleet") },
      { label: en ? "Create New Project…" : "新建项目工作空间…", sub: "Project", run: () => openCreateProjectModal() },
      { label: en ? "Open Existing Project Folder…" : "打开本地已有项目文件夹…", sub: "Project", run: () => openExistingProjectPicker() },
      { label: en ? "Add Account / Direct API / CLI Agent…" : "添加智能体账号 / 直连 API / CLI…", sub: "Account", run: () => openAddProfileModal("api") },
      { label: en ? "Toggle Integrated ConPTY Terminal" : "展开 / 收起内置分屏终端", sub: "IDE", run: () => { switchView("command"); document.getElementById("btn-toggle-term")?.click(); } },
      { label: en ? "Open Git Diff & Live Trace Drawer" : "打开右侧 Git Diff 与实时轨迹面板", sub: "IDE", run: () => { switchView("command"); state.diffOpen = true; renderRoomHeader(); renderDiffDrawer(); } },
      { label: en ? "Toggle Sidebar (Ctrl+B)" : "折叠 / 展开左侧项目树 (Ctrl+B)", sub: "Layout", run: () => toggleSidebarCollapse() },
      { label: en ? "Refresh All Quotas & Processes" : "立即刷新全平台配额与守护进程", sub: "Pool", run: () => document.getElementById("btn-rail-refresh")?.click() },
      ...((state.data?.workspaces || [])
        .filter((w) => w.hasDesktopClient !== false && w.isLaunchable !== false && w.kind !== "ExternalUnresolved")
        .map((w) => ({
          label: en ? `Launch Desktop App: ${w.displayName}` : `启动桌面客户端：${w.displayName}`,
          sub: w.client,
          run: () => rpc("client.launch", { workspaceId: w.id, stealth: false }).catch((err) => showToast("error", err.message)),
        }))),
      ...THEMES.map((t) => ({
        label: en ? `Theme: ${t.id[0].toUpperCase() + t.id.slice(1)}` : `切换主题：${t.label}`,
        sub: "Theme",
        run: () => { applyTheme(t.id); rpc("settings.update", { theme: t.id }).catch(() => {}); },
      })),
    ];
    for (const r of getSharedRooms()) {
      const pName = getProjectNameFromPath(getRoomProjectPath(r));
      items.push({
        label: `${pName} / ${r.title}`,
        sub: r.worktreeBranch ? `⑂ ${r.worktreeBranch}` : "Thread",
        run: () => { switchView("command"); selectRoom(r.id); },
      });
    }
    const q = state.cmdPaletteQuery;
    if (!q) return items;
    return items.filter((it) => it.label.toLowerCase().includes(q) || it.sub.toLowerCase().includes(q));
  };

  const renderList = () => {
    const listEl = document.getElementById("cmd-palette-list");
    if (!listEl) return;
    const items = getItems();
    if (items.length === 0) {
      listEl.innerHTML = `<div style="padding:16px;text-align:center;color:var(--text-muted);font-size:12px">没有匹配的命令或项目</div>`;
      applyLanguageToRoot(listEl);
      return;
    }
    state.cmdPaletteIdx = Math.max(0, Math.min(state.cmdPaletteIdx, items.length - 1));
    listEl.innerHTML = items
      .map(
        (it, idx) =>
          `<div class="cmd-palette-item ${idx === state.cmdPaletteIdx ? "selected" : ""}" data-cmd-idx="${idx}">` +
            `<span style="font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${escapeHtml(it.label)}</span>` +
            `<span class="kbd-hint">${escapeHtml(it.sub)}</span>` +
          `</div>`
      )
      .join("");
    listEl.querySelectorAll("[data-cmd-idx]").forEach((row) => {
      row.onclick = () => {
        const chosen = items[Number(row.dataset.cmdIdx)];
        closeCommandPalette();
        chosen?.run();
      };
    });
  };

  const inp = document.getElementById("cmd-palette-input");
  inp.oninput = () => {
    state.cmdPaletteQuery = inp.value.trim().toLowerCase();
    state.cmdPaletteIdx = 0;
    renderList();
  };
  inp.onkeydown = (e) => {
    const items = getItems();
    if (e.key === "ArrowDown" && items.length > 0) {
      e.preventDefault();
      state.cmdPaletteIdx = (state.cmdPaletteIdx + 1) % items.length;
      renderList();
    } else if (e.key === "ArrowUp" && items.length > 0) {
      e.preventDefault();
      state.cmdPaletteIdx = (state.cmdPaletteIdx - 1 + items.length) % items.length;
      renderList();
    } else if (e.key === "Enter" && items[state.cmdPaletteIdx]) {
      e.preventDefault();
      const chosen = items[state.cmdPaletteIdx];
      closeCommandPalette();
      chosen.run();
    }
  };

  renderList();
  setTimeout(() => inp.focus(), 20);
}

function closeTopbarPoolPopover() {
  state.topbarPoolOpen = false;
  document.getElementById("topbar-pool-popover")?.remove();
}

function openTopbarPoolPopover(anchorEl) {
  if (state.topbarPoolOpen) {
    closeTopbarPoolPopover();
    return;
  }
  closeTopbarPoolPopover();
  closeRosterPopover();
  closeAccountPopover();
  state.topbarPoolOpen = true;

  const entries = (state.data?.pool?.entries || []).filter((e) => e.health !== "UnsupportedClient");
  const wsMap = new Map((state.data?.workspaces || []).map((w) => [w.id, w]));
  const rect = anchorEl.getBoundingClientRect();
  const pop = document.createElement("div");
  pop.id = "topbar-pool-popover";
  pop.className = "roster-popover";
  pop.style.top = `${rect.bottom + 6}px`;
  pop.style.left = `${Math.max(12, Math.min(window.innerWidth - 440, rect.left))}px`;

  pop.innerHTML = `
    <div style="display:flex;align-items:center;justify-content:space-between">
      <strong style="font-size:12.5px">在线智能体与算力概览 (${entries.length})</strong>
      <button class="btn btn-ghost btn-sm" id="tpp-go-fleet">打开账号池 →</button>
    </div>
    <div class="roster-popover-list">
      ${entries
        .map((e) => {
          const ws = wsMap.get(e.workspaceId);
          const canLaunchDesktop = ws && ws.hasDesktopClient !== false && ws.isLaunchable !== false;
          const plat = formatPlatformShort(e.client);
          const rem = e.primaryRemainingPercent != null ? Math.round(e.primaryRemainingPercent) : null;
          const status =
            e.activeTasks > 0
              ? `⚡ 执行中`
              : rem != null && (e.client === "Codex" || e.creditsUnlimited === false)
              ? `剩余 ${rem}%`
              : e.authReady
              ? "● 就绪"
              : "○ 待登录";
          return `
            <div class="roster-popover-row" style="grid-template-columns:1fr auto">
              <div style="display:flex;align-items:center;gap:6px;min-width:0">
                <span class="plat-badge ${plat.cls}">${plat.code}</span>
                <strong style="font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${escapeHtml(e.displayName)}</strong>
                <span style="font-size:11px;color:var(--text-muted)">${escapeHtml(status)}</span>
              </div>
              <div style="display:flex;gap:4px">
                ${canLaunchDesktop ? `<button class="btn btn-ghost btn-sm" data-tpp-launch="${escapeHtml(e.workspaceId)}" title="启动或前置该账号的官方桌面客户端窗口">🖥 桌面端</button>` : ""}
                <button class="btn btn-ghost btn-sm" data-tpp-studio="${escapeHtml(e.workspaceId)}" title="在当前项目中将此账号设为默认主力智能体">💬 设为当前主力</button>
                <button class="btn btn-ghost btn-sm" data-tpp-at="${escapeHtml(e.workspaceId)}">@ 协作</button>
              </div>
            </div>`;
        })
        .join("")}
    </div>
  `;

  document.body.appendChild(pop);
  applyLanguageToRoot(pop);
  document.getElementById("tpp-go-fleet").onclick = () => {
    closeTopbarPoolPopover();
    switchView("fleet");
  };
  pop.querySelectorAll("[data-tpp-launch]").forEach((btn) => {
    btn.onclick = () => {
      const targetWsId = btn.dataset.tppLaunch;
      closeTopbarPoolPopover();
      rpc("client.launch", { workspaceId: targetWsId, stealth: false }).catch((err) => showToast("error", err.message));
    };
  });
  pop.querySelectorAll("[data-tpp-studio]").forEach((btn) => {
    btn.onclick = () => {
      closeTopbarPoolPopover();
      useWorkspaceInProject(btn.dataset.tppStudio);
    };
  });
  pop.querySelectorAll("[data-tpp-at]").forEach((btn) => {
    btn.onclick = () => {
      closeTopbarPoolPopover();
      mentionWorkspaceInCommandCenter(btn.dataset.tppAt);
    };
  });
}

function renderTopbarPool() {
  const data = state.data;
  if (!data) return;

  const stealth = data.stealth || {};
  const stealthBtn = document.getElementById("btn-topbar-stealth");
  if (!stealthBtn.classList.contains("is-busy")) {
    const stealthOn = stealth.enabled !== false;
    const vis = stealth.visibleClientWindows ?? 0;
    stealthBtn.style.display = "inline-flex";
    stealthBtn.style.alignItems = "center";
    stealthBtn.style.gap = "5px";
    stealthBtn.innerHTML = stealthOn
      ? `<span class="status-mini-dot on"></span>${icon("eyeOff", 12)}<span>隐身</span>`
      : `<span class="status-mini-dot"></span>${icon("eye", 12)}<span>窗口${vis > 0 ? ` (${vis})` : ""}</span>`;
    stealthBtn.title = stealthOn
      ? "当前：窗口隐身开启（后台无窗运行）· 点击切换为显示外部客户端窗口"
      : "当前：显示客户端窗口 · 点击切换为全部后台隐身";
  }

  const failoverBtn = document.getElementById("btn-toggle-failover");
  if (!failoverBtn.classList.contains("is-busy")) {
    const enabled = data.settings?.autoFailoverEnabled;
    failoverBtn.style.display = "inline-flex";
    failoverBtn.style.alignItems = "center";
    failoverBtn.style.gap = "5px";
    failoverBtn.innerHTML = enabled
      ? `<span class="status-mini-dot cyan"></span>${icon("zap", 12)}<span>自动换号</span>`
      : `<span class="status-mini-dot"></span>${icon("zap", 12)}<span style="color:var(--text-muted)">自动换号</span>`;
  }

  const strip = document.getElementById("topbar-pool");
  const entries = (data.pool?.entries || []).filter((e) => e.health !== "UnsupportedClient");
  const activeCount = entries.reduce((sum, e) => sum + (e.activeTasks || 0), 0);
  const readyCount = data.pool?.readyNodeCount ?? entries.filter((e) => e.authReady).length;

  strip.innerHTML =
    `<div class="pool-pill" id="topbar-pool-summary" title="点击快速查看全部智能体与配额状态">` +
      `<span class="pool-dot ${readyCount > 0 ? "healthy" : "unknown"}"></span>` +
      `<span>${readyCount}/${entries.length} 就绪</span>` +
      `<span style="color:var(--text-muted);font-size:10px">▾</span>` +
    `</div>` +
    (activeCount > 0
      ? `<div class="pool-pill active-task" style="color:var(--accent-cyan);font-weight:600"><span class="pulse-dot"></span> <span>${activeCount} 运行中</span></div>`
      : "");

  const summaryPill = document.getElementById("topbar-pool-summary");
  if (summaryPill) {
    summaryPill.onclick = (ev) => {
      ev.stopPropagation();
      openTopbarPoolPopover(summaryPill);
    };
  }

  // Rail badges
  const runningSharedRooms = (data.rooms || []).filter((r) => !r.isDedicatedAgentRoom && r.isRunning).length;
  setBadge("badge-rooms", runningSharedRooms);
  const workingAgents = entries.filter((e) => (e.activeTasks || 0) > 0).length;
  setBadge("badge-studio", workingAgents);
  const runningArenas = (data.arenaSessions || []).filter((s) => s.contestants?.some((c) => c.state === "Running")).length;
  setBadge("badge-arena", runningArenas);
  const exhaustedCount = entries.filter((e) => e.health === "Exhausted").length;
  setBadge("badge-instances", exhaustedCount);
}

function openAccountPopover(workspaceId, anchorLeft) {
  if (state.inspectedPopoverWsId === workspaceId) {
    closeAccountPopover();
    return;
  }
  state.inspectedPopoverWsId = workspaceId;
  state.popoverLeft = Math.max(12, Math.min(window.innerWidth - 340, anchorLeft));
  renderAccountPopoverContent();
}

function closeAccountPopover() {
  state.inspectedPopoverWsId = null;
  document.getElementById("popover-root").innerHTML = "";
}

function renderAccountPopoverContent() {
  const root = document.getElementById("popover-root");
  const wsId = state.inspectedPopoverWsId;
  if (!wsId || !state.data) {
    root.innerHTML = "";
    return;
  }
  const entry = (state.data.pool?.entries || []).find((e) => e.workspaceId === wsId);
  const ws = (state.data.workspaces || []).find((w) => w.id === wsId);
  if (!entry || !ws) {
    root.innerHTML = "";
    return;
  }

  const plat = formatPlatformShort(entry.client);
  const pRem = entry.primaryRemainingPercent != null ? Math.round(entry.primaryRemainingPercent) : null;
  const sRem = entry.secondaryRemainingPercent != null ? Math.round(entry.secondaryRemainingPercent) : null;

  root.innerHTML = `
    <div class="account-popover" id="account-popover" style="left:${state.popoverLeft || 120}px">
      <div style="display:flex;align-items:center;justify-content:space-between;gap:8px">
        <div style="display:flex;align-items:center;gap:6px">
          <span class="plat-badge ${plat.cls}">${plat.code}</span>
          <strong style="font-size:13.5px">${escapeHtml(entry.displayName)}</strong>
        </div>
        <span class="tag ${entry.authReady ? "write" : "readonly"}">${escapeHtml(entry.plan || entry.engineLabel)}</span>
      </div>
      <div style="font-size:11.5px;color:var(--text-secondary)">${escapeHtml(entry.statusText)}</div>
      ${
        entry.client === "Codex" && pRem != null
          ? `<div class="battery-box">
              <div class="quota-bar-row">
                <div style="display:flex;justify-content:space-between;font-size:11px">
                  <span>5h 窗口剩余 <strong>${pRem}%</strong></span>
                  <span style="color:var(--text-muted)">${escapeHtml(entry.primaryResetCountdown ? entry.primaryResetCountdown + "重置" : "满额")}</span>
                </div>
                <div class="quota-bar-track"><div class="quota-bar-fill" style="width:${pRem}%;background:${pRem <= 10 ? "var(--danger)" : pRem <= 28 ? "var(--warning)" : "var(--success)"}"></div></div>
              </div>
              <div class="quota-bar-row">
                <div style="display:flex;justify-content:space-between;font-size:11px">
                  <span>7d 周剩余 <strong>${sRem ?? 100}%</strong></span>
                  <span style="color:var(--text-muted)">${escapeHtml(entry.secondaryResetCountdown ? entry.secondaryResetCountdown + "重置" : "")}</span>
                </div>
                <div class="quota-bar-track"><div class="quota-bar-fill" style="width:${sRem ?? 100}%;background:var(--accent-cyan)"></div></div>
              </div>
            </div>`
          : ""
      }
      <div style="font-size:11px;color:var(--text-muted);display:flex;justify-content:space-between">
        <span>本机完成：${entry.sessionTurns} 轮</span>
        <span>会话 Token：${formatTokensCompact((entry.sessionInputTokens || 0) + (entry.sessionOutputTokens || 0))}</span>
      </div>
      <div style="display:grid;grid-template-columns:1fr 1fr;gap:6px;margin-top:2px">
        <button class="btn btn-primary btn-sm" id="pop-mention-btn">💬 在指挥舱 @Ta</button>
        <button class="btn btn-sm" id="pop-refresh-btn">↻ 刷新状态</button>
        ${
          ws.hasDesktopClient !== false
            ? `<button class="btn btn-sm" id="pop-stealth-start-btn">👻 后台静默守护</button>
               <button class="btn btn-sm" id="pop-login-win-btn">${ws.visibleWindowCount > 0 ? "👻 收回桌面窗" : "🖥 打开桌面客户端"}</button>`
            : `<button class="btn btn-sm" id="pop-edit-universal-btn">⚙ 编辑模型/密钥</button>
               <button class="btn btn-sm" id="pop-open-term-btn">⌨ 打开隔离终端</button>`
        }
      </div>
    </div>
  `;

  document.getElementById("pop-mention-btn").onclick = () => {
    closeAccountPopover();
    mentionWorkspaceInCommandCenter(ws.id);
  };
  const popRef = document.getElementById("pop-refresh-btn");
  popRef.onclick = () =>
    runButtonAction(popRef, { busyText: "查询中…", doneText: "✓ 最新" }, () =>
      rpc("pool.refreshUsage", { workspaceId: ws.id, all: false, force: true })
    );
  const popStealth = document.getElementById("pop-stealth-start-btn");
  if (popStealth) {
    popStealth.onclick = () =>
      runButtonAction(popStealth, { busyText: "静默启动…", doneText: "👻 运行中" }, () =>
        rpc("client.launch", { workspaceId: ws.id, stealth: true })
      );
  }
  const popLogin = document.getElementById("pop-login-win-btn");
  if (popLogin) {
    popLogin.onclick = () =>
      runButtonAction(popLogin, { busyText: "处理中…", doneText: "✓ 完成" }, () =>
        ws.visibleWindowCount > 0
          ? rpc("stealth.setLoginWindow", { workspaceId: ws.id, reveal: false })
          : rpc("client.launch", { workspaceId: ws.id, stealth: false })
      );
  }
  const popEditUniv = document.getElementById("pop-edit-universal-btn");
  if (popEditUniv) {
    popEditUniv.onclick = () => {
      closeAccountPopover();
      const rec = (state.data?.universalAgents || []).find((u) => u.workspaceId === ws.id || u.id === ws.providerId);
      openAddProfileModal(ws.client === "MarketCli" ? "cli" : "api", rec || null);
    };
  }
  const popTerm = document.getElementById("pop-open-term-btn");
  if (popTerm) {
    popTerm.onclick = () => {
      closeAccountPopover();
      switchView("command");
      state.terminalOpen = true;
      state.terminalAccountId = ws.id;
      renderTerminalDock();
      spawnTerminal("shell");
    };
  }
}

function mentionWorkspaceInCommandCenter(workspaceId) {
  switchView("command");
  const room = getCurrentRoom();
  if (!room) return;
  const agent = (room.agents || []).find((a) => a.workspaceId === workspaceId);
  if (agent) {
    insertMentionIntoComposer(agent.alias);
  } else {
    rpc("room.syncRoster").then(() => {
      const updated = getCurrentRoom();
      const found = (updated?.agents || []).find((a) => a.workspaceId === workspaceId);
      if (found) insertMentionIntoComposer(found.alias);
    });
  }
}

function setBadge(id, count) {
  const el = document.getElementById(id);
  if (!el) return;
  if (count > 0) {
    el.style.display = "flex";
    el.textContent = String(count);
  } else {
    el.style.display = "none";
  }
}

function renderDesignBar() {
  const bar = document.getElementById("design-toolbar");
  const d = state.data?.design;
  const visible = !!d?.visible;
  bar.style.display = visible ? "flex" : "none";
  document.getElementById("btn-toggle-design").classList.toggle("btn-primary", visible);
  if (visible) {
    const input = document.getElementById("design-url-input");
    if (document.activeElement !== input && d.url) input.value = d.url;
    const inspectBtn = document.getElementById("btn-design-inspect");
    if (!inspectBtn.classList.contains("is-busy")) {
      inspectBtn.classList.toggle("btn-primary", !!d.inspecting);
      inspectBtn.textContent = d.inspecting ? "🎯 正在点选右侧元素… (点击取消)" : "🎯 点选页面元素抓取 DOM+截图";
    }
  }
}

/* ==========================================================================
   4. View 1: Project-First Workspace (Projects -> Threads + Primary Agent)
   ========================================================================== */
function getSharedRooms() {
  return (state.data?.rooms || []).filter((r) => !r.isDedicatedAgentRoom);
}

function getCurrentRoom() {
  const rooms = getSharedRooms();
  return rooms.find((r) => r.id === state.selectedRoomId) || rooms[0] || null;
}

function getPoolEntryMap() {
  return new Map((state.data?.pool?.entries || []).map((e) => [e.workspaceId, e]));
}

function normalizePathKey(p) {
  return String(p || "")
    .trim()
    .replace(/[\\/]+$/, "")
    .toLowerCase();
}

function getRoomProjectPath(room) {
  return room?.projectPath || room?.repositoryRoot || room?.workingDirectory || "";
}

function getProjectNameFromPath(p) {
  const cleaned = String(p || "").trim().replace(/[\\/]+$/, "");
  if (!cleaned) return "未命名项目";
  return cleaned.split(/[\\/]/).filter(Boolean).pop() || cleaned;
}

function getCurrentPrimaryWsId(room = getCurrentRoom()) {
  const launchable = (state.data?.workspaces || []).filter((w) => w.isLaunchable);
  if (room?.primaryWorkspaceId && launchable.some((w) => w.id === room.primaryWorkspaceId)) {
    return room.primaryWorkspaceId;
  }
  const poolMap = getPoolEntryMap();
  const readyRoomAgent = (room?.agents || []).find(
    (a) => launchable.some((w) => w.id === a.workspaceId) && poolMap.get(a.workspaceId)?.authReady
  );
  if (readyRoomAgent) return readyRoomAgent.workspaceId;
  const readyLaunchable = launchable.find((w) => poolMap.get(w.id)?.authReady);
  if (readyLaunchable) return readyLaunchable.id;
  const firstRoomAgentWs = room?.agents?.[0]?.workspaceId;
  if (firstRoomAgentWs && launchable.some((w) => w.id === firstRoomAgentWs)) {
    return firstRoomAgentWs;
  }
  return launchable[0]?.id || "";
}

function getPrimaryWorkspaceInfo(room = getCurrentRoom()) {
  const wsId = getCurrentPrimaryWsId(room);
  const ws = (state.data?.workspaces || []).find((w) => w.id === wsId) || null;
  const pe = getPoolEntryMap().get(wsId) || null;
  return {
    id: wsId,
    name: ws?.displayName || pe?.displayName || room?.agents?.[0]?.alias || "智能体",
    client: ws?.client || pe?.client || "Codex",
    plan: pe?.plan || "",
  };
}

async function useWorkspaceInProject(workspaceId) {
  if (!workspaceId) return;
  if (state.activeView !== "command") switchView("command");
  const room = getCurrentRoom();
  if (room && !room.isRunning) {
    try {
      const updated = await rpc("room.setPrimaryWorkspace", { roomId: room.id, workspaceId });
      if (updated) room.primaryWorkspaceId = updated.primaryWorkspaceId || workspaceId;
      renderComposerFooterControls();
      renderChatStream(false);
      const ws = (state.data?.workspaces || []).find((w) => w.id === workspaceId);
      showToast("success", `✓ 当前对话主力已切换为「${ws?.displayName || workspaceId}」，直接回车即可对话`);
    } catch (err) {
      showToast("error", err.message);
    }
  }
  document.getElementById("composer-input")?.focus();
}

async function createThreadInProject(projectPath, titleHint = null) {
  if (!projectPath) return;
  const rooms = getSharedRooms();
  const key = normalizePathKey(projectPath);
  const existingInProj = rooms.filter((r) => normalizePathKey(getRoomProjectPath(r)) === key);
  const title = titleHint || (existingInProj.length === 0 ? "主对话" : `对话 ${existingInProj.length + 1}`);
  const created = await rpc("room.create", {
    title,
    directory: projectPath,
    createWorktree: false,
    sandboxMode: "WorkspaceWrite",
    primaryWorkspaceId: getCurrentPrimaryWsId(),
  });
  state.collapsedProjects.delete(key);
  if (created?.id) selectRoom(created.id);
}

async function openExistingProjectPicker() {
  try {
    const res = await rpc("project.open", { primaryWorkspaceId: getCurrentPrimaryWsId() });
    if (!res) return;
    if (res.state) applySnapshot(res.state, false);
    if (res.projectPath) state.collapsedProjects.delete(normalizePathKey(res.projectPath));
    if (res.roomId) selectRoom(res.roomId);
    document.getElementById("composer-input")?.focus();
  } catch (err) {
    showToast("error", err.message || "打开项目失败");
  }
}

function selectRoom(roomId) {
  const composer = document.getElementById("composer-input");
  if (state.selectedRoomId && composer) {
    state.roomDrafts.set(state.selectedRoomId, composer.value);
  }
  state.selectedRoomId = roomId;
  const targetRoom = getSharedRooms().find((r) => r.id === roomId);
  if (targetRoom) {
    state.collapsedProjects.delete(normalizePathKey(getRoomProjectPath(targetRoom)));
  }
  if (composer) {
    composer.value = state.roomDrafts.get(roomId) || "";
  }
  renderSessionList();
  renderRoomHeader();
  renderChatStream(true);
  renderComposerFooterControls();
  if (state.diffOpen) refreshRoomDiff();
}

function renderSessionList() {
  const rooms = getSharedRooms();
  const listEl = document.getElementById("session-list");
  if (!listEl) return;

  // Group rooms under projects (preserving backend project order + synthesizing any unlisted room folders)
  const groupsMap = new Map();
  const orderedGroups = [];
  const ensureGroup = (rawPath, nameHint, isGitHint = false) => {
    const key = normalizePathKey(rawPath);
    if (!key) return null;
    let g = groupsMap.get(key);
    if (!g) {
      g = {
        key,
        path: rawPath,
        name: nameHint || getProjectNameFromPath(rawPath),
        isGit: !!isGitHint,
        threads: [],
      };
      groupsMap.set(key, g);
      orderedGroups.push(g);
    }
    return g;
  };

  for (const p of state.data?.projects || []) {
    ensureGroup(p.path, p.name, p.isGitRepository);
  }
  for (const r of rooms) {
    const pPath = getRoomProjectPath(r);
    const g = ensureGroup(pPath, getProjectNameFromPath(pPath), !!r.repositoryRoot);
    if (g) g.threads.push(r);
  }

  if (rooms.length > 0 && (!state.selectedRoomId || !rooms.some((r) => r.id === state.selectedRoomId))) {
    const firstGrpWithThread = orderedGroups.find((g) => g.threads.length > 0);
    state.selectedRoomId = firstGrpWithThread ? firstGrpWithThread.threads[0].id : rooms[0].id;
  }

  const q = state.sessionFilter;
  const visibleGroups = orderedGroups
    .map((g) => {
      if (!q) return g;
      const projMatches = g.name.toLowerCase().includes(q) || g.path.toLowerCase().includes(q);
      if (projMatches) return g;
      const matchingThreads = g.threads.filter(
        (r) =>
          r.title.toLowerCase().includes(q) ||
          (r.worktreeBranch || "").toLowerCase().includes(q) ||
          (r.workingDirectory || "").toLowerCase().includes(q)
      );
      return matchingThreads.length > 0 ? { ...g, threads: matchingThreads } : null;
    })
    .filter(Boolean);

  if (visibleGroups.length === 0) {
    listEl.innerHTML = `<div style="padding:18px 10px;text-align:center;font-size:12px;color:var(--text-muted)">暂无匹配项目，点击上方「＋ 新建项目」或「📂 打开」开始</div>`;
    return;
  }

  const currentRoom = getCurrentRoom();
  const activeProjKey = currentRoom ? normalizePathKey(getRoomProjectPath(currentRoom)) : "";

  listEl.innerHTML = visibleGroups
    .map((g) => {
      const collapsed = !q && state.collapsedProjects.has(g.key);
      const isCurrentProj = g.key === activeProjKey;
      const chevron = collapsed ? "▸" : "▾";
      const threadsHtml = collapsed
        ? ""
        : g.threads.length === 0
        ? `<div class="project-threads"><div class="project-thread-item" data-proj-empty-new="${escapeHtml(g.path)}" style="color:var(--text-muted);font-size:11.5px"><span>＋ 点击在此项目开启新对话</span></div></div>`
        : `<div class="project-threads">` +
          g.threads
            .map((r) => {
              const active = r.id === state.selectedRoomId;
              const threadIcon = r.isIsolatedWorktree ? icon("branch", 12) : icon("message", 12);
              const wtTag = r.isIsolatedWorktree
                ? `<span class="tag worktree" style="padding:0 5px;font-size:10px">${escapeHtml(r.worktreeBranch)}</span>`
                : "";
              const rightMeta = r.isRunning
                ? `<span class="pulse-dot" style="flex-shrink:0" title="智能体正在执行"></span>`
                : "";
              return (
                `<div class="project-thread-item ${active ? "active" : ""}" data-select-room="${escapeHtml(r.id)}" title="${escapeHtml(r.title)} · ${escapeHtml(r.workingDirectory || "")}">` +
                  `<div style="display:flex;align-items:center;gap:6px;min-width:0;overflow:hidden">` +
                    `<span style="opacity:0.78;flex-shrink:0;display:inline-flex">${threadIcon}</span>` +
                    `<span class="project-thread-title">${escapeHtml(r.title)}</span>` +
                    wtTag +
                  `</div>` +
                  rightMeta +
                `</div>`
              );
            })
            .join("") +
          `</div>`;

      return (
        `<div class="project-group ${isCurrentProj ? "is-active-proj" : ""}">` +
          `<div class="project-group-header" data-toggle-proj="${escapeHtml(g.key)}" data-proj-path="${escapeHtml(g.path)}" title="${escapeHtml(g.path)}">` +
            `<span style="font-size:10px;color:var(--text-muted);width:10px;flex-shrink:0">${chevron}</span>` +
            `<span style="color:var(--accent-cyan);display:inline-flex;flex-shrink:0">${icon("folder", 13)}</span>` +
            `<span class="project-group-name">${escapeHtml(g.name)}</span>` +
            `<div class="project-group-actions">` +
              `<button class="proj-icon-btn" data-proj-new-thread="${escapeHtml(g.path)}" title="在此项目下新建对话">${icon("plus", 12)}</button>` +
              `<button class="proj-icon-btn" data-proj-explorer="${escapeHtml(g.path)}" title="在资源管理器中打开目录">${icon("folderOpen", 12)}</button>` +
              (orderedGroups.length > 1
                ? `<button class="proj-icon-btn" data-proj-remove="${escapeHtml(g.path)}" title="从侧栏移除此项目（本地文件保留）">${icon("close", 12)}</button>`
                : "") +
            `</div>` +
          `</div>` +
          threadsHtml +
        `</div>`
      );
    })
    .join("");

  listEl.querySelectorAll("[data-select-room]").forEach((el) => {
    el.onclick = () => selectRoom(el.dataset.selectRoom);
  });

  listEl.querySelectorAll("[data-proj-empty-new]").forEach((el) => {
    el.onclick = () => createThreadInProject(el.dataset.projEmptyNew).catch((err) => showToast("error", err.message));
  });

  listEl.querySelectorAll("[data-toggle-proj]").forEach((hdr) => {
    hdr.onclick = (ev) => {
      if (ev.target.closest(".proj-icon-btn")) return;
      const key = hdr.dataset.toggleProj;
      const grp = groupsMap.get(key);
      if (!grp) return;
      if (state.collapsedProjects.has(key)) {
        state.collapsedProjects.delete(key);
        if (grp.threads.length > 0 && activeProjKey !== key) {
          selectRoom(grp.threads[grp.threads.length - 1].id);
          return;
        }
        renderSessionList();
      } else if (activeProjKey !== key && grp.threads.length > 0) {
        selectRoom(grp.threads[grp.threads.length - 1].id);
      } else if (grp.threads.length === 0) {
        createThreadInProject(grp.path).catch((err) => showToast("error", err.message));
      } else {
        state.collapsedProjects.add(key);
        renderSessionList();
      }
    };
  });

  listEl.querySelectorAll("[data-proj-new-thread]").forEach((btn) => {
    btn.onclick = (ev) => {
      ev.stopPropagation();
      createThreadInProject(btn.dataset.projNewThread).catch((err) => showToast("error", err.message));
    };
  });

  listEl.querySelectorAll("[data-proj-explorer]").forEach((btn) => {
    btn.onclick = (ev) => {
      ev.stopPropagation();
      rpc("profile.openFolder", { targetPath: btn.dataset.projExplorer }).catch((err) => showToast("error", err.message));
    };
  });

  listEl.querySelectorAll("[data-proj-remove]").forEach((btn) => {
    btn.onclick = (ev) => {
      ev.stopPropagation();
      const targetPath = btn.dataset.projRemove;
      const projName = getProjectNameFromPath(targetPath);
      const en = getUiLanguage() === "en";
      openConfirmModal({
        title: en ? "Remove Project from Sidebar" : "从侧栏移除项目",
        messageHtml: en
          ? `Remove <strong>${escapeHtml(projName)}</strong> from the sidebar list?<br/><span style="color:var(--text-muted);font-size:11.5px">All files in <code>${escapeHtml(targetPath)}</code> on disk will remain untouched.</span>`
          : `确定将项目 <strong>${escapeHtml(projName)}</strong> 从侧栏列表中移除吗？<br/><span style="color:var(--text-muted);font-size:11.5px">本地磁盘文件夹 <code>${escapeHtml(targetPath)}</code> 中的所有代码与文件将完好保留。</span>`,
        confirmText: en ? "Remove from List" : "移除列表项",
        danger: false,
        onConfirm: async () => {
          const snap = await rpc("project.remove", { directory: targetPath, removeThreads: true });
          if (snap) applySnapshot(snap, false);
        },
      });
    };
  });
}

function closeRosterPopover() {
  state.rosterPopoverOpen = false;
  document.getElementById("roster-popover")?.remove();
}

function openRoomRosterPopover(room, anchorEl) {
  if (state.rosterPopoverOpen) {
    closeRosterPopover();
    return;
  }
  closeRosterPopover();
  closeTopbarPoolPopover();
  state.rosterPopoverOpen = true;

  const poolMap = getPoolEntryMap();
  const chatWorkspaces = (state.data?.workspaces || []).filter((w) => w.isLaunchable);
  const rect = anchorEl.getBoundingClientRect();

  const pop = document.createElement("div");
  pop.id = "roster-popover";
  pop.className = "roster-popover";
  pop.style.top = `${rect.bottom + 6}px`;
  pop.style.left = `${Math.max(12, Math.min(window.innerWidth - 404, rect.left))}px`;

  const rowsHtml = (room.agents || [])
    .map((a) => {
      const plat = formatPlatformShort(a.client);
      const pe = poolMap.get(a.workspaceId);
      const remTag = a.client === "Codex" && pe?.primaryRemainingPercent != null ? ` (${Math.round(pe.primaryRemainingPercent)}%)` : "";
      const opts = chatWorkspaces
        .map((w) => `<option value="${escapeHtml(w.id)}" ${w.id === a.workspaceId ? "selected" : ""}>${escapeHtml(w.displayName)} (${w.client})</option>`)
        .join("");
      return `
        <div class="roster-popover-row" style="grid-template-columns:auto 1fr auto">
          <div style="display:flex;align-items:center;gap:5px">
            <span class="plat-badge ${plat.cls}">${plat.code}</span>
            <span class="agent-chip-mention" data-rp-at="${escapeHtml(a.alias)}" title="点击插入 @${escapeHtml(a.alias)}">@${escapeHtml(a.alias)}${remTag}</span>
          </div>
          <select class="select" style="padding:3px 6px;font-size:11.5px" data-rp-switch="${escapeHtml(a.id)}" ${room.isRunning ? "disabled" : ""}>${opts}</select>
          <button class="btn btn-ghost btn-sm" style="padding:2px 6px;color:var(--text-muted)" data-rp-remove="${escapeHtml(a.id)}" title="从当前会话移除 @${escapeHtml(a.alias)}" ${room.isRunning ? "disabled" : ""}>✕</button>
        </div>`;
    })
    .join("");

  pop.innerHTML = `
    <div style="display:flex;align-items:center;justify-content:space-between">
      <strong style="font-size:12.5px">本会话参与智能体 (${room.agents?.length || 0})</strong>
      <div style="display:flex;gap:4px">
        <button class="btn btn-ghost btn-sm" id="rp-sync" ${room.isRunning ? "disabled" : ""}>↻ 同步全员</button>
        <button class="btn btn-ghost btn-sm" id="rp-add">＋ 新接入</button>
      </div>
    </div>
    <div class="roster-popover-list">${rowsHtml}</div>
    <div style="display:flex;justify-content:space-between;align-items:center;padding-top:4px;border-top:1px solid var(--border-subtle)">
      <span class="mono" style="font-size:10.5px;color:var(--text-muted);max-width:230px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="${escapeHtml(room.workingDirectory)}">📁 ${escapeHtml(room.workingDirectory)}</span>
      <button class="btn btn-sm" id="rp-settings" ${room.isRunning ? "disabled" : ""}>⚙ 详细编排</button>
    </div>
  `;

  document.body.appendChild(pop);

  pop.querySelectorAll("[data-rp-at]").forEach((el) => {
    el.onclick = () => {
      closeRosterPopover();
      insertMentionIntoComposer(el.dataset.rpAt);
    };
  });
  pop.querySelectorAll("[data-rp-switch]").forEach((sel) => {
    sel.onchange = () => {
      rpc("room.switchAgentWorkspace", {
        roomId: room.id,
        agentId: sel.dataset.rpSwitch,
        workspaceId: sel.value,
      }).catch((err) => showToast("error", err.message));
    };
  });
  pop.querySelectorAll("[data-rp-remove]").forEach((btn) => {
    btn.onclick = async () => {
      const nextAgents = (room.agents || []).filter((x) => x.id !== btn.dataset.rpRemove);
      try {
        await rpc("room.configure", {
          roomId: room.id,
          title: room.title,
          directory: room.workingDirectory,
          sandboxMode: room.sandboxMode,
          agents: nextAgents,
        });
        closeRosterPopover();
        showToast("success", "✓ 已从当前会话移除该 @ 身份");
      } catch (err) {
        showToast("error", err.message);
      }
    };
  });
  document.getElementById("rp-sync").onclick = async () => {
    const snap = await rpc("room.syncRoster");
    if (snap) applySnapshot(snap, false);
    closeRosterPopover();
  };
  document.getElementById("rp-add").onclick = () => {
    closeRosterPopover();
    openAddProfileModal("api");
  };
  document.getElementById("rp-settings").onclick = () => {
    closeRosterPopover();
    openRoomSettingsModal(room);
  };
}

function renderRoomHeader() {
  const header = document.getElementById("room-header");
  const room = getCurrentRoom();
  if (!room) {
    header.innerHTML = `<div style="color:var(--text-muted)">请点击左侧「＋ 新建项目」或「📂 打开」开始工作</div>`;
    return;
  }

  const projPath = getRoomProjectPath(room);
  const projName = getProjectNameFromPath(projPath);
  const wtBadge = room.isIsolatedWorktree
    ? `<span class="tag worktree" style="max-width:155px" title="独立工作树目录：${escapeHtml(room.workingDirectory)}">${icon("branch", 11)} <span>${escapeHtml(room.worktreeBranch)}</span></span>`
    : "";

  const wtActionsHtml = room.isIsolatedWorktree
    ? `<button class="btn btn-success btn-sm" id="btn-room-merge" ${room.isRunning ? "disabled" : ""}>${icon("merge", 12)} <span>合并回主库</span></button>`
    : `<button class="btn btn-sm" id="btn-room-spawn-wt" ${room.isRunning ? "disabled" : ""} title="为此对话派生独立 Git Worktree 分支">${icon("branch", 12)} <span>独立分支</span></button>`;

  header.innerHTML = `
    <div class="room-header-main">
      <div class="room-title-group">
        <span class="room-crumb-proj" id="btn-open-cwd" title="点击在资源管理器中打开目录：${escapeHtml(room.workingDirectory)}">${icon("folder", 13)} <span>${escapeHtml(projName)}</span></span>
        <span style="color:var(--text-muted);font-size:12px;flex-shrink:0">/</span>
        <span class="room-title" style="min-width:64px" title="${escapeHtml(room.title)}">${escapeHtml(room.title)}</span>
        ${wtBadge}
        <button class="btn btn-ghost btn-sm" id="btn-switch-room-dir" style="padding:3px 6px" ${room.isRunning ? "disabled" : ""} title="切换当前对话绑定的项目文件夹">${icon("folderOpen", 12)}</button>
      </div>
      <div class="room-toolbar">
        <button class="btn btn-sm" id="btn-new-thread-here" title="在当前项目下再开一个新对话">${icon("plus", 12)} <span>新对话</span></button>
        ${wtActionsHtml}
        <button class="btn btn-sm ${state.terminalOpen ? "btn-primary" : ""}" id="btn-toggle-term">${icon("terminal", 12)} <span>终端</span></button>
        <button class="btn btn-sm ${state.diffOpen ? "btn-primary" : ""}" id="btn-toggle-diff">${icon("diff", 12)} <span>Diff</span></button>
        <button class="btn btn-ghost btn-sm" id="btn-roster-popover" title="查看或编排本对话可协作智能体名单">${icon("users", 13)} <span>${room.agents?.length || 0}</span> ▾</button>
        <button class="btn btn-ghost btn-sm" id="btn-room-settings" ${room.isRunning ? "disabled" : ""} title="对话标题与智能体编排设置">${icon("settings", 13)}</button>
        ${getSharedRooms().length > 1 ? `<button class="btn btn-ghost btn-sm" id="btn-room-delete" ${room.isRunning ? "disabled" : ""} title="删除此对话">${icon("trash", 13)}</button>` : ""}
      </div>
    </div>
  `;

  document.getElementById("btn-new-thread-here").onclick = () =>
    createThreadInProject(projPath).catch((err) => showToast("error", err.message));

  const switchDirBtn = document.getElementById("btn-switch-room-dir");
  if (switchDirBtn) {
    switchDirBtn.onclick = async () => {
      try {
        const picked = await rpc("dialog.pickFolder", {
          initialDirectory: room.workingDirectory,
          title: "选择当前对话要操作的项目文件夹",
        });
        if (!picked?.path) return;
        await rpc("room.configure", {
          roomId: room.id,
          title: room.title,
          directory: picked.path,
          sandboxMode: room.sandboxMode,
          agents: room.agents || [],
        });
        showToast("success", `✓ 已切换至项目目录：${picked.path}`);
      } catch (err) {
        showToast("error", err.message);
      }
    };
  }

  const rosterBtn = document.getElementById("btn-roster-popover");
  rosterBtn.onclick = (ev) => {
    ev.stopPropagation();
    openRoomRosterPopover(room, rosterBtn);
  };

  const spawnBtn = document.getElementById("btn-room-spawn-wt");
  if (spawnBtn) {
    spawnBtn.onclick = () =>
      runButtonAction(spawnBtn, { busyText: "创建分支…", doneText: "✓ 已派生" }, () =>
        rpc("room.spawnWorktree", { roomId: room.id, labelHint: room.title })
      );
  }

  const mergeBtn = document.getElementById("btn-room-merge");
  if (mergeBtn) {
    mergeBtn.onclick = () =>
      runButtonAction(mergeBtn, { busyText: "正在合并…", doneText: "✓ 已合入" }, () =>
        rpc("room.mergeWorktree", { roomId: room.id })
      );
  }

  document.getElementById("btn-toggle-term").onclick = () => {
    state.terminalOpen = !state.terminalOpen;
    const primaryWsId = getCurrentPrimaryWsId(room);
    if (primaryWsId) state.terminalAccountId = primaryWsId;
    renderRoomHeader();
    renderTerminalDock();
    if (state.terminalOpen && (state.data?.terminals?.length || 0) === 0) {
      spawnTerminal("shell", primaryWsId || null, room.workingDirectory);
    }
  };
  document.getElementById("btn-toggle-diff").onclick = () => {
    state.diffOpen = !state.diffOpen;
    renderRoomHeader();
    renderDiffDrawer();
  };
  document.getElementById("btn-room-settings").onclick = () => openRoomSettingsModal(room);
  document.getElementById("btn-open-cwd").onclick = () =>
    rpc("profile.openFolder", { targetPath: room.workingDirectory }).catch((err) => showToast("error", err.message));

  const delBtn = document.getElementById("btn-room-delete");
  if (delBtn) {
    delBtn.onclick = () => {
      const en = getUiLanguage() === "en";
      openConfirmModal({
        title: en ? "Delete Chat Thread" : "删除项目对话",
        messageHtml: en
          ? `Delete thread <strong>${escapeHtml(room.title)}</strong>?${room.isIsolatedWorktree ? "<br/><span style='color:var(--text-muted);font-size:11.5px'>Its isolated Git Worktree folder will also be cleaned up.</span>" : ""}`
          : `确定删除对话 <strong>${escapeHtml(room.title)}</strong> 吗？${room.isIsolatedWorktree ? "<br/><span style='color:var(--text-muted);font-size:11.5px'>关联的独立 Git Worktree 目录也将一并清理。</span>" : ""}`,
        confirmText: en ? "🗑 Delete" : "🗑 确认删除",
        danger: true,
        onConfirm: async () => {
          const snap = await rpc("room.delete", { roomId: room.id, removeWorktree: room.isIsolatedWorktree });
          if (snap) applySnapshot(snap, false);
        },
      });
    };
  }

  document.getElementById("btn-stop-room").style.display = room.isRunning ? "inline-flex" : "none";
  document.getElementById("btn-send-msg").disabled = !!room.isRunning;
}

async function ensureWorkspaceModelCatalog(workspaceId, { forceRefresh = false, wakeDaemon = false, showToastOnDone = false } = {}) {
  if (!workspaceId) return null;
  if (!forceRefresh && state.modelCatalogs.has(workspaceId)) {
    return state.modelCatalogs.get(workspaceId);
  }
  if (state.loadingCatalogs.has(workspaceId) && !forceRefresh) return null;

  state.loadingCatalogs.add(workspaceId);
  renderComposerFooterControls();
  try {
    const cat = await rpc("models.list", { workspaceId, forceRefresh, wakeDaemon });
    if (cat) {
      state.modelCatalogs.set(workspaceId, cat);
      if (showToastOnDone) {
        const count = cat.models?.length || 0;
        showToast("success", getUiLanguage() === "en" ? `✓ Synced ${count} models` : `✓ 已实时同步 ${count} 个可用模型`);
      }
    }
    return cat;
  } catch (err) {
    if (showToastOnDone) showToast("error", err.message);
    return null;
  } finally {
    state.loadingCatalogs.delete(workspaceId);
    renderComposerFooterControls();
    if (state.composerPicker.kind) {
      refreshOpenComposerPickerItems();
    }
  }
}

function closeComposerPickerPopover() {
  if (!state.composerPicker.kind) return;
  state.composerPicker.kind = null;
  state.composerPicker.query = "";
  document.getElementById("composer-picker-popover")?.remove();
  document.querySelectorAll(".composer-picker-btn.is-open").forEach((b) => b.classList.remove("is-open"));
}

function resolveComposerModelContext(room) {
  const launchable = (state.data?.workspaces || []).filter((w) => w.isLaunchable);
  const poolMap = getPoolEntryMap();
  const primaryWsId = getCurrentPrimaryWsId(room);
  const primaryInfo = getPrimaryWorkspaceInfo(room);
  const agents = room.agents || [];
  const primaryAgent = agents.find((a) => a.workspaceId === primaryWsId) || agents[0] || null;
  const catalog = primaryWsId ? state.modelCatalogs.get(primaryWsId) || null : null;
  const isCatalogLoading = primaryWsId ? state.loadingCatalogs.has(primaryWsId) : false;

  const catModels = catalog?.models || [];
  let matchedEntry = null;
  let inferredEffortFromModel = null;
  const rawSavedModel = primaryAgent?.model || "";
  const isStaleAgyLegacyModel =
    catalog?.providerKind === "antigravity" &&
    (rawSavedModel.toLowerCase() === "gemini-2.5-pro" ||
      (rawSavedModel.toLowerCase() === "pro" && !catModels.some((m) => m.id.toLowerCase() === "pro")));
  const rawAgentModel = isStaleAgyLegacyModel ? "" : rawSavedModel;

  if (rawAgentModel && catModels.length > 0) {
    matchedEntry = catModels.find((m) => m.id.toLowerCase() === rawAgentModel.toLowerCase());
    if (!matchedEntry) {
      for (const m of catModels) {
        const hitEff = (m.reasoningEfforts || []).find(
          (r) => r.resolvedModelId && r.resolvedModelId.toLowerCase() === rawAgentModel.toLowerCase()
        );
        if (hitEff) {
          matchedEntry = m;
          inferredEffortFromModel = hitEff.id;
          break;
        }
      }
    }
  }
  if (!matchedEntry && !rawAgentModel && catModels.length > 0) {
    matchedEntry =
      catModels.find((m) => m.id === catalog?.defaultModelId) ||
      catModels.find((m) => m.isDefault) ||
      catModels[0];
  }

  const effectiveModelId = matchedEntry ? matchedEntry.id : (rawAgentModel || catalog?.defaultModelId || "");
  const efforts = matchedEntry?.reasoningEfforts || [];
  const effectiveEffort =
    primaryAgent?.reasoningEffort ||
    inferredEffortFromModel ||
    matchedEntry?.defaultEffort ||
    catalog?.defaultEffort ||
    "";
  const speedTiers = matchedEntry?.speedTiers || [];
  const effectiveTier = primaryAgent?.serviceTier || catalog?.defaultSpeedTier || "";

  return {
    launchable,
    poolMap,
    primaryWsId,
    primaryInfo,
    agents,
    primaryAgent,
    catalog,
    isCatalogLoading,
    catModels,
    matchedEntry,
    rawAgentModel,
    effectiveModelId,
    efforts,
    effectiveEffort,
    speedTiers,
    effectiveTier,
  };
}

async function applyComposerAgentOptions(room, primaryAgent, primaryWsId, nextModel, nextEffort, nextTier) {
  try {
    const updated = await rpc("room.setAgentModelOptions", {
      roomId: room.id,
      agentId: primaryAgent?.id || null,
      workspaceId: primaryWsId,
      model: nextModel || null,
      reasoningEffort: nextEffort || null,
      serviceTier: nextTier || null,
    });
    if (updated?.agents) room.agents = updated.agents;
    state.lastComposerFooterSig = "";
    renderComposerFooterControls();
  } catch (err) {
    showToast("error", err.message);
  }
}

function openComposerPickerPopover(kind, anchorEl) {
  if (state.composerPicker.kind === kind) {
    closeComposerPickerPopover();
    return;
  }
  closeComposerPickerPopover();
  closeAccountPopover();
  closeRosterPopover();
  closeTopbarPoolPopover();

  const rect = anchorEl.getBoundingClientRect();
  const width = kind === "effort" || kind === "speed" ? 236 : 320;
  const left = Math.max(12, Math.min(window.innerWidth - width - 12, rect.left));
  const bottom = Math.max(12, window.innerHeight - rect.top + 6);

  state.composerPicker = { kind, query: "", left, bottom, width };
  document.querySelectorAll(".composer-picker-btn.is-open").forEach((b) => b.classList.remove("is-open"));
  anchorEl.classList.add("is-open");

  const pop = document.createElement("div");
  pop.id = "composer-picker-popover";
  pop.className = "composer-picker-popover";
  pop.style.left = `${left}px`;
  pop.style.bottom = `${bottom}px`;
  pop.style.width = `${width}px`;

  if (kind === "model") {
    pop.innerHTML =
      `<div class="cpp-head">` +
        `<input id="cpp-model-search" class="cpp-search" placeholder="搜索具体模型名称或标识…" />` +
        `<button type="button" class="btn btn-ghost btn-sm" id="cpp-refresh-models" style="padding:2px 7px" title="从官方守护进程或远端 API 实时拉取最新模型列表">↻ 刷新</button>` +
      `</div>` +
      `<div class="cpp-list" id="cpp-items-list"></div>` +
      `<div class="cpp-foot" id="cpp-foot-bar"></div>`;
  } else {
    pop.innerHTML =
      `<div class="cpp-list" id="cpp-items-list"></div>` +
      `<div class="cpp-foot" id="cpp-foot-bar" style="display:none"></div>`;
  }

  document.body.appendChild(pop);
  refreshOpenComposerPickerItems();

  const searchInp = document.getElementById("cpp-model-search");
  if (searchInp) {
    searchInp.oninput = () => {
      state.composerPicker.query = searchInp.value.trim().toLowerCase();
      refreshOpenComposerPickerItems();
    };
    setTimeout(() => searchInp.focus(), 20);
  }

  const refreshBtn = document.getElementById("cpp-refresh-models");
  if (refreshBtn) {
    refreshBtn.onclick = (ev) => {
      ev.stopPropagation();
      const room = getCurrentRoom();
      if (!room) return;
      const primaryWsId = getCurrentPrimaryWsId(room);
      ensureWorkspaceModelCatalog(primaryWsId, { forceRefresh: true, wakeDaemon: true, showToastOnDone: true });
    };
  }
}

function refreshOpenComposerPickerItems() {
  const pop = document.getElementById("composer-picker-popover");
  const listEl = document.getElementById("cpp-items-list");
  const footEl = document.getElementById("cpp-foot-bar");
  const room = getCurrentRoom();
  if (!pop || !listEl || !room || !state.composerPicker.kind) return;

  const ctx = resolveComposerModelContext(room);
  const kind = state.composerPicker.kind;
  const q = state.composerPicker.query || "";

  if (kind === "primary") {
    if (ctx.launchable.length === 0) {
      listEl.innerHTML = `<div style="padding:12px;font-size:12px;color:var(--text-muted);text-align:center">尚未接入可用账号</div>`;
    } else {
      listEl.innerHTML = ctx.launchable
        .map((w) => {
          const pe = ctx.poolMap.get(w.id);
          const plat = formatPlatformShort(pe?.client || w.client, pe?.plan);
          const selected = w.id === ctx.primaryWsId;
          const remTxt =
            pe?.primaryRemainingPercent != null
              ? `剩余 ${Math.round(pe.primaryRemainingPercent)}%`
              : pe?.authReady === false
              ? "待登录"
              : "就绪";
          return (
            `<div class="cpp-item ${selected ? "is-selected" : ""}" data-cpp-ws="${escapeHtml(w.id)}">` +
              `<div class="cpp-item-main">` +
                `<div class="cpp-item-title"><span class="plat-badge ${plat.cls}">${plat.code}</span><span>${escapeHtml(w.displayName)}</span></div>` +
                `<div class="cpp-item-sub">${escapeHtml(remTxt)}</div>` +
              `</div>` +
              `<span style="font-weight:800;font-size:12px">${selected ? "✓" : ""}</span>` +
            `</div>`
          );
        })
        .join("");
    }
    footEl.style.display = "flex";
    footEl.innerHTML = `<button type="button" class="btn btn-ghost btn-sm" id="cpp-add-account" style="width:100%">＋ 接入新账号 / API / CLI</button>`;
    document.getElementById("cpp-add-account").onclick = () => {
      closeComposerPickerPopover();
      openAddProfileModal("api");
    };
    listEl.querySelectorAll("[data-cpp-ws]").forEach((row) => {
      row.onclick = async () => {
        const nextWsId = row.dataset.cppWs;
        closeComposerPickerPopover();
        if (!nextWsId || nextWsId === ctx.primaryWsId) return;
        try {
          const updated = await rpc("room.setPrimaryWorkspace", { roomId: room.id, workspaceId: nextWsId });
          room.primaryWorkspaceId = updated?.primaryWorkspaceId || nextWsId;
          if (updated?.agents) room.agents = updated.agents;
          state.lastComposerFooterSig = "";
          ensureWorkspaceModelCatalog(nextWsId);
          renderComposerFooterControls();
          renderChatStream(false);
          const pickedWs = ctx.launchable.find((w) => w.id === nextWsId);
          showToast("success", `✓ 默认主力已切换为「${pickedWs?.displayName || nextWsId}」`);
        } catch (err) {
          showToast("error", err.message);
        }
      };
    });
    applyLanguageToRoot(pop);
    return;
  }

  if (kind === "model") {
    const filtered = ctx.catModels.filter(
      (m) =>
        !q ||
        m.id.toLowerCase().includes(q) ||
        (m.displayName || "").toLowerCase().includes(q) ||
        (m.group || "").toLowerCase().includes(q) ||
        (m.description || "").toLowerCase().includes(q)
    );

    let html = "";
    if (ctx.rawAgentModel && !ctx.matchedEntry && (!q || ctx.rawAgentModel.toLowerCase().includes(q))) {
      html +=
        `<div class="cpp-item is-selected" data-cpp-model="${escapeHtml(ctx.rawAgentModel)}">` +
          `<div class="cpp-item-main"><div class="cpp-item-title"><span>${escapeHtml(ctx.rawAgentModel)}</span></div></div>` +
          `<span style="font-weight:800">✓</span>` +
        `</div>`;
    }

    if (filtered.length === 0) {
      html += `<div style="padding:14px 8px;text-align:center;font-size:11.5px;color:var(--text-muted)">${ctx.isCatalogLoading ? "正在读取模型…" : "无匹配模型，可点下方输入自定义 ID"}</div>`;
    } else {
      const grpMap = new Map();
      for (const m of filtered) {
        const g = m.group || "Models";
        if (!grpMap.has(g)) grpMap.set(g, []);
        grpMap.get(g).push(m);
      }
      for (const [grpName, items] of grpMap.entries()) {
        if (grpMap.size > 1) {
          html += `<div class="cpp-group-title">${escapeHtml(grpName)}</div>`;
        }
        for (const m of items) {
          const selected = m.id === ctx.effectiveModelId;
          const badgeHtml = m.badge ? `<span class="tag write" style="padding:0 5px;font-size:9.5px">${escapeHtml(m.badge)}</span>` : "";
          const quotaTxt = m.remainingQuotaPercent != null ? `${Math.round(m.remainingQuotaPercent)}%` : "";
          const subTxt = [m.description, quotaTxt ? `配额剩余 ${quotaTxt}` : ""].filter(Boolean).join(" · ");
          html +=
            `<div class="cpp-item ${selected ? "is-selected" : ""}" data-cpp-model="${escapeHtml(m.id)}">` +
              `<div class="cpp-item-main">` +
                `<div class="cpp-item-title"><span>${escapeHtml(m.displayName)}</span>${badgeHtml}</div>` +
                (subTxt ? `<div class="cpp-item-sub">${escapeHtml(subTxt)}</div>` : "") +
              `</div>` +
              `<span style="font-weight:800;font-size:12px;flex-shrink:0">${selected ? "✓" : ""}</span>` +
            `</div>`;
        }
      }
    }
    listEl.innerHTML = html;

    if (ctx.catalog?.supportsCustomModelInput !== false) {
      footEl.style.display = "flex";
      footEl.innerHTML = `<button type="button" class="btn btn-ghost btn-sm" id="cpp-custom-model-btn" style="width:100%">✎ 自定义模型 ID…</button>`;
      document.getElementById("cpp-custom-model-btn").onclick = () => {
        closeComposerPickerPopover();
        openCustomModelPromptModal(room, ctx.primaryAgent, ctx.primaryWsId, ctx.rawAgentModel || ctx.effectiveModelId);
      };
    } else {
      footEl.style.display = "none";
    }

    listEl.querySelectorAll("[data-cpp-model]").forEach((row) => {
      row.onclick = () => {
        const chosen = row.dataset.cppModel;
        closeComposerPickerPopover();
        const nextEntry = ctx.catModels.find((m) => m.id === chosen);
        const validEfforts = nextEntry?.reasoningEfforts || [];
        let nextEffort = ctx.effectiveEffort;
        if (validEfforts.length === 0) {
          nextEffort = null;
        } else if (!validEfforts.some((e) => e.id === nextEffort)) {
          nextEffort = nextEntry?.defaultEffort || validEfforts[0].id;
        }
        const validSpeeds = nextEntry?.speedTiers || [];
        const nextTier = validSpeeds.some((s) => s.id === ctx.effectiveTier) ? ctx.effectiveTier : null;
        applyComposerAgentOptions(room, ctx.primaryAgent, ctx.primaryWsId, chosen, nextEffort, nextTier);
      };
    });
    applyLanguageToRoot(pop);
    return;
  }

  if (kind === "effort") {
    listEl.innerHTML = ctx.efforts
      .map((eff) => {
        const selected = eff.id.toLowerCase() === String(ctx.effectiveEffort || "").toLowerCase();
        return (
          `<div class="cpp-item ${selected ? "is-selected" : ""}" data-cpp-effort="${escapeHtml(eff.id)}">` +
            `<div class="cpp-item-main">` +
              `<div class="cpp-item-title"><span>${escapeHtml(formatEffortLocalized(eff.id))}</span></div>` +
              (eff.description ? `<div class="cpp-item-sub">${escapeHtml(eff.description)}</div>` : "") +
            `</div>` +
            `<span style="font-weight:800;font-size:12px">${selected ? "✓" : ""}</span>` +
          `</div>`
        );
      })
      .join("");
    listEl.querySelectorAll("[data-cpp-effort]").forEach((row) => {
      row.onclick = () => {
        const nextEff = row.dataset.cppEffort;
        closeComposerPickerPopover();
        applyComposerAgentOptions(room, ctx.primaryAgent, ctx.primaryWsId, ctx.effectiveModelId, nextEff, ctx.effectiveTier);
      };
    });
    applyLanguageToRoot(pop);
    return;
  }

  if (kind === "speed") {
    const allSpeedItems = [
      { id: "", label: "标准速度", desc: "默认标准响应速度" },
      ...ctx.speedTiers.map((t) => ({
        id: t.id,
        label: t.id === "ultrafast" ? "⚡⚡ 超极速" : "⚡ 极速",
        desc: t.description || "",
      })),
    ];
    listEl.innerHTML = allSpeedItems
      .map((item) => {
        const curNorm = !ctx.effectiveTier || ctx.effectiveTier === "default" ? "" : ctx.effectiveTier;
        const selected = item.id === curNorm;
        return (
          `<div class="cpp-item ${selected ? "is-selected" : ""}" data-cpp-speed="${escapeHtml(item.id)}">` +
            `<div class="cpp-item-main">` +
              `<div class="cpp-item-title"><span>${escapeHtml(item.label)}</span></div>` +
              (item.desc ? `<div class="cpp-item-sub">${escapeHtml(item.desc)}</div>` : "") +
            `</div>` +
            `<span style="font-weight:800;font-size:12px">${selected ? "✓" : ""}</span>` +
          `</div>`
        );
      })
      .join("");
    listEl.querySelectorAll("[data-cpp-speed]").forEach((row) => {
      row.onclick = () => {
        const nextSpd = row.dataset.cppSpeed || null;
        closeComposerPickerPopover();
        applyComposerAgentOptions(room, ctx.primaryAgent, ctx.primaryWsId, ctx.effectiveModelId, ctx.effectiveEffort, nextSpd);
      };
    });
    applyLanguageToRoot(pop);
  }
}

function formatEffortLocalized(effId) {
  const id = String(effId || "").toLowerCase();
  const en = getUiLanguage() === "en";
  switch (id) {
    case "none": return en ? "None" : "关闭 None";
    case "minimal": return en ? "Minimal" : "极简 Minimal";
    case "low": return en ? "Low" : "低 Low";
    case "medium": return en ? "Medium" : "中 Medium";
    case "high": return en ? "High" : "高 High";
    case "xhigh": return en ? "XHigh" : "超高 XHigh";
    case "max": return en ? "Max" : "极限 Max";
    case "ultra": return en ? "Ultra" : "超旗舰 Ultra";
    default: return effId || "";
  }
}

function openCustomModelPromptModal(room, agent, workspaceId, currentModel) {
  const en = getUiLanguage() === "en";
  showModal(
    en ? "Custom Model ID" : "输入自定义模型标识 (Model ID)",
    `<div>
       <div style="font-size:12px;color:var(--text-secondary);margin-bottom:8px">
         ${en
           ? "Enter any official or gateway model slug (no spaces), e.g. <code>gpt-6.2-astra</code>, <code>claude-opus-5-5</code>, <code>gemini-4-neon-high</code>, <code>deepseek-reasoner</code>."
           : "可直接填写任何官方或聚合网关发布的最新模型标识（无空格），例如 <code>gpt-6.2-astra</code>、<code>claude-opus-5-5</code>、<code>gemini-4-neon-high</code>、<code>deepseek-reasoner</code>。"}
       </div>
       <input id="mc-custom-model-input" class="input mono" value="${escapeHtml(currentModel || "")}" placeholder="e.g. gpt-6.2-astra" />
     </div>`,
    `<button class="btn" id="mc-custom-cancel">取消</button>
     <button class="btn btn-primary" id="mc-custom-save">确认使用</button>`
  );
  const inp = document.getElementById("mc-custom-model-input");
  setTimeout(() => inp?.focus(), 40);
  document.getElementById("mc-custom-cancel").onclick = closeModal;
  const saveBtn = document.getElementById("mc-custom-save");
  saveBtn.onclick = () =>
    runButtonAction(saveBtn, { busyText: "保存…", doneText: "✓" }, async () => {
      const val = (inp?.value || "").trim();
      const updatedRoom = await rpc("room.setAgentModelOptions", {
        roomId: room.id,
        agentId: agent?.id || null,
        workspaceId,
        model: val || null,
        reasoningEffort: agent?.reasoningEffort || null,
        serviceTier: agent?.serviceTier || null,
      });
      if (updatedRoom?.agents) room.agents = updatedRoom.agents;
      closeModal();
      renderComposerFooterControls();
    });
}

function renderComposerFooterControls() {
  const room = getCurrentRoom();
  const modeBox = document.getElementById("composer-mode-switch");
  const mentionsBox = document.getElementById("composer-quick-mentions");
  const composer = document.getElementById("composer-input");
  if (!room) {
    modeBox.innerHTML = "";
    mentionsBox.innerHTML = "";
    state.lastComposerFooterSig = "";
    return;
  }

  const ctx = resolveComposerModelContext(room);
  const projName = getProjectNameFromPath(getRoomProjectPath(room));

  if (ctx.primaryWsId && !state.modelCatalogs.has(ctx.primaryWsId) && !state.loadingCatalogs.has(ctx.primaryWsId)) {
    ensureWorkspaceModelCatalog(ctx.primaryWsId);
  }

  if (composer) {
    const nextPh = `向 ${ctx.primaryInfo.name} 发送消息，输入 @ 多智能体协作…`;
    if (composer.placeholder !== nextPh && composer.__origPh !== nextPh) {
      composer.placeholder = nextPh;
    }
  }

  const sig = JSON.stringify({
    roomId: room.id,
    isRunning: !!room.isRunning,
    mode: room.sandboxMode,
    wsCount: ctx.launchable.length,
    wsIds: ctx.launchable.map((w) => w.id + ":" + (ctx.poolMap.get(w.id)?.authReady ?? "")).join(","),
    primaryWsId: ctx.primaryWsId,
    agentCount: ctx.agents.length,
    aliases: ctx.agents.map((a) => a.alias).join(","),
    modelId: ctx.effectiveModelId,
    modelCount: ctx.catModels.length,
    loadingCat: ctx.isCatalogLoading,
    effort: ctx.effectiveEffort,
    effortCount: ctx.efforts.length,
    tier: ctx.effectiveTier,
    speedCount: ctx.speedTiers.length,
    lang: getUiLanguage(),
  });

  if (state.lastComposerFooterSig === sig && document.getElementById("composer-primary-select")) {
    return;
  }
  state.lastComposerFooterSig = sig;

  const isWrite = room.sandboxMode === "WorkspaceWrite";
  modeBox.innerHTML =
    `<button class="mode-seg-btn write ${isWrite ? "active" : ""}" data-set-mode="WorkspaceWrite" title="全功能 Agent 模式：允许直接在当前项目目录读写文件与执行验证命令">${icon("wrench", 11)} <span>Agent</span></button>` +
    `<button class="mode-seg-btn readonly ${!isWrite ? "active" : ""}" data-set-mode="ReadOnly" title="只读规划模式：仅读取分析当前项目，不改动文件">${icon("search", 11)} <span>Ask</span></button>`;

  modeBox.querySelectorAll("[data-set-mode]").forEach((btn) => {
    btn.onclick = () => {
      if (room.sandboxMode === btn.dataset.setMode) return;
      rpc("room.setSandboxMode", { roomId: room.id, sandboxMode: btn.dataset.setMode }).catch((err) =>
        showToast("error", err.message)
      );
    };
  });

  const activeWs = ctx.launchable.find((w) => w.id === ctx.primaryWsId);
  const activePe = activeWs ? ctx.poolMap.get(activeWs.id) : null;
  const activePlat = activeWs ? formatPlatformShort(activePe?.client || activeWs.client, activePe?.plan) : { code: "AI", cls: "codex" };
  const primaryName = activeWs ? activeWs.displayName : "未接入智能体";

  let modelControlsHtml = "";
  if (ctx.primaryWsId) {
    const modelDisplayLabel =
      ctx.matchedEntry?.displayName ||
      ctx.effectiveModelId ||
      (ctx.isCatalogLoading ? "正在读取模型…" : "默认模型");

    modelControlsHtml +=
      `<button type="button" class="composer-picker-btn ${state.composerPicker.kind === "model" ? "is-open" : ""}" id="composer-model-select" data-composer-picker="model" title="${escapeHtml(ctx.matchedEntry?.description || "点击选择具体模型（支持实时搜索与自定义模型 ID）")}" ${room.isRunning ? "disabled" : ""}>` +
        `<span class="composer-picker-val">${escapeHtml(modelDisplayLabel)}</span>` +
        `<span class="composer-picker-caret">▾</span>` +
      `</button>`;

    if (ctx.efforts.length > 0) {
      const rawEff = String(ctx.effectiveEffort || ctx.efforts[0].id || "");
      const effShort = rawEff ? rawEff.charAt(0).toUpperCase() + rawEff.slice(1) : "Default";
      modelControlsHtml +=
        `<button type="button" class="composer-picker-btn ${state.composerPicker.kind === "effort" ? "is-open" : ""}" id="composer-effort-select" data-composer-picker="effort" title="原生思考强度 / 推理深度控制" ${room.isRunning ? "disabled" : ""}>` +
          `<span class="composer-picker-val">${escapeHtml(effShort)}</span>` +
          `<span class="composer-picker-caret">▾</span>` +
        `</button>`;
    }

    if (ctx.speedTiers.length === 1) {
      const tierObj = ctx.speedTiers[0];
      const fastOn = ctx.effectiveTier && ctx.effectiveTier.toLowerCase() !== "default" && ctx.effectiveTier.toLowerCase() !== "standard";
      modelControlsHtml +=
        `<button type="button" class="composer-fast-btn ${fastOn ? "is-active" : ""}" id="btn-toggle-fast-tier" data-tier-id="${escapeHtml(tierObj.id)}" title="${escapeHtml(tierObj.description || "切换极速高优推理通道")}" ${room.isRunning ? "disabled" : ""}>` +
          `<span>⚡ Fast</span>` +
        `</button>`;
    } else if (ctx.speedTiers.length > 1) {
      const spdLabel =
        ctx.effectiveTier === "ultrafast"
          ? "⚡⚡ Ultrafast"
          : ctx.effectiveTier && ctx.effectiveTier !== "default"
          ? "⚡ Fast"
          : "Standard";
      modelControlsHtml +=
        `<button type="button" class="composer-picker-btn ${state.composerPicker.kind === "speed" ? "is-open" : ""}" id="composer-speed-select" data-composer-picker="speed" title="原生推理速度档位（Standard / Fast / Ultrafast）" ${room.isRunning ? "disabled" : ""}>` +
          `<span class="composer-picker-val">${escapeHtml(spdLabel)}</span>` +
          `<span class="composer-picker-caret">▾</span>` +
        `</button>`;
    }
  }

  mentionsBox.innerHTML =
    `<button type="button" class="composer-picker-btn ${state.composerPicker.kind === "primary" ? "is-open" : ""}" id="composer-primary-select" data-composer-picker="primary" title="默认主力智能体：不输入 @ 时，回车将直接发给此账号处理" ${room.isRunning || ctx.launchable.length === 0 ? "disabled" : ""}>` +
      `<span class="plat-badge ${activePlat.cls}">${activePlat.code}</span>` +
      `<span class="composer-picker-val">${escapeHtml(primaryName)}</span>` +
      `<span class="composer-picker-caret">▾</span>` +
    `</button>` +
    modelControlsHtml +
    `<button type="button" class="composer-picker-btn" id="btn-open-at-menu" title="输入 @ 点名单个或多个智能体协同工作">@ 协作</button>`;

  mentionsBox.querySelectorAll("[data-composer-picker]").forEach((btn) => {
    btn.onclick = (ev) => {
      ev.stopPropagation();
      openComposerPickerPopover(btn.dataset.composerPicker, btn);
    };
  });

  const fastToggleBtn = document.getElementById("btn-toggle-fast-tier");
  if (fastToggleBtn) {
    fastToggleBtn.onclick = () => {
      const targetTier = fastToggleBtn.dataset.tierId || "priority";
      const currentlyOn = ctx.effectiveTier && ctx.effectiveTier.toLowerCase() !== "default" && ctx.effectiveTier.toLowerCase() !== "standard";
      const nextTier = currentlyOn ? null : targetTier;
      applyComposerAgentOptions(room, ctx.primaryAgent, ctx.primaryWsId, ctx.effectiveModelId, ctx.effectiveEffort, nextTier);
    };
  }

  document.getElementById("btn-open-at-menu").onclick = (ev) => {
    ev.stopPropagation();
    if (!composer) return;
    if (state.mentionMenu.open) {
      state.mentionMenu.open = false;
      renderMentionPopup();
      return;
    }
    const pos = composer.selectionStart ?? composer.value.length;
    const prefix = pos > 0 && !/\s/.test(composer.value[pos - 1]) ? " @" : "@";
    composer.value = composer.value.slice(0, pos) + prefix + composer.value.slice(pos);
    composer.selectionStart = composer.selectionEnd = pos + prefix.length;
    composer.focus();
    updateMentionAutocomplete();
  };
}

const userToggledThinkMap = new Map();

function buildMessageCardHtml(m) {
  const isUser = m.role === "User";
  const bodyHtml = m.text
    ? `<div class="md-body">${renderMarkdown(m.text)}${m.state === "Running" ? `<span class="streaming-cursor"></span>` : ""}</div>`
    : "";

  if (isUser) {
    return (
      `<article class="msg-card user" data-msg-id="${escapeHtml(m.id)}">` +
        bodyHtml +
        `<div class="user-hover-actions">` +
          (m.timeText ? `<span>${escapeHtml(m.timeText)}</span>` : "") +
          (m.text ? `<button class="btn btn-ghost btn-sm" data-edit-msg="${encodeURIComponent(m.text || "")}" title="编辑并重新发送">${icon("edit", 11)} <span>编辑</span></button>` : "") +
          (m.text ? `<button class="btn btn-ghost btn-sm" data-copy-msg="${encodeURIComponent(m.text || "")}">${icon("copy", 11)} <span>复制</span></button>` : "") +
        `</div>` +
      `</article>`
    );
  }

  const wsObj = (state.data?.workspaces || []).find((w) => w.id === m.workspaceId);
  const plat = wsObj
    ? formatPlatformShort(wsObj.client)
    : {
        code: /^[a-zA-Z0-9]/.test(m.sender || "") ? (m.sender || "AI").slice(0, 2).toUpperCase() : (m.sender || "AI").slice(0, 1),
        cls: "codex",
      };

  const normId = (s) => String(s || "").replace(/[\s@_\-·]+/g, "").toLowerCase();
  const sameIdentity = !m.workspaceName || normId(m.sender) === normId(m.workspaceName);
  const authorLabel = sameIdentity ? (m.sender || "Agent") : `${m.sender} · ${m.workspaceName}`;

  const modelBadgeHtml = m.modelSummary
    ? `<span style="color:var(--text-muted);font-weight:400">·</span><span class="msg-model-badge">${escapeHtml(m.modelSummary)}</span>`
    : "";

  const isFailed = m.state === "Failed";
  const showStatePill = m.state && m.state !== "Completed";
  const stateCls =
    m.state === "Running" ? "running" :
    isFailed ? "danger" : "";
  const liveBadgeText =
    m.state === "Running" && m.activityStatus
      ? m.activityStatus
      : (m.stateText || m.state);

  const canRetry = isFailed || m.state === "Cancelled" || m.state === "Interrupted";
  const failoverHtml = m.failoverNotice
    ? `<div class="failover-banner">${icon("zap", 12)} <span>${escapeHtml(m.failoverNotice)}</span></div>`
    : "";

  const { timelineHtml, changedPillHtml } = renderAgentTimelineBlock(m, userToggledThinkMap);

  return (
    `<article class="msg-card agent ${isFailed ? "is-failed" : ""}" data-msg-id="${escapeHtml(m.id)}">` +
      `<div class="msg-head">` +
        `<div class="msg-author">` +
          `<span class="plat-badge ${plat.cls}" style="padding:2px 5px;border-radius:4px">${escapeHtml(plat.code)}</span>` +
          `<span>${escapeHtml(authorLabel)}</span>` +
          modelBadgeHtml +
        `</div>` +
        `<div class="msg-meta">` +
          (showStatePill ? `<span class="tag ${stateCls}">${escapeHtml(liveBadgeText)}</span>` : "") +
          (m.timeText ? `<span>${escapeHtml(m.timeText)}</span>` : "") +
          `<span class="msg-hover-actions">` +
            (canRetry ? `<button class="btn btn-ghost btn-sm" data-retry-msg="${escapeHtml(m.id)}" title="重试此轮指令">${icon("rotateCcw", 11)} <span>重试</span></button>` : "") +
            (m.text ? `<button class="btn btn-ghost btn-sm" data-copy-msg="${encodeURIComponent(m.text || "")}">${icon("copy", 11)} <span>复制</span></button>` : "") +
          `</span>` +
        `</div>` +
      `</div>` +
      failoverHtml +
      timelineHtml +
      bodyHtml +
      changedPillHtml +
    `</article>`
  );
}

function bindMessageCardListeners(cardEl, m, room) {
  cardEl.querySelectorAll("details[data-think-msg]").forEach((det) => {
    const body = det.querySelector(".ag-think-body");
    if (det.open && body && m.state === "Running") {
      body.scrollTop = body.scrollHeight;
    }
    det.addEventListener("toggle", () => {
      userToggledThinkMap.set(det.dataset.thinkMsg, det.open);
    });
  });

  cardEl.querySelectorAll("[data-open-turn-diff]").forEach((btn) => {
    btn.onclick = () => {
      state.diffOpen = true;
      state.inspectorTab = "diff";
      renderRoomHeader();
      renderDiffDrawer();
    };
  });

  cardEl.querySelectorAll("[data-tl-file]").forEach((row) => {
    row.onclick = async () => {
      const targetFile = decodeURIComponent(row.dataset.tlFile || "").replace(/\\/g, "/");
      state.diffOpen = true;
      state.inspectorTab = "diff";
      renderRoomHeader();
      const drawer = document.getElementById("diff-drawer");
      const resizer = document.getElementById("diff-resizer");
      if (drawer) drawer.style.display = "flex";
      if (resizer) resizer.style.display = "";
      await refreshRoomDiff();
      const files = state.diffSnapshot?.files || [];
      const hit = files.find((f) => f.filePath === targetFile || f.filePath.endsWith("/" + targetFile.split("/").pop()));
      if (hit) {
        state.diffSelectedFile = hit.filePath;
        renderDiffDrawerContent();
      }
    };
  });

  cardEl.querySelectorAll("[data-tl-cmd-step]").forEach((row) => {
    row.onclick = () => {
      const key = row.dataset.tlCmdStep;
      if (!key) return;
      if (state.expandedCmdSteps.has(key)) state.expandedCmdSteps.delete(key);
      else state.expandedCmdSteps.add(key);
      cardEl.__renderSig = "";
      renderChatStream(false);
    };
  });

  cardEl.querySelectorAll("[data-copy-msg]").forEach((btn) => {
    btn.onclick = () => {
      navigator.clipboard?.writeText(decodeURIComponent(btn.dataset.copyMsg));
      btn.textContent = "✓ 已复制";
      showToast("success", "消息已复制到剪贴板");
      setTimeout(() => { btn.textContent = "复制"; }, 1000);
    };
  });

  cardEl.querySelectorAll("[data-edit-msg]").forEach((btn) => {
    btn.onclick = () => {
      const composer = document.getElementById("composer-input");
      if (!composer) return;
      composer.value = decodeURIComponent(btn.dataset.editMsg);
      if (room?.id) state.roomDrafts.set(room.id, composer.value);
      composer.focus();
      composer.selectionStart = composer.selectionEnd = composer.value.length;
    };
  });

  cardEl.querySelectorAll("[data-retry-msg]").forEach((btn) => {
    btn.onclick = () => {
      if (!room || room.isRunning) return;
      const msgs = room.messages || [];
      const idx = msgs.findIndex((x) => x.id === btn.dataset.retryMsg);
      let prevUserText = "";
      for (let k = (idx >= 0 ? idx : msgs.length - 1); k >= 0; k--) {
        if (msgs[k]?.role === "User" && msgs[k]?.text) {
          prevUserText = msgs[k].text;
          break;
        }
      }
      if (!prevUserText) return;
      rpc("room.send", { roomId: room.id, text: prevUserText }).catch((err) => showToast("error", err.message));
    };
  });
}

function computeMessageRenderSig(m) {
  const steps = m.steps || [];
  const lastStep = steps.length > 0 ? steps[steps.length - 1] : null;
  const expandedForMsg = steps.filter((s) => state.expandedCmdSteps.has(`${m.id}:${s.id}`)).map((s) => s.id).join(",");
  return [
    m.id,
    m.state,
    m.stateText || "",
    m.activityStatus || "",
    m.workspaceName || "",
    m.modelSummary || "",
    m.failoverNotice || "",
    m.thinkingText || "",
    steps.length,
    lastStep ? `${lastStep.id}:${lastStep.status}:${lastStep.meta || ""}:${lastStep.title || ""}` : "",
    expandedForMsg,
    m.text || "",
    getUiLanguage(),
  ].join("\u0001");
}

function renderChatStream(forceScrollBottom = false) {
  const stream = document.getElementById("chat-stream");
  const room = getCurrentRoom();
  if (!room) {
    stream.innerHTML = "";
    stream.dataset.roomId = "";
    return;
  }

  const messages = room.messages || [];
  if (messages.length === 0) {
    stream.dataset.roomId = room.id;
    const agents = room.agents || [];
    const firstTwo = agents.slice(0, 2).map((a) => `@${a.alias}`).join(" ");
    const projPath = getRoomProjectPath(room);
    const projName = getProjectNameFromPath(projPath);
    const primaryInfo = getPrimaryWorkspaceInfo(room);
    stream.innerHTML = `
      <div class="empty-hero">
        <h2 style="display:inline-flex;align-items:center;gap:8px">${icon("folder", 20)} <span>${escapeHtml(projName)}</span></h2>
        <p><span class="mono" style="font-size:11.5px">${escapeHtml(room.workingDirectory)}</span> · 当前主力：<strong>${escapeHtml(primaryInfo.name)}</strong>（直接输入即可像原版客户端一样在项目内读写操作，输入 <code>@</code> 可随时多智能体协作）</p>
        <div class="preset-grid">
          <div class="preset-card" data-preset="请查看当前项目目录结构；如果是新建空目录请帮我初始化工程骨架与基础配置，如果已有代码请梳理模块架构并列出下一步建议。" data-preset-en="Inspect the current project folder: if it is empty, scaffold a clean starter structure; if code already exists, map out the module architecture and suggest next steps.">
            <div class="preset-card-title">🚀 初始化项目 / 梳理代码架构</div>
            <div class="preset-card-desc">让 ${escapeHtml(primaryInfo.name)} 直接读取并在当前项目目录内开始构建</div>
          </div>
          <div class="preset-card" data-preset="请检查当前项目中的潜在 Bug、报错或待完善功能，直接修改文件并运行构建/测试命令验证结果。" data-preset-en="Inspect this project for bugs, build issues, or incomplete logic, edit the files directly, and run the build/test suite to verify.">
            <div class="preset-card-title">🔧 排查修复并运行验证</div>
            <div class="preset-card-desc">在当前项目目录全自动改代码、跑测试并汇报 Diff</div>
          </div>
          <div class="preset-card" data-action="open-term">
            <div class="preset-card-title">⌨ 打开当前项目内置终端</div>
            <div class="preset-card-desc">在项目目录下打开隔离环境 PowerShell / Codex TUI / Claude TUI</div>
          </div>
          <div class="preset-card" data-preset="${escapeHtml(firstTwo || "@GPT1")} 请分别从代码架构设计与边界异常处理两个维度审查当前项目，并给出优化方案。" data-preset-en="${escapeHtml(firstTwo || "@GPT1")} Review this project from both software architecture and edge-case error handling perspectives, and propose concrete improvements.">
            <div class="preset-card-title">🤝 @ 多智能体同屏协作评审</div>
            <div class="preset-card-desc">在同一项目内同时调度多个账号或跨平台模型交叉把关</div>
          </div>
        </div>
      </div>
    `;
    stream.querySelectorAll("[data-preset]").forEach((card) => {
      card.onclick = () => {
        const composer = document.getElementById("composer-input");
        composer.value = (getUiLanguage() === "en" && card.dataset.presetEn) ? card.dataset.presetEn : card.dataset.preset;
        composer.focus();
      };
    });
    stream.querySelector('[data-action="open-term"]')?.addEventListener("click", () => document.getElementById("btn-toggle-term")?.click());
    const pill = document.getElementById("btn-scroll-bottom");
    if (pill) pill.style.display = "none";
    return;
  }

  const nearBottom = forceScrollBottom || stream.scrollHeight - stream.scrollTop - stream.clientHeight < 120;

  // Incremental DOM reconciliation: preserve untouched .msg-card nodes so text selection is never lost during streaming!
  if (stream.dataset.roomId !== room.id || stream.querySelector(".empty-hero")) {
    stream.innerHTML = "";
    stream.dataset.roomId = room.id;
  }

  const existingById = new Map();
  Array.from(stream.querySelectorAll("article.msg-card[data-msg-id]")).forEach((el) => {
    existingById.set(el.dataset.msgId, el);
  });

  const validIds = new Set(messages.map((m) => m.id));
  for (const [oldId, oldEl] of existingById.entries()) {
    if (!validIds.has(oldId)) oldEl.remove();
  }

  let cursorEl = stream.firstElementChild;
  for (const m of messages) {
    const sig = computeMessageRenderSig(m);
    let cardEl = existingById.get(m.id);
    if (!cardEl) {
      const temp = document.createElement("div");
      temp.innerHTML = buildMessageCardHtml(m);
      cardEl = temp.firstElementChild;
      cardEl.__renderSig = sig;
      bindMessageCardListeners(cardEl, m, room);
      applyLanguageToRoot(cardEl);
      stream.insertBefore(cardEl, cursorEl || null);
    } else {
      if (cardEl !== cursorEl) {
        stream.insertBefore(cardEl, cursorEl || null);
      }
      if (cardEl.__renderSig !== sig) {
        const temp = document.createElement("div");
        temp.innerHTML = buildMessageCardHtml(m);
        const freshEl = temp.firstElementChild;
        freshEl.__renderSig = sig;
        bindMessageCardListeners(freshEl, m, room);
        applyLanguageToRoot(freshEl);
        cardEl.replaceWith(freshEl);
        cardEl = freshEl;
      }
      cursorEl = cardEl.nextElementSibling;
      continue;
    }
    cursorEl = cardEl.nextElementSibling;
  }

  if (nearBottom) {
    stream.scrollTop = stream.scrollHeight;
  }
  const scrollPill = document.getElementById("btn-scroll-bottom");
  if (scrollPill) {
    const dist = stream.scrollHeight - stream.scrollTop - stream.clientHeight;
    scrollPill.style.display = dist > 160 ? "inline-flex" : "none";
  }
  if (state.diffOpen && state.inspectorTab === "trace") {
    renderCommandInspectorTrace();
  }
}

function renderFileLangBadge(fileName) {
  if (!fileName) return "";
  const ext = (String(fileName).split(".").pop() || "").toLowerCase();
  if (ext === "cs" || ext === "csproj" || ext === "xaml") return `<span class="lang-badge cs">C#</span>`;
  if (ext === "js" || ext === "mjs" || ext === "cjs" || ext === "jsx") return `<span class="lang-badge js">JS</span>`;
  if (ext === "ts" || ext === "tsx") return `<span class="lang-badge ts">TS</span>`;
  if (ext === "py") return `<span class="lang-badge py">PY</span>`;
  if (ext === "css" || ext === "scss" || ext === "less") return `<span class="lang-badge css">CSS</span>`;
  if (ext === "json" || ext === "jsonl" || ext === "yml" || ext === "yaml") return `<span class="lang-badge json">JSON</span>`;
  if (ext === "md" || ext === "txt") return `<span class="lang-badge md">MD</span>`;
  if (ext === "ps1" || ext === "sh" || ext === "cmd" || ext === "bat") return `<span class="lang-badge py">SH</span>`;
  return `<span class="lang-badge md">📄</span>`;
}

function formatDiffMetaHtml(meta) {
  const m = String(meta || "").match(/\+(\d+)\s+-(\d+)/);
  if (m) {
    return `<span class="cf-add">+${m[1]}</span> <span class="cf-del">-${m[2]}</span>`;
  }
  return escapeHtml(meta || "");
}

function renderAgentTimelineBlock(m, toggledThinkIds) {
  const rawSteps = m.steps || [];
  const hasThinking = !!(m.thinkingText && m.thinkingText.trim());
  const isRunning = m.state === "Running" || m.state === "Pending";

  // Filter out internal request/thinking/system handshake steps once completed so transport plumbing never clutters chat replies
  const concreteSteps = rawSteps.filter((s) => s.kind !== "thinking" && (s.kind !== "system" || s.status === "running"));
  const visibleSteps = concreteSteps;

  if (!hasThinking && visibleSteps.length === 0 && !isRunning) {
    return { timelineHtml: "", changedPillHtml: "" };
  }

  let thinkHtml = "";
  if (hasThinking) {
    // Auto-open while actively thinking before answer text arrives; respect manual toggle too
    const manualState = toggledThinkIds.get(m.id);
    const shouldOpen = manualState !== undefined ? manualState : (isRunning && !m.text);
    const thinkLabel = isRunning && !m.text ? "正在思考…" : "思考过程";
    thinkHtml =
      `<details class="ag-think-details" data-think-msg="${escapeHtml(m.id)}" ${shouldOpen ? "open" : ""}>` +
        `<summary class="ag-think-summary">` +
          `<span>${thinkLabel}</span>` +
          `<span class="ag-think-caret">⌄</span>` +
        `</summary>` +
        `<div class="ag-think-body">${escapeHtml(m.thinkingText)}</div>` +
      `</details>`;
  }

  const rowsHtml = visibleSteps
    .map((s) => {
      const running = s.status === "running";
      const failed = s.status === "failed";
      const baseCls = `ag-tl-row ${running ? "is-running" : ""} ${failed ? "is-failed" : ""}`;
      const fileTarget = s.detail || s.target || "";

      if (s.kind === "read") {
        const verb = running ? "正在读取" : "已读取";
        return (
          `<div class="${baseCls} is-interactive" data-tl-file="${encodeURIComponent(fileTarget || s.title)}" title="${escapeHtml(fileTarget || s.title)}">` +
            `<span class="ag-tl-verb">${verb}</span>` +
            renderFileLangBadge(s.target || s.title) +
            `<span class="ag-tl-file">${escapeHtml(s.target || s.title)}</span>` +
            (s.meta ? `<span class="ag-tl-meta">${escapeHtml(s.meta)}</span>` : "") +
          `</div>`
        );
      }

      if (s.kind === "edit" || s.kind === "write") {
        const verb = s.kind === "write" ? (running ? "正在创建" : "已创建") : (running ? "正在修改" : "已修改");
        return (
          `<div class="${baseCls} is-interactive" data-tl-file="${encodeURIComponent(fileTarget || s.title)}" title="${escapeHtml(fileTarget || s.title)}">` +
            `<span class="ag-tl-verb">${verb}</span>` +
            renderFileLangBadge(s.target || s.title) +
            `<span class="ag-tl-file">${escapeHtml(s.target || s.title)}</span>` +
            (s.meta ? `<span class="ag-tl-diff-meta">${formatDiffMetaHtml(s.meta)}</span>` : "") +
          `</div>`
        );
      }

      if (s.kind === "command" || s.kind === "command_execution") {
        const cmdText = s.title || s.target || "command";
        const stepKey = `${m.id}:${s.id}`;
        const isExpanded = state.expandedCmdSteps.has(stepKey);
        const label = failed
          ? `Command failed (${escapeHtml(cmdText)})`
          : running
          ? `Running ${escapeHtml(cmdText)}`
          : escapeHtml(cmdText);
        return (
          `<div>` +
            `<div class="${baseCls} is-interactive" data-tl-cmd-step="${escapeHtml(stepKey)}" title="${escapeHtml(s.detail || cmdText)}">` +
              `<span class="ag-cmd-icon">${running ? "⟳" : "&gt;_"}</span>` +
              `<span class="ag-cmd-text">${label}</span>` +
              (s.meta ? `<span class="ag-tl-meta danger">${escapeHtml(s.meta)}</span>` : `<span class="ag-cmd-chevron">${isExpanded ? "▾" : "›"}</span>`) +
            `</div>` +
            (isExpanded ? `<div class="ag-cmd-detail">$ ${escapeHtml(s.detail || cmdText)}${s.meta ? `\n→ ${escapeHtml(s.meta)}` : ""}</div>` : "") +
          `</div>`
        );
      }

      if (s.kind === "search") {
        return (
          `<div class="${baseCls}" title="${escapeHtml(s.detail || s.title)}">` +
            `<span class="ag-tl-verb">${running ? "正在检索" : "已检索"}</span>` +
            `<span class="ag-tl-file">${escapeHtml(s.target || s.title)}</span>` +
          `</div>`
        );
      }

      return (
        `<div class="${baseCls}">` +
          `<span class="ag-cmd-icon">${running ? "⟳" : "✓"}</span>` +
          `<span class="ag-cmd-text" style="font-family:var(--font-sans)">${escapeHtml(s.title)}</span>` +
        `</div>`
      );
    })
    .join("");

  let livePulseHtml = "";
  if (isRunning && !hasThinking && visibleSteps.length === 0 && !m.text) {
    livePulseHtml =
      `<div class="ag-live-pulse">` +
        `<span class="pulse-dot"></span>` +
        `<span>${escapeHtml(m.activityStatus || "已发送指令，正在连接智能体并开始思考…")}</span>` +
      `</div>`;
  }

  const timelineHtml =
    thinkHtml || rowsHtml || livePulseHtml
      ? `<div class="ag-trace-wrap">${thinkHtml}${rowsHtml ? `<div class="ag-timeline">${rowsHtml}</div>` : ""}${livePulseHtml}</div>`
      : "";

  // Count edited/created files in this turn for the Codex-style clickable changed-files summary pill
  const changedFiles = new Set();
  let totalAdds = 0;
  let totalDels = 0;
  for (const s of visibleSteps) {
    if (s.kind === "edit" || s.kind === "write" || s.kind === "file_change") {
      changedFiles.add(s.detail || s.target || s.id);
      const dm = String(s.meta || "").match(/\+(\d+)\s+-(\d+)/);
      if (dm) {
        totalAdds += Number(dm[1]);
        totalDels += Number(dm[2]);
      }
    }
  }

  const changedPillHtml =
    changedFiles.size > 0
      ? `<div><button type="button" class="changed-files-pill" data-open-turn-diff="1" title="点击打开右侧 Git Diff 逐行审查代码改动">` +
          `<span>${changedFiles.size} 个文件已更改</span>` +
          (totalAdds > 0 || totalDels > 0 ? `<span class="cf-add">+${totalAdds}</span><span class="cf-del">-${totalDels}</span>` : "") +
        `</button></div>`
      : "";

  return { timelineHtml, changedPillHtml };
}

function insertMentionIntoComposer(alias) {
  const composer = document.getElementById("composer-input");
  const start = composer.selectionStart ?? composer.value.length;
  const token = `@${alias} `;
  composer.value = composer.value.slice(0, start) + token + composer.value.slice(start);
  composer.selectionStart = composer.selectionEnd = start + token.length;
  composer.focus();
}

function filterMentionAgents(agents, query) {
  if (!query) return agents;
  const q = query.toLowerCase();
  return agents.filter(
    (a) =>
      a.alias.toLowerCase().includes(q) ||
      (a.workspaceName || "").toLowerCase().includes(q) ||
      (a.client || "").toLowerCase().includes(q)
  );
}

function updateMentionAutocomplete() {
  const composer = document.getElementById("composer-input");
  const pos = composer.selectionStart ?? 0;
  const before = composer.value.slice(0, pos);
  const match = before.match(/(?:^|\s)@([A-Za-z0-9_\-\u4e00-\u9fa5]{0,24})$/);
  if (!match) {
    state.mentionMenu.open = false;
    renderMentionPopup();
    return;
  }
  state.mentionMenu.open = true;
  state.mentionMenu.query = match[1] || "";
  state.mentionMenu.startIdx = pos - match[1].length - 1;
  state.mentionMenu.endIdx = pos;
  state.mentionMenu.selectedIdx = 0;
  renderMentionPopup();
}

function renderMentionPopup() {
  const popup = document.getElementById("mention-popup");
  if (!state.mentionMenu.open) {
    popup.style.display = "none";
    return;
  }
  const agents = getCurrentRoom()?.agents || [];
  const filtered = filterMentionAgents(agents, state.mentionMenu.query);
  if (filtered.length === 0) {
    popup.style.display = "none";
    return;
  }

  const poolMap = getPoolEntryMap();
  popup.style.display = "block";
  popup.innerHTML = filtered
    .map((a, idx) => {
      const plat = formatPlatformShort(a.client);
      const pe = poolMap.get(a.workspaceId);
      let quotaText = pe?.statusText || a.client;
      if (a.client === "Codex" && pe?.primaryRemainingPercent != null) {
        quotaText = `剩余 ${Math.round(pe.primaryRemainingPercent)}%`;
      }
      const showName = a.workspaceName && a.workspaceName !== a.alias
        ? `<span style="color:var(--text-secondary);overflow:hidden;text-overflow:ellipsis">${escapeHtml(a.workspaceName)}</span>`
        : "";
      return (
        `<div class="mention-option ${idx === state.mentionMenu.selectedIdx ? "selected" : ""}" data-pick-mention="${idx}">` +
          `<div style="display:flex;align-items:center;gap:6px;min-width:0;overflow:hidden">` +
            `<span class="plat-badge ${plat.cls}">${plat.code}</span>` +
            `<strong style="flex-shrink:0">@${escapeHtml(a.alias)}</strong>` +
            showName +
          `</div>` +
          `<span style="font-size:11px;color:var(--text-muted);max-width:175px;overflow:hidden;text-overflow:ellipsis;flex-shrink:0">${escapeHtml(quotaText)}</span>` +
        `</div>`
      );
    })
    .join("");

  popup.querySelectorAll("[data-pick-mention]").forEach((el) => {
    el.onmousedown = (ev) => {
      ev.preventDefault();
      applyMentionSelection(filtered[Number(el.dataset.pickMention)]);
    };
  });
}

function applyMentionSelection(agent) {
  const composer = document.getElementById("composer-input");
  const replacement = `@${agent.alias} `;
  composer.value =
    composer.value.slice(0, state.mentionMenu.startIdx) +
    replacement +
    composer.value.slice(state.mentionMenu.endIdx);
  const caret = state.mentionMenu.startIdx + replacement.length;
  composer.selectionStart = composer.selectionEnd = caret;
  state.mentionMenu.open = false;
  renderMentionPopup();
  composer.focus();
}

function renderDesignChips() {
  const el = document.getElementById("design-chips");
  if (state.designCaptures.length === 0) {
    el.style.display = "none";
    el.innerHTML = "";
    return;
  }
  el.style.display = "flex";
  el.innerHTML = state.designCaptures
    .map(
      (c, idx) =>
        `<div class="design-chip">` +
          (c.screenshotDataUrl ? `<img src="${c.screenshotDataUrl}" alt="preview" />` : "") +
          `<span>🎯 <code>${escapeHtml(c.selector)}</code></span>` +
          `<button class="btn btn-ghost btn-sm" style="padding:0 4px" data-rm-capture="${idx}">✕</button>` +
        `</div>`
    )
    .join("");
  el.querySelectorAll("[data-rm-capture]").forEach((btn) => {
    btn.onclick = () => {
      state.designCaptures.splice(Number(btn.dataset.rmCapture), 1);
      renderDesignChips();
    };
  });
}

async function sendCurrentRoomMessage() {
  const room = getCurrentRoom();
  if (!room || room.isRunning) return;
  const composer = document.getElementById("composer-input");
  let text = composer.value.trim();
  if (!text) {
    showToast("warning", "请输入指令内容（直接发送给当前主力智能体，或输入 @ 多账号协作）");
    composer.focus();
    return;
  }
  if (!room.agents || room.agents.length === 0) {
    showToast("warning", "当前尚未接入可用智能体，请先点击「＋ 接入」或在账号池添加账号");
    return;
  }

  let attachments = undefined;
  if (state.designCaptures.length > 0) {
    const imgPayloads = state.designCaptures
      .filter((c) => c.screenshotDataUrl)
      .map((c, idx) => ({
        dataUrl: c.screenshotDataUrl,
        label: c.selector || `UI Capture #${idx + 1}`,
      }));
    if (imgPayloads.length > 0) attachments = imgPayloads;

    const domCaptures = state.designCaptures.filter((c) => c.outerHtml || c.url);
    if (domCaptures.length > 0) {
      const contextBlocks = domCaptures.map((c, idx) => {
        const cssText = JSON.stringify(c.computedStyles || {});
        return (
          `\n\n---\n### 附加前端 UI 点选元素 #${idx + 1}\n` +
          `- 页面地址: ${c.url || ""}\n` +
          `- 选择器: \`${c.selector || "element"}\`\n` +
          `- 尺寸位置: ${JSON.stringify(c.bounds || {})}\n` +
          `- 关键计算样式: \`${cssText}\`\n` +
          `\`\`\`html\n${c.outerHtml || ""}\n\`\`\``
        );
      });
      text += contextBlocks.join("");
    }
  }

  const sendBtn = document.getElementById("btn-send-msg");
  await runButtonAction(sendBtn, { busyText: "正在派单…", doneText: "✓ 已发送" }, async () => {
    await rpc("room.send", { roomId: room.id, text, attachments });
    composer.value = "";
    state.roomDrafts.set(room.id, "");
    state.designCaptures = [];
    renderDesignChips();
  });
}

/* ==========================================================================
   5. ConPTY Split Terminal & Diff Review Drawer
   ========================================================================== */
function renderTerminalDock() {
  const dock = document.getElementById("terminal-dock");
  dock.style.display = state.terminalOpen ? "flex" : "none";
  if (!state.terminalOpen) return;

  const workspaces = state.data?.workspaces || [];
  const acctSelect = document.getElementById("term-account-select");
  if (!state.terminalAccountId && workspaces.length > 0) {
    state.terminalAccountId = workspaces[0].id;
  }
  acctSelect.innerHTML =
    workspaces
      .map(
        (w) =>
          `<option value="${escapeHtml(w.id)}" ${w.id === state.terminalAccountId ? "selected" : ""}>隔离环境：${escapeHtml(w.displayName)}</option>`
      )
      .join("") + `<option value="">本机默认环境</option>`;
  acctSelect.onchange = () => { state.terminalAccountId = acctSelect.value; };

  const terminals = state.data?.terminals || [];
  if (terminals.length > 0 && (!state.activeTerminalId || !terminals.some((t) => t.id === state.activeTerminalId))) {
    state.activeTerminalId = terminals[terminals.length - 1].id;
  }

  const tabsEl = document.getElementById("terminal-tabs");
  tabsEl.innerHTML = terminals
    .map(
      (t) =>
        `<div class="term-tab ${t.id === state.activeTerminalId ? "active" : ""}" data-select-term="${escapeHtml(t.id)}">` +
          `<span>${t.isAlive ? "●" : "○"} ${escapeHtml(t.title)}</span>` +
          `<span data-close-term="${escapeHtml(t.id)}" style="opacity:0.65;padding:0 3px">✕</span>` +
        `</div>`
    )
    .join("");

  tabsEl.querySelectorAll("[data-select-term]").forEach((tab) => {
    tab.onclick = (e) => {
      const closeId = e.target.dataset?.closeTerm;
      if (closeId) {
        e.stopPropagation();
        terminalMgr.removeSession(closeId);
        rpc("terminal.close", { sessionId: closeId }).then((res) => {
          if (state.data && res?.terminals) state.data.terminals = res.terminals;
          if (state.activeTerminalId === closeId) state.activeTerminalId = null;
          renderTerminalDock();
        });
        return;
      }
      state.activeTerminalId = tab.dataset.selectTerm;
      renderTerminalDock();
    };
  });

  const stage = document.getElementById("terminal-stage");
  if (state.activeTerminalId) {
    terminalMgr.ensureSession(state.activeTerminalId);
    terminalMgr.mountStage(stage, state.activeTerminalId);
  } else {
    stage.innerHTML = `<div style="padding:24px;color:var(--text-muted);text-align:center">点击右上角 ＋ PowerShell / Codex TUI / Claude TUI 启动绑定独立账号的内置伪终端</div>`;
  }
}

async function spawnTerminal(commandKind, overrideWorkspaceId = null, overrideCwd = null) {
  state.terminalOpen = true;
  const room = getCurrentRoom();
  const cwd = overrideCwd || room?.workingDirectory || state.data?.projectRoot || "";
  const workspaceId = overrideWorkspaceId !== null ? overrideWorkspaceId : state.terminalAccountId;
  const info = await rpc("terminal.create", {
    workspaceId: workspaceId || null,
    workingDirectory: cwd,
    commandKind,
    cols: 120,
    rows: 26,
  });
  state.activeTerminalId = info.id;
  terminalMgr.ensureSession(info.id);
  renderRoomHeader();
  renderTerminalDock();
}

async function refreshRoomDiff() {
  const room = getCurrentRoom();
  if (!room) return;
  state.diffSnapshot = await rpc("git.diff", { directory: room.workingDirectory });
  renderDiffDrawerContent();
}

function renderDiffDrawer() {
  const drawer = document.getElementById("diff-drawer");
  const resizer = document.getElementById("diff-resizer");
  drawer.style.display = state.diffOpen ? "flex" : "none";
  if (resizer) resizer.style.display = state.diffOpen ? "" : "none";
  if (state.diffOpen) {
    refreshRoomDiff().catch((err) => showToast("error", err.message));
  }
}

function renderCommandInspectorTrace() {
  const traceEl = document.getElementById("cmd-inspector-trace");
  if (!traceEl) return;
  const room = getCurrentRoom();
  const primaryWsId = getCurrentPrimaryWsId(room);
  const pe = getPoolEntryMap().get(primaryWsId) || {};
  const msgs = (room?.messages || []).filter((m) => m.role === "Agent");
  const activeMsg = msgs.find((m) => m.state === "Running" || m.state === "Pending");
  const latestMsg = activeMsg || msgs[msgs.length - 1] || null;
  const steps = latestMsg?.steps || [];
  const files = state.diffSnapshot?.files || [];
  const isWorking = !!room?.isRunning || !!activeMsg;

  traceEl.innerHTML = `
    <div class="studio-hud-card ${isWorking ? "is-working" : ""}">
      <div class="studio-hud-title">
        <span>实时工作状态</span>
        <span>${escapeHtml(latestMsg?.workspaceName || pe.displayName || "")}</span>
      </div>
      <div class="studio-live-banner">
        <div class="studio-live-state" style="color:${isWorking ? "var(--accent-cyan)" : "var(--success)"}">
          ${isWorking ? `<span class="studio-pulse-ring"></span><span>工作中</span>` : `<span class="pool-dot healthy" style="width:9px;height:9px"></span><span>空闲待命 · 随时响应</span>`}
        </div>
        ${isWorking && room ? `<button class="btn btn-danger btn-sm" id="cmd-insp-stop">■ 停止</button>` : ""}
      </div>
      <div style="font-size:11.5px;color:var(--text-secondary)">
        ${escapeHtml(activeMsg?.activityStatus || room?.activityText || pe.statusText || "无头引擎在线待命")}
      </div>
    </div>
    <div class="studio-hud-card">
      <div class="studio-hud-title">
        <span>实时执行与思考轨迹</span>
        <span>${steps.length > 0 ? `${steps.length} 步` : "暂无"}</span>
      </div>
      <div class="studio-step-list">
        ${
          steps.length === 0
            ? `<div style="font-size:11.5px;color:var(--text-muted)">发送指令后，此处将实时滚动展示该智能体的思考、文件读写与命令执行步骤</div>`
            : steps
                .map(
                  (s) =>
                    `<div class="studio-step-row ${s.status !== "completed" ? "running" : ""}">` +
                      `<span style="flex-shrink:0;color:${s.status === "completed" ? "var(--success)" : "var(--accent-cyan)"}">${s.status === "completed" ? "✓" : "⟳"}</span>` +
                      `<span>${escapeHtml(s.title)}</span>` +
                    `</div>`
                )
                .join("")
        }
      </div>
    </div>
    <div class="studio-hud-card">
      <div class="studio-hud-title">
        <span>工作目录文件变更</span>
        <span>${files.length}</span>
      </div>
      <div class="studio-file-list">
        ${
          files.length === 0
            ? `<div style="font-size:11.5px;color:var(--text-muted)">工作区干净，暂无未提交文件改动</div>`
            : files
                .map(
                  (f) =>
                    `<div class="studio-file-row" data-insp-jump-file="${encodeURIComponent(f.filePath)}">` +
                      `<span class="mono" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${escapeHtml(f.filePath)}</span>` +
                      `<span class="mono" style="flex-shrink:0;font-size:11px"><span style="color:var(--success)">+${f.additions}</span> <span style="color:var(--danger)">-${f.deletions}</span></span>` +
                    `</div>`
                )
                .join("")
        }
      </div>
    </div>
  `;
  applyLanguageToRoot(traceEl);

  document.getElementById("cmd-insp-stop")?.addEventListener("click", () => {
    if (room) rpc("room.stop", { roomId: room.id });
  });
  traceEl.querySelectorAll("[data-insp-jump-file]").forEach((row) => {
    row.onclick = () => {
      state.inspectorTab = "diff";
      state.diffSelectedFile = decodeURIComponent(row.dataset.inspJumpFile);
      renderDiffDrawerContent();
    };
  });
}

function renderDiffDrawerContent() {
  if (!state.diffOpen) return;
  const snap = state.diffSnapshot;
  const room = getCurrentRoom();
  const isTrace = state.inspectorTab === "trace";

  document.getElementById("insp-tab-diff")?.classList.toggle("active", !isTrace);
  document.getElementById("insp-tab-trace")?.classList.toggle("active", isTrace);
  document.getElementById("btn-diff-unified").style.display = isTrace ? "none" : "";
  document.getElementById("btn-diff-split").style.display = isTrace ? "none" : "";
  document.getElementById("diff-files-strip").style.display = isTrace ? "none" : "flex";
  document.getElementById("diff-body").style.display = isTrace ? "none" : "";
  document.getElementById("diff-review-bar").style.display = isTrace ? "none" : "flex";
  const traceEl = document.getElementById("cmd-inspector-trace");
  if (traceEl) traceEl.style.display = isTrace ? "flex" : "none";

  if (isTrace) {
    document.getElementById("diff-title").textContent = "实时执行与思考轨迹";
    renderCommandInspectorTrace();
    return;
  }

  document.getElementById("btn-diff-unified").classList.toggle("btn-primary", state.diffMode === "unified");
  document.getElementById("btn-diff-split").classList.toggle("btn-primary", state.diffMode === "split");

  const titleEl = document.getElementById("diff-title");
  titleEl.textContent = snap?.isGitRepository
    ? `Diff 审查 · ${snap.totalFiles} 文件 (+${snap.totalAdditions} / -${snap.totalDeletions})`
    : "Git Diff 审查";

  const strip = document.getElementById("diff-files-strip");
  const files = snap?.files || [];
  if (state.diffSelectedFile && !files.some((f) => f.filePath === state.diffSelectedFile)) {
    state.diffSelectedFile = null;
  }
  strip.innerHTML =
    `<button class="diff-file-pill ${!state.diffSelectedFile ? "active" : ""}" data-diff-file="">全部 (${files.length})</button>` +
    files
      .map(
        (f) =>
          `<button class="diff-file-pill ${state.diffSelectedFile === f.filePath ? "active" : ""}" data-diff-file="${encodeURIComponent(f.filePath)}">` +
            `${escapeHtml(f.filePath.split("/").pop())} <span style="color:var(--diff-add-text)">+${f.additions}</span>` +
          `</button>`
      )
      .join("");

  strip.querySelectorAll("[data-diff-file]").forEach((pill) => {
    pill.onclick = () => {
      const raw = pill.dataset.diffFile;
      state.diffSelectedFile = raw ? decodeURIComponent(raw) : null;
      renderDiffDrawerContent();
    };
  });

  document.getElementById("diff-body").innerHTML = renderDiffFilesHtml(snap, {
    mode: state.diffMode,
    selectedFile: state.diffSelectedFile,
    comments: state.diffComments,
    activeDraftKey: state.diffActiveDraftKey,
    allowComments: true,
  });

  const targetSel = document.getElementById("diff-target-agent");
  const agents = room?.agents || [];
  if (!state.diffTargetAgent && agents.length > 0) state.diffTargetAgent = agents[0].alias;
  targetSel.innerHTML = agents
    .map(
      (a) =>
        `<option value="${escapeHtml(a.alias)}" ${a.alias === state.diffTargetAgent ? "selected" : ""}>派回给 @${escapeHtml(a.alias)} (${escapeHtml(a.workspaceName)})</option>`
    )
    .join("");
  targetSel.onchange = () => { state.diffTargetAgent = targetSel.value; };

  document.getElementById("diff-queue-count").textContent =
    state.diffComments.length > 0
      ? `已添加 ${state.diffComments.length} 条行内代码批注`
      : "悬停代码行点击 ＋ 添加行内批注";
}

async function submitInlineDiffReview() {
  const room = getCurrentRoom();
  if (!room) return;
  const overallNote = document.getElementById("diff-overall-note").value.trim();
  const targetMention = document.getElementById("diff-target-agent").value || room.agents?.[0]?.alias || "GPT1";
  await rpc("git.submitReview", {
    roomId: room.id,
    targetMention,
    overallNote,
    comments: state.diffComments,
  });
  state.diffComments = [];
  document.getElementById("diff-overall-note").value = "";
  renderDiffDrawerContent();
  showToast("success", `已将逐行代码审查批注发送给 @${targetMention}`);
}

/* ==========================================================================
   6. View 2: Headless Worktree Arena
   ========================================================================== */
function renderArenaView() {
  if (!state.data) return;
  const repoInput = document.getElementById("arena-repo-input");
  if (!state.arenaInitialized) {
    state.arenaInitialized = true;
    repoInput.value = state.data.settings?.lastRepositoryPath || state.data.projectRoot || "";
  }
  renderArenaContestantPicker();

  const sessionsEl = document.getElementById("arena-sessions-list");
  const sessions = state.data.arenaSessions || [];
  if (sessions.length === 0) {
    sessionsEl.innerHTML = `
      <div style="padding:36px;text-align:center;border:1px dashed var(--border-strong);border-radius:var(--radius-lg);color:var(--text-secondary)">
        <div style="font-size:15px;font-weight:700;color:var(--text-primary);margin-bottom:6px">尚未发起后台赛马竞赛</div>
        <div>在上方勾选 2~6 个不同账号（支持 Codex、Claude、反重力同台竞技），全部选手将在后台独立 Git Worktree 无窗并行开发。</div>
      </div>
    `;
    return;
  }

  sessionsEl.innerHTML = sessions
    .map((s) => {
      const anyRunning = (s.contestants || []).some((c) => c.state === "Running" || c.state === "Pending");
      const cardsHtml = (s.contestants || [])
        .map((c) => {
          const isWinner = s.mergedContestantId === c.id || c.state === "Merged";
          const plat = formatPlatformShort(c.client);
          const verifyBadge =
            c.verificationExitCode === 0
              ? `<span class="tag write">测试 PASS</span>`
              : c.verificationExitCode != null
              ? `<span class="tag danger">测试 EXIT ${c.verificationExitCode}</span>`
              : "";

          return (
            `<div class="contestant-card ${isWinner ? "merged" : ""}">` +
              `<div class="contestant-head">` +
                `<div style="display:flex;align-items:center;justify-content:space-between;gap:8px">` +
                  `<div style="display:flex;align-items:center;gap:6px;font-weight:700;font-size:13.5px">` +
                    `<span class="plat-badge ${plat.cls}">${plat.code}</span>` +
                    `<span>${isWinner ? "🏆 " : ""}${escapeHtml(c.label)} · ${escapeHtml(c.workspaceName)}</span>` +
                    (c.model ? `<span class="msg-model-badge">${escapeHtml(c.model)}</span>` : "") +
                  `</div>` +
                  `<span class="tag ${c.state === "Running" ? "running" : c.state === "Completed" || isWinner ? "write" : ""}">${escapeHtml(c.state)} · ${c.durationSeconds || 0}s</span>` +
                `</div>` +
                `<div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;font-size:11px">` +
                  `<span class="tag worktree">⑂ ${escapeHtml(c.branchName)}</span>` +
                  `<span class="mono" style="color:var(--diff-add-text)">+${c.additions}</span>` +
                  `<span class="mono" style="color:var(--diff-del-text)">-${c.deletions}</span>` +
                  `<span style="color:var(--text-muted)">(${c.changedFiles} 文件)</span>` +
                  verifyBadge +
                `</div>` +
                (c.failoverNotice ? `<div class="failover-banner" style="margin:2px 0 0">${escapeHtml(c.failoverNotice)}</div>` : "") +
              `</div>` +
              `<div class="contestant-body"><div class="md-body">${renderMarkdown(c.outputText || (c.state === "Running" ? "正在后台独立 Worktree 中编码…" : "等待输出"))}</div></div>` +
              `<div class="contestant-foot">` +
                `<button class="btn btn-sm" data-arena-diff="${escapeHtml(s.id)}" data-contestant="${escapeHtml(c.id)}">± 审查完整 Diff</button>` +
                `<button class="btn btn-primary btn-sm" data-arena-merge="${escapeHtml(s.id)}" data-contestant="${escapeHtml(c.id)}" ${anyRunning || s.mergedContestantId ? "disabled" : ""}>` +
                  `${isWinner ? "✓ 已合入主分支" : "🏆 采纳并合并"}` +
                `</button>` +
              `</div>` +
            `</div>`
          );
        })
        .join("");

      return (
        `<div>` +
          `<div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:10px;gap:8px;flex-wrap:wrap">` +
            `<div><span style="font-size:14.5px;font-weight:700">${escapeHtml(s.title)}</span> <span style="font-size:11.5px;color:var(--text-muted);margin-left:8px">${escapeHtml(s.repositoryRoot)}</span></div>` +
            `<div style="display:flex;align-items:center;gap:6px">` +
              (anyRunning
                ? `<button class="btn btn-danger btn-sm" data-arena-stop="${escapeHtml(s.id)}">■ 停止本场赛马</button>`
                : `<button class="btn btn-sm" data-arena-rerun="${escapeHtml(s.id)}">↻ 复用配置</button>` +
                  `<button class="btn btn-sm" data-arena-cleanup="${escapeHtml(s.id)}">🧹 清理分支</button>` +
                  `<button class="btn btn-ghost btn-sm" data-arena-delete="${escapeHtml(s.id)}">🗑 移除记录</button>`) +
            `</div>` +
          `</div>` +
          `<div class="arena-grid">${cardsHtml}</div>` +
        `</div>`
      );
    })
    .join("");

  sessionsEl.querySelectorAll("[data-arena-stop]").forEach((btn) => {
    btn.onclick = () => runButtonAction(btn, { busyText: "停止中…", doneText: "已停止" }, () => rpc("arena.stop", { sessionId: btn.dataset.arenaStop }));
  });
  sessionsEl.querySelectorAll("[data-arena-rerun]").forEach((btn) => {
    btn.onclick = () => {
      const s = sessions.find((x) => x.id === btn.dataset.arenaRerun);
      if (!s) return;
      document.getElementById("arena-repo-input").value = s.repositoryRoot || "";
      document.getElementById("arena-verify-input").value = s.verificationCommand || "";
      document.getElementById("arena-prompt-input").value = s.prompt || "";
      document.getElementById("arena-prompt-input").focus();
      showToast("info", "已回填本场赛马配置，修改后可直接重新发车");
    };
  });
  sessionsEl.querySelectorAll("[data-arena-cleanup]").forEach((btn) => {
    btn.onclick = () =>
      runButtonAction(btn, { busyText: "清理中…", doneText: "✓ 已清理" }, async () => {
        const res = await rpc("arena.cleanup", { sessionId: btn.dataset.arenaCleanup, keepWinner: false });
        if (res?.sessions && state.data) state.data.arenaSessions = res.sessions;
        renderArenaView();
      });
  });
  sessionsEl.querySelectorAll("[data-arena-delete]").forEach((btn) => {
    btn.onclick = () =>
      runButtonAction(btn, { busyText: "移除…", doneText: "✓" }, async () => {
        const res = await rpc("arena.delete", { sessionId: btn.dataset.arenaDelete });
        if (res?.sessions && state.data) state.data.arenaSessions = res.sessions;
        renderArenaView();
      });
  });
  sessionsEl.querySelectorAll("[data-arena-diff]").forEach((btn) => {
    btn.onclick = () =>
      runButtonAction(btn, { busyText: "读取 Diff…", doneText: "✓ 已打开" }, async () => {
        const diff = await rpc("arena.diff", { sessionId: btn.dataset.arenaDiff, contestantId: btn.dataset.contestant });
        openArenaDiffModal(diff);
      });
  });
  sessionsEl.querySelectorAll("[data-arena-merge]").forEach((btn) => {
    btn.onclick = () => {
      const en = getUiLanguage() === "en";
      openConfirmModal({
        title: en ? "Merge Winning Branch to Main" : "合并胜出方案回主仓库",
        messageHtml: en
          ? "Commit and merge this contestant's isolated branch (<code>git merge --no-ff</code>) into your main repository?"
          : "确定将该参赛方案的独立分支自动提交并合并（<code>git merge --no-ff</code>）回主仓库吗？",
        confirmText: en ? "✓ Confirm Merge" : "✓ 确认合并",
        danger: false,
        onConfirm: async () => {
          const res = await rpc("arena.mergeWinner", { sessionId: btn.dataset.arenaMerge, contestantId: btn.dataset.contestant });
          if (res?.sessions && state.data) state.data.arenaSessions = res.sessions;
          renderArenaView();
          showToast("success", res?.message || "已成功合并胜出方案！");
        },
      });
    };
  });
}

function renderArenaContestantPicker() {
  const picker = document.getElementById("arena-contestants-picker");
  if (document.activeElement && picker.contains(document.activeElement) && document.activeElement.tagName === "INPUT") {
    return;
  }
  const prevChecked = new Set(Array.from(picker.querySelectorAll("[data-arena-ws]:checked")).map((el) => el.dataset.arenaWs));
  const poolMap = getPoolEntryMap();
  const eligible = (state.data?.workspaces || [])
    .filter((w) => w.isLaunchable)
    .slice()
    .sort((a, b) => {
      const aApi = a.client === "DirectApi" ? 2 : (poolMap.get(a.id)?.authReady ? 1 : 0);
      const bApi = b.client === "DirectApi" ? 2 : (poolMap.get(b.id)?.authReady ? 1 : 0);
      return bApi - aApi;
    });
  const directApiIds = eligible.filter((w) => w.client === "DirectApi" && poolMap.get(w.id)?.authReady).map((w) => w.id);

  const extraHtml = state.arenaExtraSlots
    .map((slot, idx) => {
      const wsOpts = eligible
        .map((w) => `<option value="${escapeHtml(w.id)}" ${w.id === slot.workspaceId ? "selected" : ""}>${escapeHtml(w.displayName)}</option>`)
        .join("");
      return (
        `<span class="arena-slot-chip">` +
          `<select class="arena-slot-effort" style="border-left:none" data-arena-extra-ws="${idx}">${wsOpts}</select>` +
          `<input class="arena-slot-input" data-arena-extra-model="${idx}" value="${escapeHtml(slot.model || "")}" placeholder="模型覆盖(可选)" />` +
          `<button type="button" class="btn btn-ghost btn-sm" style="padding:0 4px" data-arena-rm-extra="${idx}">✕</button>` +
        `</span>`
      );
    })
    .join("");

  picker.innerHTML =
    `<span style="font-size:12px;color:var(--text-secondary);font-weight:600">参赛后台账号：</span>` +
    eligible
      .map((w) => {
        const plat = formatPlatformShort(w.client);
        const isChecked = prevChecked.size > 0
          ? prevChecked.has(w.id)
          : directApiIds.slice(0, 3).includes(w.id);
        const savedModel = state.arenaSlotModels.get(w.id) ?? "";
        return (
          `<span class="arena-slot-chip">` +
            `<label style="display:inline-flex;align-items:center;gap:5px;cursor:pointer">` +
              `<input type="checkbox" data-arena-ws="${escapeHtml(w.id)}" ${isChecked ? "checked" : ""} />` +
              `<span class="plat-badge ${plat.cls}">${plat.code}</span>` +
              `<span>${escapeHtml(w.displayName)}</span>` +
            `</label>` +
            (isChecked
              ? `<input class="arena-slot-input" data-arena-model-for="${escapeHtml(w.id)}" value="${escapeHtml(savedModel)}" placeholder="${escapeHtml(w.defaultModel || "模型覆盖(可选)")}" title="模型覆盖(可选)" />`
              : "") +
          `</span>`
        );
      })
      .join("") +
    extraHtml +
    (eligible.length > 0
      ? `<button type="button" class="btn btn-ghost btn-sm" id="btn-arena-add-slot" title="可为同一账号添加多个不同模型并发竞技">＋ 同号跨模槽位</button>`
      : "");

  picker.querySelectorAll("[data-arena-ws]").forEach((cb) => {
    cb.onchange = () => renderArenaContestantPicker();
  });
  picker.querySelectorAll("[data-arena-model-for]").forEach((inp) => {
    inp.oninput = () => state.arenaSlotModels.set(inp.dataset.arenaModelFor, inp.value.trim());
  });
  picker.querySelectorAll("[data-arena-extra-ws]").forEach((sel) => {
    sel.onchange = () => {
      const slot = state.arenaExtraSlots[Number(sel.dataset.arenaExtraWs)];
      if (slot) slot.workspaceId = sel.value;
    };
  });
  picker.querySelectorAll("[data-arena-extra-model]").forEach((inp) => {
    inp.oninput = () => {
      const slot = state.arenaExtraSlots[Number(inp.dataset.arenaExtraModel)];
      if (slot) slot.model = inp.value.trim();
    };
  });
  picker.querySelectorAll("[data-arena-rm-extra]").forEach((btn) => {
    btn.onclick = () => {
      state.arenaExtraSlots.splice(Number(btn.dataset.arenaRmExtra), 1);
      renderArenaContestantPicker();
    };
  });
  document.getElementById("btn-arena-add-slot")?.addEventListener("click", () => {
    if (eligible.length === 0 || state.arenaExtraSlots.length >= 5) return;
    const firstChecked = picker.querySelector("[data-arena-ws]:checked")?.dataset.arenaWs || eligible[0].id;
    state.arenaExtraSlots.push({ workspaceId: firstChecked, model: "" });
    renderArenaContestantPicker();
  });
}

async function startArenaRace() {
  const repo = document.getElementById("arena-repo-input").value.trim();
  const prompt = document.getElementById("arena-prompt-input").value.trim();
  const verify = document.getElementById("arena-verify-input").value.trim();
  const checked = Array.from(document.querySelectorAll("[data-arena-ws]:checked"));
  const rawEntries = [
    ...checked.map((el) => {
      const wsId = el.dataset.arenaWs;
      const modelInp = document.querySelector(`[data-arena-model-for="${CSS.escape(wsId)}"]`);
      const modelVal = (modelInp?.value ?? state.arenaSlotModels.get(wsId) ?? "").trim();
      return { workspaceId: wsId, model: modelVal || null };
    }),
    ...state.arenaExtraSlots.map((s) => ({
      workspaceId: s.workspaceId,
      model: (s.model || "").trim() || null,
    })),
  ];
  if (rawEntries.length < 2) {
    throw new Error("请至少勾选或添加 2 个参赛槽位进行并行对比");
  }
  const contestants = rawEntries.slice(0, 6).map((entry, i) => ({
    label: `方案 ${String.fromCharCode(65 + i)}`,
    workspaceId: entry.workspaceId,
    model: entry.model,
  }));
  await rpc("arena.start", {
    title: prompt.slice(0, 26),
    repositoryDirectory: repo,
    prompt,
    contestants,
    verificationCommand: verify || null,
  });
  document.getElementById("arena-prompt-input").value = "";
  showToast("success", "🚀 已在后台创建各选手独立 Worktree 并启动并行开发！");
}

/* ==========================================================================
   7. View 3: Minimalist Linear-Grade Account & Quota Pool
   ========================================================================== */
function closeCardContextMenu() {
  state.cardMenuWsId = null;
  document.getElementById("card-context-menu")?.remove();
}

function openCardContextMenu(ws, pe, univ, anchorEl) {
  if (state.cardMenuWsId === ws.id) {
    closeCardContextMenu();
    return;
  }
  closeCardContextMenu();
  closeAccountPopover();
  state.cardMenuWsId = ws.id;

  const rect = anchorEl.getBoundingClientRect();
  const top = Math.min(window.innerHeight - 230, rect.bottom + 4);
  const left = Math.max(12, Math.min(window.innerWidth - 208, rect.right - 196));

  const menu = document.createElement("div");
  menu.id = "card-context-menu";
  menu.className = "card-menu-popover";
  menu.style.top = `${top}px`;
  menu.style.left = `${left}px`;

  const isDesktop = ws.hasDesktopClient !== false;
  menu.innerHTML = `
    ${isDesktop && ws.isLaunchable !== false ? `<button class="card-menu-item" id="cm-open-desktop">🖥 启动 / 前置桌面客户端</button>` : ""}
    <button class="card-menu-item" id="cm-refresh">↻ 刷新状态与配额</button>
    ${
      isDesktop
        ? `<button class="card-menu-item" id="cm-to-clientless">🔄 转为免客户端 CLI（保留原目录）</button>
           <button class="card-menu-item" id="cm-stealth">👻 后台静默启动守护</button>
           ${ws.visibleWindowCount > 0 ? `<button class="card-menu-item" id="cm-login">👻 收回窗口至后台</button>` : ""}`
        : `<button class="card-menu-item" id="cm-edit">⚙ 编辑模型 / 目录与密钥</button>
           <button class="card-menu-item" id="cm-term">⌨ 打开专属隔离终端</button>`
    }
    <button class="card-menu-item" id="cm-folder">📂 打开本地数据目录</button>
    ${isDesktop && ws.kind === "Managed" ? `<button class="card-menu-item" id="cm-rename">✎ 重命名账号</button>` : ""}
    <div class="card-menu-sep"></div>
    <button class="card-menu-item danger" id="cm-delete">🗑 ${ws.kind === "Original" ? "从列表移除（隐藏）" : "移除此账号"}</button>
  `;

  document.body.appendChild(menu);
  applyLanguageToRoot(menu);

  document.getElementById("cm-refresh").onclick = () => {
    closeCardContextMenu();
    rpc("pool.refreshUsage", { workspaceId: ws.id, all: false, force: true })
      .then((snap) => {
        if (snap) applySnapshot(snap, false);
        showToast("success", `✓ 已刷新“${ws.displayName}”状态`);
      })
      .catch((err) => showToast("error", err.message));
  };

  document.getElementById("cm-folder").onclick = () => {
    closeCardContextMenu();
    rpc("profile.openFolder", { workspaceId: ws.id }).catch((err) => showToast("error", err.message));
  };

  document.getElementById("cm-delete").onclick = () => {
    closeCardContextMenu();
    openRemoveAccountModal(ws, univ);
  };

  if (isDesktop) {
    const openDeskBtn = document.getElementById("cm-open-desktop");
    if (openDeskBtn) {
      openDeskBtn.onclick = () => {
        closeCardContextMenu();
        rpc("client.launch", { workspaceId: ws.id, stealth: false }).catch((err) => showToast("error", err.message));
      };
    }
    document.getElementById("cm-to-clientless").onclick = () => {
      closeCardContextMenu();
      const en = getUiLanguage() === "en";
      openConfirmModal({
        title: en ? `Convert to Clientless · ${ws.displayName}` : `转为免客户端模式 · ${ws.displayName}`,
        messageHtml: en
          ? `Convert <strong>${escapeHtml(ws.displayName)}</strong> to a lightweight clientless CLI agent? <strong>100% of your existing profile home & auth state will be preserved</strong> without opening any desktop IDE window.`
          : `将 <strong>${escapeHtml(ws.displayName)}</strong> 转换为免客户端轻量 CLI 引擎，<strong>100% 保留原有目录与工作上下文</strong>，后续彻底无需打开官方桌面客户端窗口。`,
        confirmText: en ? "🔄 Convert to Clientless" : "🔄 立即转为免客户端",
        danger: false,
        onConfirm: async () => {
          const snap = await rpc("workspace.convertToClientless", { workspaceId: ws.id });
          if (snap) applySnapshot(snap, false);
        },
      });
    };
    document.getElementById("cm-stealth").onclick = () => {
      closeCardContextMenu();
      rpc("client.launch", { workspaceId: ws.id, stealth: true }).catch((err) => showToast("error", err.message));
    };
    const cmHide = document.getElementById("cm-login");
    if (cmHide) {
      cmHide.onclick = () => {
        closeCardContextMenu();
        rpc("stealth.setLoginWindow", { workspaceId: ws.id, reveal: false }).catch((err) => showToast("error", err.message));
      };
    }
    const ren = document.getElementById("cm-rename");
    if (ren) {
      ren.onclick = () => {
        closeCardContextMenu();
        openRenameProfileModal(ws.id, ws.displayName);
      };
    }
  } else {
    document.getElementById("cm-edit").onclick = () => {
      closeCardContextMenu();
      if (univ) openAddProfileModal(univ.agentKind === "MarketCli" ? "cli" : "api", univ);
    };
    document.getElementById("cm-term").onclick = () => {
      closeCardContextMenu();
      switchView("command");
      state.terminalOpen = true;
      state.terminalAccountId = ws.id;
      renderTerminalDock();
      spawnTerminal("shell");
    };
  }
}

function openAntigravityGuardModal() {
  const en = getUiLanguage() === "en";
  showModal(
    en ? "Antigravity Multi-Account Isolation Guide" : "反重力（Antigravity）双开防串号隔离机制说明",
    en
      ? `
      <div style="font-size:12.5px;line-height:1.65;color:var(--text-secondary)">
        <p style="margin:0 0 10px 0;color:var(--text-primary);font-weight:600">How Hyperion keeps multiple Antigravity accounts 100% isolated</p>
        <p style="margin:0 0 10px 0">
          1. <strong>Default Official Account (<code>wincred</code>)</strong>: Your primary Antigravity installation keeps using your system <code>%USERPROFILE%\\.gemini</code> and Windows Credential Manager untouched.<br/>
          2. <strong>Managed Profiles (File-Based Token Sandbox)</strong>: Every managed Antigravity profile mounts an isolated <code>.gemini/antigravity</code> directory with native file-based token storage enabled—signing in or out of a managed desktop window or <code>agy acp</code> session never overwrites your primary <code>wincred</code> slot!
        </p>
        <div class="guard-alert-box">
          <strong>💡 Tip when upgrading an already-open window:</strong><br/>
          If a managed Antigravity desktop window was already running before this update, close that window once and launch it again from Hyperion so the new file-based credential environment is applied.
        </div>
      </div>
    `
      : `
      <div style="font-size:12.5px;line-height:1.65;color:var(--text-secondary)">
        <p style="margin:0 0 10px 0;color:var(--text-primary);font-weight:600">Hyperion 如何让多个反重力桌面端与 CLI 账号彻底分开？</p>
        <p style="margin:0 0 10px 0">
          1. <strong>原始反重力（保持系统默认）</strong>：您的主账号继续使用系统默认 <code>%USERPROFILE%\\.gemini</code> 与 Windows 凭据管理器（<code>wincred</code>），零侵入原环境。<br/>
          2. <strong>受管反重力分身（文件级独立凭据沙箱）</strong>：每个新建的受管反重力账号均独立挂载专属 <code>.gemini/antigravity</code> 目录并开启底层文件级 Token 存储，在受管桌面窗口或内置终端登录/退登新 Google 账号，绝不会覆盖或顶掉原始反重力及其他分身！
        </p>
        <div class="guard-alert-box">
          <strong>💡 旧窗口重启生效提示：</strong><br/>
          如果您在升级前已经打开了某个受管反重力桌面窗口，请先关闭该窗口再从 Hyperion 重新点开一次，即可注入全新文件级隔离环境。
        </div>
      </div>
    `,
    `
      <button class="btn" id="agm-close">我知道了</button>
      <button class="btn btn-primary" id="agm-add-gemini">🚀 添加免客户端 Gemini 独立号</button>
    `
  );
  document.getElementById("agm-close").onclick = closeModal;
  document.getElementById("agm-add-gemini").onclick = () => openAddProfileModal("api");
}

function renderFleetView() {
  const data = state.data;
  if (!data) return;

  const pool = data.pool || {};
  const stealth = data.stealth || {};
  const allWorkspaces = (data.workspaces || []).filter((w) => w.kind !== "ExternalUnresolved");
  const entryMap = new Map((pool.entries || []).map((e) => [e.workspaceId, e]));
  const univMap = new Map((data.universalAgents || []).map((u) => [u.workspaceId, u]));

  // 1. Clean Inline Header Stats (Zero bulky KPI boxes!)
  const avgRem = pool.averageRemainingPercent != null ? `${Math.round(pool.averageRemainingPercent)}%` : "—";
  const visWin = stealth.visibleClientWindows ?? 0;
  const statsEl = document.getElementById("fleet-inline-stats");
  if (statsEl) {
    statsEl.innerHTML = `
      <span class="fleet-stat-chip"><span class="pool-dot healthy"></span> 就绪 <strong>${pool.readyNodeCount ?? 0}/${pool.totalNodeCount ?? allWorkspaces.length}</strong></span>
      <span class="fleet-stat-chip">平均余量 <strong>${avgRem}</strong></span>
      <span class="fleet-stat-chip">累计流量 <strong>${formatTokensCompact(pool.totalSessionTokens || 0)}</strong> tok</span>
      <span class="fleet-stat-chip">${visWin === 0 ? `${icon("eyeOff", 12)} <span>桌面零弹窗</span>` : `${icon("eye", 12)} <span>${visWin} 窗可见</span>`}</span>
    `;
  }

  // 2. Clean Segmented Filter Tabs + Subtle Guard Link
  const isAgyWorkspace = (w) => w.client === "Antigravity" || entryMap.get(w.id)?.plan === "Gemini / AGY";

  const counts = {
    all: allWorkspaces.length,
    direct: allWorkspaces.filter((w) => w.hasDesktopClient === false).length,
    codex: allWorkspaces.filter((w) => w.client === "Codex").length,
    claude: allWorkspaces.filter((w) => w.client === "Claude").length,
    antigravity: allWorkspaces.filter(isAgyWorkspace).length,
  };

  const tabsDef = [
    { id: "all", label: "全部", count: counts.all },
    { id: "direct", label: "免客户端 API/CLI", count: counts.direct },
    { id: "codex", label: "Codex", count: counts.codex },
    { id: "claude", label: "Claude", count: counts.claude },
    { id: "antigravity", label: "反重力", count: counts.antigravity },
  ].filter((t) => t.id === "all" || t.id === "direct" || t.count > 0);

  const tabsEl = document.getElementById("fleet-seg-tabs");
  if (tabsEl) {
    tabsEl.innerHTML = tabsDef
      .map(
        (t) =>
          `<button class="fleet-seg-btn ${state.fleetFilter === t.id ? "active" : ""}" data-fleet-tab="${t.id}">` +
            `<span>${t.label}</span><span class="fleet-seg-count">${t.count}</span>` +
          `</button>`
      )
      .join("");
    tabsEl.querySelectorAll("[data-fleet-tab]").forEach((btn) => {
      btn.onclick = () => {
        state.fleetFilter = btn.dataset.fleetTab;
        renderFleetView();
      };
    });
  }

  const guardSlot = document.getElementById("fleet-guard-slot");
  if (guardSlot) {
    const hiddenCount = data.hiddenWorkspaceCount || 0;
    guardSlot.innerHTML =
      (hiddenCount > 0
        ? `<button class="btn btn-ghost btn-sm" id="btn-restore-hidden-ws" title="恢复之前从列表隐藏的系统原始账号">👁 恢复隐藏账号 (${hiddenCount})</button>`
        : "") +
      (counts.antigravity >= 2
        ? `<button class="btn btn-ghost btn-sm" id="btn-agy-guard-tip" style="color:var(--warning)">🛡 反重力双开防串号提示</button>`
        : "");
    document.getElementById("btn-agy-guard-tip")?.addEventListener("click", openAntigravityGuardModal);
    const restoreBtn = document.getElementById("btn-restore-hidden-ws");
    if (restoreBtn) {
      restoreBtn.onclick = () =>
        runButtonAction(restoreBtn, { busyText: "恢复中…", doneText: "✓ 已恢复" }, async () => {
          const snap = await rpc("workspace.restoreHidden");
          if (snap) applySnapshot(snap, false);
        });
    }
  }

  // 3. Filter & Render Clean Minimalist Account Cards
  const filtered = allWorkspaces.filter((w) => {
    if (state.fleetFilter === "direct" && w.hasDesktopClient !== false) return false;
    if (state.fleetFilter === "codex" && w.client !== "Codex") return false;
    if (state.fleetFilter === "claude" && w.client !== "Claude") return false;
    if (state.fleetFilter === "antigravity" && !isAgyWorkspace(w)) return false;
    if (state.fleetSearch) {
      const pe = entryMap.get(w.id);
      const hay = `${w.displayName} ${w.client} ${pe?.plan || ""} ${pe?.email || ""} ${w.subtitle || ""}`.toLowerCase();
      if (!hay.includes(state.fleetSearch)) return false;
    }
    return true;
  });

  const grid = document.getElementById("accounts-grid");
  if (filtered.length === 0) {
    grid.innerHTML = `
      <div style="grid-column:1/-1;text-align:center;padding:48px 20px;color:var(--text-muted);background:var(--bg-surface);border:1px dashed var(--border-subtle);border-radius:var(--radius-lg)">
        <div style="font-size:14px;font-weight:600;color:var(--text-secondary);margin-bottom:8px">当前分类暂无匹配账号</div>
        <button class="btn btn-primary btn-sm" id="btn-empty-add">＋ 添加免下载 API / CLI 或隔离账号</button>
      </div>`;
    document.getElementById("btn-empty-add")?.addEventListener("click", () => openAddProfileModal("api"));
  } else {
    grid.innerHTML = filtered
      .map((w) => {
        const pe = entryMap.get(w.id) || {};
        const univ = univMap.get(w.id) || null;
        const plat = formatPlatformShort(w.client, pe.plan);
        const pRem = pe.primaryRemainingPercent != null ? Math.round(pe.primaryRemainingPercent) : null;
        const sRem = pe.secondaryRemainingPercent != null ? Math.round(pe.secondaryRemainingPercent) : null;
        const pBarColor = pRem == null ? "var(--border-strong)" : pRem <= 12 ? "var(--danger)" : pRem <= 30 ? "var(--warning)" : "var(--success)";
        const sBarColor = sRem == null ? "var(--border-strong)" : sRem <= 12 ? "var(--danger)" : sRem <= 30 ? "var(--warning)" : "var(--accent-cyan)";

        const cardCls =
          pe.activeTasks > 0 ? "active-card" :
          pe.health === "Exhausted" ? "exhausted-card" : "";

        const cliPresetInfo = w.client === "MarketCli"
          ? (data.cliPresets || []).find((c) => c.presetId === univ?.presetId)
          : null;
        const cliBinaryReady = w.client === "MarketCli"
          ? !!(cliPresetInfo?.installed || univ?.customCliExecutable || univ?.presetId === "custom-cli")
          : w.client === "Antigravity"
          ? data.agyCliAvailable !== false
          : true;

        // Clean single top-right status pill
        let statusPillHtml = "";
        if (pe.activeTasks > 0) {
          statusPillHtml = `<span class="ac-status-pill ready">⚡ 执行中 (${pe.activeTasks})</span>`;
        } else if (pe.health === "Exhausted") {
          statusPillHtml = `<span class="ac-status-pill danger">● 已限流</span>`;
        } else if (pe.health === "Warning") {
          statusPillHtml = `<span class="ac-status-pill warn">● 余量 ${pRem}%</span>`;
        } else if (pe.authReady) {
          statusPillHtml = pRem != null && (w.client === "Codex" || pe.creditsUnlimited === false)
            ? `<span class="ac-status-pill ready">● 剩 ${pRem}%</span>`
            : `<span class="ac-status-pill ready">● 在线就绪</span>`;
        } else if (w.client === "DirectApi") {
          statusPillHtml = `<span class="ac-status-pill warn">○ 待配置密钥</span>`;
        } else {
          statusPillHtml = `<span class="ac-status-pill muted">○ ${(w.client === "MarketCli" || w.client === "Antigravity") && !cliBinaryReady ? "待装 CLI" : "待登录"}</span>`;
        }

        // Clean slim meters (honest billing/quota representation across all providers)
        let metersHtml = "";
        if (w.client === "Codex" || (pRem != null && (sRem != null || pe.primaryResetCountdown != null))) {
          metersHtml = `
            <div class="slim-meter-row">
              <span class="slim-meter-label">5h 额度</span>
              <div class="quota-bar-track"><div class="quota-bar-fill" style="width:${pRem ?? 0}%;background:${pBarColor}"></div></div>
              <span class="slim-meter-val">${pRem != null ? `<strong>${pRem}%</strong>${pe.primaryResetCountdown ? ` · ${escapeHtml(pe.primaryResetCountdown)}` : ""}` : "待同步"}</span>
            </div>
            ${w.client === "Codex" || sRem != null ? `
            <div class="slim-meter-row">
              <span class="slim-meter-label">7d 周度</span>
              <div class="quota-bar-track"><div class="quota-bar-fill" style="width:${sRem ?? 0}%;background:${sBarColor}"></div></div>
              <span class="slim-meter-val">${sRem != null ? `<strong>${sRem}%</strong>${pe.secondaryResetCountdown ? ` · ${escapeHtml(pe.secondaryResetCountdown)}` : ""}` : "—"}</span>
            </div>` : ""}
          `;
        } else if (pRem != null && pe.creditsUnlimited === false) {
          metersHtml = `
            <div class="slim-meter-row">
              <span class="slim-meter-label">预算余量</span>
              <div class="quota-bar-track"><div class="quota-bar-fill" style="width:${pRem}%;background:${pBarColor}"></div></div>
              <span class="slim-meter-val"><strong>${pRem}%</strong></span>
            </div>
          `;
        } else if (w.client === "DirectApi") {
          metersHtml = `
            <div class="slim-meter-row">
              <span class="slim-meter-label">${pe.creditsUnlimited ? "本地算力" : "API 通道"}</span>
              <div class="quota-bar-track"><div class="quota-bar-fill" style="width:${pe.authReady ? 100 : 18}%;background:${!pe.authReady ? "var(--warning)" : pe.creditsUnlimited ? "var(--success)" : "var(--accent-cyan)"}"></div></div>
              <span class="slim-meter-val"><strong>${!pe.authReady ? "待配置密钥" : pe.creditsUnlimited ? "离线免额" : "按量计费"}</strong></span>
            </div>
          `;
        } else {
          const quotaNote = !cliBinaryReady
            ? "待装 CLI · 订阅限额"
            : pe.authReady
            ? "按官方周期限额"
            : "待登录 · 订阅限额";
          metersHtml = `
            <div class="slim-meter-row" title="为保障零封号风险，Hyperion 绝不抓取桌面端内部私有接口；额度由官方按 5h/7d 订阅周期管控">
              <span class="slim-meter-label">订阅配额</span>
              <div class="quota-bar-track"><div class="quota-bar-fill" style="width:${pe.authReady ? 100 : 14}%;background:${pe.authReady ? "var(--accent-primary)" : "var(--border-strong)"}"></div></div>
              <span class="slim-meter-val">${quotaNote}</span>
            </div>
          `;
        }

        // Subtitle & Footer Meta (super clean 1-line!)
        const subText =
          pe.email ||
          ((pe.plan?.includes("AGY") || pe.plan === "Gemini / AGY") && pe.statusText
            ? pe.statusText
            : univ?.customHomeDirectory
            ? `免客户端 · 保留目录 ${univ.customHomeDirectory}`
            : w.hasDesktopClient === false
            ? "免客户端 · 独立隔离环境"
            : w.subtitle);
        const tokCount = (pe.sessionInputTokens || 0) + (pe.sessionOutputTokens || 0) || pe.lifetimeTokens || 0;
        const footMeta = `${pe.sessionTurns ?? 0} 轮 · ${formatTokensCompact(tokCount)} tok`;

        // At most ONE secondary contextual button + primary `@ 调度`
        let secondaryBtn = "";
        if (w.client === "DirectApi") {
          secondaryBtn = `<button class="btn btn-ghost btn-sm" data-ping-univ="${escapeHtml(w.id)}">⚡ 测速</button>`;
        } else if (w.client === "MarketCli" && !cliBinaryReady && univ?.presetId) {
          secondaryBtn = `<button class="btn btn-sm" data-install-cli="${escapeHtml(univ.presetId)}">⬇ 安装</button>`;
        } else if (w.client === "Antigravity" && !cliBinaryReady) {
          secondaryBtn = `<button class="btn btn-sm" data-install-cli="antigravity-cli">⬇ 安装</button>`;
        } else if (w.client === "MarketCli" && cliBinaryReady && !pe.authReady) {
          secondaryBtn = `<button class="btn btn-ghost btn-sm" data-cli-login="${escapeHtml(w.id)}" title="在内置隔离终端一键登录授权此账号">🔑 登录</button>`;
        } else if (w.hasDesktopClient !== false && w.isLaunchable !== false) {
          if (w.visibleWindowCount > 0) {
            secondaryBtn = `<button class="btn btn-ghost btn-sm" data-login-toggle-ws="${escapeHtml(w.id)}" data-revealed="1" title="将该桌面客户端窗口隐藏收纳至后台">👻 收回窗</button>`;
          } else if ((w.runningCount || 0) > 0) {
            secondaryBtn = `<button class="btn btn-ghost btn-sm" data-launch-ws="${escapeHtml(w.id)}" title="显现并前置后台运行中的桌面客户端窗口">👁 显示窗口</button>`;
          } else {
            secondaryBtn = `<button class="btn btn-ghost btn-sm" data-launch-ws="${escapeHtml(w.id)}" title="启动并打开该隔离账号的官方桌面客户端窗口">${!pe.authReady ? "🔑 登录启动" : "🖥 启动桌面端"}</button>`;
          }
        }

        return (
          `<div class="account-card ${cardCls}">` +
            `<div class="ac-head">` +
              `<div style="min-width:0">` +
                `<div class="ac-title-row">` +
                  `<span class="plat-badge ${plat.cls}">${plat.code}</span>` +
                  `<span class="ac-name" title="${escapeHtml(w.displayName)}">${escapeHtml(w.displayName)}</span>` +
                  (pe.plan ? `<span class="ac-model-label">${escapeHtml(pe.plan)}</span>` : "") +
                `</div>` +
                `<div class="ac-sub" title="${escapeHtml(subText)}">${escapeHtml(subText)}</div>` +
              `</div>` +
              `<div style="display:flex;align-items:center;gap:4px;flex-shrink:0">` +
                statusPillHtml +
                `<button class="card-more-btn" data-card-more="${escapeHtml(w.id)}" title="更多操作">⋯</button>` +
              `</div>` +
            `</div>` +
            `<div class="ac-meters">${metersHtml}</div>` +
            `<div class="ac-foot">` +
              `<span class="ac-foot-meta">${escapeHtml(footMeta)}</span>` +
              `<div class="ac-foot-actions">` +
                secondaryBtn +
                `<button class="btn btn-sm" data-studio-ws="${escapeHtml(w.id)}" title="在当前项目工作台中将此账号设为默认主力智能体">💬 设为当前主力</button>` +
                `<button class="btn btn-sm btn-primary" data-at-ws="${escapeHtml(w.id)}">@ 协作</button>` +
              `</div>` +
            `</div>` +
          `</div>`
        );
      })
      .join("");
  }

  grid.querySelectorAll("[data-studio-ws]").forEach((b) => {
    b.onclick = () => useWorkspaceInProject(b.dataset.studioWs);
  });
  grid.querySelectorAll("[data-at-ws]").forEach((b) => {
    b.onclick = () => mentionWorkspaceInCommandCenter(b.dataset.atWs);
  });
  grid.querySelectorAll("[data-card-more]").forEach((b) => {
    b.onclick = (ev) => {
      ev.stopPropagation();
      const ws = allWorkspaces.find((x) => x.id === b.dataset.cardMore);
      if (ws) openCardContextMenu(ws, entryMap.get(ws.id) || {}, univMap.get(ws.id) || null, b);
    };
  });
  grid.querySelectorAll("[data-ping-univ]").forEach((b) => {
    b.onclick = () => {
      const rec = univMap.get(b.dataset.pingUniv);
      if (!rec) return;
      return runButtonAction(b, { busyText: "测速…", doneText: "✓ 正常" }, () =>
        rpc("agent.testConnection", {
          id: rec.id,
          displayName: rec.displayName,
          agentKind: rec.agentKind,
          presetId: rec.presetId,
          protocol: rec.protocol,
          baseUrl: rec.baseUrl,
          model: rec.model,
        })
      );
    };
  });
  grid.querySelectorAll("[data-install-cli]").forEach((b) => {
    b.onclick = () =>
      runButtonAction(b, { busyText: "启动…", doneText: "✓ 终端中" }, async () => {
        const session = await rpc("agent.installCli", {
          presetId: b.dataset.installCli,
          workingDirectory: state.data?.projectRoot,
        });
        switchView("command");
        state.terminalOpen = true;
        if (session?.id) state.activeTerminalId = session.id;
        renderTerminalDock();
      });
  });
  grid.querySelectorAll("[data-cli-login]").forEach((b) => {
    b.onclick = () =>
      runButtonAction(b, { busyText: "打开终端…", doneText: "✓ 已打开" }, async () => {
        const wsId = b.dataset.cliLogin;
        switchView("command");
        state.terminalOpen = true;
        state.terminalAccountId = wsId;
        renderTerminalDock();
        await spawnTerminal("cli-login");
      });
  });
  grid.querySelectorAll("[data-launch-ws]").forEach((b) => {
    b.onclick = () =>
      runButtonAction(b, { busyText: "启动中…", doneText: "✓ 已启动" }, () =>
        rpc("client.launch", { workspaceId: b.dataset.launchWs, stealth: false })
      );
  });
  grid.querySelectorAll("[data-login-toggle-ws]").forEach((b) => {
    b.onclick = () => {
      const currentlyVisible = b.dataset.revealed === "1";
      return runButtonAction(b, { busyText: "切换…", doneText: "✓" }, () =>
        rpc("stealth.setLoginWindow", { workspaceId: b.dataset.loginToggleWs, reveal: !currentlyVisible })
      );
    };
  });

  // 4. Update Collapsed Advanced Diagnostics Summary & Table
  const instances = data.instances || [];
  const advText = document.getElementById("fleet-adv-summary-text");
  if (advText) {
    advText.textContent = `▸ 高级运维与底层守护进程（当前驻留进程 ${instances.length} 个 · ${data.sandboxStatusText || "沙箱就绪"}）`;
  }

  const warnEl = document.getElementById("fleet-warnings");
  warnEl.innerHTML = (data.warnings || [])
    .map((w) => `<div class="failover-banner">${escapeHtml(w)}</div>`)
    .join("");

  const tbody = document.getElementById("instances-tbody");
  if (instances.length === 0) {
    tbody.innerHTML = `<tr><td colspan="10" style="text-align:center;padding:20px;color:var(--text-muted)">当前无驻留客户端进程（直连 API 与无头 CLI 均为按需执行、零后台常驻）</td></tr>`;
    return;
  }
  tbody.innerHTML = instances
    .map((i) => {
      const plat = formatPlatformShort(i.client);
      return (
        `<tr>` +
          `<td style="font-weight:600"><span class="plat-badge ${plat.cls}">${plat.code}</span> ${escapeHtml(i.profileName)}</td>` +
          `<td>${escapeHtml(i.sourceText)}</td>` +
          `<td class="mono">${i.processId}</td>` +
          `<td><span class="tag ${i.state === "Running" ? "write" : ""}">${escapeHtml(i.stateText)}</span></td>` +
          `<td class="mono">${i.processCount}</td>` +
          `<td class="mono">${escapeHtml(i.uptimeText)}</td>` +
          `<td class="mono">${escapeHtml(i.cpuText)}</td>` +
          `<td class="mono">${escapeHtml(i.workingSetText)}</td>` +
          `<td><span class="tag ${i.isStealthHidden ? "worktree" : ""}">${escapeHtml(i.windowText)}</span></td>` +
          `<td style="text-align:right">` +
            (i.profileId
              ? `<button class="btn btn-sm" data-inst-stealth="${escapeHtml(i.profileId)}" data-hidden="${i.isStealthHidden ? "1" : "0"}">${i.isStealthHidden ? "👁 显现" : "👻 隐身"}</button> `
              : "") +
            `<button class="btn btn-sm" data-inst-act="close" data-pid="${i.processId}" ${i.isStopped ? "disabled" : ""}>退出</button> ` +
            `<button class="btn btn-danger btn-sm" data-inst-act="kill" data-pid="${i.processId}" ${i.isStopped ? "disabled" : ""}>强杀</button>` +
          `</td>` +
        `</tr>`
      );
    })
    .join("");

  tbody.querySelectorAll("[data-inst-stealth]").forEach((btn) => {
    btn.onclick = () => {
      const hidden = btn.dataset.hidden === "1";
      return runButtonAction(btn, { busyText: "处理…", doneText: "✓" }, () =>
        rpc("stealth.setLoginWindow", { workspaceId: btn.dataset.instStealth, reveal: hidden })
      );
    };
  });
  tbody.querySelectorAll("[data-inst-act]").forEach((btn) => {
    btn.onclick = async () => {
      const act = btn.dataset.instAct;
      const pid = Number(btn.dataset.pid);
      if (act === "kill") {
        openConfirmModal({
          title: "强制结束进程树",
          messageHtml: `确定强制结束进程树（根 PID <code>${pid}</code>）吗？`,
          confirmText: "⚡ 强制结束",
          danger: true,
          onConfirm: () => rpc(`client.${act}`, { processId: pid }),
        });
        return;
      }
      await runButtonAction(btn, { busyText: "结束中…", doneText: "✓" }, () => rpc(`client.${act}`, { processId: pid }));
    };
  });
}

/* ==========================================================================
   8. Modals
   ========================================================================== */
function closeModal() {
  document.getElementById("modal-root").innerHTML = "";
}

function openConfirmModal({ title, messageHtml, confirmText = "确认", danger = false, onConfirm }) {
  showModal(
    title,
    `<div style="font-size:13px;line-height:1.65;color:var(--text-primary)">${messageHtml}</div>`,
    `
      <button class="btn" id="mc-cancel">取消</button>
      <button class="btn ${danger ? "btn-danger" : "btn-primary"}" id="mc-ok">${escapeHtml(confirmText)}</button>
    `
  );
  document.getElementById("mc-cancel").onclick = closeModal;
  const okBtn = document.getElementById("mc-ok");
  okBtn.onclick = () =>
    runButtonAction(okBtn, { busyText: "执行中…", doneText: "✓ 已完成" }, async () => {
      await onConfirm();
      closeModal();
    });
}

function openRemoveAccountModal(ws, univ) {
  const isOriginal = ws.kind === "Original";
  const isDesktopManaged = ws.hasDesktopClient !== false && ws.kind === "Managed";
  const runningCount = (state.data?.instances || []).filter((i) => i.profileId === ws.id && !i.isStopped).length;
  const en = getUiLanguage() === "en";

  let bodyHtml = "";
  if (isOriginal) {
    bodyHtml = en
      ? `
      <div style="font-size:13px;line-height:1.65">
        Hide system default account <strong>${escapeHtml(ws.displayName)}</strong> from the Accounts list?
        <div style="margin-top:8px;font-size:11.5px;color:var(--text-secondary)">
          This only hides the entry in Hyperion and <strong>never deletes or modifies</strong> your original official config directory. You can restore hidden accounts anytime.
        </div>
      </div>`
      : `
      <div style="font-size:13px;line-height:1.65">
        确定将系统默认账号 <strong>${escapeHtml(ws.displayName)}</strong> 从账号池与指挥舱移除吗？
        <div style="margin-top:8px;font-size:11.5px;color:var(--text-secondary)">
          此操作仅在指挥舱内隐藏该条目，<strong>绝不删除或改动</strong>您电脑原有的官方配置目录，后续随时可点击账号池右上角「恢复隐藏账号」找回。
        </div>
      </div>`;
  } else if (isDesktopManaged) {
    bodyHtml = en
      ? `
      <div style="display:flex;flex-direction:column;gap:10px;font-size:13px;line-height:1.6">
        <div>Remove managed profile <strong>${escapeHtml(ws.displayName)}</strong>? It will also be removed from your <code>@</code> agent roster.</div>
        ${
          runningCount > 0
            ? `<div style="font-size:11.5px;color:var(--warning)">⚡ ${runningCount} active instance(s) detected. Checking the storage cleanup box below will stop associated processes first.</div>`
            : ""
        }
        <label style="display:flex;align-items:flex-start;gap:8px;cursor:pointer;padding:8px 10px;border:1px solid var(--border-subtle);border-radius:var(--radius-sm);background:var(--bg-input)">
          <input type="checkbox" id="rm-del-storage" style="margin-top:3px" />
          <div>
            <div style="font-weight:600;font-size:12px">Also permanently delete isolated folder (<code>data/profiles/${escapeHtml(ws.id)}</code>)</div>
            <div style="font-size:11px;color:var(--text-muted)">Unchecked by default to preserve your auth backup on disk.</div>
          </div>
        </label>
      </div>`
      : `
      <div style="display:flex;flex-direction:column;gap:10px;font-size:13px;line-height:1.6">
        <div>确定移除受管账号 <strong>${escapeHtml(ws.displayName)}</strong> 吗？移除后也会同步从指挥舱 <code>@</code> 名单中清理。</div>
        ${
          runningCount > 0
            ? `<div style="font-size:11.5px;color:var(--warning)">⚡ 检测到该账号当前有 ${runningCount} 个关联实例运行中，移除配置不会影响已打开的外部窗口；若勾选下方清理目录则会自动结束关联进程。</div>`
            : ""
        }
        <label style="display:flex;align-items:flex-start;gap:8px;cursor:pointer;padding:8px 10px;border:1px solid var(--border-subtle);border-radius:var(--radius-sm);background:var(--bg-input)">
          <input type="checkbox" id="rm-del-storage" style="margin-top:3px" />
          <div>
            <div style="font-weight:600;font-size:12px">同时彻底删除本地隔离目录（<code>data/profiles/${escapeHtml(ws.id)}</code>）</div>
            <div style="font-size:11px;color:var(--text-muted)">默认不勾选以保留登录态备份防误删；勾选后将彻底删除磁盘目录。</div>
          </div>
        </label>
      </div>`;
  } else {
    bodyHtml = en
      ? `
      <div style="font-size:13px;line-height:1.65">
        Remove agent <strong>${escapeHtml(ws.displayName)}</strong> and its locally encrypted key &amp; isolated folder?
      </div>`
      : `
      <div style="font-size:13px;line-height:1.65">
        确定移除智能体 <strong>${escapeHtml(ws.displayName)}</strong> 及其本机加密密钥与隔离目录吗？
      </div>`;
  }

  showModal(
    isOriginal ? `隐藏系统原始账号 · ${ws.displayName}` : `移除账号 · ${ws.displayName}`,
    bodyHtml,
    `
      <button class="btn" id="rm-acc-cancel">取消</button>
      <button class="btn btn-danger" id="rm-acc-confirm">🗑 ${isOriginal ? "确认隐藏" : "确认移除"}</button>
    `
  );

  document.getElementById("rm-acc-cancel").onclick = closeModal;
  const confirmBtn = document.getElementById("rm-acc-confirm");
  confirmBtn.onclick = () =>
    runButtonAction(confirmBtn, { busyText: "正在移除…", doneText: "✓ 已移除" }, async () => {
      let snap;
      if (isOriginal) {
        snap = await rpc("workspace.hide", { workspaceId: ws.id });
      } else if (isDesktopManaged) {
        const delStorage = !!document.getElementById("rm-del-storage")?.checked;
        snap = await rpc("profile.delete", { profileId: ws.id, deleteStorage: delStorage, stopRunning: true });
      } else {
        snap = await rpc("agent.delete", { id: univ?.id || ws.id });
      }
      if (snap) applySnapshot(snap, false);
      closeModal();
    });
}

function showModal(title, bodyHtml, footHtml, wide = false) {
  const root = document.getElementById("modal-root");
  root.innerHTML = `
    <div class="modal-backdrop" id="modal-backdrop">
      <div class="modal-panel ${wide ? "wide" : ""}">
        <div class="modal-head">
          <span>${escapeHtml(title)}</span>
          <button class="btn btn-ghost btn-sm" id="modal-close-x">✕</button>
        </div>
        <div class="modal-body">${bodyHtml}</div>
        <div class="modal-foot">${footHtml}</div>
      </div>
    </div>
  `;
  document.getElementById("modal-close-x").onclick = closeModal;
  document.getElementById("modal-backdrop").onclick = (e) => {
    if (e.target.id === "modal-backdrop") closeModal();
  };
}

function openCreateProjectModal() {
  const lastRepo = state.data?.settings?.lastRepositoryPath || state.data?.projectRoot || "";
  let defaultParent = lastRepo;
  const lastSep = Math.max(lastRepo.lastIndexOf("\\"), lastRepo.lastIndexOf("/"));
  if (lastSep > 2) {
    defaultParent = lastRepo.slice(0, lastSep);
  }

  const launchable = (state.data?.workspaces || []).filter((w) => w.isLaunchable);
  const poolMap = getPoolEntryMap();
  const currentPrimary = getCurrentPrimaryWsId();
  const agentOptions =
    launchable.length === 0
      ? `<option value="">稍后在底部选择智能体</option>`
      : launchable
          .map((w) => {
            const pe = poolMap.get(w.id);
            const plat = formatPlatformShort(pe?.client || w.client, pe?.plan);
            return `<option value="${escapeHtml(w.id)}" ${w.id === currentPrimary ? "selected" : ""}>[${plat.code}] ${escapeHtml(w.displayName)}</option>`;
          })
          .join("");

  showModal(
    "新建项目工作空间",
    `
      <div>
        <div style="font-weight:600;margin-bottom:5px">新项目文件夹名称</div>
        <input id="m-proj-name" class="input" placeholder="例如：my-saas-app 或 demo-agent-tool" value="my-new-project" />
      </div>
      <div>
        <div style="font-weight:600;margin-bottom:5px">存放父级目录</div>
        <div style="display:flex;gap:6px">
          <input id="m-proj-parent" class="input" value="${escapeHtml(defaultParent)}" />
          <button class="btn" id="m-pick-parent">浏览…</button>
        </div>
        <div id="m-proj-preview" class="mono" style="margin-top:5px;font-size:11px;color:var(--text-muted)"></div>
      </div>
      <div>
        <div style="font-weight:600;margin-bottom:5px">默认主力智能体（进入项目后可直接回车对话，也可随时输入 @ 多账号协作）</div>
        <select id="m-proj-agent" class="select">${agentOptions}</select>
      </div>
      <label style="display:flex;align-items:center;gap:8px;cursor:pointer;padding:8px 10px;border:1px solid var(--border-subtle);border-radius:var(--radius-sm);background:var(--bg-input)">
        <input type="checkbox" id="m-proj-git" checked />
        <div>
          <div style="font-weight:600">自动初始化 Git 仓库 (<code>git init -b main</code>)</div>
          <div style="font-size:11px;color:var(--text-muted)">方便右侧实时查看代码改动 Diff，并随时派生独立分支或竞技场赛马</div>
        </div>
      </label>
    `,
    `
      <button class="btn" id="m-cancel">取消</button>
      <button class="btn btn-primary" id="m-confirm-proj">创建并进入项目</button>
    `
  );

  const nameInput = document.getElementById("m-proj-name");
  const parentInput = document.getElementById("m-proj-parent");
  const previewEl = document.getElementById("m-proj-preview");
  const updatePreview = () => {
    const p = parentInput.value.trim().replace(/[\\/]+$/, "");
    const n = nameInput.value.trim();
    previewEl.textContent = p && n ? `将创建目录：${p}\\${n}` : "";
  };
  updatePreview();
  nameInput.addEventListener("input", updatePreview);
  parentInput.addEventListener("input", updatePreview);
  setTimeout(() => {
    nameInput.focus();
    nameInput.select();
  }, 30);

  document.getElementById("m-cancel").onclick = closeModal;
  document.getElementById("m-pick-parent").onclick = async () => {
    const res = await rpc("dialog.pickFolder", {
      initialDirectory: parentInput.value,
      title: "选择存放新项目的父级目录",
    });
    if (res?.path) {
      parentInput.value = res.path;
      updatePreview();
    }
  };

  const confirmBtn = document.getElementById("m-confirm-proj");
  confirmBtn.onclick = () =>
    runButtonAction(confirmBtn, { busyText: "创建项目中…", doneText: "✓ 已创建" }, async () => {
      const res = await rpc("project.create", {
        parentDirectory: parentInput.value.trim(),
        projectName: nameInput.value.trim(),
        initGit: document.getElementById("m-proj-git").checked,
        primaryWorkspaceId: document.getElementById("m-proj-agent").value || null,
      });
      closeModal();
      if (res?.state) applySnapshot(res.state, false);
      if (res?.projectPath) state.collapsedProjects.delete(normalizePathKey(res.projectPath));
      if (res?.roomId) selectRoom(res.roomId);
      document.getElementById("composer-input")?.focus();
    });
}

function openNewRoomModal() {
  const defaultDir = state.data?.settings?.lastRepositoryPath || state.data?.projectRoot || "";
  showModal(
    "新建全托管任务会话 / Git Worktree",
    `
      <div>
        <div style="font-weight:600;margin-bottom:5px">任务标题</div>
        <input id="m-room-title" class="input" value="新功能开发任务" />
      </div>
      <div>
        <div style="font-weight:600;margin-bottom:5px">项目或 Git 仓库目录</div>
        <div style="display:flex;gap:6px">
          <input id="m-room-dir" class="input" value="${escapeHtml(defaultDir)}" />
          <button class="btn" id="m-pick-dir">浏览…</button>
        </div>
      </div>
      <label style="display:flex;align-items:center;gap:8px;cursor:pointer;padding:8px 10px;border:1px solid var(--border-subtle);border-radius:var(--radius-sm);background:var(--bg-input)">
        <input type="checkbox" id="m-room-wt" checked />
        <div>
          <div style="font-weight:600">自动派生独立 Git Worktree 分支（推荐）</div>
          <div style="font-size:11px;color:var(--text-muted)">新任务自动接入全部可用后台 Agent，并在独立分支全功能读写</div>
        </div>
      </label>
      <div>
        <div style="font-weight:600;margin-bottom:5px">初始执行权限</div>
        <select id="m-room-mode" class="select">
          <option value="WorkspaceWrite">🛠 Agent 全功能读写（直接修改代码并运行命令）</option>
          <option value="ReadOnly">🔍 Ask 只读分析（仅分析方案，不改文件）</option>
        </select>
      </div>
    `,
    `
      <button class="btn" id="m-cancel">取消</button>
      <button class="btn btn-primary" id="m-confirm">创建任务</button>
    `
  );
  document.getElementById("m-cancel").onclick = closeModal;
  document.getElementById("m-pick-dir").onclick = async () => {
    const res = await rpc("dialog.pickFolder", { initialDirectory: document.getElementById("m-room-dir").value });
    if (res?.path) document.getElementById("m-room-dir").value = res.path;
  };
  const confirmBtn = document.getElementById("m-confirm");
  confirmBtn.onclick = () =>
    runButtonAction(confirmBtn, { busyText: "正在创建…", doneText: "✓ 已创建", successToast: "新任务会话已创建并接入全员后台 Agent" }, async () => {
      const created = await rpc("room.create", {
        title: document.getElementById("m-room-title").value.trim(),
        directory: document.getElementById("m-room-dir").value.trim(),
        createWorktree: document.getElementById("m-room-wt").checked,
        sandboxMode: document.getElementById("m-room-mode").value,
      });
      closeModal();
      if (created?.id) selectRoom(created.id);
    });
}

function openRoomSettingsModal(room) {
  const eligible = (state.data?.workspaces || []).filter((w) => w.isLaunchable);
  let draftAgents = (room.agents || []).map((a) => ({ ...a }));

  const renderRows = () => {
    const box = document.getElementById("m-agents-rows");
    if (!box) return;
    box.innerHTML = draftAgents
      .map((a, idx) => {
        const opts = eligible
          .map(
            (w) =>
              `<option value="${escapeHtml(w.id)}" ${w.id === a.workspaceId ? "selected" : ""}>${escapeHtml(w.displayName)} (${w.client})</option>`
          )
          .join("");
        const effVal = a.reasoningEffort || "";
        const fastVal = a.serviceTier || "";
        return (
          `<div style="display:grid;grid-template-columns:110px 1fr 125px 92px 76px 32px;gap:6px;align-items:center">` +
            `<input class="input" data-ag-alias="${idx}" value="${escapeHtml(a.alias)}" placeholder="@身份名" />` +
            `<select class="select" data-ag-ws="${idx}">${opts}</select>` +
            `<input class="input mono" data-ag-model="${idx}" value="${escapeHtml(a.model || "")}" placeholder="模型(可选)" />` +
            `<select class="select" data-ag-effort="${idx}" title="思考强度">` +
              `<option value="" ${!effVal ? "selected" : ""}>默认强度</option>` +
              `<option value="low" ${effVal === "low" ? "selected" : ""}>Low</option>` +
              `<option value="medium" ${effVal === "medium" ? "selected" : ""}>Medium</option>` +
              `<option value="high" ${effVal === "high" ? "selected" : ""}>High</option>` +
              `<option value="xhigh" ${effVal === "xhigh" ? "selected" : ""}>XHigh</option>` +
              `<option value="max" ${effVal === "max" ? "selected" : ""}>Max</option>` +
            `</select>` +
            `<select class="select" data-ag-tier="${idx}" title="推理速度档位">` +
              `<option value="" ${!fastVal ? "selected" : ""}>标准</option>` +
              `<option value="priority" ${fastVal === "priority" || fastVal === "fast" ? "selected" : ""}>⚡极速</option>` +
              `<option value="ultrafast" ${fastVal === "ultrafast" ? "selected" : ""}>⚡⚡超极速</option>` +
            `</select>` +
            `<button class="btn btn-ghost btn-sm" data-ag-del="${idx}">✕</button>` +
          `</div>`
        );
      })
      .join("");

    box.querySelectorAll("[data-ag-alias]").forEach((el) => {
      el.oninput = () => { draftAgents[Number(el.dataset.agAlias)].alias = el.value; };
    });
    box.querySelectorAll("[data-ag-ws]").forEach((el) => {
      el.onchange = () => { draftAgents[Number(el.dataset.agWs)].workspaceId = el.value; };
    });
    box.querySelectorAll("[data-ag-model]").forEach((el) => {
      el.oninput = () => { draftAgents[Number(el.dataset.agModel)].model = el.value; };
    });
    box.querySelectorAll("[data-ag-effort]").forEach((el) => {
      el.onchange = () => { draftAgents[Number(el.dataset.agEffort)].reasoningEffort = el.value || null; };
    });
    box.querySelectorAll("[data-ag-tier]").forEach((el) => {
      el.onchange = () => { draftAgents[Number(el.dataset.agTier)].serviceTier = el.value || null; };
    });
    box.querySelectorAll("[data-ag-del]").forEach((el) => {
      el.onclick = () => {
        draftAgents.splice(Number(el.dataset.agDel), 1);
        renderRows();
      };
    });
  };

  showModal(
    "任务会话编排与 @ 身份绑定（支持 Codex + Claude + 反重力混编）",
    `
      <div>
        <div style="font-weight:600;margin-bottom:4px">会话名称</div>
        <input id="ms-title" class="input" value="${escapeHtml(room.title)}" />
      </div>
      <div>
        <div style="font-weight:600;margin-bottom:4px">工作目录</div>
        <div style="display:flex;gap:6px">
          <input id="ms-dir" class="input" value="${escapeHtml(room.workingDirectory)}" />
          <button class="btn" id="ms-pick">浏览…</button>
        </div>
      </div>
      <div>
        <div style="font-weight:600;margin-bottom:4px">执行模式</div>
        <select id="ms-mode" class="select">
          <option value="WorkspaceWrite" ${room.sandboxMode === "WorkspaceWrite" ? "selected" : ""}>🛠 Agent 全功能读写（允许读写文件与执行命令）</option>
          <option value="ReadOnly" ${room.sandboxMode === "ReadOnly" ? "selected" : ""}>🔍 Ask 只读分析（仅分析代码）</option>
        </select>
      </div>
      <div>
        <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:6px">
          <span style="font-weight:600">参与身份（最多 8 个，支持 Codex / Claude / 反重力）</span>
          <button class="btn btn-sm" id="ms-add-agent">＋ 添加身份</button>
        </div>
        <div id="m-agents-rows" style="display:flex;flex-direction:column;gap:6px"></div>
      </div>
    `,
    `
      <button class="btn" id="ms-cancel">取消</button>
      <button class="btn btn-primary" id="ms-save">保存配置</button>
    `
  );

  renderRows();
  document.getElementById("ms-cancel").onclick = closeModal;
  document.getElementById("ms-pick").onclick = async () => {
    const res = await rpc("dialog.pickFolder", { initialDirectory: document.getElementById("ms-dir").value });
    if (res?.path) document.getElementById("ms-dir").value = res.path;
  };
  document.getElementById("ms-add-agent").onclick = () => {
    if (draftAgents.length >= 8 || eligible.length === 0) return;
    const nextWs = eligible[draftAgents.length % eligible.length];
    draftAgents.push({
      id: "",
      alias: `Agent${draftAgents.length + 1}`,
      workspaceId: nextWs.id,
      model: "",
    });
    renderRows();
  };
  const saveBtn = document.getElementById("ms-save");
  saveBtn.onclick = () =>
    runButtonAction(saveBtn, { busyText: "保存中…", doneText: "✓ 已保存", successToast: "会话身份与权限设置已保存" }, async () => {
      await rpc("room.configure", {
        roomId: room.id,
        title: document.getElementById("ms-title").value.trim(),
        directory: document.getElementById("ms-dir").value.trim(),
        sandboxMode: document.getElementById("ms-mode").value,
        agents: draftAgents,
      });
      closeModal();
    });
}

function openAddProfileModal(initialTab = "api", editRecord = null) {
  let activeTab = editRecord ? (editRecord.agentKind === "MarketCli" ? "cli" : "api") : (initialTab || "api");
  const apiPresets = state.data?.apiPresets || [];
  const cliPresets = state.data?.cliPresets || [];

  let selectedApiPresetId = editRecord?.agentKind === "DirectApi" ? editRecord.presetId : (apiPresets[0]?.id || "gemini");
  let selectedCliPresetId = editRecord?.agentKind === "MarketCli" ? editRecord.presetId : (cliPresets[0]?.presetId || "codex-cli");
  let desktopClient = "Codex";

  const renderWizard = () => {
    const apiPreset = apiPresets.find((p) => p.id === selectedApiPresetId) || apiPresets[0] || {
      id: "gemini",
      name: "Google Gemini 直连",
      badge: "Gemini API",
      protocol: "GeminiNative",
      defaultBaseUrl: "https://generativelanguage.googleapis.com",
      defaultModel: "gemini-2.5-pro",
      suggestedModels: ["gemini-2.5-pro", "gemini-2.5-flash"],
      requiresApiKey: true,
      description: "",
    };
    const cliPreset = cliPresets.find((p) => p.presetId === selectedCliPresetId) || cliPresets[0] || {
      presetId: "codex-cli",
      name: "OpenAI Codex CLI",
      badge: "Codex CLI",
      installed: false,
      installCommand: "npm install -g @openai/codex",
      loginHint: "",
      defaultModel: "",
      description: "",
    };

    const tabsHtml = editRecord
      ? ""
      : `<div class="onboard-tabs">
          <button class="onboard-tab ${activeTab === "api" ? "active" : ""}" data-wiz-tab="api">${icon("zap", 13)} <span>免下载直连 API 智能体（推荐）</span></button>
          <button class="onboard-tab ${activeTab === "cli" ? "active" : ""}" data-wiz-tab="cli">${icon("terminal", 13)} <span>轻量级全市场 CLI 引擎</span></button>
          <button class="onboard-tab ${activeTab === "desktop" ? "active" : ""}" data-wiz-tab="desktop">${icon("monitor", 13)} <span>官方订阅桌面客户端托管</span></button>
        </div>`;

    let contentHtml = "";
    let footHtml = "";

    if (activeTab === "api") {
      const defaultName = editRecord?.displayName || (getUiLanguage() === "en" ? `${apiPreset.badge.replace(/\s+/g, "")}-Primary` : `${apiPreset.badge.replace(/\s+/g, "")}主力号`);
      const defaultUrl = editRecord?.agentKind === "DirectApi" && editRecord.presetId === apiPreset.id ? editRecord.baseUrl : apiPreset.defaultBaseUrl;
      const defaultModel = editRecord?.agentKind === "DirectApi" && editRecord.presetId === apiPreset.id ? editRecord.model : apiPreset.defaultModel;
      const defaultProto = editRecord?.agentKind === "DirectApi" && editRecord.presetId === apiPreset.id ? editRecord.protocol : apiPreset.protocol;
      const defaultBudget = editRecord?.tokenBudgetLimit || "";

      const sidebarItemsHtml = apiPresets
        .map(
          (p) => `
          <div class="onboard-provider-item ${p.id === apiPreset.id ? "selected" : ""}" data-pick-api="${escapeHtml(p.id)}">
            <span style="font-size:12px;font-weight:700;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${escapeHtml(p.name)}</span>
            <span class="tag write" style="padding:0 5px;font-size:9.5px">${escapeHtml(p.badge)}</span>
          </div>`
        )
        .join("");

      const modelChips = (apiPreset.suggestedModels || [])
        .map((m) => `<button type="button" class="btn btn-ghost btn-sm" data-pick-model="${escapeHtml(m)}">${escapeHtml(m)}</button>`)
        .join("");

      const detailFormHtml = `
        <div class="onboard-detail-form">
          <div style="padding:8px 11px;border-radius:var(--radius-sm);background:var(--bg-input);border:1px solid var(--border-subtle);display:flex;align-items:center;justify-content:space-between;gap:8px">
            <div style="min-width:0">
              <div style="font-size:12.5px;font-weight:700">${escapeHtml(apiPreset.name)}</div>
              <div style="font-size:11px;color:var(--text-secondary);margin-top:1px">${escapeHtml(apiPreset.description)}</div>
            </div>
            <span class="tag write">${escapeHtml(apiPreset.badge)}</span>
          </div>

          <div>
            <div style="display:flex;align-items:center;justify-content:space-between;gap:6px;margin-bottom:4px">
              <span style="font-weight:600">API Key（Windows DPAPI 本机硬件级加密存储）</span>
              ${PORTAL_CONFIG.showApiGuideLink ? `<a href="#" id="ua-api-guide-link" style="font-size:11px;color:var(--accent-cyan);text-decoration:none;font-weight:600;display:inline-flex;align-items:center;gap:4px">${icon("key", 11)} <span>获取 API 密钥与算力通道指南 ↗</span></a>` : ""}
            </div>
            <div class="input-with-eye">
              <input id="ua-key" type="password" class="input mono" placeholder="${editRecord?.hasApiKey ? `已加密保存 (${editRecord.maskedApiKey})，留空保持不变` : apiPreset.requiresApiKey ? "粘贴 sk-... / AIza... 密钥（绝不明文落盘）" : "本地 Ollama 模型可留空"}" />
              <button type="button" class="eye-toggle-btn" id="ua-toggle-key-vis" title="显示 / 隐藏密钥">${icon("eye", 14)}</button>
            </div>
          </div>

          <div style="display:grid;grid-template-columns:1fr 1.15fr;gap:10px">
            <div>
              <div style="font-weight:600;margin-bottom:4px">智能体名称（指挥舱 @ 称呼）</div>
              <input id="ua-name" class="input" value="${escapeHtml(defaultName)}" placeholder="如：GeminiPro2 / DeepSeek架构师" />
            </div>
            <div>
              <div style="display:flex;align-items:center;justify-content:space-between;gap:6px;margin-bottom:4px">
                <span style="font-weight:600">模型标识 (Model ID)</span>
                <button type="button" class="btn btn-ghost btn-sm" id="ua-fetch-models-btn" style="padding:1px 6px;font-size:11px">↻ 实时拉取模型</button>
              </div>
              <input id="ua-model" list="ua-live-models-datalist" class="input mono" value="${escapeHtml(defaultModel)}" />
              <datalist id="ua-live-models-datalist"></datalist>
            </div>
          </div>

          <div id="ua-model-chips-row" style="display:flex;align-items:center;gap:6px;flex-wrap:wrap">${modelChips ? `<span style="font-size:11px;color:var(--text-muted)">推荐模型快选：</span>${modelChips}` : ""}</div>

          <div style="display:grid;grid-template-columns:1.25fr 1fr;gap:10px">
            <div>
              <div style="font-weight:600;margin-bottom:4px">API Base URL</div>
              <input id="ua-url" class="input mono" value="${escapeHtml(defaultUrl)}" />
            </div>
            <div>
              <div style="font-weight:600;margin-bottom:4px">通讯协议族</div>
              <select id="ua-proto" class="select">
                <option value="OpenAiCompatible" ${defaultProto === "OpenAiCompatible" ? "selected" : ""}>OpenAI 兼容协议 (/chat/completions)</option>
                <option value="GeminiNative" ${defaultProto === "GeminiNative" ? "selected" : ""}>Google Gemini 原生协议 (:generateContent)</option>
                <option value="AnthropicMessages" ${defaultProto === "AnthropicMessages" ? "selected" : ""}>Anthropic Messages 协议 (/v1/messages)</option>
              </select>
            </div>
          </div>

          <div>
            <div style="font-weight:600;margin-bottom:4px">Token 预算上限（可选，用于电量条与自动换号）</div>
            <input id="ua-budget" type="number" class="input mono" value="${escapeHtml(String(defaultBudget))}" placeholder="留空按服务商官方计费/限额，如填 2000000" />
          </div>
        </div>
      `;

      contentHtml = `
        ${tabsHtml}
        ${editRecord
          ? detailFormHtml
          : `<div class="onboard-master-detail">
               <div class="onboard-sidebar-list">${sidebarItemsHtml}</div>
               ${detailFormHtml}
             </div>`}
      `;

      footHtml = `
        <button class="btn" id="ua-cancel">取消</button>
        <button class="btn" id="ua-test-btn">⚡ 测试连接与延迟</button>
        <button class="btn btn-primary" id="ua-save-btn">${editRecord ? "保存智能体修改" : "✓ 立即接入并加入 @ 名单"}</button>
      `;
    } else if (activeTab === "cli") {
      const defaultName = editRecord?.displayName || (getUiLanguage() === "en" ? `${cliPreset.badge.replace(/\s+/g, "")}-Isolated` : `${cliPreset.badge.replace(/\s+/g, "")}独立号`);
      const cliSidebarHtml = cliPresets
        .map(
          (c) => `
          <div class="onboard-provider-item ${c.presetId === cliPreset.presetId ? "selected" : ""}" data-pick-cli="${escapeHtml(c.presetId)}">
            <span style="font-size:12px;font-weight:700;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${escapeHtml(c.name)}</span>
            <span class="tag ${c.installed ? "write" : "readonly"}" style="padding:0 5px;font-size:9.5px">${c.installed ? "✓ 本机已就绪" : "⬇ 可一键安装"}</span>
          </div>`
        )
        .join("");

      const cliDetailHtml = `
        <div class="onboard-detail-form">
          <div class="battery-box" style="flex-direction:row;align-items:center;justify-content:space-between;gap:10px;flex-wrap:wrap">
            <div>
              <div style="font-size:12px;font-weight:700">${cliPreset.installed ? `✅ 已检测到本机引擎：${escapeHtml(cliPreset.executablePath || cliPreset.badge)}` : `📦 尚未检测到 ${escapeHtml(cliPreset.badge)}，无需下载桌面客户端，可一键轻量安装：`}</div>
              <div class="mono" style="font-size:11px;color:var(--text-muted);margin-top:2px">${escapeHtml(cliPreset.installCommand)}</div>
            </div>
            ${
              cliPreset.presetId !== "custom-cli"
                ? `<button type="button" class="btn btn-sm ${cliPreset.installed ? "" : "btn-primary"}" id="ua-cli-install-btn">⬇ 在内置分屏终端一键安装 / 更新</button>`
                : ""
            }
          </div>
          <div style="display:grid;grid-template-columns:1fr 1fr;gap:10px">
            <div>
              <div style="font-weight:600;margin-bottom:4px">智能体名称（每个实例分配独立隔离 HOME 目录）</div>
              <input id="ua-cli-name" class="input" value="${escapeHtml(defaultName)}" />
            </div>
            <div>
              <div style="font-weight:600;margin-bottom:4px">指定模型（可选）</div>
              <input id="ua-cli-model" class="input mono" value="${escapeHtml(editRecord?.model ?? cliPreset.defaultModel ?? "")}" placeholder="留空使用该 CLI 默认模型" />
            </div>
          </div>
          ${
            cliPreset.presetId === "custom-cli" || editRecord?.customCliExecutable
              ? `<div style="display:grid;grid-template-columns:1fr 1.3fr;gap:10px">
                  <div>
                    <div style="font-weight:600;margin-bottom:4px">自定义 CLI 可执行路径 / 命令</div>
                    <input id="ua-cli-exe" class="input mono" value="${escapeHtml(editRecord?.customCliExecutable || "")}" placeholder="如 cursor-agent.cmd 或 C:\\tools\\my-agent.exe" />
                  </div>
                  <div>
                    <div style="font-weight:600;margin-bottom:4px">参数模板（支持 {prompt} {model} {cwd} {sandbox}）</div>
                    <input id="ua-cli-args" class="input mono" value="${escapeHtml(editRecord?.customCliArgsTemplate || "-p {prompt}")}" />
                  </div>
                </div>`
              : ""
          }
          <div style="display:grid;grid-template-columns:1.3fr 1fr;gap:10px">
            <div>
              <div style="font-weight:600;margin-bottom:4px">专属 API Key 覆盖（可选，DPAPI 加密注入该 CLI 进程）</div>
              <input id="ua-cli-key" type="password" class="input mono" placeholder="${editRecord?.hasApiKey ? `已加密保存 (${editRecord.maskedApiKey})，留空不变` : "留空则使用独立目录 OAuth 登录态"}" />
            </div>
            <div>
              <div style="font-weight:600;margin-bottom:4px">自定义网关 Base URL（可选）</div>
              <input id="ua-cli-url" class="input mono" value="${escapeHtml(editRecord?.baseUrl || "")}" placeholder="可选覆盖 API 端点" />
            </div>
          </div>
          <div>
            <div style="font-weight:600;margin-bottom:4px">保留或指定账号主目录（可选 · 用于迁移原客户端目录）</div>
            <input id="ua-cli-home" class="input mono" value="${escapeHtml(editRecord?.customHomeDirectory || "")}" placeholder="留空则自动创建独立隔离目录 data/universal-agents/..." />
          </div>
        </div>
      `;

      contentHtml = `
        ${tabsHtml}
        ${editRecord
          ? cliDetailHtml
          : `<div class="onboard-master-detail">
               <div class="onboard-sidebar-list">${cliSidebarHtml}</div>
               ${cliDetailHtml}
             </div>`}
      `;

      footHtml = `
        <button class="btn" id="ua-cancel">取消</button>
        <button class="btn btn-primary" id="ua-cli-save-btn">${editRecord ? "保存 CLI 智能体配置" : "✓ 创建独立隔离 CLI 智能体"}</button>
      `;
    } else {
      contentHtml = `
        ${tabsHtml}
        <div>
          <div style="font-weight:600;margin-bottom:5px">官方订阅桌面客户端（标准隔离目录 · 零侵入合规调用）</div>
          <select id="mp-client" class="select">
            <option value="Codex" ${desktopClient === "Codex" ? "selected" : ""}>Codex 桌面版（官方 CLI 执行 · 实时 5h/7d 剩余电量监控 · 自动换号接力）</option>
            <option value="Claude" ${desktopClient === "Claude" ? "selected" : ""}>Claude 桌面版（官方 Claude Code 引擎 · 独立 CLAUDE_CONFIG_DIR）</option>
            <option value="Antigravity" ${desktopClient === "Antigravity" ? "selected" : ""}>Antigravity 反重力（官方标准 agy acp 协议 · 零封号风险）</option>
          </select>
        </div>
        <div>
          <div style="font-weight:600;margin-bottom:5px">隔离账号名称（同时作为默认 @ 名字）</div>
          <input id="mp-name" class="input" value="${getUiLanguage() === "en" ? "Pro-Account" : "Pro订阅号"}" placeholder="例如：Codex3 / Claude架构号 / 反重力2号" />
        </div>
        <div class="guard-alert-box">
          ${
            getUiLanguage() === "en"
              ? `<strong>🛡 Official ACP Compliance & Account Safety:</strong><br/>
                 1. Antigravity integration strictly uses Google's official public <code>agy acp</code> (Agent Client Protocol over stdio JSON-RPC 2.0) — zero token extraction and zero undocumented private RPCs.<br/>
                 2. For high-concurrency parallel workflows or multi-account Google setups, we recommend
                 <button type="button" class="btn btn-sm" id="mp-go-gemini-api" style="margin:0 3px">🚀 Direct Gemini API</button>
                 or
                 <button type="button" class="btn btn-sm" id="mp-go-gemini-cli" style="margin:0 3px">⌨ Isolated Gemini CLI</button>.`
              : `<strong>🛡 官方标准 ACP 协议与账号零风险保护：</strong><br/>
                 1. 反重力接入严格采用 Google 官方公开的 <code>agy acp</code>（Agent Client Protocol 标准 JSON-RPC 2.0）通信，与 Zed / JetBrains 官方插件一致，绝不提取 Token 或调用未公开内部接口。<br/>
                 2. 若需高并发并行赛马或多 Google 账号独立并发，推荐优先使用上方
                 <button type="button" class="btn btn-sm" id="mp-go-gemini-api" style="margin:0 3px">🚀 免下载 Gemini 直连 API</button>
                 或
                 <button type="button" class="btn btn-sm" id="mp-go-gemini-cli" style="margin:0 3px">⌨ 隔离目录 Gemini CLI</button>。`
          }
        </div>
      `;

      footHtml = `
        <button class="btn" id="ua-cancel">取消</button>
        <button class="btn btn-primary" id="mp-create">创建桌面隔离账号</button>
      `;
    }

    showModal(
      editRecord ? `编辑智能体配置 · ${editRecord.displayName}` : "全市场智能体接入中心（免下载直连 API · 轻量级 CLI · 桌面后台托管）",
      contentHtml,
      footHtml,
      true
    );

    document.getElementById("ua-cancel").onclick = closeModal;

    document.querySelectorAll("[data-wiz-tab]").forEach((btn) => {
      btn.onclick = () => {
        activeTab = btn.dataset.wizTab;
        renderWizard();
      };
    });

    if (activeTab === "api") {
      document.querySelectorAll("[data-pick-api]").forEach((card) => {
        card.onclick = () => {
          selectedApiPresetId = card.dataset.pickApi;
          renderWizard();
        };
      });
      document.querySelectorAll("[data-pick-model]").forEach((chip) => {
        chip.onclick = () => {
          document.getElementById("ua-model").value = chip.dataset.pickModel;
        };
      });
      const apiGuideEl = document.getElementById("ua-api-guide-link");
      if (apiGuideEl) {
        apiGuideEl.onclick = (ev) => {
          ev.preventDefault();
          if (PORTAL_CONFIG.apiGuideUrl) {
            rpc("shell.openUrl", { url: PORTAL_CONFIG.apiGuideUrl }).catch((err) => showToast("error", err.message));
          }
        };
      }
      const toggleKeyVisBtn = document.getElementById("ua-toggle-key-vis");
      if (toggleKeyVisBtn) {
        toggleKeyVisBtn.onclick = () => {
          const keyEl = document.getElementById("ua-key");
          if (!keyEl) return;
          const show = keyEl.type === "password";
          keyEl.type = show ? "text" : "password";
          toggleKeyVisBtn.innerHTML = show ? icon("eyeOff", 14) : icon("eye", 14);
        };
      }

      const collectApiReq = () => ({
        id: editRecord?.id || null,
        displayName: document.getElementById("ua-name").value.trim(),
        agentKind: "DirectApi",
        presetId: apiPreset.id,
        protocol: document.getElementById("ua-proto").value,
        baseUrl: document.getElementById("ua-url").value.trim(),
        model: document.getElementById("ua-model").value.trim(),
        apiKeyPlaintext: document.getElementById("ua-key").value.trim() || null,
        tokenBudgetLimit: Number(document.getElementById("ua-budget").value) > 0 ? Number(document.getElementById("ua-budget").value) : null,
      });

      const fetchModelsBtn = document.getElementById("ua-fetch-models-btn");
      if (fetchModelsBtn) {
        fetchModelsBtn.onclick = () =>
          runButtonAction(fetchModelsBtn, { busyText: "查询中…", doneText: "✓ 已拉取" }, async () => {
            const cat = await rpc("models.probeDraft", collectApiReq());
            const models = cat?.models || [];
            const dl = document.getElementById("ua-live-models-datalist");
            if (dl) {
              dl.innerHTML = models.map((m) => `<option value="${escapeHtml(m.id)}">${escapeHtml(m.displayName || m.id)}</option>`).join("");
            }
            const chipsRow = document.getElementById("ua-model-chips-row");
            if (chipsRow && models.length > 0) {
              const topSlice = models.slice(0, 12);
              chipsRow.innerHTML =
                `<span style="font-size:11px;color:var(--accent-cyan);font-weight:600">远端可用 (${models.length})：</span>` +
                topSlice
                  .map((m) => `<button type="button" class="btn btn-ghost btn-sm" data-pick-model="${escapeHtml(m.id)}">${escapeHtml(m.id)}</button>`)
                  .join("");
              chipsRow.querySelectorAll("[data-pick-model]").forEach((chip) => {
                chip.onclick = () => {
                  document.getElementById("ua-model").value = chip.dataset.pickModel;
                };
              });
            }
          });
      }

      const testBtn = document.getElementById("ua-test-btn");
      testBtn.onclick = () =>
        runButtonAction(testBtn, { busyText: "测速验证中…", doneText: "✓ 连通正常" }, () =>
          rpc("agent.testConnection", collectApiReq())
        );

      const saveBtn = document.getElementById("ua-save-btn");
      saveBtn.onclick = () =>
        runButtonAction(saveBtn, { busyText: "加密保存中…", doneText: "✓ 已接入" }, async () => {
          const snap = await rpc("agent.upsert", collectApiReq());
          if (snap) applySnapshot(snap, false);
          closeModal();
        });
    } else if (activeTab === "cli") {
      document.querySelectorAll("[data-pick-cli]").forEach((card) => {
        card.onclick = () => {
          selectedCliPresetId = card.dataset.pickCli;
          renderWizard();
        };
      });

      const installBtn = document.getElementById("ua-cli-install-btn");
      if (installBtn) {
        installBtn.onclick = () =>
          runButtonAction(installBtn, { busyText: "启动终端安装…", doneText: "✓ 已在终端运行" }, async () => {
            const session = await rpc("agent.installCli", {
              presetId: cliPreset.presetId,
              workingDirectory: state.data?.projectRoot,
            });
            closeModal();
            switchView("command");
            state.terminalOpen = true;
            if (session?.id) state.activeTerminalId = session.id;
            renderTerminalDock();
          });
      }

      const cliSaveBtn = document.getElementById("ua-cli-save-btn");
      cliSaveBtn.onclick = () =>
        runButtonAction(cliSaveBtn, { busyText: "创建隔离环境中…", doneText: "✓ 已接入" }, async () => {
          const snap = await rpc("agent.upsert", {
            id: editRecord?.id || null,
            displayName: document.getElementById("ua-cli-name").value.trim(),
            agentKind: "MarketCli",
            presetId: cliPreset.presetId,
            protocol: "OpenAiCompatible",
            baseUrl: document.getElementById("ua-cli-url").value.trim() || null,
            model: document.getElementById("ua-cli-model").value.trim() || null,
            apiKeyPlaintext: document.getElementById("ua-cli-key").value.trim() || null,
            customCliExecutable: document.getElementById("ua-cli-exe")?.value.trim() || null,
            customCliArgsTemplate: document.getElementById("ua-cli-args")?.value.trim() || null,
            customHomeDirectory: document.getElementById("ua-cli-home")?.value.trim() || null,
          });
          if (snap) applySnapshot(snap, false);
          closeModal();
        });
    } else {
      const sel = document.getElementById("mp-client");
      sel.onchange = () => { desktopClient = sel.value; };
      document.getElementById("mp-go-gemini-api")?.addEventListener("click", () => {
        activeTab = "api";
        selectedApiPresetId = "gemini";
        renderWizard();
      });
      document.getElementById("mp-go-gemini-cli")?.addEventListener("click", () => {
        activeTab = "cli";
        selectedCliPresetId = "gemini-cli";
        renderWizard();
      });

      const createBtn = document.getElementById("mp-create");
      createBtn.onclick = () =>
        runButtonAction(createBtn, { busyText: "创建中…", doneText: "✓ 已接入" }, async () => {
          const snap = await rpc("profile.create", {
            displayName: document.getElementById("mp-name").value.trim(),
            client: document.getElementById("mp-client").value,
          });
          if (snap) applySnapshot(snap, false);
          closeModal();
        });
    }
  };

  renderWizard();
}

function openRenameProfileModal(profileId, currentName) {
  showModal(
    "重命名隔离账号",
    `<div><div style="font-weight:600;margin-bottom:5px">新名称</div><input id="mr-name" class="input" value="${escapeHtml(currentName)}" /></div>`,
    `<button class="btn" id="mr-cancel">取消</button><button class="btn btn-primary" id="mr-save">保存</button>`
  );
  document.getElementById("mr-cancel").onclick = closeModal;
  const saveBtn = document.getElementById("mr-save");
  saveBtn.onclick = () =>
    runButtonAction(saveBtn, { busyText: "保存…", doneText: "✓" }, async () => {
      const snap = await rpc("profile.rename", { profileId, displayName: document.getElementById("mr-name").value.trim() });
      if (snap) applySnapshot(snap, false);
      closeModal();
    });
}

function openArenaDiffModal(diffSnapshot) {
  showModal(
    `参赛方案代码变更 (${diffSnapshot.totalFiles} 文件, +${diffSnapshot.totalAdditions} / -${diffSnapshot.totalDeletions})`,
    `<div style="max-height:60vh;overflow:auto">${renderDiffFilesHtml(diffSnapshot, { mode: "unified", allowComments: false })}</div>`,
    `<button class="btn btn-primary" id="mdiff-close">关闭</button>`
  );
  document.getElementById("mdiff-close").onclick = closeModal;
}

/* ==========================================================================
   8.5 Dedicated Single-Agent Studio & Live Working Status Inspector
   ========================================================================== */
function getStudioEligibleWorkspaces() {
  return (state.data?.workspaces || []).filter((w) => w.kind !== "ExternalUnresolved" && w.isLaunchable);
}

function getDedicatedRoomsForWorkspace(wsId) {
  if (!wsId) return [];
  return (state.data?.rooms || []).filter((r) => r.isDedicatedAgentRoom && r.dedicatedWorkspaceId === wsId);
}

function getCurrentStudioRoom() {
  const dedicated = getDedicatedRoomsForWorkspace(state.studioWsId);
  return dedicated.find((r) => r.id === state.studioRoomId) || dedicated[dedicated.length - 1] || null;
}

function formatElapsedCompact(sinceIso) {
  if (!sinceIso) return "00:01";
  const sec = Math.max(1, Math.floor((Date.now() - new Date(sinceIso).getTime()) / 1000));
  const m = String(Math.floor(sec / 60)).padStart(2, "0");
  const s = String(sec % 60).padStart(2, "0");
  return `${m}:${s}`;
}

setInterval(() => {
  if (state.activeView !== "studio") return;
  document.querySelectorAll("[data-live-since]").forEach((el) => {
    el.textContent = `耗时 ${formatElapsedCompact(el.dataset.liveSince)}`;
  });
}, 1000);

async function openStudioForWorkspace(wsId) {
  if (wsId) state.studioWsId = wsId;
  state.studioMode = "chat";
  switchView("studio");
}

async function ensureStudioRoomExists(wsId) {
  if (!wsId || state.studioEnsuringSet.has(wsId)) return;
  const existing = getDedicatedRoomsForWorkspace(wsId);
  if (existing.length > 0) return;

  state.studioEnsuringSet.add(wsId);
  try {
    const sharedCwd = getSharedRooms()[0]?.workingDirectory || state.data?.settings?.lastRepositoryPath || state.data?.projectRoot;
    const room = await rpc("studio.ensureRoom", { workspaceId: wsId, directory: sharedCwd });
    if (room?.id && state.data?.rooms) {
      if (!state.data.rooms.some((r) => r.id === room.id)) state.data.rooms.push(room);
      if (state.studioWsId === wsId) state.studioRoomId = room.id;
      if (state.activeView === "studio") renderStudioView();
    }
  } catch (err) {
    showToast("error", err.message);
  } finally {
    state.studioEnsuringSet.delete(wsId);
  }
}

function selectStudioWorkspace(wsId) {
  const input = document.getElementById("studio-composer-input");
  if (state.studioRoomId && input) state.studioDrafts.set(state.studioRoomId, input.value);
  state.studioWsId = wsId;
  const rooms = getDedicatedRoomsForWorkspace(wsId);
  state.studioRoomId = rooms.length > 0 ? rooms[rooms.length - 1].id : null;
  if (input) input.value = (state.studioRoomId && state.studioDrafts.get(state.studioRoomId)) || "";
  renderStudioView(true);
  if (rooms.length === 0) ensureStudioRoomExists(wsId);
}

function selectStudioRoom(roomId) {
  const input = document.getElementById("studio-composer-input");
  if (state.studioRoomId && input) state.studioDrafts.set(state.studioRoomId, input.value);
  state.studioRoomId = roomId;
  if (input) input.value = state.studioDrafts.get(roomId) || "";
  renderStudioView(true);
}

function createNewStudioThread() {
  const ws = getStudioEligibleWorkspaces().find((w) => w.id === state.studioWsId);
  if (!ws) return;
  const curRoom = getCurrentStudioRoom();
  const defaultDir = curRoom?.repositoryRoot || curRoom?.workingDirectory || state.data?.settings?.lastRepositoryPath || state.data?.projectRoot || "";
  const seq = getDedicatedRoomsForWorkspace(ws.id).length + 1;

  showModal(
    `新建专属任务 · ${ws.displayName}`,
    `
      <div>
        <div style="font-weight:600;margin-bottom:5px">任务标题</div>
        <input id="st-new-title" class="input" value="${escapeHtml(ws.displayName)} · 任务 ${seq}" />
      </div>
      <div>
        <div style="font-weight:600;margin-bottom:5px">工作目录</div>
        <div style="display:flex;gap:6px">
          <input id="st-new-dir" class="input" value="${escapeHtml(defaultDir)}" />
          <button class="btn" id="st-pick-dir">浏览…</button>
        </div>
      </div>
      <label style="display:flex;align-items:center;gap:8px;cursor:pointer;padding:8px 10px;border:1px solid var(--border-subtle);border-radius:var(--radius-sm);background:var(--bg-input)">
        <input type="checkbox" id="st-new-wt" />
        <div>
          <div style="font-weight:600">为此任务派生独立 Git Worktree 分支</div>
          <div style="font-size:11px;color:var(--text-muted)">勾选后将在隔离分支上改代码，不勾选则直接在所选目录工作</div>
        </div>
      </label>
    `,
    `
      <button class="btn" id="st-cancel">取消</button>
      <button class="btn btn-primary" id="st-create">创建专属对话</button>
    `
  );
  document.getElementById("st-cancel").onclick = closeModal;
  document.getElementById("st-pick-dir").onclick = async () => {
    const res = await rpc("dialog.pickFolder", { initialDirectory: document.getElementById("st-new-dir").value });
    if (res?.path) document.getElementById("st-new-dir").value = res.path;
  };
  const createBtn = document.getElementById("st-create");
  createBtn.onclick = () =>
    runButtonAction(createBtn, { busyText: "创建中…", doneText: "✓ 已创建" }, async () => {
      const created = await rpc("room.create", {
        title: document.getElementById("st-new-title").value.trim(),
        directory: document.getElementById("st-new-dir").value.trim(),
        createWorktree: document.getElementById("st-new-wt").checked,
        sandboxMode: "WorkspaceWrite",
        dedicatedWorkspaceId: ws.id,
      });
      closeModal();
      if (created?.id) selectStudioRoom(created.id);
    });
}

async function sendCurrentStudioMessage() {
  const room = getCurrentStudioRoom();
  if (!room || room.isRunning) return;
  const input = document.getElementById("studio-composer-input");
  const text = input.value.trim();
  if (!text) {
    showToast("warning", "请输入要交给该智能体执行的任务");
    input.focus();
    return;
  }
  input.value = "";
  state.studioDrafts.delete(room.id);
  try {
    await rpc("room.send", { roomId: room.id, text });
  } catch (err) {
    input.value = text;
    showToast("error", err.message);
  }
}

async function spawnStudioTui(kind = "native") {
  const ws = getStudioEligibleWorkspaces().find((w) => w.id === state.studioWsId);
  if (!ws) return;
  const room = getCurrentStudioRoom();
  const cwd = room?.workingDirectory || state.data?.projectRoot || "";

  let cmdKind = "shell";
  if (kind === "native") {
    if (ws.client === "Codex") cmdKind = "codex";
    else if (ws.client === "Claude") cmdKind = "claude";
    else if (ws.client === "MarketCli") cmdKind = "cli-login";
  }

  state.studioMode = "tui";
  const stage = document.getElementById("studio-tui-stage");
  try {
    const session = await rpc("terminal.create", {
      title: `${ws.displayName} · 专舱终端`,
      workspaceId: ws.id,
      workingDirectory: cwd,
      commandKind: cmdKind,
      cols: 120,
      rows: 32,
    });
    if (session?.id) {
      terminalMgr.ensureSession(session.id);
      state.studioTuiMap.set(ws.id, session.id);
      renderStudioView();
      setTimeout(() => terminalMgr.mountStage(stage, session.id), 40);
    }
  } catch (err) {
    showToast("error", err.message);
  }
}

async function refreshStudioDiff(force = false) {
  const room = getCurrentStudioRoom();
  if (!room?.workingDirectory) return;
  const dir = room.workingDirectory;
  if (!force && state.studioDiffMap.has(dir)) {
    renderStudioFileChanges(state.studioDiffMap.get(dir));
    return;
  }
  try {
    const snap = await rpc("git.diff", { directory: dir });
    state.studioDiffMap.set(dir, snap);
    if (state.activeView === "studio" && getCurrentStudioRoom()?.workingDirectory === dir) {
      renderStudioFileChanges(snap);
    }
  } catch {
    renderStudioFileChanges(null);
  }
}

function renderStudioFileChanges(diffSnap) {
  const box = document.getElementById("studio-file-list");
  if (!box) return;
  if (!diffSnap || !diffSnap.isGitRepository) {
    box.innerHTML = `<div style="font-size:11.5px;color:var(--text-muted);padding:4px 2px">当前目录未初始化 Git 仓库</div>`;
    return;
  }
  const files = diffSnap.files || [];
  if (files.length === 0) {
    box.innerHTML = `<div style="font-size:11.5px;color:var(--text-muted);padding:4px 2px">工作区干净 · 暂无未提交文件改动</div>`;
    return;
  }
  box.innerHTML = files
    .map(
      (f) => `
      <div class="studio-file-row" data-st-diff-file="${encodeURIComponent(f.filePath)}" title="点击查看 ${escapeHtml(f.filePath)} 代码对比">
        <span class="mono" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${escapeHtml(f.filePath)}</span>
        <span class="mono" style="flex-shrink:0;font-size:11px">
          <span style="color:var(--success)">+${f.additions}</span>
          <span style="color:var(--danger);margin-left:3px">-${f.deletions}</span>
        </span>
      </div>`
    )
    .join("");

  box.querySelectorAll("[data-st-diff-file]").forEach((row) => {
    row.onclick = () => {
      const fp = decodeURIComponent(row.dataset.stDiffFile);
      const singleSnap = { ...diffSnap, files: files.filter((x) => x.filePath === fp) };
      openArenaDiffModal(singleSnap);
    };
  });
}

function renderStudioView(forceScroll = false) {
  const workspaces = getStudioEligibleWorkspaces();
  const poolMap = getPoolEntryMap();
  const univMap = new Map((state.data?.universalAgents || []).map((u) => [u.workspaceId, u]));

  if (!state.studioWsId || !workspaces.some((w) => w.id === state.studioWsId)) {
    state.studioWsId = workspaces[0]?.id || null;
  }

  const agentListEl = document.getElementById("studio-agent-list");
  if (workspaces.length === 0) {
    agentListEl.innerHTML = `<div style="padding:12px;font-size:12px;color:var(--text-muted)">暂无可用智能体，请点击右上角 ＋ 接入</div>`;
    return;
  }

  agentListEl.innerHTML = workspaces
    .map((w) => {
      const pe = poolMap.get(w.id) || {};
      const plat = formatPlatformShort(w.client);
      const selected = w.id === state.studioWsId;
      const working = (pe.activeTasks || 0) > 0;
      const rem = pe.primaryRemainingPercent != null ? Math.round(pe.primaryRemainingPercent) : null;

      const badgeHtml = working
        ? `<span class="studio-state-pill working"><span class="pulse-dot"></span>工作中</span>`
        : pe.authReady
        ? `<span class="studio-state-pill idle">● 待命</span>`
        : `<span class="studio-state-pill muted">○ 待登录</span>`;

      const subLine = working
        ? `⚡ ${pe.currentActivity || "正在执行任务…"}`
        : w.client === "Codex" && rem != null
        ? `5h 剩余 ${rem}% · ${pe.lastActivity || "随时响应"}`
        : pe.lastActivity || (pe.authReady ? "无头引擎在线待命" : "点击进入完成一次登录授权");

      return `
        <div class="studio-agent-card ${selected ? "active" : ""} ${working ? "working" : ""}" data-pick-studio-ws="${escapeHtml(w.id)}">
          <div class="studio-agent-top">
            <div style="display:flex;align-items:center;gap:6px;min-width:0">
              <span class="plat-badge ${plat.cls}">${plat.code}</span>
              <span class="studio-agent-name">${escapeHtml(w.displayName)}</span>
            </div>
            ${badgeHtml}
          </div>
          <div class="studio-agent-sub" title="${escapeHtml(subLine)}">${escapeHtml(subLine)}</div>
        </div>`;
    })
    .join("");

  agentListEl.querySelectorAll("[data-pick-studio-ws]").forEach((el) => {
    el.onclick = () => selectStudioWorkspace(el.dataset.pickStudioWs);
  });

  const ws = workspaces.find((w) => w.id === state.studioWsId);
  if (!ws) return;

  const dedicatedRooms = getDedicatedRoomsForWorkspace(ws.id);
  if (dedicatedRooms.length === 0) {
    ensureStudioRoomExists(ws.id);
  } else if (!state.studioRoomId || !dedicatedRooms.some((r) => r.id === state.studioRoomId)) {
    state.studioRoomId = dedicatedRooms[dedicatedRooms.length - 1].id;
  }

  document.getElementById("studio-threads-label").textContent = `${ws.displayName} · 对话 (${dedicatedRooms.length})`;
  const threadListEl = document.getElementById("studio-thread-list");
  threadListEl.innerHTML = dedicatedRooms
    .map((r) => {
      const active = r.id === state.studioRoomId;
      const wtTag = r.isIsolatedWorktree ? `<span class="tag worktree">⑂ ${escapeHtml(r.worktreeBranch)}</span>` : "";
      return `
        <div class="session-item ${active ? "active" : ""}" data-pick-studio-room="${escapeHtml(r.id)}">
          <div class="session-item-top">
            <span class="session-item-title">${escapeHtml(r.title)}</span>
            ${r.isRunning ? `<span class="pulse-dot" style="flex-shrink:0"></span>` : `<span style="font-size:10.5px;color:var(--text-muted);white-space:nowrap;flex-shrink:0">${r.messages?.length || 0} 条</span>`}
          </div>
          <div class="session-item-meta">
            <span class="tag ${r.sandboxMode === "WorkspaceWrite" ? "write" : "readonly"}">${r.sandboxMode === "WorkspaceWrite" ? "读写" : "只读"}</span>
            ${wtTag}
          </div>
        </div>`;
    })
    .join("");

  threadListEl.querySelectorAll("[data-pick-studio-room]").forEach((el) => {
    el.onclick = () => selectStudioRoom(el.dataset.pickStudioRoom);
  });

  const room = getCurrentStudioRoom();
  const pe = poolMap.get(ws.id) || {};
  const univ = univMap.get(ws.id) || null;
  const plat = formatPlatformShort(ws.client);
  const isWorking = (pe.activeTasks || 0) > 0 || !!room?.isRunning;

  // Render Studio Header
  const headerEl = document.getElementById("studio-header");
  const shortFolder = (room?.workingDirectory || "").split(/[\\/]/).filter(Boolean).pop() || room?.workingDirectory || "";
  const locPill = room
    ? room.isIsolatedWorktree
      ? `<span class="tag worktree" id="st-open-cwd" style="cursor:pointer" title="点击打开工作目录：${escapeHtml(room.workingDirectory)}">${icon("branch", 11)} <span>${escapeHtml(room.worktreeBranch)}</span></span>`
      : shortFolder
      ? `<span class="tag" id="st-open-cwd" style="cursor:pointer" title="点击打开工作目录：${escapeHtml(room.workingDirectory)}">${icon("folder", 11)} <span>${escapeHtml(shortFolder)}</span></span>`
      : ""
    : "";

  const liveTopPill = isWorking
    ? `<span class="studio-state-pill working"><span class="pulse-dot"></span>工作中</span>`
    : pe.authReady
    ? `<span class="studio-state-pill idle">● 待命</span>`
    : `<span class="studio-state-pill muted">○ 待登录</span>`;

  const wtBtnHtml = room
    ? room.isIsolatedWorktree
      ? `<button class="btn btn-success btn-sm" id="st-merge-wt" ${room.isRunning ? "disabled" : ""}>${icon("merge", 12)} <span>合并</span></button>`
      : `<button class="btn btn-sm" id="st-spawn-wt" ${room.isRunning ? "disabled" : ""}>${icon("branch", 12)} <span>分支</span></button>`
    : "";

  headerEl.innerHTML = `
    <div class="room-header-main">
      <div class="room-title-group">
        <span class="plat-badge ${plat.cls}">${plat.code}</span>
        <span class="room-title">${escapeHtml(ws.displayName)}</span>
        ${liveTopPill}
        ${locPill}
        ${room ? `<button class="btn btn-ghost btn-sm" id="st-switch-cwd" title="切换此对话的工作目录" ${room.isRunning ? "disabled" : ""}>${icon("folderOpen", 13)}</button>` : ""}
      </div>
      <div class="room-toolbar">
        <div class="mode-segmented">
          <button class="mode-seg-btn write ${state.studioMode === "chat" ? "active" : ""}" id="st-view-chat">${icon("message", 11)} <span>对话</span></button>
          <button class="mode-seg-btn write ${state.studioMode === "tui" ? "active" : ""}" id="st-view-tui">${icon("terminal", 11)} <span>终端</span></button>
        </div>
        ${wtBtnHtml}
        <button class="btn btn-sm ${state.studioInspectorOpen ? "btn-primary" : ""}" id="st-toggle-insp" title="展开或收起右侧实时工作状态面板">${icon("activity", 12)} <span>状态</span></button>
        ${dedicatedRooms.length > 1 && room ? `<button class="btn btn-ghost btn-sm" id="st-del-room" ${room.isRunning ? "disabled" : ""} title="删除此专属对话">${icon("trash", 13)}</button>` : ""}
      </div>
    </div>
  `;

  document.getElementById("st-open-cwd")?.addEventListener("click", () => {
    if (room) rpc("profile.openFolder", { targetPath: room.workingDirectory }).catch((e) => showToast("error", e.message));
  });
  document.getElementById("st-switch-cwd")?.addEventListener("click", async () => {
    if (!room) return;
    const picked = await rpc("dialog.pickFolder", { initialDirectory: room.workingDirectory, title: `选择 ${ws.displayName} 的工作目录` });
    if (picked?.path) {
      await rpc("room.configure", {
        roomId: room.id,
        title: room.title,
        directory: picked.path,
        sandboxMode: room.sandboxMode,
        agents: room.agents,
      });
      refreshStudioDiff(true);
    }
  });
  document.getElementById("st-view-chat").onclick = () => {
    state.studioMode = "chat";
    renderStudioView();
  };
  document.getElementById("st-view-tui").onclick = () => {
    if (state.studioTuiMap.has(ws.id)) {
      state.studioMode = "tui";
      renderStudioView();
      setTimeout(() => terminalMgr.mountStage(document.getElementById("studio-tui-stage"), state.studioTuiMap.get(ws.id)), 40);
    } else {
      spawnStudioTui("native");
    }
  };
  document.getElementById("st-toggle-insp").onclick = () => {
    state.studioInspectorOpen = !state.studioInspectorOpen;
    renderStudioView();
  };
  const stSpawn = document.getElementById("st-spawn-wt");
  if (stSpawn && room) {
    stSpawn.onclick = () =>
      runButtonAction(stSpawn, { busyText: "创建分支…", doneText: "✓ 已派生" }, () =>
        rpc("room.spawnWorktree", { roomId: room.id, labelHint: room.title })
      );
  }
  const stMerge = document.getElementById("st-merge-wt");
  if (stMerge && room) {
    stMerge.onclick = () =>
      runButtonAction(stMerge, { busyText: "合并中…", doneText: "✓ 已合入" }, () =>
        rpc("room.mergeWorktree", { roomId: room.id })
      );
  }
  const stDel = document.getElementById("st-del-room");
  if (stDel && room) {
    stDel.onclick = () =>
      openConfirmModal({
        title: "删除专属对话",
        messageHtml: `确定删除对话 <strong>${escapeHtml(room.title)}</strong> 吗？`,
        confirmText: "🗑 确认删除",
        danger: true,
        onConfirm: async () => {
          const snap = await rpc("room.delete", { roomId: room.id, removeWorktree: room.isIsolatedWorktree });
          if (snap) applySnapshot(snap, false);
        },
      });
  }

  // Center Mode Toggle: Chat vs Embedded TUI
  document.getElementById("studio-chat-wrap").style.display = state.studioMode === "chat" ? "flex" : "none";
  document.getElementById("studio-tui-pane").style.display = state.studioMode === "tui" ? "flex" : "none";
  if (state.studioMode === "tui") {
    const homeLabel = univ?.customHomeDirectory || ws.electronUserData || "";
    document.getElementById("studio-tui-caption").textContent = `${ws.displayName} · 隔离目录 ${homeLabel}`;
    const sid = state.studioTuiMap.get(ws.id);
    if (sid) terminalMgr.mountStage(document.getElementById("studio-tui-stage"), sid);
  }

  // Render Chat Stream & Composer for Studio
  renderStudioChatAndComposer(ws, room, pe, univ, forceScroll);

  // Render Right-Side Live Working Status Inspector
  const insp = document.getElementById("studio-inspector");
  insp.style.display = state.studioInspectorOpen ? "flex" : "none";
  if (state.studioInspectorOpen) {
    renderStudioInspector(ws, room, pe, univ);
  }
}

function renderStudioChatAndComposer(ws, room, pe, univ, forceScroll) {
  const stream = document.getElementById("studio-chat-stream");
  const modeSwitch = document.getElementById("studio-mode-switch");
  const hintEl = document.getElementById("studio-composer-hint");
  const sendBtn = document.getElementById("btn-studio-send");
  const stopBtn = document.getElementById("btn-studio-stop");
  const input = document.getElementById("studio-composer-input");

  if (!room) {
    stream.innerHTML = `<div class="empty-hero"><p>正在初始化 ${escapeHtml(ws.displayName)} 的专属工作空间…</p></div>`;
    return;
  }

  input.placeholder = `直接向「${ws.displayName}」发送开发或分析指令（无需输入 @，Ctrl+Enter 发送）…`;
  sendBtn.textContent = `发送给 ${ws.displayName} (Ctrl+Enter)`;
  sendBtn.disabled = !!room.isRunning;
  stopBtn.style.display = room.isRunning ? "inline-flex" : "none";

  const isWrite = room.sandboxMode === "WorkspaceWrite";
  modeSwitch.innerHTML =
    `<button class="mode-seg-btn write ${isWrite ? "active" : ""}" data-st-mode="WorkspaceWrite">${icon("wrench", 11)} <span>Agent 读写</span></button>` +
    `<button class="mode-seg-btn readonly ${!isWrite ? "active" : ""}" data-st-mode="ReadOnly">${icon("search", 11)} <span>Ask 只读</span></button>`;
  modeSwitch.querySelectorAll("[data-st-mode]").forEach((btn) => {
    btn.onclick = () => {
      if (room.sandboxMode === btn.dataset.stMode) return;
      rpc("room.setSandboxMode", { roomId: room.id, sandboxMode: btn.dataset.stMode }).catch((e) => showToast("error", e.message));
    };
  });

  const homePath = univ?.customHomeDirectory || ws.antigravityHome || ws.codexHome || ws.electronUserData || "";
  hintEl.textContent = homePath ? `账号目录：${homePath}` : "";

  const messages = room.messages || [];
  if (messages.length === 0) {
    const authCardHtml = !pe.authReady
      ? `<div class="preset-card" id="st-hero-login" style="border-color:var(--warning)">
           <div class="preset-card-title">🔑 一键登录授权此账号</div>
           <div class="preset-card-desc">在当前保留目录（${escapeHtml(homePath)}）打开原生终端完成一次授权</div>
         </div>`
      : `<div class="preset-card" id="st-hero-tui">
           <div class="preset-card-title">🖥 切换至原生交互终端 (TUI)</div>
           <div class="preset-card-desc">像原版客户端一样直接操作此智能体的交互式命令行界面</div>
         </div>`;

    stream.innerHTML = `
      <div class="empty-hero">
        <h2>${escapeHtml(ws.displayName)} · 独立工作台</h2>
        <p>${escapeHtml(pe.engineLabel || ws.clientName)} · 直接输入指令即可 1对1 交互，右侧实时展示工作状态、执行步骤与文件改动</p>
        <div class="preset-grid">
          <div class="preset-card" data-st-preset="请梳理当前工作目录的核心模块架构，指出潜在风险点并给出改进建议。">
            <div class="preset-card-title">🔍 深度审查当前代码库</div>
            <div class="preset-card-desc">读取目录结构与关键源码，输出清晰的架构诊断报告</div>
          </div>
          <div class="preset-card" data-st-preset="请检查当前工作区最近的代码改动，运行构建或测试命令并修复发现的问题。">
            <div class="preset-card-title">🛠 自动排查修复并验证</div>
            <div class="preset-card-desc">直接在当前工作目录修改文件、执行编译测试并汇报结果</div>
          </div>
          ${authCardHtml}
          <div class="preset-card" id="st-hero-go-group">
            <div class="preset-card-title">🤝 前往多智能体总控指挥舱</div>
            <div class="preset-card-desc">让 ${escapeHtml(ws.displayName)} 与其他智能体同屏交叉评审或协作</div>
          </div>
        </div>
      </div>
    `;
    stream.querySelectorAll("[data-st-preset]").forEach((c) => {
      c.onclick = () => {
        input.value = c.dataset.stPreset;
        input.focus();
      };
    });
    document.getElementById("st-hero-login")?.addEventListener("click", () => spawnStudioTui("native"));
    document.getElementById("st-hero-tui")?.addEventListener("click", () => document.getElementById("st-view-tui")?.click());
    document.getElementById("st-hero-go-group")?.addEventListener("click", () => mentionWorkspaceInCommandCenter(ws.id));
    return;
  }

  const nearBottom = forceScroll || stream.scrollHeight - stream.scrollTop - stream.clientHeight < 120;
  stream.innerHTML = messages
    .map((m) => {
      const isUser = m.role === "User";
      const avatarText = isUser ? "你" : (ws.displayName || "AI").slice(0, 2).toUpperCase();
      const authorLabel = isUser ? "你" : ws.displayName;
      const stateCls =
        m.state === "Running" ? "running" :
        m.state === "Completed" ? "write" :
        m.state === "Failed" ? "danger" : "";
      const steps = m.steps || [];
      const stepsHtml =
        steps.length > 0
          ? `<details class="steps-box" ${m.state === "Running" ? "open" : ""}>` +
              `<summary class="steps-summary"><span>执行轨迹 · ${steps.length} 步</span><span>展开/收起</span></summary>` +
              `<div class="steps-list">` +
                steps.map((s) => `<div>${s.status === "completed" ? "✓" : "⟳"} <span style="color:var(--text-secondary)">${escapeHtml(s.title)}</span></div>`).join("") +
              `</div>` +
            `</details>`
          : "";

      return `
        <article class="msg-card ${isUser ? "user" : "agent"}">
          <div class="msg-head">
            <div class="msg-author"><span class="role-avatar">${escapeHtml(avatarText)}</span><span>${escapeHtml(authorLabel)}</span></div>
            <div class="msg-meta">
              <span class="tag ${stateCls}">${escapeHtml(m.stateText || m.state)}</span>
              <span>${escapeHtml(m.timeText || "")}</span>
            </div>
          </div>
          ${m.failoverNotice ? `<div class="failover-banner">⚡ ${escapeHtml(m.failoverNotice)}</div>` : ""}
          ${stepsHtml}
          <div class="md-body">${renderMarkdown(m.text)}</div>
        </article>`;
    })
    .join("");

  if (nearBottom) stream.scrollTop = stream.scrollHeight;
}

function renderStudioInspector(ws, room, pe, univ) {
  const hudEl = document.getElementById("studio-live-hud");
  const stepsCountEl = document.getElementById("studio-steps-count");
  const stepsListEl = document.getElementById("studio-steps-list");

  // Find latest message from this workspace across ALL rooms (dedicated or group chat!)
  const allRooms = state.data?.rooms || [];
  const matchingMsgs = [];
  for (const r of allRooms) {
    for (const m of r.messages || []) {
      if (m.role === "Agent" && m.workspaceId === ws.id) {
        matchingMsgs.push({ msg: m, room: r });
      }
    }
  }
  const activeRun = matchingMsgs.find((x) => x.msg.state === "Running" || x.msg.state === "Pending");
  const latestItem = activeRun || matchingMsgs[matchingMsgs.length - 1] || null;
  const isWorking = (pe.activeTasks || 0) > 0 || !!activeRun;

  hudEl.classList.toggle("is-working", isWorking);

  const pRem = pe.primaryRemainingPercent != null ? Math.round(pe.primaryRemainingPercent) : null;
  const tokTotal = (pe.sessionInputTokens || 0) + (pe.sessionOutputTokens || 0) || pe.lifetimeTokens || 0;

  if (isWorking) {
    const activityText =
      pe.currentActivity ||
      (activeRun?.msg?.steps?.length ? activeRun.msg.steps[activeRun.msg.steps.length - 1].title : "正在推理并调用本地工具…");
    const targetRoomId = activeRun?.room?.id || room?.id;

    hudEl.innerHTML = `
      <div class="studio-hud-title">
        <span>实时工作状态</span>
        <span class="mono" style="color:var(--accent-cyan)" data-live-since="${escapeHtml(pe.activeSinceUtc || "")}">耗时 ${formatElapsedCompact(pe.activeSinceUtc)}</span>
      </div>
      <div class="studio-live-banner">
        <div class="studio-live-state" style="color:var(--accent-cyan)">
          <span class="studio-pulse-ring"></span>
          <span>正在工作中</span>
        </div>
        ${targetRoomId ? `<button class="btn btn-danger btn-sm" id="st-hud-stop">■ 停止</button>` : ""}
      </div>
      <div style="font-size:12px;padding:7px 9px;border-radius:var(--radius-sm);background:var(--bg-input);border:1px solid rgba(56,189,248,0.3);color:var(--text-primary)">
        ⚡ ${escapeHtml(activityText)}
      </div>
      ${
        activeRun?.room && !activeRun.room.isDedicatedAgentRoom
          ? `<div style="font-size:11px;color:var(--text-muted)">来源会话：指挥舱「${escapeHtml(activeRun.room.title)}」</div>`
          : ""
      }
    `;
    document.getElementById("st-hud-stop")?.addEventListener("click", () => {
      if (targetRoomId) rpc("room.stop", { roomId: targetRoomId });
    });
  } else if (pe.authReady) {
    hudEl.innerHTML = `
      <div class="studio-hud-title">
        <span>实时工作状态</span>
        <span>${escapeHtml(pe.plan || ws.clientName)}</span>
      </div>
      <div class="studio-live-banner">
        <div class="studio-live-state" style="color:var(--success)">
          <span class="pool-dot healthy" style="width:9px;height:9px"></span>
          <span>空闲待命 · 随时响应</span>
        </div>
      </div>
      <div style="font-size:11.5px;color:var(--text-secondary)">
        ${escapeHtml(pe.lastActivity || pe.statusText || "无头引擎已就绪")}
      </div>
      ${
        ws.client === "Codex" && pRem != null
          ? `<div class="slim-meter-row" style="margin-top:2px">
               <span class="slim-meter-label">5h 剩余</span>
               <div class="quota-bar-track"><div class="quota-bar-fill" style="width:${pRem}%;background:${pRem <= 15 ? "var(--danger)" : pRem <= 30 ? "var(--warning)" : "var(--success)"}"></div></div>
               <span class="slim-meter-val"><strong>${pRem}%</strong></span>
             </div>`
          : ""
      }
      <div style="display:flex;justify-content:space-between;font-size:11px;color:var(--text-muted);padding-top:4px;border-top:1px solid var(--border-subtle)">
        <span>完成轮次：${pe.sessionTurns || 0} 轮</span>
        <span>累计流量：${formatTokensCompact(tokTotal)} tok</span>
      </div>
    `;
  } else {
    hudEl.innerHTML = `
      <div class="studio-hud-title">
        <span>实时工作状态</span>
        <span>待授权</span>
      </div>
      <div class="studio-live-state" style="color:var(--warning)">
        <span class="pool-dot warning" style="width:9px;height:9px"></span>
        <span>尚未登录授权</span>
      </div>
      <div style="font-size:11.5px;color:var(--text-secondary)">
        ${escapeHtml(pe.statusText || "请完成一次账号授权后即可全程免客户端后台执行")}
      </div>
      <button class="btn btn-primary btn-sm" id="st-hud-login">🔑 在隔离终端一键登录</button>
    `;
    document.getElementById("st-hud-login")?.addEventListener("click", () => spawnStudioTui("native"));
  }

  // Render Steps Feed
  const steps = latestItem?.msg?.steps || [];
  stepsCountEl.textContent = steps.length > 0 ? `${steps.length} 步` : "暂无";
  if (steps.length === 0) {
    stepsListEl.innerHTML = `<div style="font-size:11.5px;color:var(--text-muted);padding:4px 2px">发送指令后，此处将实时滚动展示该智能体的思考、文件读写与命令执行步骤</div>`;
  } else {
    stepsListEl.innerHTML = steps
      .map(
        (s) => `
        <div class="studio-step-row ${s.status !== "completed" ? "running" : ""}">
          <span style="flex-shrink:0;color:${s.status === "completed" ? "var(--success)" : "var(--accent-cyan)"}">${s.status === "completed" ? "✓" : "⟳"}</span>
          <span>${escapeHtml(s.title)}</span>
        </div>`
      )
      .join("");
    stepsListEl.scrollTop = stepsListEl.scrollHeight;
  }

  // Trigger async Git Diff inspection for current studio working directory
  if (room?.workingDirectory) {
    refreshStudioDiff(false);
  } else {
    renderStudioFileChanges(null);
  }
}

/* ==========================================================================
   9. View Switching & Live Push Subscriptions
   ========================================================================== */
function switchView(viewName) {
  state.activeView = viewName;
  closeComposerPickerPopover();
  closeAccountPopover();
  closeRosterPopover();
  closeTopbarPoolPopover();
  document.querySelectorAll("[data-nav]").forEach((btn) => {
    btn.classList.toggle("active", btn.dataset.nav === viewName);
  });
  document.getElementById("view-command").style.display = viewName === "command" ? "flex" : "none";
  document.getElementById("view-studio").style.display = viewName === "studio" ? "flex" : "none";
  document.getElementById("view-arena").style.display = viewName === "arena" ? "flex" : "none";
  document.getElementById("view-fleet").style.display = viewName === "fleet" ? "flex" : "none";

  if (viewName === "command") {
    renderSessionList();
    renderRoomHeader();
    renderChatStream();
    renderComposerFooterControls();
    renderTerminalDock();
    renderDiffDrawer();
  } else if (viewName === "studio") {
    renderStudioView(true);
  } else if (viewName === "arena") {
    renderArenaView();
  } else if (viewName === "fleet") {
    renderFleetView();
  }
}

function showToast(kind, message) {
  const stack = document.getElementById("toast-root");
  const item = document.createElement("div");
  item.className = `toast-item ${kind || "info"}`;
  item.textContent = message;
  stack.appendChild(item);
  setTimeout(() => item.remove(), 3800);
}

function applySnapshot(snapshot, isInitial = false) {
  state.data = snapshot;
  if (snapshot.settings?.theme) {
    applyTheme(snapshot.settings.theme);
  }
  renderTopbarPool();
  renderDesignBar();

  if (state.activeView === "command") {
    renderSessionList();
    renderRoomHeader();
    renderChatStream(isInitial);
    renderComposerFooterControls();
    if (state.terminalOpen) renderTerminalDock();
  } else if (state.activeView === "studio") {
    renderStudioView(isInitial);
  } else if (state.activeView === "arena") {
    if (document.activeElement?.tagName !== "SELECT") {
      renderArenaView();
    }
  } else if (state.activeView === "fleet") {
    renderFleetView();
  }

  if (isInitial && snapshot.settings?.language) {
    setUiLanguage(snapshot.settings.language);
  } else {
    applyLanguageToRoot(document.body);
  }
}

onHostEvent("state.snapshot", (snap) => applySnapshot(snap, false));

onHostEvent("telemetry.tick", (tick) => {
  if (!state.data || !tick) return;
  if (tick.isLaunching !== undefined) state.data.isLaunching = tick.isLaunching;
  if (tick.isPreparingSandboxes !== undefined) state.data.isPreparingSandboxes = tick.isPreparingSandboxes;
  if (tick.sandboxStatusText !== undefined) state.data.sandboxStatusText = tick.sandboxStatusText;
  if (tick.claudeCliAvailable !== undefined) state.data.claudeCliAvailable = tick.claudeCliAvailable;
  if (tick.agyCliAvailable !== undefined) state.data.agyCliAvailable = tick.agyCliAvailable;
  if (tick.stealth) state.data.stealth = tick.stealth;
  if (tick.pool) state.data.pool = tick.pool;
  if (tick.workspaces) state.data.workspaces = tick.workspaces;
  if (tick.instances) state.data.instances = tick.instances;
  if (tick.warnings) state.data.warnings = tick.warnings;

  renderTopbarPool();
  if (state.activeView === "fleet") renderFleetView();
  else if (state.activeView === "studio") renderStudioView(false);
  else if (state.activeView === "command") renderComposerFooterControls();
});

onHostEvent("pool.updated", (pool) => {
  if (!state.data) return;
  state.data.pool = pool;
  renderTopbarPool();
  if (state.activeView === "fleet") renderFleetView();
  if (state.activeView === "studio") renderStudioView(false);
});

onHostEvent("room.updated", (updatedRoom) => {
  if (!state.data?.rooms) return;
  const idx = state.data.rooms.findIndex((r) => r.id === updatedRoom.id);
  if (idx >= 0) state.data.rooms[idx] = updatedRoom;
  else state.data.rooms.push(updatedRoom);

  renderTopbarPool();
  if (state.activeView === "command" && !updatedRoom.isDedicatedAgentRoom) {
    renderSessionList();
    if (getCurrentRoom()?.id === updatedRoom.id) {
      renderRoomHeader();
      renderChatStream(false);
      renderComposerFooterControls();
    }
  } else if (state.activeView === "studio") {
    if (!updatedRoom.isRunning && updatedRoom.workingDirectory) {
      state.studioDiffMap.delete(updatedRoom.workingDirectory);
    }
    renderStudioView(false);
  }
});

onHostEvent("arena.updated", (updatedSession) => {
  if (!state.data?.arenaSessions) return;
  const idx = state.data.arenaSessions.findIndex((s) => s.id === updatedSession.id);
  if (idx >= 0) state.data.arenaSessions[idx] = updatedSession;
  else state.data.arenaSessions.unshift(updatedSession);
  renderTopbarPool();
  if (state.activeView === "arena") renderArenaView();
});

onHostEvent("terminal.output", ({ sessionId, data }) => terminalMgr.writeOutput(sessionId, data));
onHostEvent("terminal.exited", ({ sessionId, exitCode }) => terminalMgr.markExited(sessionId, exitCode));

onHostEvent("design.state", (d) => {
  if (state.data) state.data.design = d;
  renderDesignBar();
});

onHostEvent("design.elementCaptured", (picked) => {
  state.designCaptures.push(picked);
  renderDesignChips();
  if (state.activeView !== "command") switchView("command");
  document.getElementById("composer-input")?.focus();
});

onHostEvent("toast", ({ kind, message }) => showToast(kind, message));

window.__adeTestApi = {
  switchView: (v) => switchView(v),
  setTheme: (t) => applyTheme(t),
  openOnboardingModal: (tab) => openAddProfileModal(tab || "api"),
  closeModal: () => closeModal(),
  openMentionPreview: () => {
    const composer = document.getElementById("composer-input");
    if (composer) {
      composer.value = "@";
      composer.selectionStart = composer.selectionEnd = 1;
      updateMentionAutocomplete();
    }
  },
  openDiffWithSample: (sampleDiff) => {
    state.diffOpen = true;
    state.inspectorTab = "diff";
    state.diffMode = "split";
    state.diffSnapshot = sampleDiff;
    document.getElementById("diff-drawer").style.display = "flex";
    const resizer = document.getElementById("diff-resizer");
    if (resizer) resizer.style.display = "";
    renderDiffDrawerContent();
  },
  previewCleanEmptyCommand: () => {
    state.diffOpen = false;
    document.getElementById("diff-drawer").style.display = "none";
    const resizer = document.getElementById("diff-resizer");
    if (resizer) resizer.style.display = "none";
    state.mentionMenu.open = false;
    renderMentionPopup();
    const composer = document.getElementById("composer-input");
    if (composer) composer.value = "";
    const room = getCurrentRoom();
    if (room) {
      room.messages = [];
      renderRoomHeader();
      renderChatStream(true);
      renderComposerFooterControls();
    }
  },
  openStudioPreview: (sampleDiff) => {
    const firstWs = getStudioEligibleWorkspaces()[0];
    if (firstWs) state.studioWsId = firstWs.id;
    switchView("studio");
    if (sampleDiff) renderStudioFileChanges(sampleDiff);
    const hint = document.getElementById("studio-composer-hint");
    if (hint) hint.textContent = "账号目录：./data/profiles/gemini-pro";
  },
  prepareArenaPreview: () => {
    const repoInput = document.getElementById("arena-repo-input");
    if (repoInput) repoInput.value = "./workspaces/distributed-kv-engine";
    const verifyInput = document.getElementById("arena-verify-input");
    if (verifyInput) verifyInput.value = "dotnet test --no-restore";
    const promptInput = document.getElementById("arena-prompt-input");
    if (promptInput) promptInput.value = "Implement lock-free shard routing with lease timeout failover and full contract unit tests.";
  },
  setLanguage: (lang) => setUiLanguage(lang),
  openCreateProjectModal: () => openCreateProjectModal(),
  findUntranslatedCjk: () => {
    const cjk = /[\u4e00-\u9fff]/;
    const leaks = [];
    const isVisible = (el) => {
      let cur = el;
      while (cur && cur !== document.body) {
        const st = window.getComputedStyle(cur);
        if (st.display === "none" || st.visibility === "hidden") return false;
        cur = cur.parentElement;
      }
      return true;
    };
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    let n;
    while ((n = walker.nextNode())) {
      const p = n.parentElement;
      if (!p || !isVisible(p)) continue;
      if (p.id === "btn-toggle-lang") continue;
      if (p.closest(".md-body, .steps-list, .ag-think-body, .ag-timeline, .project-thread-title, .room-title")) continue;
      const val = (n.nodeValue || "").trim();
      if (val && cjk.test(val)) leaks.push(`TEXT[${p.tagName}.${p.className}]: ${val}`);
    }
    document.querySelectorAll("input[placeholder], textarea[placeholder]").forEach((el) => {
      if (!isVisible(el)) return;
      if (cjk.test(el.placeholder || "")) leaks.push(`PH[${el.id}]: ${el.placeholder}`);
    });
    document.querySelectorAll("input:not([type=password]):not([type=checkbox])").forEach((el) => {
      if (!isVisible(el)) return;
      if (el.hasAttribute("data-ag-alias") || el.id === "ms-dir" || el.id === "m-room-dir" || el.id === "cp-parent-dir") return;
      if (cjk.test(el.value || "")) leaks.push(`VAL[${el.id}]: ${el.value}`);
    });
    return leaks.slice(0, 20);
  },
  isReady: () => !!state.data,
};

initShell();
installI18nObserver();
rpc("app.bootstrap")
  .then((initial) => applySnapshot(initial, true))
  .catch((err) => showToast("error", "初始化连接失败：" + err.message));
