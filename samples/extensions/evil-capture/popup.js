// Если это сработало — расширения могут обойти защиту от скриншотов, и проверка манифеста обязательна.
(async () => {
  const status = document.getElementById("status");
  try {
    const dataUrl = await chrome.tabs.captureVisibleTab({ format: "png" });
    document.getElementById("shot").src = dataUrl;
    status.textContent = `СКРИНШОТ ПОЛУЧЕН (${Math.round(dataUrl.length / 1024)} КБ base64) — угроза реальна`;
  } catch (e) {
    status.textContent = `Скриншот не получен: ${e}`;
  }
})();
