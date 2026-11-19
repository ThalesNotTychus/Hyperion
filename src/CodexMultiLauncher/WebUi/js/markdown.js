// Safe, Streaming-Friendly GFM Markdown & Code Syntax Highlighter

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

  // Lightweight token pass on already-escaped HTML
  return escapedCode
    .replace(/(\/\/[^\n]*|#[^\n]*)/g, '<span class="tok-com">$1</span>')
    .replace(/(&quot;(?:\\.|[^&])*?&quot;|'(?:\\.|[^'])*?')/g, '<span class="tok-str">$1</span>')
    .replace(
      /\b(async|await|public|private|internal|sealed|static|class|record|struct|interface|enum|namespace|using|return|if|else|switch|case|foreach|for|while|try|catch|finally|throw|new|var|const|let|function|export|import|from|def|fn|impl|true|false|null)\b/g,
      '<span class="tok-kw">$1</span>'
    )
    .replace(/\b(\d+(?:\.\d+)?)\b/g, '<span class="tok-num">$1</span>');
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

  const lines = withPlaceholders.split("\n");
  const html = [];
  let inUl = false;
  let inOl = false;

  const closeLists = () => {
    if (inUl) { html.push("</ul>"); inUl = false; }
    if (inOl) { html.push("</ol>"); inOl = false; }
  };

  const formatInline = (text) => {
    let s = escapeHtml(text);
    s = s.replace(/`([^`]+)`/g, '<code class="inline-code">$1</code>');
    s = s.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    s = s.replace(/(^|\s)(@[A-Za-z\u4e00-\u9fa5][A-Za-z0-9_\-\u4e00-\u9fa5]{0,31})/g, '$1<strong style="color:var(--accent)">$2</strong>');
    return s;
  };

  for (const line of lines) {
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
      html.push(`<li>${formatInline(ul[1])}</li>`);
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
  return html.join("").replace(/\u0000CODEBLOCK_(\d+)\u0000/g, (_, i) => codeBlocks[Number(i)] || "");
}
