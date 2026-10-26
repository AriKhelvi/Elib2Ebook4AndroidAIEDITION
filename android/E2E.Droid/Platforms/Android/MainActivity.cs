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
    /// <summary>Холодный старт: приложение могли запустить прямо из «Поделиться».</summary>
    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // savedInstanceState != null — Activity восстанавливается системой; тот же Intent уже был обработан
        if (savedInstanceState == null)
        {
            HandleIntent(Intent);
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
