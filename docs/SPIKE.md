# Проверка прототипа на Windows (spike)

Цель — за 2–3 дня ответить на три вопроса, от которых зависит выбор движка:

1. Скрывает ли `WDA_EXCLUDEFROMCAPTURE` на нашем окне контент WebView2 (он рисуется в чужом процессе)?
2. Работает ли popup расширения, открытый как `chrome-extension://<id>/popup.html` в отдельном WebView2?
3. Работают ли расширения, которые реально нужны пользователям?

Плюс проверки, что защитные слои прототипа срабатывают.

> Снимок экрана окна браузера сделать нельзя — в этом и смысл. Результаты фиксируйте текстом,
> фото экрана на телефон или по `audit.log`.

## Подготовка

- Windows 10 2004+ или Windows 11, **не** через RDP (браузер откажется запускаться — это шаг 5).
- .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10`
- WebView2 Runtime (в Windows 11 уже есть): https://developer.microsoft.com/microsoft-edge/webview2/
- OBS Studio, ShareX — по желанию; Teams или Zoom для проверки демонстрации экрана.

```powershell
git clone <repo> ; cd shadepane
dotnet test
dotnet build
# тестовая страница по http (file:// браузер не открывает намеренно):
python -m http.server 8000 --directory samples\test-pages     # или: npx --yes http-server samples\test-pages -p 8000
dotnet run --project src\Shadepane.App
```

В адресной строке: `http://localhost:8000/capture-test.html` (с `http://` — без схемы подставляется https).

Журнал: `%LOCALAPPDATA%\Shadepane\audit.log` (в Проводнике и cmd; в PowerShell —
`$env:LOCALAPPDATA\Shadepane\audit.log`). Папка `AppData` в Проводнике скрыта. Строки о расширениях:
```powershell
Select-String -Path "$env:LOCALAPPDATA\Shadepane\audit.log" -Pattern '"extension-' | ForEach-Object Line
``` Запустить заново «с чистого листа» —
закрыть браузер и удалить `%LOCALAPPDATA%\Shadepane`.

## Шаг 1. Захват экрана (вопрос 1) — главный

На открытой `capture-test.html` попробуйте каждый способ. Ожидаемо: окна браузера нет в снимке
(или на его месте пусто/чёрное), остальной рабочий стол виден.

| Способ | Окно скрыто? |
|---|---|
| Win+Shift+S (Ножницы), прямоугольник поверх окна | |
| PrintScreen → вставить в Paint | |
| Alt+PrintScreen (активное окно) | |
| OBS: «Захват экрана» | |
| OBS: «Захват окна» на Shadepane | |
| Teams/Zoom: демонстрация всего экрана | |
| Teams/Zoom: демонстрация окна Shadepane | |
| ShareX: прямоугольная область | |
| Win+G (Xbox Game Bar), запись | |

**Всплывающие элементы** (раздел 1 тестовой страницы). «Ножницы» с таймером не подходят: их окно
забирает фокус, и Chromium закрывает элемент до снимка. Используйте `tools/screen.ps1` — он снимает
экран без своего окна:
```powershell
powershell -ExecutionPolicy Bypass -File tools\screen.ps1 -Delay 5
```
Запустите, откройте элемент в браузере и ничего не трогайте до снимка. Убедитесь, что элемент
ещё открыт в момент снимка.

| Элемент | Содержимое видно на снимке? |
|---|---|
| Открытый `<select>` со списком клиентов | |
| Календарь `input type=date` | |
| Подсказки datalist | |
| title-подсказка при наведении | |
| Выбор цвета | |
| Контекстное меню (правый клик) | |
| `alert()` / `confirm()` / `prompt()` (раздел 2) | |

Это отдельные окна процесса `msedgewebview2.exe`: на них мы флаг поставить не можем.
Если содержимое видно — это ограничение WebView2, зафиксируйте его.

## Шаг 2. Popup расширения (вопрос 2)

1. «+ Расширение…» → `samples\extensions\spike-popup`. На панели появится зелёная иконка.
2. Кликните по ней. Ожидаемо — все пункты зелёные:
   `chrome.runtime доступен`, `chrome.storage доступен`, `сообщение в service worker`, `onInstalled записал данные`.
   `chrome.tabs.query` — информативно, запишите результат.
3. Нажмите «+1» несколько раз, закройте popup (клик мимо), откройте снова — счётчик сохранился?
4. Кнопка `window.close()` закрывает popup?
5. Сделайте снимок экрана с открытым popup (как в шаге 1) — popup скрыт?
6. Перезапустите браузер — расширение на месте, счётчик сохранился?

## Шаг 3. Угроза через расширение и политика

1. С политикой по умолчанию: «+ Расширение…» → `samples\extensions\evil-capture`.
   Ожидаемо: **отказ** с причинами (`activeTab`, `tabs`, `<all_urls>`).
