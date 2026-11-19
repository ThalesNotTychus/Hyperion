// Interactive Unified & Split (Side-by-Side) Git Diff Viewer + Inline Review Annotations

import { escapeHtml } from "./markdown.js";

export function pairSplitLines(lines) {
  const rows = [];
  let i = 0;
  while (i < lines.length) {
    const cur = lines[i];
    if (cur.kind === "ctx") {
      rows.push({ left: cur, right: cur });
      i++;
      continue;
    }
    if (cur.kind === "del") {
      const dels = [];
      while (i < lines.length && lines[i].kind === "del") dels.push(lines[i++]);
      const adds = [];
      while (i < lines.length && lines[i].kind === "add") adds.push(lines[i++]);
      const max = Math.max(dels.length, adds.length);
      for (let k = 0; k < max; k++) {
        rows.push({ left: dels[k] || null, right: adds[k] || null });
      }
      continue;
    }
    if (cur.kind === "add") {
      rows.push({ left: null, right: cur });
      i++;
      continue;
    }
    i++;
  }
  return rows;
}

export function renderDiffFilesHtml(diffSnapshot, options = {}) {
  const {
    mode = "unified", // 'unified' | 'split'
    selectedFile = null,
    comments = [],
    activeDraftKey = null,
    allowComments = true,
  } = options;

  if (!diffSnapshot || !diffSnapshot.isGitRepository) {
    return `<div style="padding:28px 16px;text-align:center;color:var(--text-muted)">当前目录不是 Git 仓库，无法计算代码 Diff。</div>`;
  }

  const files = selectedFile
    ? diffSnapshot.files.filter((f) => f.filePath === selectedFile)
    : diffSnapshot.files;

  if (!files || files.length === 0) {
    return `<div style="padding:32px 16px;text-align:center;color:var(--text-muted)">工作树干净，暂无未提交的代码变更。</div>`;
  }

  const commentsByKey = new Map();
  comments.forEach((c, idx) => {
    const k = `${c.filePath}:${c.side}:${c.lineNumber}`;
    if (!commentsByKey.has(k)) commentsByKey.set(k, []);
    commentsByKey.get(k).push({ ...c, index: idx });
  });

  return files
    .map((file) => {
      const statusBadge = `<span class="tag">${escapeHtml(file.status)}</span>`;
      const statBadge =
        `<span style="color:var(--diff-add-text);font-family:monospace">+${file.additions}</span> ` +
        `<span style="color:var(--diff-del-text);font-family:monospace">-${file.deletions}</span>`;

      const hunksHtml = (file.hunks || [])
        .map((hunk) => {
          const headerRow = `<div style="padding:3px 10px;background:var(--bg-elevated);color:var(--accent-cyan);font-family:monospace;font-size:11px;border-top:1px solid var(--border-subtle);border-bottom:1px solid var(--border-subtle)">${escapeHtml(hunk.header)}</div>`;
          if (mode === "split") {
            return headerRow + renderSplitHunkTable(file.filePath, hunk.lines || [], commentsByKey, activeDraftKey, allowComments);
          }
          return headerRow + renderUnifiedHunkTable(file.filePath, hunk.lines || [], commentsByKey, activeDraftKey, allowComments);
        })
        .join("");

      return (
        `<div class="diff-file-card">` +
          `<div class="diff-file-head">` +
            `<span class="mono" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${escapeHtml(file.filePath)}</span>` +
            `<span style="display:flex;align-items:center;gap:8px">${statBadge}${statusBadge}</span>` +
          `</div>` +
          hunksHtml +
        `</div>`
      );
    })
    .join("");
}

function renderUnifiedHunkTable(filePath, lines, commentsByKey, activeDraftKey, allowComments) {
  const rows = [];
  for (const line of lines) {
    const cls = line.kind === "add" ? "add" : line.kind === "del" ? "del" : "ctx";
    const side = line.kind === "del" ? "old" : "new";
    const lineNum = side === "old" ? line.oldLine : line.newLine;
    const key = `${filePath}:${side}:${lineNum}`;
    const prefix = line.kind === "add" ? "+" : line.kind === "del" ? "-" : " ";
    const encodedSnippet = encodeURIComponent(line.content || "");
    const encodedFile = encodeURIComponent(filePath);

    rows.push(
      `<tr class="diff-row ${cls}">` +
        `<td class="diff-num">${line.oldLine ?? ""}</td>` +
        `<td class="diff-num">${line.newLine ?? ""}</td>` +
        (allowComments
          ? `<td class="diff-gutter-btn" title="在此行添加审查批注" data-diff-draft="${escapeHtml(key)}" data-file="${encodedFile}" data-line="${lineNum ?? 1}" data-side="${side}" data-snippet="${encodedSnippet}">＋</td>`
          : `<td></td>`) +
        `<td class="diff-code">${prefix}${escapeHtml(line.content)}</td>` +
      `</tr>`
    );

    appendCommentsAndDraftRow(rows, 4, key, filePath, lineNum ?? 1, side, line.content || "", commentsByKey, activeDraftKey);
  }
  return `<table class="diff-table"><tbody>${rows.join("")}</tbody></table>`;
}

