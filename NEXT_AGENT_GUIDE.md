# FoxBrowser — інструкція для наступного агента

## Мета проєкту

FoxBrowser — Windows-first легкий фоновий браузер для музики/відео. Мета — не зробити ще один Chromium-клон, а зменшити кількість одночасно живого web-коду, renderer-ів, мережевих ресурсів і візуального навантаження.

Ключовий сценарій: користувач відкриває багато логічних вкладок, але у фоні реально працює одна або кілька медіа-вкладок. Важливо не ламати звук і повертати стан вкладки після перемикання.

## Стек і запуск

- C# / .NET 8;
- WPF;
- Microsoft.Web.WebView2 `1.0.2903.40`;
- Windows 10 1809+ і Microsoft Edge WebView2 Runtime.

Поточний агент працює в Linux sandbox без `dotnet`, Windows SDK та WebView2 Runtime, тому перед релізом потрібно запускати:

```powershell
dotnet restore
dotnet build -c Debug
dotnet run --project .\FoxBrowser.csproj
```

## Що вже зроблено

### Shell і UI

- WPF shell, адресний рядок, назад/вперед/оновити;
- логічні вкладки;
- системний трей;
- кнопка `♫ Audio`;
- явна кнопка `Перейти`;
- address bar синхронізується з активною вкладкою і оновлюється після навігації по посиланнях;
- закриття вкладок виправлено на WPF `Button`.

### Один WebView2

`Core/TabManager.cs` використовує **один спільний WebView2**. `BrowserTab` у `Tab.cs` містить тільки metadata: URL, title, scroll position, media position, прапори Audio/playing/loading.

При перемиканні:

1. поточний URL/scroll/media state зберігається через JS;
2. активною стає інша логічна вкладка;
3. єдиний WebView2 переходить на її URL;
4. після `NavigationCompleted` відновлюються scroll position, currentTime і playback, якщо попереднє медіа грало.

### Resource firewall

Через `WebResourceRequested` блокуються відомі рекламні й аналітичні host-и. У lightweight/minimized режимі блокуються images і fonts. Окремого `CoreWebView2WebResourceContext.Iframe` **не існує**, тому iframe/embed фільтруються тільки консервативно за URL (`/embed/`, `livechat`). Не повертати enum `Iframe` у код.

### Audio / minimal YouTube

Кнопка Audio:

- приховує картинки, SVG, canvas і відеовізуалізацію;
- прибирає частину YouTube UI, рекомендації, comments, chat, Shorts/shelves;
- прибирає preload/prefetch/next;
- залишає media element для звуку.

CSS/JS не може звільнити базову пам’ять JavaScript runtime YouTube. Це оптимізація трафіку/DOM/GPU, а не повна заміна renderer.

### Minimized і watchdog

- при згортанні у трей встановлюється minimized mode;
- нові навігації блокуються, візуальний шар стискається, звук не зупиняється;
- watchdog кожні 30 секунд перевіряє Working Set;
- поріг soft-reload — 850 MB;
- перед reload зберігаються URL, scrollY, currentTime, playing;
- після reload стан відновлюється з повторними спробами через 300 і 1000 мс.

### Native audio boundary

`Core/NativeAudioSession.cs` містить `INativeAudioSession` і WPF `MediaPlayer` для **direct audio URL**. Автоматичного YouTube extractor немає. Це навмисно: extractor нестабільний через зміни YouTube, авторизацію, DRM та правила сервісу.

## Що ще не зроблено

1. Фінальна компіляція на Windows і тест WebView2 Runtime.
2. Реальний вимір RAM/CPU/GPU у старій і новій архітектурі.
3. Надійне збереження cookies/session state між переходами — профіль WebView2 існує, але стан вкладки поки metadata-only.
4. Повне відновлення плейлистів, form state, history та відкритих popup/window.
5. Відокремлений native YouTube audio backend із optional extractor.
6. Win32 always-on-bottom (`SetWinEventHook`) ще не реалізований.
7. Не можна блокувати всі iframe, scripts або media: YouTube зламається. Firewall має бути allow-by-default, deny тільки для перевірених ресурсів.

## Відомі ризики

- `ExecuteScriptAsync` повертає JSON-encoded string; для state parsing використовувати `JsonSerializer.Deserialize<string>(result)`, а не простий `Trim('"')`.
- Перемикання вкладки не повинно повторно запускати navigation через `SelectionChanged`; у `TabManager` є guard для вже активної вкладки.
- Після переходу посиланням address bar оновлюється через `ActiveTabStateChanged`.
- `WasPlaying` не гарантує autoplay: сайт може блокувати `play()` після navigation.
- Сховати `<video>` не означає гарантовано вимкнути video decode.
- SSD може зберігати metadata, але не може замінити RAM для живого JS/audio renderer.

