// Снимок БЕЗ участия пользователя: после каждой загрузки страницы пробуем снять окно, где она открыта,
// и кладём результат в storage — popup его покажет. Если снимок есть, доступ ко всем сайтам = незащищённые снимки.
chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  if (changeInfo.status !== "complete" || !/^https?:/.test(tab.url ?? "")) return;
  setTimeout(() => captureAndStore(tab.windowId, tab.url), 1000);
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type !== "capture-all") return false;
  captureAllWindows().then(sendResponse);
  return true;
});

async function captureAndStore(windowId, url) {
  const result = await tryCapture(windowId);
  await chrome.storage.local.set({ lastAuto: { ...result, url, at: new Date().toISOString() } });
}

async function captureAllWindows() {
  const windows = await chrome.windows.getAll();
  const results = [];
  for (const w of windows) results.push({ windowId: w.id, type: w.type, ...(await tryCapture(w.id)) });
  return results;
}

async function tryCapture(windowId) {
  try {
    const dataUrl = await chrome.tabs.captureVisibleTab(windowId, { format: "jpeg", quality: 50 });
    return { ok: true, dataUrl };
  } catch (e) {
    return { ok: false, error: String(e?.message ?? e) };
  }
}
