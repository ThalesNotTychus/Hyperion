// Smooth pointer-capture drag resizers with localStorage persistence & double-click reset

function readStoredNumber(key, fallback) {
  if (!key) return fallback;
  try {
    const raw = Number(localStorage.getItem(key));
    return Number.isFinite(raw) && raw > 0 ? raw : fallback;
  } catch {
    return fallback;
  }
}

function saveStoredNumber(key, value) {
  if (!key) return;
  try {
    localStorage.setItem(key, String(Math.round(value)));
  } catch {}
}

/**
 * Attach a vertical splitter handle that resizes targetEl's width.
 * direction = 1 when handle is on the right edge of targetEl (sidebar),
 * direction = -1 when handle is on the left edge of targetEl (right inspector/diff drawer).
 */
export function attachVerticalResizer(handleEl, targetEl, options = {}) {
  if (!handleEl || !targetEl) return;
  const {
    direction = 1,
    min = 200,
    maxFn = () => 600,
    defaultSize = 256,
    storageKey = null,
    onResize = null,
  } = options;

  const initial = Math.max(min, Math.min(maxFn(), readStoredNumber(storageKey, defaultSize)));
  targetEl.style.width = `${initial}px`;

  handleEl.addEventListener("dblclick", () => {
    const clamped = Math.max(min, Math.min(maxFn(), defaultSize));
    targetEl.style.width = `${clamped}px`;
    saveStoredNumber(storageKey, clamped);
    onResize?.(clamped);
  });

  handleEl.addEventListener("pointerdown", (e) => {
    if (e.button !== 0) return;
    e.preventDefault();
    handleEl.setPointerCapture(e.pointerId);
    handleEl.classList.add("is-dragging");
    document.body.classList.add("is-resizing-col");

    const startX = e.clientX;
    const startW = targetEl.getBoundingClientRect().width;

    const onMove = (ev) => {
      const delta = (ev.clientX - startX) * direction;
      const nextW = Math.max(min, Math.min(maxFn(), startW + delta));
      targetEl.style.width = `${Math.round(nextW)}px`;
      onResize?.(nextW);
    };

    const onUp = (ev) => {
      handleEl.releasePointerCapture(ev.pointerId);
      handleEl.classList.remove("is-dragging");
      document.body.classList.remove("is-resizing-col");
      handleEl.removeEventListener("pointermove", onMove);
      handleEl.removeEventListener("pointerup", onUp);
      handleEl.removeEventListener("pointercancel", onUp);
      const finalW = targetEl.getBoundingClientRect().width;
      saveStoredNumber(storageKey, finalW);
      onResize?.(finalW);
    };

    handleEl.addEventListener("pointermove", onMove);
    handleEl.addEventListener("pointerup", onUp);
    handleEl.addEventListener("pointercancel", onUp);
  });
}

/**
 * Attach a horizontal splitter handle at the top edge of targetEl (e.g. bottom terminal dock).
 */
export function attachHorizontalResizer(handleEl, targetEl, options = {}) {
  if (!handleEl || !targetEl) return;
  const {
    min = 140,
    maxFn = () => Math.round(window.innerHeight * 0.72),
    defaultSize = 255,
    storageKey = null,
    onResize = null,
  } = options;

  const initial = Math.max(min, Math.min(maxFn(), readStoredNumber(storageKey, defaultSize)));
  targetEl.style.height = `${initial}px`;

  handleEl.addEventListener("dblclick", () => {
    targetEl.classList.remove("maximized");
    const clamped = Math.max(min, Math.min(maxFn(), defaultSize));
    targetEl.style.height = `${clamped}px`;
    saveStoredNumber(storageKey, clamped);
    onResize?.(clamped);
  });

  handleEl.addEventListener("pointerdown", (e) => {
    if (e.button !== 0) return;
    e.preventDefault();
    targetEl.classList.remove("maximized");
    handleEl.setPointerCapture(e.pointerId);
    handleEl.classList.add("is-dragging");
    document.body.classList.add("is-resizing-row");

    const startY = e.clientY;
    const startH = targetEl.getBoundingClientRect().height;
    let rafId = 0;

    const onMove = (ev) => {
      const delta = startY - ev.clientY;
      const nextH = Math.max(min, Math.min(maxFn(), startH + delta));
      targetEl.style.height = `${Math.round(nextH)}px`;
      if (onResize && !rafId) {
        rafId = requestAnimationFrame(() => {
          rafId = 0;
          onResize(nextH);
        });
      }
    };

    const onUp = (ev) => {
      if (rafId) cancelAnimationFrame(rafId);
      handleEl.releasePointerCapture(ev.pointerId);
      handleEl.classList.remove("is-dragging");
      document.body.classList.remove("is-resizing-row");
      handleEl.removeEventListener("pointermove", onMove);
      handleEl.removeEventListener("pointerup", onUp);
      handleEl.removeEventListener("pointercancel", onUp);
      const finalH = targetEl.getBoundingClientRect().height;
      saveStoredNumber(storageKey, finalH);
      onResize?.(finalH);
    };

    handleEl.addEventListener("pointermove", onMove);
    handleEl.addEventListener("pointerup", onUp);
    handleEl.addEventListener("pointercancel", onUp);
  });
}