## Що з власним браузерним engine

Повний власний engine HTML/CSS/JS не є практичним наступним кроком. Потрібні DOM/layout, CSS engine, JS engine, network/security sandbox, cookies/CORS, media codecs, WebRTC, DRM і web compatibility. YouTube-сумісний engine — це багаторічний проєкт рівня Chromium/WebKit.

Реалістичні альтернативи:

- власний **browser shell** над системним WebView2;
- один живий WebView2 + metadata tabs;
- власний resource firewall;
- окремий native audio backend для direct streams;
- у далекій перспективі — власний вузький media client, але не власний універсальний HTML engine.

Критерій переходу до іншої технології: спочатку виміряти shared WebView2. Якщо одна активна YouTube-вкладка все одно споживає неприйнятну RAM, не писати engine з нуля; робити окремий lightweight media client/native audio path.


## FEATURE_REQUEST_2026-09 — виконано

### 1. Chrome-сумісні unpacked extensions

У `Core/ExtensionManager.cs` додано роботу з `CoreWebView2Profile`: перечитування встановлених розширень, додавання папки з `manifest.json`, enable/disable через `EnableAsync(bool)` і видалення через `RemoveAsync()`. Важливо: у .NET WebView2 `IsEnabled` — read-only property; його не можна присвоювати напряму.

`MainWindow` створює WebView2 environment з `AreBrowserExtensionsEnabled = true`. У `UI/ExtensionsSettingsWindow.xaml` додано список розширень, перемикач стану, видалення і вибір папки через `FolderBrowserDialog`. WebView2 SDK `1.0.2903.40` містить потрібний API.

Ручний тест Windows:

1. Підготувати unpacked extension folder із `manifest.json`.
2. Запустити FoxBrowser і натиснути **Розширення**.
3. Натиснути **Додати розширення…**, вибрати папку.
4. Перевірити, що extension з’явився у списку.
5. Вимкнути/увімкнути checkbox і перевірити поведінку extension у новій навігації.
6. Видалити extension, перезапустити FoxBrowser і переконатися, що він не повернувся.

### 2. Темна title bar

У `Native/Win32Interop.cs` додано `DwmSetWindowAttribute` з атрибутом 20 і fallback на 19. `MainWindow` викликає `ApplyDarkTitleBar(true)` у `SourceInitialized`, коли HWND уже створений.

Ручний тест Windows:

1. Запустити FoxBrowser на Windows 10 19041+ або Windows 11.
2. Перевірити, що системна title bar темна, а не стандартна світла.
3. Перевірити, що вікно можна згортати, розгортати й закривати без зміни поведінки.

### 3. Прибрана кнопка «Трей» з головного вікна

Кнопка `Трей` видалена з `MainWindow.xaml`. Сам `TrayIconManager` і згортання через системні кнопки вікна залишені. Невикористаний `HideToTray_Click` прибраний із code-behind.

Ручний тест Windows:

1. Перевірити, що у toolbar немає кнопки `Трей`.
2. Натиснути стандартну кнопку minimize.
3. Переконатися, що застосунок ховається у tray.
4. Через tray відкрити його назад.

### 4. Одинарний клік по tray icon

`TrayIconManager` більше не використовує `DoubleClick`. Обробляється `MouseClick` з перевіркою `MouseButtons.Left`. В одному atomic-ланцюжку виставляються `ShowInTaskbar = true`, `Show()`, `WindowState = Normal`, `Activate()`. При приховуванні `ShowInTaskbar = false` виставляється до `Hide()`.

Ручний тест Windows:

1. Запустити FoxBrowser.
2. Згорнути його в tray.
3. Натиснути лівою кнопкою один раз на tray icon.
4. Перевірити, що вікно одразу видиме, активне і має taskbar entry.
5. Перевірити, що правий клік і меню `Показати / сховати` та `Вийти` працюють окремо.

Окремі git commits не створені через I/O-помилку файлової системи змонтованого каталогу; зміни залишені в робочих файлах і описані тут по незалежних етапах.


## Startup recovery fix — 0x8007139F

`MainWindow_Loaded` now retries WebView2 with a disposable recovery user-data folder when the persistent profile returns HRESULT `0x8007139F` (often caused by another WebView2/FoxBrowser process holding the profile lock or by a profile in an invalid state). The normal profile remains the first choice so extensions and cookies persist normally; recovery mode is only a startup fallback.

`TabManager.InitializeAsync` also treats extension-profile enumeration as optional: if extension APIs fail, normal browsing still starts and the extension manager is disabled for that session.

