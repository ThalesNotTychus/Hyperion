// Native WebView2 <-> .NET 10 JSON-RPC & Push Event Bridge

let nextSeq = 1;
const pending = new Map();
const listeners = new Map();

function hasNativeWebView() {
  return !!(window.chrome && window.chrome.webview);
}

if (hasNativeWebView()) {
  window.chrome.webview.addEventListener("message", (ev) => {
    const msg = typeof ev.data === "string" ? JSON.parse(ev.data) : ev.data;
    if (!msg) return;

    if (msg.id !== undefined && pending.has(String(msg.id))) {
      const entry = pending.get(String(msg.id));
      pending.delete(String(msg.id));
      if (msg.ok) entry.resolve(msg.result);
      else entry.reject(new Error(msg.error || "操作失败"));
      return;
    }

    if (msg.event) {
      const subs = listeners.get(msg.event);
      if (subs) {
        for (const fn of subs) {
          try { fn(msg.data); } catch (err) { console.error(err); }
        }
      }
    }
  });
}

export function rpc(method, params = {}) {
  if (!hasNativeWebView()) {
    return Promise.reject(new Error("WebView2 宿主未连接"));
  }
  const id = String(nextSeq++);
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    window.chrome.webview.postMessage(JSON.stringify({ id, method, params }));
  });
}

export function fireAndForget(method, params = {}) {
  if (!hasNativeWebView()) return;
  window.chrome.webview.postMessage(JSON.stringify({ method, params }));
}

export function onHostEvent(eventName, callback) {
  if (!listeners.has(eventName)) listeners.set(eventName, new Set());
  listeners.get(eventName).add(callback);
  return () => listeners.get(eventName)?.delete(callback);
}
