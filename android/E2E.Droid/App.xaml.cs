// ============================================================================
// E2E для Android — приложение MAUI: создаёт окно и отслеживает, видно ли приложение на экране
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

namespace E2E.Droid;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState activationState)
    {
        var window = new Window(new MainPage()) { Title = "E2E" };

        // Уведомление «Книга готова» показываем, только если приложение не на экране
        window.Activated += (_, _) => AndroidPlatformServices.Foreground = true;
        window.Resumed += (_, _) => AndroidPlatformServices.Foreground = true;
        window.Stopped += (_, _) => AndroidPlatformServices.Foreground = false;
        window.Destroying += (_, _) => AndroidPlatformServices.Foreground = false;

        return window;
    }
}