function renderSplitHunkTable(filePath, lines, commentsByKey, activeDraftKey, allowComments) {
  const pairs = pairSplitLines(lines);
  const rows = [];
  const encodedFile = encodeURIComponent(filePath);

  for (const pair of pairs) {
    const left = pair.left;
    const right = pair.right;
    const leftCls = left ? (left.kind === "del" ? "del" : "ctx") : "";
    const rightCls = right ? (right.kind === "add" ? "add" : "ctx") : "";

    const leftKey = left ? `${filePath}:old:${left.oldLine}` : null;
    const rightKey = right ? `${filePath}:new:${right.newLine}` : null;

    rows.push(
      `<tr class="diff-row">` +
        `<td class="diff-num ${leftCls}">${left?.oldLine ?? ""}</td>` +
        `<td class="diff-code ${leftCls}" style="width:50%;border-right:1px solid var(--border-subtle)">${left ? escapeHtml(left.content) : ""}</td>` +
        `<td class="diff-num ${rightCls}">${right?.newLine ?? ""}</td>` +
        (allowComments && right
          ? `<td class="diff-gutter-btn ${rightCls}" title="添加批注" data-diff-draft="${escapeHtml(rightKey)}" data-file="${encodedFile}" data-line="${right.newLine ?? 1}" data-side="new" data-snippet="${encodeURIComponent(right.content || "")}">＋</td>`
          : `<td class="${rightCls}"></td>`) +
        `<td class="diff-code ${rightCls}" style="width:50%">${right ? escapeHtml(right.content) : ""}</td>` +
      `</tr>`
    );

    if (leftKey) {
      appendCommentsAndDraftRow(rows, 5, leftKey, filePath, left.oldLine ?? 1, "old", left.content || "", commentsByKey, activeDraftKey);
    }
    if (rightKey && rightKey !== leftKey) {
      appendCommentsAndDraftRow(rows, 5, rightKey, filePath, right.newLine ?? 1, "new", right.content || "", commentsByKey, activeDraftKey);
    }
  }
  return `<table class="diff-table"><tbody>${rows.join("")}</tbody></table>`;
}

function appendCommentsAndDraftRow(rows, colSpan, key, filePath, lineNum, side, snippet, commentsByKey, activeDraftKey) {
  const existing = commentsByKey.get(key) || [];
  for (const item of existing) {
    rows.push(
      `<tr><td colspan="${colSpan}" class="diff-comment-badge">` +
        `<div style="display:flex;align-items:center;justify-content:space-between;gap:8px">` +
          `<div><span class="tag worktree">批注 #${item.index + 1} · L${item.lineNumber}</span> <span style="margin-left:6px">${escapeHtml(item.comment)}</span></div>` +
          `<button class="btn btn-ghost btn-sm" data-remove-diff-comment="${item.index}">移除</button>` +
        `</div>` +
      `</td></tr>`
    );
  }

  if (activeDraftKey === key) {
    rows.push(
      `<tr><td colspan="${colSpan}" class="diff-comment-editor">` +
        `<div style="display:flex;flex-direction:column;gap:6px">` +
          `<div style="font-size:11px;color:var(--text-secondary)">批注 <code>${escapeHtml(filePath)}:${lineNum}</code></div>` +
          `<textarea id="inline-diff-draft-input" class="textarea" rows="2" placeholder="输入针对此行的修改意见（支持 Markdown），稍后可一键打包发给 Agent…"></textarea>` +
          `<div style="display:flex;justify-content:flex-end;gap:6px">` +
            `<button class="btn btn-sm" data-cancel-diff-draft="1">取消</button>` +
            `<button class="btn btn-primary btn-sm" data-save-diff-draft="1" data-file="${encodeURIComponent(filePath)}" data-line="${lineNum}" data-side="${side}" data-snippet="${encodeURIComponent(snippet)}">加入审查队列</button>` +
          `</div>` +
        `</div>` +
      `</td></tr>`
    );
  }
}
