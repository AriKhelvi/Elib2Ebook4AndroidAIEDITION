// ============================================================================
// E2E для Android — точка входа MAUI: регистрация сервисов, кодировок и списка сайтов
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Text;
using E2E.Droid.Services;

namespace E2E.Droid;

public static class MauiProgram
{
    public static IServiceProvider Services { get; private set; }

    public static MauiApp CreateMauiApp()
    {
        // Сайты на windows-1251 и т.п. — как в Elib2EbookCli
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        GetterResolver.Configure(ExternalServicesList.AssemblyNames);
        SupportedSites.Configure(ExternalServicesList.DisabledSites);

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        builder.Services.AddSingleton<AppSettings>();
        builder.Services.AddSingleton<HistoryStore>();
        builder.Services.AddSingleton<ISecureStorage>(SecureStorage.Default);
        builder.Services.AddSingleton<IPreferences>(Preferences.Default);
        builder.Services.AddSingleton<CredentialStore>();
        builder.Services.AddSingleton<ToastService>();
        builder.Services.AddSingleton<FormState>();
        builder.Services.AddSingleton<IBookStorage, AndroidBookStorage>();
        builder.Services.AddSingleton<IPlatformServices, AndroidPlatformServices>();
        builder.Services.AddSingleton<DownloadManager>();
        builder.Services.AddSingleton<BookActions>();

        var app = builder.Build();
        Services = app.Services;

        // Подчищаем рабочие папки прошлых запусков и заранее грузим сборки сайтов (~60 dll)
        Task.Run(() =>
        {
            DownloadManager.CleanupLeftovers();
            _ = GetterResolver.GetterTypes;
        });

        return app;
    }
}
