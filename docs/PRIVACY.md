# Shadepane privacy policy

Last updated: November 5, 2026.

Shadepane is a web browser for Windows. It has no accounts, no telemetry and no analytics.
The Shadepane developers do not receive any data about you or how you use the browser.

## What stays on your computer

Shadepane keeps the following only on your computer, in `%LOCALAPPDATA%\Shadepane`:

- settings, bookmarks and browsing history (history can be turned off and cleared in settings);
- icons of bookmarked sites, taken from the pages you open (Shadepane never downloads them separately) and deleted
  together with the last bookmark of a site;
- cookies, cache and site data of the browser engine;
- installed extensions;
- if "Open the tabs that were open before closing" is on: the addresses of open tabs (`session.json`),
  deleted when the option is turned off;
- a security log (`audit.log`) with protection events, such as blocked navigations, blocked screen capture
  requests and extension checks.

None of this is sent anywhere by Shadepane. Uninstalling keeps this folder; delete it to remove the data.

## What goes over the network

- **Websites you visit and the search engine you choose** receive your requests, as in any browser. Their own
  privacy policies apply. The default search engine is Startpage.
- **Microsoft Edge WebView2.** Shadepane uses the WebView2 Runtime by Microsoft as its browser engine. WebView2
  checks visited pages and downloads with Microsoft Defender SmartScreen and may send diagnostic data according to
  your Windows settings. This is covered by the [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement).
- **Update checks:** Shadepane asks `api.github.com` for the latest release, which shows GitHub your IP address and the browser
  version. This can be turned off in settings.
- **Extensions** you install may send data according to their own policies.

## Contact

Questions: [github.com/aintlikeu/shadepane/issues](https://github.com/aintlikeu/shadepane/issues).

---

# Политика конфиденциальности Shadepane

Обновлено: 5 ноября 2026 г.

Shadepane — браузер для Windows. В нём нет учётных записей, телеметрии и аналитики.
Разработчики Shadepane не получают никаких данных о вас и о том, как вы пользуетесь браузером.

## Что хранится только на вашем компьютере

В папке `%LOCALAPPDATA%\Shadepane`:

- настройки, закладки и история посещений (историю можно отключить и очистить в настройках);
- значки сайтов из закладок: берутся у открытых вами страниц (отдельно Shadepane их не скачивает) и удаляются
  вместе с последней закладкой сайта;
- cookie, кэш и данные сайтов движка браузера;
- установленные расширения;
- если включено «Открывать вкладки, которые были открыты перед закрытием»: адреса открытых вкладок
  (`session.json`), удаляются при выключении опции;
- журнал защиты (`audit.log`): заблокированные переходы, отклонённые запросы захвата экрана, проверки расширений.

Shadepane никуда это не отправляет. При удалении браузера папка остаётся — удалите её, чтобы стереть данные.

## Что уходит в сеть

- **Сайты, которые вы открываете, и выбранный поисковик** получают ваши запросы, как в любом браузере. Действуют их
  политики конфиденциальности. Поисковик по умолчанию — Startpage.
- **Microsoft Edge WebView2** — движок браузера от Microsoft. Он проверяет страницы и загрузки через Microsoft
  Defender SmartScreen и может отправлять диагностические данные по настройкам Windows. Это регулирует
  [заявление о конфиденциальности Microsoft](https://privacy.microsoft.com/privacystatement).
- **Проверка обновлений:** запрос к `api.github.com` показывает GitHub ваш IP-адрес и версию браузера. Проверку можно отключить в настройках.
- **Расширения**, которые вы устанавливаете, могут передавать данные по своим правилам.

## Связь

Вопросы: [github.com/aintlikeu/shadepane/issues](https://github.com/aintlikeu/shadepane/issues).
