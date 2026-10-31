// ============================================================================
// E2E для Android — foreground-сервис, который держит загрузку живой при свёрнутом приложении
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using E2E.Droid.Services;

namespace E2E.Droid;

/// <summary>
/// Foreground-сервис на время скачивания: держит процесс живым, когда приложение свёрнуто,
/// и показывает прогресс в шторке. Сама загрузка идёт в DownloadManager.
/// </summary>
/// <remarks>
/// Зачем он нужен: когда приложение уходит с экрана, Android вправе в любой момент «усыпить» или убить
/// его процесс — и загрузка книги на сотню глав оборвалась бы. Процесс с foreground-сервисом
/// (и обязательным уведомлением в шторке) система не трогает.
/// <para>Тип dataSync — «передача данных по инициативе пользователя». С Android 14 тип обязателен
/// (и отдельное разрешение FOREGROUND_SERVICE_DATA_SYNC в манифесте), с Android 15 у него лимит
/// 6 часов в сутки — по истечении система вызывает OnTimeout.</para>
/// <para>Приложение работает с Android 7, а запуск сервиса и уведомления там устроены иначе (нет каналов
/// уведомлений, нет startForegroundService и типов сервиса). Поэтому всё делается через обёртки AndroidX —
/// ContextCompat, ServiceCompat, NotificationCompat: они сами выбирают способ под версию системы.</para>
/// <para>Сервис сам ничего не качает: он только живёт, пока DownloadManager держит его (идёт задание или запись
/// файлов — в том числе после отмены), и рисует уведомление.</para>
/// </remarks>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class DownloadService : Service
{
    /// <summary>Канал «тихих» уведомлений о прогрессе (без звука).</summary>
    public const string ProgressChannel = "downloads";

    /// <summary>Канал уведомлений «Книга готова» (обычная важность).</summary>
    public const string FinishedChannel = "finished";

    private const int ProgressId = 1001;

    /// <summary>
    /// ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC числом: константа Android помечена как «только с Android 10»,
    /// а ServiceCompat на старых версиях тип и не передаёт.
    /// </summary>
    private const int ForegroundServiceTypeDataSync = 1;

    /// <summary>Действие кнопки «Отмена» в уведомлении.</summary>
    private const string ActionCancel = "e2e.droid.CANCEL";

    private const string ExtraText = "text";

    // Состояние сервиса общее для всего процесса: Start/Stop/Update вызываются из DownloadManager
    // (из любых потоков), а сам экземпляр создаёт Android — поэтому статические поля под lock.
    private static readonly object Sync = new();

    /// <summary>Экземпляр, уже вызвавший StartForeground (null — сервис не запущен или ещё стартует).</summary>
    private static DownloadService _running;

    /// <summary>
    /// Нужен ли сервис сейчас: true после Start, false после Stop. Решение принимается в момент остановки
    /// (StopIfUnwanted), а не в момент вызова Stop: между ними мог прийти Start следующего задания.
    /// </summary>
    private static bool _wanted;

    /// <summary>startId последнего OnStartCommand — для stopSelf(startId), см. StopIfUnwanted.</summary>
    private int _lastStartId;

    private PowerManager.WakeLock _wakeLock;

    public static void Start(Context context, string text)
    {
        lock (Sync)
        {
            _wanted = true;
        }

        var intent = new Intent(context, typeof(DownloadService));
        intent.PutExtra(ExtraText, text);
        // Android 8+: startForegroundService; Android 7: обычный startService
        ContextCompat.StartForegroundService(context, intent);
    }

    /// <summary>
    /// Если книга упала мгновенно, Stop приходит раньше, чем сервис успел вызвать startForeground, —
    /// а остановка до startForeground роняет приложение. Поэтому до старта только помечаем, что сервис
    /// не нужен: он остановится сам сразу после StartForeground (см. OnStartCommand).
    /// </summary>
    public static void Stop(Context context)
    {
        DownloadService running;
        lock (Sync)
        {
            _wanted = false;
            running = _running;
        }

        if (running != null)
        {
            MainThread.BeginInvokeOnMainThread(running.StopIfUnwanted);
        }
    }

    public static void Update(Context context, string text)
    {
        lock (Sync)
        {
            // Запоздалое обновление после остановки оставило бы в шторке неснимаемое уведомление
            if (_running == null || !_wanted)
            {
                return;
            }
        }

        NotificationManagerCompat.From(context).Notify(ProgressId, BuildProgress(context, text));
    }

    public override IBinder OnBind(Intent intent) => null;

    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
    {
        lock (Sync)
        {
            // Любой запуск (и кнопка «Отмена» тоже) получает новый startId — stopSelf должен знать последний
            _lastStartId = startId;
        }

        if (intent?.Action == ActionCancel)
        {
            MainThread.BeginInvokeOnMainThread(() => MauiProgram.Services?.GetService<Services.DownloadManager>()?.Cancel());
            // Нажали «Отмена» в уведомлении, оставшемся от уже остановленного сервиса, — Android создал
            // сервис заново только ради этого вызова: сразу гасим
            StopIfUnwanted();
            return StartCommandResult.NotSticky;
        }

        EnsureChannels(this);
        var notification = BuildProgress(this, intent?.GetStringExtra(ExtraText) ?? "Скачивание…");
        // Тип сервиса передаётся только с Android 10 — ServiceCompat сам выберет нужный вызов.
        // Вызывается при каждом запуске: каждый startForegroundService ждёт своего startForeground
        ServiceCompat.StartForeground(this, ProgressId, notification, ForegroundServiceTypeDataSync);

        bool stopNow;
        lock (Sync)
        {
            _running = this;
            stopNow = !_wanted;
        }

        if (stopNow)
        {
            StopIfUnwanted();
            return StartCommandResult.NotSticky;
        }

        if (_wakeLock == null && GetSystemService(PowerService) is PowerManager power)
        {
            // Без wake lock при выключенном экране процессор засыпает и загрузка встаёт
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "e2e:download");
            _wakeLock!.SetReferenceCounted(false);
            _wakeLock.Acquire((long)TimeSpan.FromHours(6).TotalMilliseconds);
        }

        return StartCommandResult.NotSticky;
    }

    /// <summary>Android 15+: лимит dataSync-сервиса (6 ч в сутки) исчерпан.</summary>
    public override void OnTimeout(int startId, ForegroundService fgsType)
    {
        MainThread.BeginInvokeOnMainThread(() => MauiProgram.Services?.GetService<Services.DownloadManager>()?.Cancel());
        // Система требует остановиться немедленно — без проверок _wanted
        ForceStop();
    }

    /// <summary>
    /// Остановка, если сервис больше не нужен (после Stop не было нового Start). Только из главного потока.
    /// </summary>
    /// <remarks>
    /// stopSelf(startId) не останавливает сервис, если после этого startId пришёл новый запуск, ещё не
    /// дошедший до OnStartCommand (Stop одного задания и сразу Start следующего). Обычный stopSelf() в такой
    /// момент уронил бы приложение: Android ждёт startForeground на каждый startForegroundService.
    /// Новый OnStartCommand сам решит, жить ли сервису дальше.
    /// </remarks>
    private void StopIfUnwanted()
    {
        int startId;
        lock (Sync)
        {
            if (_wanted)
            {
                return; // после Stop успел прийти Start следующего задания
            }

            startId = _lastStartId;
        }

        if (!StopSelfResult(startId))
        {
            return; // новый запуск уже в очереди
        }

        lock (Sync)
        {
            if (_running == this)
            {
                _running = null;
            }
        }

        ServiceCompat.StopForeground(this, ServiceCompat.StopForegroundRemove);
    }

    private void ForceStop()
    {
        lock (Sync)
        {
            if (_running == this)
            {
                _running = null;
            }
        }

        ServiceCompat.StopForeground(this, ServiceCompat.StopForegroundRemove);
        StopSelf();
    }

    public override void OnDestroy()
    {
        lock (Sync)
        {
            if (_running == this)
            {
                _running = null;
            }
        }

        if (_wakeLock is { IsHeld: true })
        {
            _wakeLock.Release();
        }

        _wakeLock = null;
        ServiceCompat.StopForeground(this, ServiceCompat.StopForegroundRemove);
        base.OnDestroy();
    }

    /// <summary>Каналы уведомлений (Android 8+; на Android 7 каналов нет, и вызов ничего не делает).</summary>
    public static void EnsureChannels(Context context)
    {
        var manager = NotificationManagerCompat.From(context);
        manager.CreateNotificationChannel(new NotificationChannelCompat.Builder(ProgressChannel, NotificationManagerCompat.ImportanceLow)
            .SetName("Скачивание книг")!
            .SetDescription("Прогресс скачивания")!
            .Build());
        manager.CreateNotificationChannel(new NotificationChannelCompat.Builder(FinishedChannel, NotificationManagerCompat.ImportanceDefault)
            .SetName("Готовые книги")!
            .SetDescription("Уведомление, когда книга скачана")!
            .Build());
    }

    public static PendingIntent OpenAppIntent(Context context)
    {
        var intent = new Intent(context, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        return PendingIntent.GetActivity(context, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    private static Notification BuildProgress(Context context, string text)
    {
        var cancel = new Intent(context, typeof(DownloadService));
        cancel.SetAction(ActionCancel);
        var cancelIntent = PendingIntent.GetService(context, 1, cancel, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        return new NotificationCompat.Builder(context, ProgressChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_e2e)!
            .SetContentTitle("Скачиваю книгу")!
            .SetContentText(text)!
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(text))!
            .SetProgress(0, 0, true)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            // На Android 7 каналов нет — «тихое» уведомление задаётся приоритетом
            .SetPriority(NotificationCompat.PriorityLow)!
            .SetContentIntent(OpenAppIntent(context))!
            .AddAction(0, "Отмена", cancelIntent)!
            // Android 12+: показать уведомление сразу, а не через 10 секунд после старта сервиса
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)!
            .Build()!;
    }
}
