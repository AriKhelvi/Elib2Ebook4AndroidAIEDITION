// ============================================================================
// E2E для Android — уведомления, фоновый сервис и цвета системных панелей
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using AndroidX.Core.App;
using AndroidX.Core.View;
using E2E.Droid.Services;
using Microsoft.Maui.Platform;

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

    /// <summary>versionName — версия приложения от git-тега (0.2.0, 0.2.1-dev.5), см. android/README.md, «Версии».</summary>
    public string AppVersion => AppInfo.Current.VersionString;

    /// <summary>versionCode — номер сборки (минуты с 01.01.2026), растёт с каждой сборкой.</summary>
    public string BuildNumber => AppInfo.Current.BuildString;

    public bool SupportsDynamicColor => DynamicColors.IsSupported;

    public IReadOnlyDictionary<string, string> GetDynamicColorScheme(bool dark) => DynamicColors.GetScheme(Context, dark);

    /// <summary>
    /// Обложка уменьшается средствами Android: сначала читаются только размеры, затем картинка декодируется
    /// с прореживанием (inSampleSize) и масштабируется до 240 px по высоте — так не тратится память на огромные обложки.
    /// </summary>
    public string SaveThumbnail(byte[] image, string name)
    {
        const int targetHeight = 240;
        try
        {
            var bounds = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
            Android.Graphics.BitmapFactory.DecodeByteArray(image, 0, image.Length, bounds);
            if (bounds.OutHeight <= 0 || bounds.OutWidth <= 0)
            {
                return null;
            }

            var sample = 1;
            while (bounds.OutHeight / (sample * 2) >= targetHeight)
            {
                sample *= 2;
            }

            using var decoded = Android.Graphics.BitmapFactory.DecodeByteArray(image, 0, image.Length,
                new Android.Graphics.BitmapFactory.Options { InSampleSize = sample });
            if (decoded == null)
            {
                return null;
            }

            var scale = Math.Min(1.0, targetHeight / (double)decoded.Height);
            using var scaled = scale < 1
                ? Android.Graphics.Bitmap.CreateScaledBitmap(decoded, Math.Max(1, (int)(decoded.Width * scale)), targetHeight, true)
                : null;

            var dir = Path.Combine(FileSystem.AppDataDirectory, "covers");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name + ".jpg");
            using var stream = File.Create(path);
            (scaled ?? decoded).Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 82, stream);
            return path;
        }
        catch
        {
            return null; // обложка — не главное, запись о книге сохранится и без неё
        }
    }

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
        DownloadService.EnsureChannels(Context);
        var notification = new NotificationCompat.Builder(Context, DownloadService.FinishedChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_e2e)!
            .SetContentTitle(title)!
            .SetContentText(text)!
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(text))!
            .SetPriority(NotificationCompat.PriorityDefault)!
            .SetAutoCancel(true)!
            .SetContentIntent(DownloadService.OpenAppIntent(Context))!
            .Build()!;

        try
        {
            NotificationManagerCompat.From(Context).Notify(FinishedId, notification);
        }
        catch
        {
            // нет разрешения на уведомления — книга всё равно сохранена
        }
    }

    /// <summary>
    /// Синхронизация системных панелей с темой интерфейса (см. wwwroot/js/app.js).
    /// <list type="bullet">
    /// <item>Android 15+: приложение рисуется «от края до края», а MainPage с SafeAreaEdges=All отступает от
    /// статус-бара и жестовой панели — под ними виден фон страницы. Его и красим в цвет темы.</item>
    /// <item>Android 7–14: статус-бар и навигационная панель — отдельные полосы, красим и их.</item>
    /// </list>
    /// Значки на панелях — тёмные на светлом фоне и светлые на тёмном. Тёмные значки навигационной панели
    /// бывают только с Android 8, поэтому на Android 7 в светлой теме она остаётся чёрной.
    /// </summary>
    /// <param name="dark">Тёмная тема (тёмная или AMOLED).</param>
    /// <param name="background">Цвет фона темы (#RRGGBB) — --md-surface из m3.css.</param>
    public void ApplyTheme(bool dark, string background)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var color = Color.FromArgb(string.IsNullOrWhiteSpace(background) ? (dark ? "#111418" : "#F8F9FF") : background.Trim());
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page != null)
            {
                page.BackgroundColor = color;
            }

            var window = Platform.CurrentActivity?.Window;
            if (window == null)
            {
                return;
            }

            var lightNavigationIcons = !dark && OperatingSystem.IsAndroidVersionAtLeast(26);
            if (!OperatingSystem.IsAndroidVersionAtLeast(35))
            {
                window.SetStatusBarColor(color.ToPlatform());
                window.SetNavigationBarColor(dark || lightNavigationIcons ? color.ToPlatform() : Android.Graphics.Color.Black);
            }

            var controller = WindowCompat.GetInsetsController(window, window.DecorView);
            controller.AppearanceLightStatusBars = !dark;
            controller.AppearanceLightNavigationBars = lightNavigationIcons;
        });
    }
}
