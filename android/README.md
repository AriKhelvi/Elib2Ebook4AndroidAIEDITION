# 📱 E2E для Android

> [!WARNING]
> **Всё в этой папке сгенерировано ИИ (Claude, Anthropic)** по запросу и под присмотром человека.
> Проект собирается, ядро проверено на ПК реальными загрузками, APK проверен по подписи и манифесту,
> но **стабильность не гарантируется**: приложение не проходило систематического тестирования на разных
> устройствах. Используйте на свой риск, о проблемах пишите в Issues форка (приложите журнал — см. ниже).
> Код намеренно снабжён подробными комментариями, чтобы его было легко проверить и поправить.

Android-приложение для [Elib2Ebook](https://github.com/OnlyFart/Elib2Ebook): скачивает книги с тех же 80+ сайтов
в EPUB / FB2 / CBZ / TXT прямо на телефоне — без компьютера и без сервера. Внутри работает то же ядро
(`src/Elib2Ebook.DomainServices` + все проекты сайтов из `src/ExternalServices`), что у консольной и веб-версии;
интерфейс сделан по мотивам веб-версии.

Android 10 и новее (targetSdk 36). APK собирается под arm64 — это почти все современные телефоны
(собиралось под Pixel 9a).

## Установка на телефон

1. Возьмите APK из [Releases](../../releases) или соберите сами (см. ниже) — он появится в `android\dist\`.
2. Перекиньте его на телефон: «Избранное» в Telegram, Google Диск, почта, USB-кабель.
3. Откройте файл на телефоне и разрешите установку из этого источника.
4. Play Защита предупредит о неизвестном разработчике — «Подробнее» → «Всё равно установить».

## Как пользоваться

- **Из браузера:** откройте страницу книги → «Поделиться» → **«Скачать книгу (E2E)»** — ссылка подставится сама.
  В «Настройках» можно включить «Скачивать сразу после „Поделиться“».
- **Вручную:** вставьте ссылку (кнопка «Вставить»), выберите формат(ы), «Скачать». Стрелка рядом — «Скачать без картинок».
- **Логин и пароль** — в разделе «Авторизация», с галкой «Запомнить для сайта» (хранятся в зашифрованном хранилище Android).
- **Диапазон глав** — как `--start/--end` и `--start-name/--end-name` в консольной версии.
- Загрузка идёт в фоне с уведомлением в шторке (там же «Отмена») — приложение можно свернуть.
- Готовые файлы — в **«Загрузки/Elib2Ebook»** и во вкладке **«Книги»**: открыть в читалке или
  **«Поделиться»** → Telegram, почта, Send to Kindle, облако PocketBook/Boox и т.п.
- В «Настройках» можно выбрать **свою папку** — например, ту, что Syncthing или облако читалки синхронизирует
  с электронной книгой. Там же: шаблон имени файла, обложка отдельным файлом, доп. файлы, таймаут, пауза между
  запросами, прокси.
- Если что-то не скачалось: на главном экране «Журнал» → «Скопировать журнал» и приложите его к Issue.

## Ограничения

- Некоторые сайты защищены от автоматических загрузок (Cloudflare и т.п.) и могут отдавать служебную страницу
  вместо книги — так же, как консольной версии. Обходить такую защиту приложение не умеет и не пытается.
  В частности, **Ficbook** на длинных работах через какое-то время начинает отвечать страницей
  «Проверка безопасности» — тогда загрузка обрывается с понятной ошибкой; остаётся подождать и повторить позже.
- Android 15+ ограничивает фоновую загрузку 6 часами в сутки — для книг с запасом.
- Файлы в «Загрузках», созданные до переустановки приложения, удалить из вкладки «Книги» нельзя
  (Android считает их «чужими») — только в приложении «Файлы».

## Сборка APK

### Подготовка ПК (один раз, Windows, без прав администратора)

Всё ставится в `%LOCALAPPDATA%\Elib2EbookAndroidToolchain` и удаляется вместе с этой папкой (~5 ГБ):

| Что | Куда | Откуда |
|:--|:--|:--|
| .NET SDK 10 (zip) | `...Toolchain\dotnet` | https://dotnet.microsoft.com/download/dotnet/10.0 |
| Microsoft OpenJDK 21 (zip) | `...Toolchain\jdk` | https://learn.microsoft.com/java/openjdk/download |
| workload MAUI для Android | внутрь `dotnet` | `dotnet workload install maui-android` |
| Android SDK (platform 36, build-tools) | `...Toolchain\android-sdk` | см. команду ниже |

Android SDK ставится самой сборкой (принимая лицензию Android SDK):

```powershell
$tc = "$env:LOCALAPPDATA\Elib2EbookAndroidToolchain"
$env:DOTNET_ROOT = "$tc\dotnet"
& "$tc\dotnet\dotnet.exe" build android\E2E.Droid\E2E.Droid.csproj -t:InstallAndroidDependencies -f net10.0-android `
    "-p:AndroidSdkDirectory=$tc\android-sdk" "-p:JavaSdkDirectory=$tc\jdk" -p:AcceptAndroidSdkLicenses=True
```

Если в глобальном NuGet.Config нет источников, хватит `nuget.config` в корне репозитория (там указан nuget.org).

### Сборка

Двойной щелчок по **`android\build.cmd`** (или `powershell -ExecutionPolicy Bypass -File android\build.ps1`).
Готовый подписанный APK появится в `android\dist\`.

- `build.cmd -Pull` — сначала `git pull` (например, после «Sync fork» на GitHub).
- `build.cmd -Abi "android-arm64;android-x64"` — добавить x86_64 для эмулятора.
- `build.cmd -SigningDir D:\keys\e2e` — взять ключ подписи из другой папки (или переменная `E2E_SIGNING_DIR`).

> [!IMPORTANT]
> **Ключ подписи хранится ВНЕ репозитория** — по умолчанию в `%LOCALAPPDATA%\Elib2EbookAndroid\signing`
> (`e2e.keystore` + `password.txt`), создаётся при первой сборке. Не теряйте его и не коммитьте:
> APK, подписанный другим ключом, не встанет поверх установленного — придётся удалять приложение вместе с
> настройками. `.gitignore` дополнительно не пускает `*.keystore` и `android/signing/` в репозиторий.

### Автосборка в GitHub Actions

Workflow [`.github/workflows/android.yml`](../.github/workflows/android.yml) собирает APK на серверах GitHub:

| Событие | Результат |
|:--|:--|
| Пуш в `master`, затрагивающий `android/` или ядро | APK в артефактах запуска (Actions → запуск → Artifacts) |
| Actions → **Android APK** → **Run workflow** | То же, вручную |
| Публикация релиза (Releases → Draft a new release → Publish) | APK прикрепляется к релизу автоматически |
| Pull request | Только проверка, что всё собирается |

В сводке каждого запуска — размер, versionCode, число сайтов и отпечаток сертификата подписи.

**Настройка (один раз):**

1. **Включите Actions в форке.** GitHub не запускает workflow в форках, пока владелец не разрешит:
   вкладка **Actions** → «I understand my workflows, go ahead and enable them».
2. **Добавьте ключ подписи в секреты** — иначе APK из Actions будет подписан одноразовым ключом и не встанет
   поверх установленного приложения (а релиз не соберётся вовсе). Settings → Secrets and variables → Actions →
   New repository secret:
   - `E2E_KEYSTORE_BASE64` — содержимое `e2e.keystore` в base64. Скопировать в буфер обмена (PowerShell):
     ```powershell
     [Convert]::ToBase64String([IO.File]::ReadAllBytes("$env:LOCALAPPDATA\Elib2EbookAndroid\signing\e2e.keystore")) | Set-Clipboard
     ```
   - `E2E_KEYSTORE_PASSWORD` — содержимое `password.txt`:
     ```powershell
     Get-Content "$env:LOCALAPPDATA\Elib2EbookAndroid\signing\password.txt" -Raw | Set-Clipboard
     ```
   Секреты видят только workflow этого репозитория; в логах GitHub их маскирует. Никуда больше их не вставляйте.
3. Проверка: Actions → **Android APK** → **Run workflow**. В сводке запуска должно быть «Подпись: ключ владельца»
   и тот же SHA-256 сертификата, что у APK, собранного локально.

Сборка на `ubuntu-latest` занимает около 3 минут (большую часть — AOT-компиляция). Артефакты хранятся 90 дней.

> [!NOTE]
> В форке остался и CI самого ядра (`dotnet.yml`, `codeql.yml`) — он собирает консольную и веб-версию
> и с Android-сборкой не связан.

### Обновление ядра из оригинального Elib2Ebook

Новые сайты и исправления приходят из [OnlyFart/Elib2Ebook](https://github.com/OnlyFart/Elib2Ebook):
на странице форка нажмите **Sync fork → Update branch**, затем `build.cmd -Pull`. Новые проекты сайтов
подхватываются приложением автоматически (маска `src\ExternalServices\**\*.csproj`).

### Проверка ядра на ПК (без телефона)

`android\CoreSmoke` прогоняет тот же конвейер, что и приложение (файлы подключены ссылками), и проверяет
получившиеся EPUB/FB2:

```powershell
& "$tc\dotnet\dotnet.exe" run -c Release --project android\CoreSmoke -- https://author.today/work/212721 epub,fb2 --end 3
```

Без аргументов — только самопроверки: сколько сайтов загрузилось и как список из README сходится с загрузчиками.
Ключи: `--end N` (первые N глав), `--no-image`, `--verbose` (весь журнал ядра).

## Как это устроено

| Путь | Что это |
|:--|:--|
| `android\E2E.Droid\` | Приложение: .NET MAUI Blazor Hybrid, .NET 10, только Android |
| `android\E2E.Droid\Services\` | Конвейер скачивания, очередь, настройки, история — без Android-специфики |
| `android\E2E.Droid\Platforms\Android\` | Сохранение (MediaStore / выбранная папка), foreground-сервис, «Поделиться» |
| `android\E2E.Droid\Components\` | Интерфейс (Razor); стили веб-версии — `wwwroot\css\e2e-web.css`, мобильные — `app.css` |
| `android\CoreSmoke\` | Проверка конвейера на ПК |
| `android\Directory.*.props` | Изолируют android/ от настроек сборки ядра (TargetFramework, централизованные версии пакетов) |
| `android\build.ps1`, `build.cmd` | Сборка подписанного APK |

Ключевые решения (подробности — в комментариях в коде):

- **Ядро подключено как есть** через `ProjectReference`; логика сайтов в приложении не дублируется.
- **Поиск загрузчиков.** Штатный `GetterFactory` ищет сборки сайтов через `deps.json` или файлы `*.dll`, а на
  Android их нет. Поэтому список сборок генерируется при сборке из `ProjectReference` (`ExternalServicesList`),
  а `GetterResolver` загружает их по имени. Все эти сборки исключены из тримминга целиком.
- **HTTP как на ПК.** `UseNativeHttpHandler=false` — используется `SocketsHttpHandler`: ядро работает с HTTP/2
  и настраивает `HttpClientHandler`, а нативный Android-обработчик HTTP/2 не умеет.
- **Фоновая загрузка** — foreground-сервис типа dataSync с уведомлением и wake lock.
- **Сохранение** — MediaStore (без разрешений) или папка, выбранная через Storage Access Framework.
- **Вкладка «Сайты»** — список из README ядра (вшит в APK), сверенный с доменами загрузчиков: видно, что из README
  реально работает и что ядро умеет, но в README не попало.

## Лицензия

Elib2Ebook распространяется под GPL-3.0 (см. [LICENSE](../LICENSE)); Android-приложение — производная работа
под той же лицензией.
