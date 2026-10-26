// ============================================================================
// E2E для Android — уведомления, фоновый сервис и цвета системных панелей
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using Android.App;
using AndroidX.Core.View;
using E2E.Droid.Services;

namespace E2E.Droid;

/// <summary>
/// Android-реализация <see cref="IPlatformServices"/>: всё, что общая логика (DownloadManager, интерфейс)
/// просит у системы — уведомления, фоновый сервис, цвет системных панелей.
/// </summary>
public sealed class AndroidPlatformServices : IPlatformServices
{
    /// <summary>Id уведомления «Книга готова» (каждое новое заменяет предыдущее).</summary>
    private const int FinishedId = 2001;

    /// <summary>Спрашиваем разрешение на уведомления один раз за запуск, а не перед каждой книгой.</summary>
    private bool _notificationsAsked;

    private static Android.Content.Context Context => Platform.AppContext;

    /// <summary>Видно ли приложение на экране. Обновляется из событий окна в App.xaml.cs.</summary>
    public static bool Foreground { get; set; }

    public bool IsInForeground => Foreground;

    /// <summary>versionName (версия ядра) и versionCode (номер сборки) — для «О приложении».</summary>
    public string AppVersion => $"{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";

    /// <summary>
    /// Android 13+ требует явного разрешения на уведомления. Отказ ничего не ломает:
    /// загрузка идёт, просто прогресса в шторке не видно.
    /// </summary>
    public async Task RequestNotificationPermissionAsync()
    {
        if (_notificationsAsked || !OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return;
        }

        _notificationsAsked = true;
        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
                {
                    await Permissions.RequestAsync<Permissions.PostNotifications>();
                }
            });
        }
        catch
        {
            // без уведомлений загрузка всё равно работает
        }
    }

    /// <summary>Запуск foreground-сервиса с уведомлением «Скачиваю книгу».</summary>
    public void StartBackgroundWork(string text)
    {
        try
        {
            DownloadService.EnsureChannels(Context);
            DownloadService.Start(Context, text);
        }
        catch
        {
            // если система не дала запустить сервис, качаем без него — пока приложение открыто, это работает
        }
    }

    public void UpdateBackgroundWork(string text)
    {
        try
        {
            DownloadService.Update(Context, text);
        }
        catch
        {
            // уведомление — не критично
        }
    }

    /// <summary>Остановка сервиса (безопасна, даже если он ещё не успел стартовать — см. DownloadService.Stop).</summary>
    public void StopBackgroundWork() => DownloadService.Stop(Context);

    /// <summary>Уведомление «Книга готова» — показывается, только если приложение свёрнуто; по нажатию открывает его.</summary>
    public void NotifyFinished(string title, string text)
    {
        if (Context.GetSystemService(Android.Content.Context.NotificationService) is not NotificationManager manager)
        {
            return;
        }

        DownloadService.EnsureChannels(Context);
        var notification = new Notification.Builder(Context, DownloadService.FinishedChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_e2e)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetStyle(new Notification.BigTextStyle().BigText(text))
            .SetAutoCancel(true)
            .SetContentIntent(DownloadService.OpenAppIntent(Context))
            .Build();
        manager.Notify(FinishedId, notification);
    }

    /// <summary>
    /// Синхронизация системных панелей с темой веб-интерфейса (светлая/тёмная/как в системе).
    /// На Android 15+ приложение рисуется «от края до края», а MainPage с SafeAreaEdges=All отступает
    /// от статус-бара и жестовой панели — под ними виден фон страницы. Его и красим в цвет темы,
    /// а значки статус-бара делаем тёмными на светлом фоне и светлыми на тёмном.
    /// </summary>
    public void ApplyTheme(bool dark)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Цвета совпадают с --bg в e2e-web.css: светлая тема #F5F5F7, тёмная #000000
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page != null)
            {
                page.BackgroundColor = Color.FromArgb(dark ? "#000000" : "#F5F5F7");
            }

            var window = Platform.CurrentActivity?.Window;
            if (window != null)
            {
                var controller = WindowCompat.GetInsetsController(window, window.DecorView);
                controller.AppearanceLightStatusBars = !dark;
                controller.AppearanceLightNavigationBars = !dark;
            }
        });
    }
}
