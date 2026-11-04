# Публикация в Microsoft Store

Продукт в Partner Center: **Shadepane**, издатель **Quietglass**, тип «Приложение MSIX или PWA».
Данные пакета из Partner Center (Product management → Product identity) уже записаны в
[`installer/msix/AppxManifest.xml`](../installer/msix/AppxManifest.xml) — их не менять.

Пакет собирает CI на каждый push: артефакт **Shadepane-msix** на странице запуска в Actions.
Для отправки в Store нужен пакет из сборки по тегу `vX.Y.Z`: у сборок из веток версия `0.0.0`.
Каждая следующая отправка — с большей версией.

## Чем версия из Store отличается

- Пакет подписывает Store. Предупреждений SmartScreen и Smart App Control нет.
- Обновляет Store. Своя проверка обновлений через GitHub выключена (правила Store), в настройках — «Обновления
  приходят через Microsoft Store».
- Данные лежат в папке пакета: `%LOCALAPPDATA%\Packages\Quietglass.Shadepane_<…>\LocalCache\Local\Shadepane`.
  При удалении приложения они удаляются.
- Политику администратора (HKLM, ProgramData) версия из Store читает так же.

## Проверка пакета у себя

Пакет не подписан, поэтому устанавливается только как «распакованный» в режиме разработчика:

1. «Параметры → Система → Для разработчиков» (Windows 10: «Обновление и безопасность → Для разработчиков») →
   **Режим разработчика** — включить.
2. Скачать артефакт — он не в репозитории, `git pull` его не приносит. Actions → нужный запуск CI → внизу
   **Artifacts** → **Shadepane-msix** (нужно войти в GitHub), или через GitHub CLI:
   ```powershell
   gh run download <номер запуска> -R aintlikeu/shadepane -n Shadepane-msix -D $env:USERPROFILE\Downloads\Shadepane-msix
   ```
   Распаковать скачанный zip, перейти в папку с `.msix` (`cd $env:USERPROFILE\Downloads\Shadepane-msix`)
   и в PowerShell (обычный пользователь):
   ```powershell
   $msix = (Get-ChildItem Shadepane-*-win-x64.msix | Select-Object -First 1).FullName
   Copy-Item $msix "$env:TEMP\shadepane-msix.zip" -Force
   Expand-Archive "$env:TEMP\shadepane-msix.zip" "$env:TEMP\shadepane-msix" -Force
   Add-AppxPackage -Register "$env:TEMP\shadepane-msix\AppxManifest.xml"
   ```
3. Запустить «Shadepane» из меню «Пуск». Проверить:
   - иконка в «Пуске», на панели задач и в заголовке окна;
   - ⋯ → Настройки → «Обновления приходят через Microsoft Store», флажка и «Проверить сейчас» нет;
   - защита от снимков экрана, расширения, тёмная тема, язык — как в обычной версии (docs/TESTING.md).
4. Удалить: `Get-AppxPackage Quietglass.Shadepane | Remove-AppxPackage`, затем выключить режим разработчика.

## Скриншоты для карточки

Обычный Shadepane на скриншоте — пустое окно. Для карточки есть сборка без защиты (только Debug, в релиз не попадает):

```powershell
dotnet run --project src\Shadepane.App -p:ScreenshotBuild=true
# окно ровно 1920×1080 (видимая часть, физические пиксели) в левом верхнем углу экрана:
dotnet run --project src\Shadepane.App -p:ScreenshotBuild=true -- --window-size=1920x1080
```

`--window-size` работает только в этой сборке. Демонстрацию экрана (кнопка на демо-странице, кадр 4) браузер даёт только
https и `localhost`; для своего тестового адреса из hosts добавьте `--treat-as-secure=http://wiki.northwind.example:8000`
(тоже только в этой сборке). Не берите адрес под `*.corp.example`: в тестовой политике это защищённый домен, и индикатор
покажет «Корпоративный сайт» вместо «Защищено».
Если экран сам 1920×1080, окно уходит под панель задач — на время
съёмки включите «Параметры → Персонализация → Панель задач → Автоматически скрывать панель задач».
Масштаб Windows лучше 100%: при 125–150% в те же 1920×1080 поместится меньше интерфейса.
Снимок окна — Alt+PrintScreen (вставить в Paint и сохранить PNG) или Win+Shift+S → «Окно».

