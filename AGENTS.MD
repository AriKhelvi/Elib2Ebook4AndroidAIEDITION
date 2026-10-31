# Elib2Ebook Android
Форк Elib2Ebook (upstream: OnlyFart/Elib2Ebook) с Android-приложением на .NET 10 MAUI.

## Структура
- src/ — код upstream: Domain, DomainServices, ExternalServices (парсеры сайтов). Менять только по необходимости.
- android/E2E.Droid — Android-приложение: Razor-страницы, Services, Platforms/Android.
- android/CoreSmoke — смоук-проверка ядра.
- tests/ — тесты upstream.

## Сборка
- dotnet build android/E2E.Droid/E2E.Droid.csproj -f net10.0-android -c Debug

## Правила
- Не коммитить APK, bin/, obj/, ключи и пароли.
- Одна задача — один pull request с описанием изменений.
- Ответы, комментарии и описания PR — на русском.

## Code Review Rules
- Проверять ошибки логики, падения на Android, утечки ключей и паролей.
- Отмечать изменения в src/ (код upstream) и просить обоснование.
- Проверять, что проект собирается командой из раздела «Сборка».
