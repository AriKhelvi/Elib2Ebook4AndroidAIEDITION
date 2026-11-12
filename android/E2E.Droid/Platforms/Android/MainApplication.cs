// ============================================================================
// E2E для Android — класс приложения Android, запускающий MAUI
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using Android.App;
using Android.Runtime;
using E2E.Droid.Services;

namespace E2E.Droid;

[Application]
public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    public override void OnCreate()
    {
        // До MAUI, WebView и разрешений на общее хранилище.
        StartupLog.Begin(Path.Combine(FilesDir!.AbsolutePath, "logs"));
        StartupLog.Write($"Android {Android.OS.Build.VERSION.Release}; {Android.OS.Build.Manufacturer} {Android.OS.Build.Model}; ABI {string.Join(",", Android.OS.Build.SupportedAbis ?? [])}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => StartupLog.Write("Необработанная ошибка: " + e.ExceptionObject);
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => StartupLog.Write("Ошибка Android: " + e.Exception);
        try { base.OnCreate(); StartupLog.Write("Android Application создано"); }
        catch (Exception ex) { StartupLog.Write("Сбой запуска: " + ex); throw; }
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