2. Проверка, реальна ли угроза в WebView2. Замените политику на стендовую:
   ```powershell
   Copy-Item samples\policies\spike-permissive.json src\Shadepane.App\bin\Debug\net10.0-windows\policy.json
   ```
   Запускайте с `dotnet run --no-build --project src\Shadepane.App` — обычный `dotnet run` пересоберёт
   проект и может перезаписать подложенный `policy.json` файлом `default.json`.
   Откройте `capture-test.html`, добавьте `evil-capture`, кликните по красной иконке.
   Запишите текст в popup: «СКРИНШОТ ПОЛУЧЕН…» (угроза реальна) или «Скриншот не получен: …» (с текстом ошибки).
3. Верните политику и проверьте **автоматический отзыв**:
   ```powershell
   Copy-Item samples\policies\default.json src\Shadepane.App\bin\Debug\net10.0-windows\policy.json
   ```
   Перезапустите. Ожидаемо: красной иконки нет, в `audit.log` — событие `extension-revoked`.
4. Подмена файлов. Закройте браузер и выполните в PowerShell (из любой папки) — команды сами находят
   копию spike-popup, которую сделал браузер, и дописывают строку в её `popup.js`:
   ```powershell
   $d = Get-ChildItem "$env:LOCALAPPDATA\Shadepane\extensions" -Directory |
        Where-Object { Select-String -Path (Join-Path $_.FullName manifest.json) -Pattern 'Spike: popup' -Quiet }
   $d.FullName                                     # должна быть одна папка
   Add-Content (Join-Path $d.FullName popup.js) "`n// tampered"
   ```
   Вручную: Проводник → `%LOCALAPPDATA%\Shadepane\extensions` → папка с `popup.html` и `background.js` →
   `popup.js` в Блокноте → дописать строку. Папку `samples\extensions\spike-popup` в репозитории НЕ трогать.
   Запустите браузер. Ожидаемо: зелёной иконки нет, в журнале `extension-revoked` с причиной
   «файлы расширения изменены после одобрения». Затем добавьте `spike-popup` заново — он нужен дальше.

## Шаг 3б. Защищённый источник политики (Release-сборка)

Шаги 1–3 идут на Debug-сборке: она читает `policy.json` рядом с exe без проверки прав. Release так не делает.
Перед началом убедитесь, что нет политики в реестре и ProgramData (команды очистки — в конце шага).

1. **Подложенный файл.** Обычной PowerShell (НЕ от администратора):
   ```powershell
   New-Item -ItemType Directory -Force C:\ProgramData\Shadepane | Out-Null
   Copy-Item samples\policies\spike-permissive.json C:\ProgramData\Shadepane\policy.json
   dotnet run -c Release --project src\Shadepane.App
   ```
   Ожидаемо: отказ запуска «политику может изменить не только администратор (папка владелец …)».
   Если папка уже существовала, созданная администратором, — ожидаемо «право записи у S-1-5-32-545 (BUILTIN\Users)».
   Запишите точный текст сообщения.
   Затем проверьте атаку через junction: удалите созданную папку и обычным пользователем выполните
   ```powershell
   New-Item -ItemType Directory -Force $env:TEMP\pb-fake | Out-Null
   Copy-Item samples\policies\spike-permissive.json $env:TEMP\pb-fake\policy.json
   New-Item -ItemType Junction -Path C:\ProgramData\Shadepane -Target $env:TEMP\pb-fake
   dotnet run -c Release --project src\Shadepane.App
   ```
   Ожидаемо: отказ «C:\ProgramData\Shadepane — ссылка (symlink/junction)». После — удалите junction
   командой `cmd /c rmdir C:\ProgramData\Shadepane` (удаляет только ссылку). НЕ используйте
   `Remove-Item`: в Windows PowerShell 5.1 он для junction спрашивает про вложенные элементы, и ответ «Да»
   может удалить содержимое цели ссылки.
2. **Реестр.** От администратора — команды «Вариант 1» из README, раздел «Политика». Запустите Release
   обычным пользователем. Ожидаемо: браузер запускается, в `audit.log` у `app-start`
   `policySource` = `HKLM\Software\Policies\Shadepane\Policy`. Если вместо этого отказ из-за владельца
   ключа — запишите SID из сообщения: значит, на этой машине владельцем нового ключа становится сам
   администратор, а не группа Administrators, и правило нужно доработать.
3. **ProgramData с закрытыми правами.** От администратора удалите ключ реестра и выполните «Вариант 2»
   из README. Запустите Release обычным пользователем. Ожидаемо: запуск, `policySource` = путь к файлу.
4. **Битая политика.** От администратора впишите в `policy.json` мусор. Ожидаемо: отказ запуска, а не
   откат к политике по умолчанию.

Очистка (от администратора):
```powershell
Remove-Item -Recurse -Force 'HKLM:\Software\Policies\Shadepane' -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force C:\ProgramData\Shadepane -ErrorAction SilentlyContinue
```

## Шаг 4. Контрольный цикл (симуляция атаки)

Только Debug-сборка. Кликните в адресную строку и нажмите **Ctrl+Alt+Shift+D** — это снимает флаг
защиты с окна, как сделал бы внедрённый код. Ожидаемо: в течение ~200 мс контент скрывается,
сообщение «Защита от захвата экрана нарушена (affinity-changed…)», браузер закрывается,
в журнале `capture-guard-tripped`.

## Шаг 5. RDP

1. Подключитесь к машине по RDP и запустите браузер — ожидаемо: отказ запуска.
2. Запустите браузер локально, затем подключитесь к этой же сессии по RDP —
   ожидаемо: срабатывание защиты (`remote-session`) и закрытие.

## Шаг 6. Каналы утечки (раздел 3 тестовой страницы)

| Действие | Ожидаемо | Факт |
|---|---|---|
| Ctrl+P, кнопка `window.print()` | ничего не происходит | |
| F12, Ctrl+Shift+I | DevTools не открываются | |
| Правый клик | нет «Печать», «Сохранить как», «Просмотреть код» | |
| `getDisplayMedia()` | «заблокирован», `screen-capture-blocked` в журнале | |
| `window.open(https)` | открывается в этом же окне | |
| `window.open(data:)` | ничего, `new-window-blocked` в журнале | |
| «Скачать файл» | по умолчанию скачивается (видно в Ctrl+J); при `allowDownloads: false` или выключенных загрузках в настройках — не скачивается, `download-blocked` в журнале | |
| В адресной строке `javascript:alert(1)`, `file:///C:/Windows/win.ini` | отказ с сообщением в строке состояния | |
| «Открыть PDF» | PDF открывается во встроенном просмотрщике | |
| Панель просмотрщика PDF | нет кнопок «Сохранить», «Сохранить как», «Печать» | |
| Ctrl+P / Ctrl+S в PDF | ничего не происходит, окна сохранения нет (в журнале `shortcut-blocked`) | |
| Снимок `tools\screen.ps1` с открытым PDF | просмотрщика нет на снимке | |
| Правый клик в PDF | нет печати/сохранения/поворотов — только разрешённые пункты (белый список) | |
| Правый клик по выделенному тексту / в поле ввода | есть «Копировать» / «Вставить» / «Выделить всё» | |