All navigation methods (`GoBack`, `GoForward`, `Reload`, `NavigateAsync`, `SwitchToTabAsync`) now return safely while `CoreWebView2` is not initialized. This prevents the previous `NullReferenceException` when a user clicked a tab or Reload while initialization had failed or was still in progress.

Manual test:

1. Close all FoxBrowser and WebView2 processes in Task Manager and start normally.
2. Start a second instance or leave an old WebView2 profile locked; the new instance should use recovery data instead of showing only an initialization error.
3. Click Reload, Back, Forward and tabs before/while startup completes; no `NullReferenceException` should appear.
4. After normal startup, close the recovery/duplicate process and start again to return to the persistent profile.


## FEATURE_REQUEST_2026-09-25 — частково виконано без запуску команд

### 1. WireGuard VPN — базова CLI-фаза

Додано `Core/VpnManager.cs` і `UI/VpnSettingsWindow.xaml(.cs)`. FoxBrowser:

- шукає зовнішній `wireguard.exe` у Program Files та App Paths registry;
- імпортує `.conf` у `%LOCALAPPDATA%\\FoxBrowser\\VPN`;
- запускає `/installtunnelservice <config>`;
- зупиняє `/uninstalltunnelservice <TunnelName>`;
- читає exit code, stdout/stderr і показує помилку;
- не встановлює WireGuard автоматично;
- показує статус VPN у головній панелі.

Це навмисно не вбудований VPN. Потрібен офіційний WireGuard for Windows.

Ручна перевірка:

1. Встановити WireGuard for Windows з https://www.wireguard.com/install/.
2. Запустити FoxBrowser і натиснути `VPN: Вимкнено`.
3. Імпортувати коректний `.conf`.
4. Вибрати профіль і натиснути Connect.
5. Перевірити статус `Connected` і мережевий маршрут.
6. Натиснути Disconnect і перевірити, що статус повернувся до `Disconnected`.
7. Повторити тест без WireGuard — має бути зрозуміле повідомлення, що зовнішній клієнт не встановлено.

### 2. Extensions settings

`ExtensionsSettingsWindow` уже існує і кнопка `Розширення` залишається в головному toolbar. Воно відкривається модально через `ShowDialog()`, так само як VPN settings window.

### 3a. Нативний плеєр прибрано з UI та code-behind

Кнопку `Native`, нижню панель плеєра, `NativeAudio_Click`, pause/stop handlers і поля `NativeAudioSession`/`yt-dlp` прибрано з `MainWindow`. JS-based Audio mode і state capture у `TabManager` не змінювалися.

`Core/NativeAudioSession.cs` і `Core/YtDlpExtractor.cs` залишені як невикористані source-файли, оскільки поточне обмеження задачі забороняє файлові shell-команди, а видалення не підтримується файловим patch API. Вони більше ніде не підключені й не впливають на runtime.

### 3b. «Тримати вкладки активними» — свідомо НЕ реалізовано напівмірою

Поточний `TabManager` має один shared `WebView2`; тому такий toggle потребує справжнього multi-WebView2 refactor. Простий bool був би брехливим: сторінки все одно перезавантажувалися б при перемиканні. Повний режим має винести resource firewall, navigation events, title/audio tracking, state capture і minimized-mode застосування з жорстко зашитого `_view` у per-tab controller abstraction.

Цей пункт зупинено до окремого рішення/етапу, як і вимагав feature request: не приховувати конфлікт архітектури під виглядом працюючого перемикача. Дефолтний shared режим і всі існуючі memory optimizations залишилися без змін.

Фінальну компіляцію та ручний тест потрібно виконати на Windows вручну.


## Extensions follow-up

Each extension row now has `Налаштування`. FoxBrowser opens the conventional `chrome-extension://<id>/options.html` page. This works only for extensions that actually declare an options page; extensions without one may show a blank/error page.

A `Магазин розширень` button opens the Chrome Web Store in the active FoxBrowser tab. WebView2's supported .NET API installs unpacked extensions from a local folder via `AddBrowserExtensionAsync`; it does not provide a Chrome Web Store/CRX installer API. Therefore the store button is a catalog/browser shortcut, not a direct one-click installer. The user must download/unpack an extension and use `Імпортувати розширення…`.

The WebView2 profile is `%LOCALAPPDATA%\\FoxBrowser\\WebView2`. The source files of an unpacked extension remain in the folder selected during import; WebView2 persists the registration in the profile but does not copy the extension source into the profile.

## Audio identity in Windows

Windows Volume Mixer usually attributes WebView2 page audio to the child process `msedgewebview2.exe`, because that renderer process owns the Chromium audio session. The parent executable name `FoxBrowser.exe` cannot reliably rename that session through ordinary WebView2 APIs. A native WASAPI session-label workaround would need to enumerate child audio sessions and repeatedly set display names; it is version-sensitive and may affect unrelated WebView2 sessions, so it was not added as a fake promise.

