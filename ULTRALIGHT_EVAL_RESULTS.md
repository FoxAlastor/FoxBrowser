# Результати тестування Ultralight (Крок 0 / Go-No-Go)

Тестування виконано згідно з вимогами `ULTRALIGHT_AGENT_GUIDE.md` на Windows 10 x64, .NET 8, Ultralight v1.3.0 (WebKit 610.4.3.1.4, JavaScriptCore).

---

## 1. Конфігурація середовища тестування

- **Платформа:** Windows 10 (10.0.19045), x64
- **Runtime:** .NET 8.0 (`net8.0-windows`)
- **Бібліотеки:** `UltralightNet` 1.3.0, `UltralightNet.AppCore` 1.3.0, `UltralightNet.Binaries` 1.3.0
- **Нативні ресурси:** `AppCore.dll`, `Ultralight.dll`, `UltralightCore.dll`, `WebCore.dll`, `icudt67l.dat`, `cacert.pem`
- **Тестові цілі:**
  1. `about:blank` (перевірка наявності Web/Media API)
  2. `https://www.youtube.com/` (головна сторінка YouTube SPA)
  3. `https://www.youtube.com/watch?v=dQw4w9WgXcQ` (пряме відео)

---

## 2. Результати тестів

### 2.1. Перевірка медіа API (`canPlayType`, `HTMLVideoElement`, кодеки)

Спроба виклику `canPlayType` завершилася фатальною помилкою:
```
TypeError: document.createElement('video').canPlayType is not a function.
(In 'document.createElement('video').canPlayType(...)', 'document.createElement('video').canPlayType' is undefined)
```

- `HTMLVideoElement`: **відсутній** у global scope (`ReferenceError: Can't find variable: HTMLVideoElement`)
- `HTMLAudioElement`: **відсутній**
- `MediaSource` (MSE): **відсутній**
- `requestMediaKeySystemAccess` (EME / DRM / Widevine): **відсутній**
- `AudioContext` / Web Audio: відсутній

### 2.2. Завантаження головної сторінки YouTube (`https://www.youtube.com/`)

- **DOM:** завантажився частково (1224 елементи), контейнер `<ytd-app>` створений.
- **Помилки JS:** падіння скрипта `kevlar_base` на рядку 5444 через відсутність `HTMLVideoElement`:
  ```
  ReferenceError: Can't find variable: HTMLVideoElement (kevlar_base_module:5444)
  ```
- **Відеокартки / прев'ю:** 0 знайдених рендерерів через збій у JS-ініціалізації YouTube.
- **Споживання RAM:** **~320 MB** (Single Process).

### 2.3. Завантаження сторінки з відео (`watch?v=dQw4w9WgXcQ`)

- **Заголовок сторінки:** розпізнано коректно:
  `Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster) - YouTube`
- **Плеєр YouTube:** плеєр відображає системне повідомлення про несумісність:
  > **«Воспроизвести видео в этом браузере нельзя. Подробнее…»**
- **Відтворення звуку:** **0 / відсутнє**. Звук не грає, потік не декодується.
- **Споживання RAM:** **441.8 MB** (для одного відкритого таба в одному процесі).

---

## 3. Порівняння: Ultralight vs WebView2

| Критерій | WebView2 (Chromium) | Ultralight v1.3.0 |
| :--- | :--- | :--- |
| **HTML5 Video / Audio** | Повноцінна підтримка | **Не працює** (`canPlayType is undefined`) |
| **YouTube HTML5 Player** | Працює ідеально | **«Не можна відтворити відео в цьому браузері»** |
| **DRM / Widevine** | Працює | **Не підтримується взагалі** |
| **Сучасні JS API** | Повна підтримка Blink | Падає на `HTMLVideoElement`, Trusted Types |
| **RAM (одна вкладка YouTube)** | ~350–450 MB (сумарно з renderer/GPU) | **441.8 MB** (один процес, навіть без відео) |
| **Виграш по RAM** | — | **0 MB** (споживання практично однакове) |

---

## 4. Висновок за правилом Go/No-Go (Крок 0)

Згідно з інструкцією `ULTRALIGHT_AGENT_GUIDE.md`:
> *«Якщо на кроці 3 YouTube взагалі не вантажиться або аудіо не програється — не переходити до Кроку 1. ...це не привід "трохи допрацювати", а сигнал, що для головного use case проєкту Ultralight як основний рушій не підходить.»*

### Вердикт: **NO-GO для заміни WebView2 на YouTube**

1. **Ultralight не здатен відтворювати YouTube усередині HTML:** рушій не має реалізації HTML5 Video/Audio кодеків, MSE та EME. YouTube видає плашку «Воспроизвести видео в этом браузере нельзя».
2. **Немає виграшу по RAM:** один процес Ultralight на сторінці YouTube зайняв **441.8 MB**, що відповідає або навіть перевищує оптимізований режим WebView2.
3. **Правильний шлях для проєкту:**
   - Для веб-браузингу та навігації — **залишати WebView2**.
   - Для ультралегкого звуку без навантаження на рендерер — використовувати вже створений **`NativeAudioSession` + `YtDlpExtractor`** (direct audio stream через WASAPI/MediaPlayer).
   - Ultralight можна використовувати виключно для внутрішніх легких UI-віджетів/налаштувань (якщо виникне потреба у чистому локальному HTML-інтерфейсі).