Снимки — Win+Shift+S или PrintScreen. Store требует хотя бы один скриншот, рекомендуется 1920×1080 или 1366×768.
На скриншотах не должно быть настоящих рабочих данных: перед съёмкой закройте браузер и временно переименуйте
`%LOCALAPPDATA%\Shadepane` (история, закладки), после — верните имя.

**Демо-страница** — вымышленная внутренняя wiki с планом релиза, бюджетом и пометкой «конфиденциально»:
```powershell
python -m http.server 8000 --directory samples\test-pages
```
В Shadepane: `http://localhost:8000/demo.html` — страница на английском; русская версия — `demo.html?lang=ru`.
Тема — как у Shadepane. Кнопка «Показать экран на встрече» запрашивает демонстрацию экрана: Shadepane отклоняет запрос
и на 4 секунды показывает «Захват экрана отклонён» — успейте сделать снимок.

### Главный кадр: «что видите вы» / «что получает снимок»

Один кадр 1920×1080, разрезанный пополам: слева экран, как его видит пользователь, справа — тот же экран на снимке.
Окно браузера разрезано посередине: слева оно есть, справа на его месте видны программы под ним.

1. Расставьте на рабочем столе обычные программы без личных данных: Блокнот с парой строк текста, Калькулятор,
   Проводник (например, «Этот компьютер»), Часы или Календарь. Пусть они занимают весь экран и частично уходят
   под середину экрана, где будет браузер.
2. В настройках Shadepane включите «При запуске → Открывать вкладки, которые были открыты перед закрытием» —
   тогда в обоих снимках будут одни и те же вкладки.
3. **Снимок А** (окно видно) — сборка для скриншотов, окно ровно по центру:
   ```powershell
   dotnet run --project src\Shadepane.App -p:ScreenshotBuild=true -- --window-rect=360,150,1200,760 --treat-as-secure=http://wiki.northwind.example:8000
   ```
   Откройте демо-страницу, щёлкните по окну браузера (оно должно быть поверх программ), затем
   `powershell -ExecutionPolicy Bypass -File tools\screen.ps1 -Delay 5` → переименуйте снимок в `A.png`.
   Закройте браузер, **не трогая остальные окна**.
4. **Снимок Б** (окно скрыто) — обычная Debug-сборка с защитой, тот же прямоугольник:
   ```powershell
   dotnet run --project src\Shadepane.App -- --window-rect=360,150,1200,760
   ```
   Щёлкните по окну браузера, снова `tools\screen.ps1 -Delay 5` → `B.png`. На снимке окна нет, программы под ним видны.
5. Пришлите `A.png` и `B.png` — кадр из половин собирает `design/` (или Claude).

`--window-rect=X,Y,Ш,В` и `--window-size` работают только в Debug-сборках; X, Y, Ш, В — видимая часть окна
в физических пикселях. Панель задач в этом кадре можно не скрывать: часы на ней будут из снимка Б.

### Остальные кадры — каждый про своё

| № | Кадр | Подпись RU | Подпись EN |
|---|---|---|---|
| 1 | Главный кадр (выше) | Окно Shadepane не попадает на снимки, записи и демонстрацию экрана | Shadepane stays out of screenshots, recordings and screen sharing |
| 2 | Открыта панель «Сведения о защите»: что защищено и как | Защита всегда на виду: индикатор и сведения в адресной строке | Protection status is always visible in the address bar |
| 3 | Вкладка «Настройки» (⋯ → Настройки) в тёмной теме | Привычный браузер: вкладки, закладки, тёмная тема, русский и английский | A familiar browser: tabs, bookmarks, dark theme, English and Russian |
| 4 | Политика администратора: настройки с пометками «Задано администратором» (политика с `homeUrl`, `allowDownloads`, `restoreSession`) или окно «Расширение отклонено политикой» | Настройки и расширения под контролем администратора | Settings and extensions under administrator control |
| 5 (по желанию) | Демонстрация экрана в Teams или Zoom: у участников на месте браузера пусто | Не попадает и в демонстрацию экрана на встречах | Stays out of screen sharing in meetings, too |