Пришлите строки `debug-context-menu` из журнала: теперь в них есть подписи пунктов (`Label`) и содержимое
подменю — по ним уточню список блокируемых пунктов (`other` и `moreTools` пока не опознаны).

## Шаг 7. Реальные расширения (вопрос 3)

Сначала — проверка по политике без браузера (пройдёт ли установку и почему):
```powershell
dotnet run tools/check-extension.cs -- <папка с manifest.json> samples\policies\default.json
```

Затем для каждого нужного пользователям расширения: распакуйте (`.zip` — `Expand-Archive`; `.crx` — это zip с
заголовком; проще взять исходники или распакованную папку из профиля Chrome
`%LOCALAPPDATA%\Google\Chrome\User Data\Default\Extensions\<id>\<версия>`), добавьте через «+ Расширение…»
(выбирать папку, в которой лежит `manifest.json`) и запишите:

| Расширение | Принято политикой? (если нет — причины) | Popup работает? | Основная функция работает? | Что не работает |
|---|---|---|---|---|

### Расширение-помощник с чтением буфера обмена

Для расширений, которые читают буфер в фоне (offscreen-документ + `clipboardRead`) и управляют вкладкой
своего сайта, проверить отдельно — это API, поддержку которых в WebView2 мы ещё не видели:

| Проверка | Какой API | Результат |
|---|---|---|
| Расширение принято (при `allowExtensionClipboardRead: true`) | политика | |
| Popup открывается и показывает статус | popup | |
| Переключатель на странице сайта меняет состояние расширения | `externally_connectable` / `onMessageExternal` | |
| Копирование текста в ДРУГОМ приложении (браузер в фоне) → вкладка сайта реагирует | `offscreen`, `execCommand('paste')`, `tabs.query`, `tabs.update` | |
| Сообщение/подсказка на странице сайта от расширения | `scripting.executeScript` | |
| После перезапуска браузера состояние сохранено и функция работает | `storage`, `onStartup`, `alarms` | |
| Снимок экрана со страницей сайта (`tools\screen.ps1`) | защита окна | |

## Что прислать

1. Таблицы шагов 1–7 и тексты сообщений шага 3б.
2. `%LOCALAPPDATA%\Shadepane\audit.log`.
3. Версию Windows (`winver`) и строку `webview-ready` из журнала (версия WebView2 Runtime).

## Как принять решение

- Шаги 1 и 2 пройдены, всплывающие элементы скрыты или их утечка приемлема → остаёмся на WebView2.
- Шаг 1 провален (контент WebView2 виден в захвате) или шаг 2 (popup не работает) → форк Chromium.
- Шаг 7 провален для отдельных расширений → решаем по каждому: насколько оно критично.