A guaranteed `FoxBrowser` audio identity would require a separate native audio pipeline that owns playback itself, which is exactly the native player path removed from the current product because it was not reliable for the intended YouTube scenario.


## 25.09.2026 — extension diagnostics and Windows audio labeler

### Extension diagnostics

The recovery branch now uses the shared `CreateWebViewOptions()` method, so both normal and recovery environments receive `AreBrowserExtensionsEnabled = true`. `TabManager.ExtensionsAvailable` is false when the app had to use the disposable recovery profile or when extension profile enumeration fails. The settings window displays an explicit recovery-mode message instead of silently showing an empty list.

The checkbox remains `Mode=OneWay`; `Toggle_Click` calls `EnableAsync(bool)` and restores the old visual state on failure. `FolderBrowserDialog` now receives an `IWin32Window` owner wrapper created from `WindowInteropHelper(this).Handle`.

Extension options no longer navigate the main shared WebView2. `ExtensionsSettingsWindow` creates its own lazy `WebView2` using the same `CoreWebView2Environment`, displays it inside the settings window, and navigates that isolated control to `chrome-extension://<id>/options.html`. The main tab is not replaced. The Web Store button remains a catalog shortcut; WebView2 still cannot install directly from the store.

### Audio session labeler

Added `Core/AudioSessionLabeler.cs` and the `NAudio 2.2.1` package reference. The labeler enumerates the process tree from the FoxBrowser PID, keeps only descendant `msedgewebview2.exe` PIDs, and changes `DisplayName`/`IconPath` only for matching WASAPI sessions. It subscribes to NAudio `OnSessionCreated` and also rescans every two seconds because session labels live only for the lifetime of each WASAPI session. It does not touch unrelated Edge, Teams, Outlook, or other WebView2 sessions.

The existing `FoxBrowser.ico` is already present and is used for the audio session icon. The labeler starts after WebView2 initialization and is disposed during application shutdown.

Final build and runtime verification remain Windows-only.


## Fix: extension options reported «file not found»

The previous implementation always navigated to `chrome-extension://<id>/options.html`. That is not valid for many extensions: Manifest V3 commonly declares `options_ui.page`, and older extensions may use `options_page`, with arbitrary relative paths.

`ExtensionManager` now reads `manifest.json` during import, extracts `options_ui.page` first and `options_page` second, normalizes the relative path, and persists the mapping in `%LOCALAPPDATA%\\FoxBrowser\\extension-options.json`. The isolated options WebView2 navigates to the stored manifest path instead of a hard-coded `options.html`.

Extensions imported before this fix have no stored manifest mapping. For those, remove the extension from FoxBrowser and import its folder again once. If the manifest has no `options_ui.page` or `options_page`, the extension simply has no options page and the button reports that clearly instead of showing a misleading file-not-found navigation.


## 26.09.2026 — extension UX and VPN removal

- Added `Назад до списку розширень` inside the isolated options WebView2 view. It hides the options view and restores the extension list without closing the settings window.
- Added persisted pinning via `ExtensionManager`: IDs are stored in `%LOCALAPPDATA%\\FoxBrowser\\pinned-extensions.json`, and `PinnedExtensions` updates live.
- Added a puzzle-piece icon strip near the address bar. Clicking a pinned icon opens the extensions window; the pin state itself is controlled per extension in the settings list.
- Removed the VPN button, field, startup subscription, and VPN handlers from the main browser code. The old VPN source files remain as unreferenced files because this agent session's file API does not support physical deletion; they are no longer used by the application. They can be deleted manually from the project in Visual Studio.


## 26.09.2026 — pinned extension popups and back-button layout

Pinned extension metadata now reads `action.default_popup` / `browser_action.default_popup` and `action.default_icon` / `browser_action.default_icon` from the unpacked extension manifest. It is persisted in `%LOCALAPPDATA%\\FoxBrowser\\extension-ui.json`. The pinned buttons are now on the right side of the address bar, immediately before the general `Розширення` button. An icon file is rendered when available; otherwise the first character of the extension name is used as a fallback glyph.

A pinned extension with `default_popup` opens `ExtensionPopupWindow`, a borderless 360x520 WebView2 window positioned below the clicked button, using the same WebView2 environment and closing on `Deactivated`. Extensions without a popup use the general extension list as the documented fallback. Disabling an extension immediately removes its icon from the pinned strip; re-enabling restores it.

The options view now uses a two-row Grid: a dedicated top row contains the back button and a separate bottom row contains the isolated WebView2. This removes the previous overlay/z-order ambiguity and keeps `Назад до списку розширень` visible whenever options are open.