Для английской карточки — те же кадры с английским интерфейсом (⋯ → Настройки → «Язык / Language» → English, перезапуск).

## Отправка (Partner Center → Shadepane → Start submission)

1. **Pricing and availability**: Free, рынки — все или нужные.
2. **Properties**: категория (подходят Productivity или Utilities & tools — выберите из списка Partner Center);
   **Privacy policy URL**: `https://github.com/aintlikeu/shadepane/blob/main/docs/PRIVACY.md`
   (файл должен быть в main — значит, после слияния develop в main); Website и Support contact —
   `https://github.com/aintlikeu/shadepane` и `https://github.com/aintlikeu/shadepane/issues`.
3. **Age ratings**: анкета IARC; для браузера — «неограниченный доступ в интернет: да».
4. **Packages**: загрузить `Shadepane-X.Y.Z-win-x64.msix` из сборки по тегу.
5. **Store listings** — для English (United States) и Русский (Россия): описание, скриншоты, при желании краткое
   описание и ключевые слова. Тексты ниже.
6. **Submission options → Notes for certification** и обоснование **runFullTrust** — тексты ниже.

### Описание (English)

> Shadepane is a web browser that keeps its windows out of screenshots, screen recordings and screen sharing.
> Everything you open in it — pages, menus, extension popups — stays off the screen capture, so confidential pages
> don't end up in someone's screenshot or meeting recording.
>
> - Screen capture protection for the whole browser window and its popups
> - A protection indicator in the address bar and a log of blocked capture requests
> - Unpacked Chrome extensions, checked against a policy before they are installed
> - Administrator policy: locked settings, protected domains, download rules, no remote desktop sessions
> - Tabs, bookmarks, history, downloads, find on page, light and dark theme, English and Russian
>
> Built on Microsoft Edge WebView2. Free and open source (MIT): github.com/aintlikeu/shadepane

### Описание (русский)

> Shadepane — браузер, окна которого не попадают на снимки экрана, в запись и в демонстрацию экрана.
> Всё, что в нём открыто, — страницы, меню, окна расширений — остаётся за кадром, поэтому конфиденциальные страницы
> не окажутся на чужом скриншоте или в записи встречи.
>
> - Защита от захвата экрана для всего окна браузера и его всплывающих окон
> - Индикатор защиты в адресной строке и журнал отклонённых запросов захвата
> - Распакованные расширения Chrome с проверкой по политике перед установкой
> - Политика администратора: закреплённые настройки, защищённые домены, правила загрузок, запрет удалённого рабочего стола
> - Вкладки, закладки, история, загрузки, поиск на странице, светлая и тёмная тема, русский и английский язык
>
> Работает на Microsoft Edge WebView2. Бесплатно, открытый код (MIT): github.com/aintlikeu/shadepane

### Notes for certification

> Shadepane is a web browser built on Microsoft Edge WebView2 (Chromium engine, Store policy 10.2.1).
> Its main feature is protection from screen capture: every browser window calls
> SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE). Because of this, screenshots and screen recordings
> made during testing will show an empty area instead of the browser window. This is the intended
> behaviour, not a rendering failure; the app works normally on screen.
> The app does not update itself: the GitHub update check is turned off when it runs from the MSIX package.
> No account or sign-in is needed. Test page with capture and popup checks:
> https://github.com/aintlikeu/shadepane/blob/main/samples/test-pages/capture-test.html

### runFullTrust

> Shadepane is a WPF desktop application (.NET) hosting Microsoft Edge WebView2. It needs full trust to run as a
> desktop app, to protect its windows from screen capture (SetWindowDisplayAffinity), to read the administrator
> policy from HKLM\Software\Policies\Shadepane and C:\ProgramData\Shadepane, and to load unpacked browser
> extensions from folders the user selects.
