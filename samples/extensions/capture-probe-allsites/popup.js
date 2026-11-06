// ✔ (красным) — снимок получен: расширение с этим правом обходит защиту окна от захвата.
// ✘ (зелёным) — снимок не получен; текст ошибки — что ответил WebView2.
const results = document.getElementById("results");
const kind = chrome.runtime.getManifest().host_permissions?.includes("<all_urls>") ? "<all_urls>" : "activeTab";
document.getElementById("kind").textContent = kind;

function report(name, result) {
  const li = document.createElement("li");
  li.className = result.ok === undefined ? "info" : result.ok ? "ok" : "fail";
  const mark = result.ok === undefined ? "•" : result.ok ? "✔ СНИМОК ПОЛУЧЕН" : "✘ нет снимка";
  li.textContent = `${mark}: ${name}${result.error ? " — " + result.error : ""}${result.detail ? " — " + result.detail : ""}`;
  if (result.dataUrl) {
    const img = document.createElement("img");
    img.src = result.dataUrl;
    li.appendChild(img);
  }
  results.appendChild(li);
}

async function tryCapture(windowId) {
  try {
    const dataUrl = windowId === undefined
      ? await chrome.tabs.captureVisibleTab({ format: "jpeg", quality: 50 })
      : await chrome.tabs.captureVisibleTab(windowId, { format: "jpeg", quality: 50 });
    return { ok: true, dataUrl };
  } catch (e) {
    return { ok: false, error: String(e?.message ?? e) };
  }
}

async function run() {
  if (typeof chrome.tabs?.captureVisibleTab !== "function") {
    report("chrome.tabs.captureVisibleTab", { ok: false, error: "API нет в этом движке" });
    return;
  }

  report("снимок «текущего» окна из popup", await tryCapture());

  let windows = [];
  try {
    windows = await chrome.windows.getAll({ populate: true });
    const summary = windows.map(w => `#${w.id} ${w.type}, вкладок ${w.tabs?.length ?? "?"}`).join("; ");
    report("окна, видимые расширению", { detail: summary || "ни одного" });
  } catch (e) {
    report("chrome.windows.getAll", { ok: false, error: String(e?.message ?? e) });
  }
  for (const w of windows) report(`снимок окна #${w.id} (${w.type}) из popup`, await tryCapture(w.id));

  if (kind === "<all_urls>") {
    try {
      const fromWorker = await chrome.runtime.sendMessage({ type: "capture-all" });
      for (const r of fromWorker ?? []) report(`снимок окна #${r.windowId} из service worker`, r);
    } catch (e) {
      report("запрос в service worker", { ok: false, error: String(e?.message ?? e) });
    }
    const { lastAuto } = await chrome.storage.local.get("lastAuto");
    report("автоматический снимок после загрузки страницы (без клика)",
      lastAuto ? { ...lastAuto, detail: `${lastAuto.at}, ${lastAuto.url}` } : { detail: "ещё не было: загрузите сайт и откройте popup снова" });
  }
}

run();
