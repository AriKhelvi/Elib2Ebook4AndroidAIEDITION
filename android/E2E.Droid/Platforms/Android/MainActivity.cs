// ============================================================================
// E2E для Android — главная Activity: приём «Поделиться», результаты системных диалогов
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using E2E.Droid.Services;

namespace E2E.Droid;

/// <summary>
/// Единственная Activity приложения. Внутри неё MAUI показывает MainPage с BlazorWebView.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>LaunchMode.SingleTask — при «Поделиться» из браузера не создаётся вторая копия приложения:
/// уже открытая получает ссылку через OnNewIntent.</item>
/// <item>ConfigurationChanges — поворот экрана, смена темы и т.п. не пересоздают Activity
/// (иначе WebView перезагрузился бы и форма сбросилась).</item>
/// <item>IntentFilter ACTION_SEND text/plain — именно он добавляет «Скачать книгу (E2E)» в меню «Поделиться».</item>
/// </list>
/// </remarks>
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// «Поделиться» → E2E из браузера или любого приложения, которое шлёт ссылку текстом
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain", Label = "Скачать книгу (E2E)")]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>
    /// Минимальная версия встроенного браузера (Android System WebView / Chrome). Скрипт Blazor
    /// (_framework/blazor.webview.js из .NET 10) написан на ES2022 — статические блоки классов, ??=, ?. —
    /// и на более старом движке падает на разборе: вместо интерфейса остаётся пустой экран.
    /// </summary>
    private const int MinWebViewMajor = 94;

    /// <summary>Холодный старт: приложение могли запустить прямо из «Поделиться».</summary>
    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // savedInstanceState != null — Activity восстанавливается системой; тот же Intent уже был обработан
        if (savedInstanceState == null)
        {
            HandleIntent(Intent);
            WarnIfWebViewTooOld();
        }
    }

    /// <summary>
    /// Читалки и телефоны без Google Play часто живут с тем браузером, что был в прошивке (на Android 7–10 это
    /// Chrome 51–80). Тогда вместо пустого экрана объясняем, что обновить.
    /// </summary>
    private void WarnIfWebViewTooOld()
    {
        int? major;
        try
        {
            // «… Chrome/119.0.6045.193 Mobile Safari/537.36» — версию движка проще всего взять из User-Agent
            var userAgent = Android.Webkit.WebSettings.GetDefaultUserAgent(this) ?? string.Empty;
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Chrome/(\d+)\.");
            major = match.Success ? int.Parse(match.Groups[1].Value) : null;
        }
        catch
        {
            return; // не смогли узнать — не мешаем запуску
        }

        if (major is not { } version || version >= MinWebViewMajor)
        {
            return;
        }

        new AndroidX.AppCompat.App.AlertDialog.Builder(this)
            .SetTitle("Нужно обновить встроенный браузер")!
            .SetMessage(
                $"Интерфейс приложения работает во встроенном браузере Android, а на этом устройстве он слишком старый " +
                $"(Chrome {version}, нужен {MinWebViewMajor} или новее).\n\n" +
                "Обновите «Android System WebView» (на Android 7–9 — или Chrome) в Google Play. " +
                "Если Google Play нет, установите свежий APK Android System WebView вручную.")!
            .SetPositiveButton("Открыть Google Play", (_, _) => OpenWebViewInStore())!
            .SetNegativeButton("Закрыть", (_, _) => { })!
            .Show();
    }

    private void OpenWebViewInStore()
    {
        const string package = "com.google.android.webview";
        try
        {
            StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse($"market://details?id={package}")));
        }
        catch (ActivityNotFoundException)
        {
            // Google Play нет — открываем страницу в браузере (если хоть какой-то есть)
            try
            {
                StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse($"https://play.google.com/store/apps/details?id={package}")));
            }
            catch (ActivityNotFoundException)
            {
                // открыть нечем — текст диалога уже объяснил, что делать
            }
        }
    }

    /// <summary>Приложение уже открыто (SingleTask), и в него снова «поделились» ссылкой.</summary>
    protected override void OnNewIntent(Intent intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        HandleIntent(intent);
    }

    /// <summary>Результат системных диалогов (выбор папки) — передаём в ActivityResults, где его ждёт await.</summary>
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        ActivityResults.Handle(requestCode, resultCode, data);
    }

    /// <summary>Достаёт ссылку из «Поделиться» и кладёт в SharedLinkInbox — оттуда её заберёт главный экран.</summary>
    private static void HandleIntent(Intent intent)
    {
        if (intent?.Action != Intent.ActionSend)
        {
            return;
        }

        // Браузеры кладут ссылку в EXTRA_TEXT (иногда вместе с заголовком страницы), некоторые приложения — в SUBJECT
        var text = intent.GetStringExtra(Intent.ExtraText);
        if (SharedLinkInbox.ExtractUrls(text).Count == 0)
        {
            text = $"{text} {intent.GetStringExtra(Intent.ExtraSubject)}";
        }

        SharedLinkInbox.Publish(text);
    }
}
