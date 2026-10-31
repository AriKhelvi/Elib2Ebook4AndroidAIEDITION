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
    private static int _cacheWarmupStarted;

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
        builder.Services.AddSingleton<DiagnosticsRunner>();

        var app = builder.Build();
        Services = app.Services;

        // Ядро само принимает любой сертификат — на телефоне проверяем (см. CertificatePolicy).
        // «Не проверять сертификаты» в настройках читается при каждом соединении — перезапуск не нужен
        var settings = app.Services.GetRequiredService<AppSettings>();
        CertificatePolicy.AllowInsecure = () => settings.AllowInsecureTls;
        Elib2Ebook.DomainServices.Configs.BookGetterConfig.ServerCertificateValidator = CertificatePolicy.Validate;

        // Очистку старых рабочих папок запускаем сразу; прогрев сборок дождётся первого кадра интерфейса.
        _ = Task.Run(DownloadManager.CleanupLeftovers);

        return app;
    }

    /// <summary>Вызывается из Blazor после первого отображённого кадра; один раз за жизнь приложения.</summary>
    internal static void WarmUpAfterFirstRender()
    {
        if (Interlocked.Exchange(ref _cacheWarmupStarted, 1) == 0)
        {
            _ = Task.Run(() => { _ = GetterResolver.GetterTypes; });
        }
    }
}
