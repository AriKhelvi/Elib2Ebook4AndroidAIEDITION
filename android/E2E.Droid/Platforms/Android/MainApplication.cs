// ============================================================================
// E2E для Android — класс приложения Android, запускающий MAUI
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using Android.App;
using Android.Runtime;

namespace E2E.Droid;

[Application]
public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
