namespace CodexMultiLauncher;

/// <summary>
/// Injects an unobtrusive element-inspection overlay into the secondary Design Mode WebView2
/// so clicking any live DOM element captures its selector, HTML snippet, computed CSS, and viewport screenshot.
/// </summary>
public static class DesignModeInspector
{
    public const string BootstrapScript = """
        (() => {
          if (window.__adeDesignInspectorInstalled) return;
          window.__adeDesignInspectorInstalled = true;
          let active = false;
          let overlay = null;
          let badge = null;

          function ensureOverlay() {
            if (overlay) return;
            overlay = document.createElement('div');
            overlay.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483646;border:2px solid #38bdf8;background:rgba(56,189,248,0.14);border-radius:4px;display:none;transition:all 60ms ease-out;';
            badge = document.createElement('div');
            badge.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483647;background:#0f172a;color:#f8fafc;font:11px/1.4 Consolas,monospace;padding:3px 7px;border-radius:4px;border:1px solid #38bdf8;display:none;box-shadow:0 4px 12px rgba(0,0,0,0.35);';
            document.documentElement.appendChild(overlay);
            document.documentElement.appendChild(badge);
          }

          function buildSelector(el) {
            if (!el || el.nodeType !== 1) return 'body';
            if (el.id) return '#' + el.id;
            const parts = [];
            let cur = el;
            while (cur && cur.nodeType === 1 && parts.length < 4 && cur.tagName.toLowerCase() !== 'html') {
              let seg = cur.tagName.toLowerCase();
              if (cur.classList && cur.classList.length > 0) {
                seg += '.' + Array.from(cur.classList).slice(0, 2).join('.');
              }
              parts.unshift(seg);
              cur = cur.parentElement;
            }
            return parts.join(' > ');
          }

          function pickComputedStyles(el) {
            const cs = window.getComputedStyle(el);
            const keys = ['display','position','width','height','padding','margin','color','backgroundColor','fontSize','fontWeight','borderRadius','gap','justifyContent','alignItems'];
            const out = {};
            for (const k of keys) {
              const v = cs[k];
              if (v && v !== 'none' && v !== 'normal' && v !== '0px' && v !== 'rgba(0, 0, 0, 0)') out[k] = v;
            }
            return out;
          }

          window.__adeSetInspectMode = (enabled) => {
            active = !!enabled;
            ensureOverlay();
            if (!active && overlay && badge) {
              overlay.style.display = 'none';
              badge.style.display = 'none';
            }
          };

          document.addEventListener('mousemove', (e) => {
            if (!active) return;
            ensureOverlay();
            const el = document.elementFromPoint(e.clientX, e.clientY);
            if (!el || el === overlay || el === badge) return;
            const rect = el.getBoundingClientRect();
            overlay.style.display = 'block';
            overlay.style.left = rect.left + 'px';
            overlay.style.top = rect.top + 'px';
            overlay.style.width = rect.width + 'px';
            overlay.style.height = rect.height + 'px';
            badge.style.display = 'block';
            badge.style.left = Math.max(6, Math.min(window.innerWidth - 240, rect.left)) + 'px';
            badge.style.top = Math.max(6, rect.top - 26) + 'px';
            badge.textContent = `${buildSelector(el)} (${Math.round(rect.width)}×${Math.round(rect.height)})`;
          }, true);

          document.addEventListener('click', (e) => {
            if (!active) return;
            e.preventDefault();
            e.stopPropagation();
            const el = document.elementFromPoint(e.clientX, e.clientY);
            if (!el || el === overlay || el === badge) return;
            const rect = el.getBoundingClientRect();
            const html = (el.outerHTML || '').slice(0, 1200);
            const payload = {
              type: 'elementPicked',
              url: location.href,
              selector: buildSelector(el),
              tagName: el.tagName.toLowerCase(),
              textPreview: (el.innerText || '').trim().slice(0, 200),
              outerHtml: html,
              computedStyles: pickComputedStyles(el),
              bounds: { x: Math.round(rect.x), y: Math.round(rect.y), width: Math.round(rect.width), height: Math.round(rect.height) }
            };
            active = false;
            if (overlay) overlay.style.display = 'none';
            if (badge) badge.style.display = 'none';
            if (window.chrome && window.chrome.webview) {
              window.chrome.webview.postMessage(JSON.stringify(payload));
            }
          }, true);
        })();
        """;
}
