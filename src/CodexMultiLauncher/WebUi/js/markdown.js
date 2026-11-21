// Safe, Streaming-Friendly GFM Markdown & Code Syntax Highlighter
// Supports fenced code blocks, GFM tables, blockquotes & [!NOTE] callouts, task lists, hr, and safe external links.

export function escapeHtml(str) {
  if (str === null || str === undefined) return "";
  return String(str)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function highlightCode(escapedCode, lang) {
  const l = (lang || "").toLowerCase();
  if (l === "diff" || l === "patch") {
    return escapedCode
      .split("\n")
      .map((line) => {
        if (line.startsWith("+")) return `<span style="color:var(--diff-add-text)">${line}</span>`;
        if (line.startsWith("-")) return `<span style="color:var(--diff-del-text)">${line}</span>`;
        if (line.startsWith("@@")) return `<span style="color:var(--accent-cyan)">${line}</span>`;
        return line;
      })
      .join("\n");
  }

  return escapedCode
    .replace(/(\/\/[^\n]*|#[^\n]*)/g, '<span class="tok-com">$1</span>')
    .replace(/(&quot;(?:\\.|[^&])*?&quot;|'(?:\\.|[^'])*?')/g, '<span class="tok-str">$1</span>')
    .replace(
      /\b(async|await|public|private|internal|sealed|static|class|record|struct|interface|enum|namespace|using|return|if|else|switch|case|foreach|for|while|try|catch|finally|throw|new|var|const|let|function|export|import|from|def|fn|impl|true|false|null|void|string|int|bool|Task)\b/g,
      '<span class="tok-kw">$1</span>'
    )
    .replace(/\b(\d+(?:\.\d+)?)\b/g, '<span class="tok-num">$1</span>');
}

function splitTableCells(rowLine) {
  const trimmed = rowLine.trim().replace(/^\|/, "").replace(/\|$/, "");
  const cells = [];
  let cur = "";
  for (let i = 0; i < trimmed.length; i++) {
    if (trimmed[i] === "\\" && trimmed[i + 1] === "|") {
      cur += "|";
      i++;
    } else if (trimmed[i] === "|") {
      cells.push(cur.trim());
      cur = "";
    } else {
      cur += trimmed[i];
    }
  }
  cells.push(cur.trim());
  return cells;
}

function isTableSeparatorLine(line) {
  const t = line.trim();
  if (!t.includes("|") && !t.includes("-")) return false;
  const cells = splitTableCells(t);
  return cells.length >= 2 && cells.every((c) => /^:?-{3,}:?$/.test(c));
}

function parseTableAlignments(sepLine) {
  return splitTableCells(sepLine).map((c) => {
    const left = c.startsWith(":");
    const right = c.endsWith(":");
    if (left && right) return "center";
    if (right) return "right";
    return "left";
  });
}

export function renderMarkdown(rawText) {
  if (!rawText) return '<span style="color:var(--text-muted)">正在等待响应…</span>';
  const normalized = String(rawText).replace(/\r\n/g, "\n");
  const codeBlocks = [];

  // Extract fenced code blocks (including still-streaming unclosed block at end)
  const withPlaceholders = normalized.replace(/```([a-zA-Z0-9_+-]*)\n([\s\S]*?)(?:```|$)/g, (_, lang, code) => {
    const idx = codeBlocks.length;
    const cleanLang = escapeHtml(lang || "code");
    const rawTrimmed = code.replace(/\n$/, "");
    const highlighted = highlightCode(escapeHtml(rawTrimmed), lang);
    const encodedRaw = encodeURIComponent(rawTrimmed);
    codeBlocks.push(
      `<div class="code-block">` +
        `<div class="code-block-header"><span>${cleanLang}</span>` +
        `<button class="btn btn-ghost btn-sm" data-copy-code="${encodedRaw}">复制代码</button></div>` +
        `<pre><code>${highlighted}</code></pre>` +
      `</div>`
    );
    return `\u0000CODEBLOCK_${idx}\u0000`;
  });

  const formatInline = (text) => {
    const inlineCodes = [];
    let s = String(text).replace(/`([^`\n]+)`/g, (_, code) => {
      const idx = inlineCodes.length;
      inlineCodes.push(`<code class="inline-code">${escapeHtml(code)}</code>`);
      return `\u0000INLINECODE_${idx}\u0000`;
    });

    s = escapeHtml(s);

    // Safe external links [text](https://...)
    s = s.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, (_, label, rawUrl) => {
      const cleanUrl = rawUrl.replace(/&amp;/g, "&");
      return `<a href="#" class="md-link" data-ext-url="${escapeHtml(cleanUrl)}" title="${escapeHtml(cleanUrl)}">${label} ↗</a>`;
    });

    s = s.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    s = s.replace(/(^|[^*])\*([^*\n]+)\*(?!\*)/g, "$1<em>$2</em>");
    s = s.replace(/~~([^~\n]+)~~/g, "<del>$1</del>");
    s = s.replace(/(^|\s)(@[A-Za-z\u4e00-\u9fa5][A-Za-z0-9_\-\u4e00-\u9fa5]{0,31})/g, '$1<strong style="color:var(--accent)">$2</strong>');

    return s.replace(/\u0000INLINECODE_(\d+)\u0000/g, (_, idx) => inlineCodes[Number(idx)] || "");
  };

  const lines = withPlaceholders.split("\n");
  const html = [];
  let inUl = false;
  let inOl = false;

  const closeLists = () => {
    if (inUl) { html.push("</ul>"); inUl = false; }
    if (inOl) { html.push("</ol>"); inOl = false; }
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const placeholderMatch = line.match(/^\u0000CODEBLOCK_(\d+)\u0000$/);
    if (placeholderMatch) {
      closeLists();
      html.push(codeBlocks[Number(placeholderMatch[1])]);
      continue;
    }

    const trimmed = line.trim();
    if (!trimmed) {
      closeLists();
      continue;
    }

    // Horizontal rule
    if (/^(?:---|\*\*\*|___)$/.test(trimmed)) {
      closeLists();
      html.push('<hr class="md-hr" />');
      continue;
    }

    // GFM Table: header row + alignment separator row
    if (trimmed.includes("|") && i + 1 < lines.length && isTableSeparatorLine(lines[i + 1])) {
      closeLists();
      const headers = splitTableCells(trimmed);
      const aligns = parseTableAlignments(lines[i + 1]);
      i += 2;
      const bodyRows = [];
      while (i < lines.length && lines[i].trim().includes("|") && !/^\u0000CODEBLOCK_/.test(lines[i].trim())) {
        bodyRows.push(splitTableCells(lines[i].trim()));
        i++;
      }
      i--;
      const thHtml = headers
        .map((h, cIdx) => `<th style="text-align:${aligns[cIdx] || "left"}">${formatInline(h)}</th>`)
        .join("");
      const tbHtml = bodyRows
        .map(
          (row) =>
            `<tr>` +
            headers
              .map((_, cIdx) => `<td style="text-align:${aligns[cIdx] || "left"}">${formatInline(row[cIdx] ?? "")}</td>`)
              .join("") +
            `</tr>`
        )
        .join("");
      html.push(`<div class="md-table-wrap"><table class="md-table"><thead><tr>${thHtml}</tr></thead><tbody>${tbHtml}</tbody></table></div>`);
      continue;
    }

    // Blockquote & GitHub Callout (> [!NOTE], > [!WARNING], etc.)
    if (trimmed.startsWith(">")) {
      closeLists();
      const quoteLines = [];
      while (i < lines.length && lines[i].trim().startsWith(">")) {
        quoteLines.push(lines[i].trim().replace(/^>\s?/, ""));
        i++;
      }
      i--;
      const alertMatch = quoteLines[0]?.match(/^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*(.*)$/i);
      if (alertMatch) {
        const kind = alertMatch[1].toUpperCase();
        const cls = kind === "WARNING" || kind === "CAUTION" ? "warn" : kind === "TIP" ? "tip" : "note";
        const rest = [alertMatch[2], ...quoteLines.slice(1)].filter(Boolean);
        html.push(
          `<div class="md-callout ${cls}">` +
            `<div class="md-callout-title">${kind}</div>` +
            `<div>${rest.map((ql) => formatInline(ql)).join("<br/>")}</div>` +
          `</div>`
        );
      } else {
        html.push(`<blockquote class="md-quote">${quoteLines.map((ql) => formatInline(ql)).join("<br/>")}</blockquote>`);
      }
      continue;
    }

    const heading = trimmed.match(/^(#{1,4})\s+(.+)$/);
    if (heading) {
      closeLists();
      const level = heading[1].length;
      html.push(`<h${level}>${formatInline(heading[2])}</h${level}>`);
      continue;
    }

    const ul = trimmed.match(/^[-*]\s+(.+)$/);
    if (ul) {
      if (inOl) { html.push("</ol>"); inOl = false; }
      if (!inUl) { html.push("<ul>"); inUl = true; }
      const taskMatch = ul[1].match(/^\[([ xX])\]\s+(.+)$/);
      if (taskMatch) {
        const checked = taskMatch[1].toLowerCase() === "x";
        html.push(`<li class="md-task-li"><span class="md-task-box ${checked ? "is-checked" : ""}">${checked ? "✓" : ""}</span><span>${formatInline(taskMatch[2])}</span></li>`);
      } else {
        html.push(`<li>${formatInline(ul[1])}</li>`);
      }
      continue;
    }

    const ol = trimmed.match(/^\d+\.\s+(.+)$/);
    if (ol) {
      if (inUl) { html.push("</ul>"); inUl = false; }
      if (!inOl) { html.push("<ol>"); inOl = true; }
      html.push(`<li>${formatInline(ol[1])}</li>`);
      continue;
    }

    closeLists();
    html.push(`<p>${formatInline(line)}</p>`);
  }

  closeLists();
  return html.join("").replace(/\u0000CODEBLOCK_(\d+)\u0000/g, (_, idx) => codeBlocks[Number(idx)] || "");
}
