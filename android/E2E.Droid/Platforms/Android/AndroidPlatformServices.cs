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

    public bool IsBackgroundWorkRunning => DownloadService.IsRunning;

    public bool IsIgnoringBatteryOptimizations
    {
        get
        {
            try
            {
                return Context.GetSystemService(Android.Content.Context.PowerService) is Android.OS.PowerManager power
                       && power.IsIgnoringBatteryOptimizations(Context.PackageName);
            }
            catch
            {
                return true; // не узнать — не пристаём с вопросом
            }
        }
    }

    public async Task<bool> RequestIgnoreBatteryOptimizationsAsync()
    {
        if (IsIgnoringBatteryOptimizations)
        {
            return true;
        }

        // Прямой вопрос про это приложение (нужно разрешение REQUEST_IGNORE_BATTERY_OPTIMIZATIONS в манифесте)
        var ask = new Android.Content.Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
            Android.Net.Uri.Parse("package:" + Context.PackageName));
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => ActivityResults.StartAsync(ask));
        }
        catch (Exception)
        {
            // Диалога нет (урезанная прошивка) — открываем список в настройках батареи, дальше пользователь сам
            try
            {
                var list = new Android.Content.Intent(Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings);
                list.AddFlags(Android.Content.ActivityFlags.NewTask);
                Context.StartActivity(list);
            }
            catch
            {
                // и этого экрана нет — ничего не поделать
            }
        }

        return IsIgnoringBatteryOptimizations;
    }

    public IReadOnlyList<KeyValuePair<string, string>> DescribeDevice()
    {
        var result = new List<KeyValuePair<string, string>>();

        void Add(string key, Func<string> value)
        {
            try
            {
                result.Add(new(key, value() ?? "—"));
            }
            catch (Exception ex)
            {
                result.Add(new(key, $"не узнать: {ex.GetType().Name}: {ex.Message}"));
            }
        }

        Add("Android", () => $"{Android.OS.Build.VERSION.Release} (API {(int)Android.OS.Build.VERSION.SdkInt}), " +
                             $"патч безопасности {Android.OS.Build.VERSION.SecurityPatch}");
        Add("Устройство", () => $"{Android.OS.Build.Manufacturer} {Android.OS.Build.Model} " +
                                $"({Android.OS.Build.Brand}/{Android.OS.Build.Device}/{Android.OS.Build.Product})");
        Add("Архитектуры", () => string.Join(", ", Android.OS.Build.SupportedAbis ?? []));
        Add("Сборка системы", () => Android.OS.Build.Fingerprint);
        Add("WebView", () => OperatingSystem.IsAndroidVersionAtLeast(26)
            ? Android.Webkit.WebView.CurrentWebViewPackage is { } webView ? $"{webView.PackageName} {webView.VersionName}" : "не найден"
            : "на Android 7 версию не узнать");
        Add("Приложение", () => $"{AppVersion} (сборка {BuildNumber}), пакет {Context.PackageName}");
        Add("Уведомления", () => NotificationManagerCompat.From(Context).AreNotificationsEnabled() ? "разрешены" : "ЗАПРЕЩЕНЫ");
        Add("Ограничение батареи", () => Context.GetSystemService(Android.Content.Context.PowerService) is Android.OS.PowerManager power
            ? power.IsIgnoringBatteryOptimizations(Context.PackageName) ? "приложение не ограничивается" : "приложение ограничивается (может мешать работе в фоне)"
            : "—");
        Add("Фоновый сервис", DownloadService.DescribeState);
        Add("Свободно: личная папка", () => FreeSpace(FileSystem.AppDataDirectory));
        Add("Свободно: кэш", () => FreeSpace(FileSystem.CacheDirectory));
        Add("Память", () =>
        {
            var info = new Android.App.ActivityManager.MemoryInfo();
            (Context.GetSystemService(Android.Content.Context.ActivityService) as Android.App.ActivityManager)?.GetMemoryInfo(info);
            return $"доступно {info.AvailMem / 1048576} из {info.TotalMem / 1048576} МБ, мало памяти: {(info.LowMemory ? "да" : "нет")}";
        });
        Add("Экран", () =>
        {
            var metrics = Context.Resources!.DisplayMetrics!;
            return $"{metrics.WidthPixels}×{metrics.HeightPixels}, плотность {metrics.Density:0.##}";
        });

        return result;

        static string FreeSpace(string path)
        {
            var stat = new Android.OS.StatFs(path);
            return $"{stat.AvailableBytes / 1048576} из {stat.TotalBytes / 1048576} МБ";
        }
    }

    public IReadOnlyDictionary<string, string> GetDynamicColorScheme(bool dark) => DynamicColors.GetScheme(Context, dark);

    /// <summary>
    /// Обложка уменьшается средствами Android: сначала читаются только размеры, затем картинка декодируется
    /// с прореживанием (inSampleSize) и масштабируется до 240 px по высоте — так не тратится память на огромные обложки.
    /// </summary>
    public string SaveThumbnail(byte[] image, string path)
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

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bool written;
            using (var stream = File.Create(path))
            {
                written = (scaled ?? decoded).Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 82, stream);
            }

            if (!written)
            {
                File.Delete(path);
                return null;
            }

            return path;
        }
        catch
        {
            // Недописанный файл не оставляем: интерфейс показал бы битую картинку
            try
            {
                File.Delete(path);
            }
            catch
            {
                // нет файла — тем лучше
            }

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
