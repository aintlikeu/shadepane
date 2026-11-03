// Service worker: отвечает на ping из popup и считает свои запуски.
chrome.runtime.onInstalled.addListener(() => {
  chrome.storage.local.set({ installedAt: new Date().toISOString() });
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "ping") {
    sendResponse({ pong: true, at: new Date().toISOString() });
  }
  return false;
});
