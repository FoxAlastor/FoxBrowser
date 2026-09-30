Реалізуй чотири незалежні покращення в FoxBrowser. Роби їх послідовно, 
кожне — окремим комітом, з коротким тестовим сценарієм на кінці. 
Не чіпай TabManager/watchdog-логіку, якщо це не потрібно напряму для задачі.

## 1. Підтримка розширень (Chrome-сумісні extensions)

WebView2 має нативну підтримку unpacked-розширень через CoreWebView2Profile:

- При створенні CoreWebView2Environment виставити 
  `CoreWebView2EnvironmentOptions.AreBrowserExtensionsEnabled = true`
  (без цього прапорця AddBrowserExtensionAsync повертає ERROR_NOT_SUPPORTED).
- Додати `Core/ExtensionManager.cs`:
  - `AddExtensionAsync(string folderPath)` → `profile.AddBrowserExtensionAsync(folderPath)`.
    Важливо: приймається тільки шлях до **розпакованої** папки з manifest.json 
    (той самий формат, що й Chrome "Load unpacked" у режимі розробника) — 
    прямого встановлення з Chrome Web Store за URL немає в API, це нормально, 
    не намагайся емулювати завантаження з магазину.
  - `GetInstalledExtensionsAsync()` → `profile.GetBrowserExtensionsAsync()`.
  - Enable/disable через `CoreWebView2BrowserExtension.IsEnabled`, видалення — `.Remove()`.
- UI: нове вікно/секція в налаштуваннях `ExtensionsSettingsWindow.xaml` — список 
  встановлених розширень (назва, іконка якщо є, перемикач увімк/вимк, кнопка 
  видалити) + кнопка "Додати" з `System.Windows.Forms.FolderBrowserDialog` для 
  вибору папки розширення.
- Розширення персистяться в профілі (user data folder) автоматично — 
  не треба вручну зберігати список між запусками, просто перечитувати 
  GetBrowserExtensionsAsync() при старті.
- ⚠️ Це API позначене в документації Microsoft як таке, що ще може змінюватись 
  ("prerelease"/experimental) — перевір актуальну версію WebView2 SDK 
  в csproj, і якщо AddBrowserExtensionAsync недоступний у поточній версії, 
  онови NuGet-пакет Microsoft.Web.WebView2 до версії, де метод є.

## 2. Темна смужка заголовка вікна (title bar)

Стандартний WPF Window малює системну (світлу) title bar незалежно від теми 
застосунку. Потрібен P/Invoke до DWM:

- У `Native/Win32Interop.cs` додати сигнатуру:
  `DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size)`.
- Атрибут `DWMWA_USE_IMMERSIVE_DARK_MODE` = 20 (Windows 10 build 19041+ і всі 
  Windows 11); для старіших білдів Windows 10 (до 19041) значення атрибута — 19. 
  Визнач білд через `Environment.OSVersion` і підстав правильне число, або 
  просто пробуй 20 і фолбек на 19, якщо виклик повернув помилку.
- Викликати з value=1 (true) у `MainWindow.xaml.cs` в обробнику 
  `SourceInitialized` (handle вікна вже створений на цьому етапі, 
  раніше — hwnd ще нульовий і виклик не подіє).
- Якщо застосунок надалі планує світлу/темну тему як перемикач — винести 
  виклик у окремий метод `ApplyDarkTitleBar(bool enabled)` і викликати його 
  і при старті, і при зміні теми в налаштуваннях, а не хардкодити один раз.

## 3. Прибрати кнопку "у трей" з головного вікна

- У `MainWindow.xaml` знайти і видалити XAML-кнопку, що ховає вікно в трей 
  (і відповідний Click-обробник у code-behind, якщо він не використовується 
  більше ніде).
- Функціональність згортання в трей НЕ прибирати — вона має лишитись 
  доступною через стандартну поведінку: клік на "згорнути" (мінімізувати) 
  вікна або на "закрити" (якщо зараз close перехоплюється і ховає замість 
  завершення застосунку — залиш як є). Перевір `TrayIconManager.cs` — 
  контекстне меню трею (Show/Hide, Exit) лишається без змін.
- Ціль — прибрати саме зайвий UI-елемент з панелі інструментів вікна, 
  не ламаючи існуючий цикл згортання/розгортання.

## 4. Виправити подвійний клік по іконці трею

Зараз: перший клік на іконку в треї лише "проявляє" застосунок на панелі 
задач, і тільки другий клік по цій новій іконці на панелі задач реально 
відкриває вікно. Це означає, що десь у `TrayIconManager.cs` логіка розбита 
на два окремі кроки замість одного atomic-виклику, або що обробник висить 
на `MouseDoubleClick` замість одинарного кліку лівою кнопкою, а проміжний 
стан (ShowInTaskbar=true) відбувається окремо від Show()/Activate().

Виправлення:
- Знайти обробник кліку по NotifyIcon (ймовірно `MouseClick`/`MouseDoubleClick` 
  на `System.Windows.Forms.NotifyIcon` або аналог з бібліотеки трею, якщо 
  використовується інша, напр. Hardcodet.NotifyIcon.Wpf).
- Прибрати залежність на подвійний клік — підписатись на одинарний клік 
  лівою кнопкою (`MouseClick` з перевіркою `e.Button == MouseButtons.Left`, 
  або відповідний однократний eventArgs у використовуваній бібліотеці).
- В одному обробнику, синхронно й без проміжних станів, виконати весь ланцюжок:
```csharp
  mainWindow.ShowInTaskbar = true;
  mainWindow.Show();
  mainWindow.WindowState = WindowState.Normal;
  mainWindow.Activate();
```
  Порядок важливий: ShowInTaskbar виставляється ДО Show(), інакше вікно 
  може з'явитись без іконки на панелі задач до наступного перемальовування.
- Протестувати сценарій: застосунок згорнутий у трей (ShowInTaskbar=false, 
  Hide()) → один клік лівою кнопкою по іконці трею → вікно одразу видиме, 
  активне, і з'являється на панелі задач за один крок, без другого кліка.
- Right-click (контекстне меню Show/Hide/Exit) не займати — воно вже працює 
  окремо і його логіку не чіпати.

Після кожного пункту напиши короткий підсумок, що саме змінено і як це 
перевірити вручну на Windows-машині (агент у Linux sandbox без dotnet/WPF 
runtime, тому фінальну компіляцію й ручний тест виконує користувач).