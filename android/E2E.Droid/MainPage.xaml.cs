// ============================================================================
// E2E для Android — единственная страница MAUI: системная кнопка «Назад»
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

namespace E2E.Droid;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    /// <summary>Системная «Назад» листает историю вкладок внутри приложения, а не закрывает его сразу.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (blazorWebView.Handler?.PlatformView is Android.Webkit.WebView webView && webView.CanGoBack())
        {
            webView.GoBack();
            return true;
        }

        return base.OnBackButtonPressed();
    }
}
