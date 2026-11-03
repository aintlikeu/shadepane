const results = document.getElementById("results");

function report(name, ok, detail = "") {
  const li = document.createElement("li");
  li.className = ok ? "ok" : "fail";
  li.textContent = `${ok ? "✔" : "✘"} ${name}${detail ? " — " + detail : ""}`;
  results.appendChild(li);
}

async function run() {
  report("chrome.runtime доступен", typeof chrome?.runtime?.id === "string", chrome?.runtime?.id ?? "");
  report("chrome.storage доступен", !!chrome?.storage?.local);

  try {
    const reply = await chrome.runtime.sendMessage({ type: "ping" });
    report("сообщение в service worker", reply?.pong === true, reply?.at ?? "нет ответа");
  } catch (e) {
    report("сообщение в service worker", false, String(e));
  }

  try {
    const { installedAt } = await chrome.storage.local.get("installedAt");
    report("onInstalled записал данные", !!installedAt, installedAt ?? "");
  } catch (e) {
    report("чтение storage", false, String(e));
  }

  try {
    const tabs = await chrome.tabs?.query?.({});
    report("chrome.tabs.query (информативно)", Array.isArray(tabs), Array.isArray(tabs) ? `вкладок: ${tabs.length}` : "API нет");
  } catch (e) {
    report("chrome.tabs.query (информативно)", false, String(e));
  }

  await refreshCounter();
}

async function refreshCounter() {
  const { counter = 0 } = await chrome.storage.local.get("counter");
  document.getElementById("counter").textContent = counter;
}

document.getElementById("inc").addEventListener("click", async () => {
  const { counter = 0 } = await chrome.storage.local.get("counter");
  await chrome.storage.local.set({ counter: counter + 1 });
  await refreshCounter();
});

document.getElementById("close").addEventListener("click", () => window.close());

run();
