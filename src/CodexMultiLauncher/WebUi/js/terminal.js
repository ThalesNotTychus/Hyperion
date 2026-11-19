// xterm.js + ConPTY Multi-Session Terminal Manager

import { fireAndForget } from "./bridge.js";

const THEME_PALETTES = {
  obsidian: {
    background: "#090c12",
    foreground: "#e2e8f0",
    cursor: "#38bdf8",
    selectionBackground: "rgba(99,102,241,0.35)",
  },
  matrix: {
    background: "#040608",
    foreground: "#d1fae5",
    cursor: "#10b981",
    selectionBackground: "rgba(16,185,129,0.32)",
  },
  daylight: {
    background: "#0f172a",
    foreground: "#f8fafc",
    cursor: "#38bdf8",
    selectionBackground: "rgba(56,189,248,0.35)",
  },
  aurora: {
    background: "#10131f",
    foreground: "#e0e7ff",
    cursor: "#2dd4bf",
    selectionBackground: "rgba(139,92,246,0.35)",
  },
};

export class TerminalManager {
  constructor() {
    this.instances = new Map(); // sessionId -> { term, fit, container }
    this.currentTheme = "obsidian";
    this.activeSessionId = null;
    this.resizeObserver = new ResizeObserver(() => this.fitActive());
  }

  setTheme(theme) {
    this.currentTheme = THEME_PALETTES[theme] ? theme : "obsidian";
    const palette = THEME_PALETTES[this.currentTheme];
    for (const entry of this.instances.values()) {
      entry.term.options.theme = palette;
    }
  }

  mountStage(stageElement, activeSessionId) {
    this.activeSessionId = activeSessionId;
    if (!stageElement) return;
    this.resizeObserver.disconnect();
    this.resizeObserver.observe(stageElement);

    for (const [id, entry] of this.instances.entries()) {
      if (id === activeSessionId) {
        if (entry.container.parentElement !== stageElement) {
          stageElement.innerHTML = "";
          stageElement.appendChild(entry.container);
        }
        entry.container.style.display = "block";
        requestAnimationFrame(() => {
          this.fitSession(id);
          entry.term.focus();
        });
      } else if (entry.container.parentElement === stageElement) {
        entry.container.style.display = "none";
      }
    }
  }

  ensureSession(sessionId) {
    if (this.instances.has(sessionId)) return this.instances.get(sessionId);
    const TerminalCtor = window.Terminal;
    const FitCtor = window.FitAddon?.FitAddon || window.FitAddon;
    if (!TerminalCtor) return null;

    const container = document.createElement("div");
    container.className = "term-instance";

    const term = new TerminalCtor({
      fontFamily: '"Cascadia Code", "JetBrains Mono", "Consolas", monospace',
      fontSize: 12.5,
      lineHeight: 1.28,
      cursorBlink: true,
      allowProposedApi: true,
      scrollback: 5000,
      theme: THEME_PALETTES[this.currentTheme] || THEME_PALETTES.obsidian,
    });

    const fit = FitCtor ? new FitCtor() : null;
    if (fit) term.loadAddon(fit);
    term.open(container);

    term.onData((data) => {
      fireAndForget("terminal.write", { sessionId, data });
    });

    const record = { term, fit, container };
    this.instances.set(sessionId, record);
    return record;
  }

  writeOutput(sessionId, chunk) {
    const entry = this.ensureSession(sessionId);
    if (entry && chunk) entry.term.write(chunk);
  }

  markExited(sessionId, exitCode) {
    const entry = this.instances.get(sessionId);
    if (entry) {
      entry.term.writeln(`\r\n\x1b[90m[终端进程已退出，代码 ${exitCode}]\x1b[0m`);
    }
  }

  removeSession(sessionId) {
    const entry = this.instances.get(sessionId);
    if (!entry) return;
    try { entry.term.dispose(); } catch {}
    entry.container.remove();
    this.instances.delete(sessionId);
  }

  fitActive() {
    if (this.activeSessionId) this.fitSession(this.activeSessionId);
  }

  fitSession(sessionId) {
    const entry = this.instances.get(sessionId);
    if (!entry || !entry.fit || !entry.container.isConnected) return;
    try {
      entry.fit.fit();
      if (entry.term.cols > 0 && entry.term.rows > 0) {
        fireAndForget("terminal.resize", {
          sessionId,
          cols: entry.term.cols,
          rows: entry.term.rows,
        });
      }
    } catch {}
  }
}
