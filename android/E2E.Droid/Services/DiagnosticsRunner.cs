// ============================================================================
// E2E для Android — диагностика (Настройки → Отладка): готовый сценарий проверок с подробным журналом
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================
//
// Зачем: проверить на настоящем телефоне или читалке то, что не проверить в облаке и на ПК, — сертификаты сайтов
// в сети пользователя, обход блокировок по IP, встроенные корни на старом Android, сохранение файлов, фоновый
// сервис, миниатюры. Пользователь нажимает одну кнопку, сценарий идёт в фоне (под foreground-сервисом), журнал
// сохраняется файлом в папку загрузок приложения — его и присылают разработчикам.
//
// Формат журнала: строки «#» — шапка и итог (в шапке — пометка для ИИ-агентов, см. BuildInfo.AgentNote),
// «== N. шаг ==» — начало шага, «[ok]/[FAIL]/[warn]/[skip]» — проверки, «[i]» — сведения, «ядро |», «tls |» — строки
// ядра и проверок сертификатов во время пробного скачивания. Паролей и логинов в журнале нет.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc;
using Microsoft.Extensions.FileProviders;

namespace E2E.Droid.Services;

/// <summary>Сценарий диагностики. Зарегистрирован в DI как singleton (см. MauiProgram): переживает уход со страницы.</summary>
public sealed class DiagnosticsRunner(
    AppSettings settings,
    IBookStorage storage,
    IPlatformServices platform,
    DownloadManager downloads,
    ToastService toasts)
{
    /// <summary>Открытый пример для пробного скачивания; доступность сайта проверяется при каждом запуске.</summary>
    public const string DefaultTestUrl = "https://tl.rulate.ru/book/209502";
    public const string ScenarioVersion = "2026-11-13.1";

    private const int TestChapters = 2;
    private const int MaxLines = 6000;
    private const int MaxLogChars = 512 * 1024;
    private const int SiteParallelism = 6;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Куда ядро ходит по IP в обход блокировок, с настоящим именем сайта в заголовке Host. Адреса — как в
    /// src/ExternalServices: AuthorTodayGetter (_apiIp, _systemIp) и ReadliGetter (_host).
    /// </summary>
    private static readonly (string Ip, string Host, string Why)[] BypassTargets =
    [
        ("185.26.98.195", "api.author.today", "author.today: API, когда api.author.today недоступен"),
        ("185.26.98.195", "author.today", "author.today: сайт в обход блокировки"),
        ("213.227.140.81", "readli.net", "readli: всегда по IP (обход Cloudflare)")
    ];

    private readonly List<string> _log = [];
    private CancellationTokenSource _cts;

    /// <summary>1 — идёт запуск или сценарий (защита от двойного нажатия, как в DownloadManager).</summary>
    private int _busy;

    private int _ok;
    private int _failed;
    private int _skipped;
    private int _warnings;
    private int _logChars;
    private bool _logTruncated;
    private bool _completed;
    private BoundedFileLog _runLog;
    private IReadOnlyList<string> _previousDiagnostic = [];

    /// <summary>Обложка пробной книги — для шага с миниатюрой.</summary>
    private byte[] _cover;
    private string _coverMime;

    /// <summary>Интерфейс перерисовывается. Может вызываться из фонового потока!</summary>
    public event Action Changed;

    public bool IsRunning => Volatile.Read(ref _busy) == 1;

    /// <summary>Очистка недоступна, в том числе пока скачивание ожидает разрешения Android.</summary>
    public bool CanClearLogs => !IsRunning && !downloads.IsBusy;

    /// <summary>Какой шаг идёт сейчас.</summary>
    public string Status { get; private set; }

    /// <summary>Итог последнего запуска; null — ещё не запускали.</summary>
    public string Summary { get; private set; }

    /// <summary>Журнал, сохранённый в папку загрузок (для «Поделиться»); null — не сохранился.</summary>
    public SavedFile Result { get; private set; }

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToList();
            }
        }
    }

    /// <summary>
    /// Журнал для отправки: шапка, затем краткий итог со всеми проблемами (чтобы и обрезанная вставка в чат была
    /// полезной), затем всё подряд. Так он сохраняется в файл и копируется.
    /// </summary>
    public IReadOnlyList<string> ReportLines
    {
        get
        {
            var log = Log;
            var head = log.TakeWhile(l => l.StartsWith('#')).ToList();
            // Строки «#» (шапка, легенда с «[FAIL]» и «[skip]») — не проблемы
            var problems = log
                .Where(l => !l.StartsWith('#') && (l.Contains("[FAIL]", StringComparison.Ordinal) || l.Contains("[warn]", StringComparison.Ordinal) || l.Contains("[skip]", StringComparison.Ordinal)))
                .Select(l => "#   " + FirstLine(StripTime(l)))
                .ToList();

            var result = new List<string>(head);
            if (Summary != null)
            {
                result.Add($"# Кратко: {Summary}");
            }

            result.Add(_completed ? "# Сценарий выполнен до конца; результат относится только к выполненным проверкам." : "# Сценарий не завершён; отсутствие ошибок не означает успешную проверку.");
            if (_logTruncated) result.Add("# Журнал ограничен по размеру: часть ранних подробностей удалена, итоговые счётчики сохранены.");
            result.Add(problems.Count == 0 ? "# В сохранённых строках замечаний нет." : $"# Ошибки, предупреждения и пропуски ({problems.Count}):");
            result.AddRange(problems);
            result.AddRange(log.Skip(head.Count));
            return result;

            static string StripTime(string line) => line.Length > 13 && line[2] == ':' && line[5] == ':' && line[8] == '.' ? line[13..] : line;

            static string FirstLine(string line)
            {
                var end = line.IndexOf('\n');
                return end < 0 ? line : line[..end].TrimEnd('\r') + " …";
            }
        }
    }

    /// <summary>Запуск из UI-потока (может спросить разрешения). Повторное нажатие, пока идёт, ничего не делает.</summary>
    public async Task StartAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        var started = false;
        try
        {
            if (downloads.IsRunning)
            {
                toasts.Show("Сейчас идёт скачивание — запустите диагностику, когда оно закончится");
                return;
            }

            if (!await storage.EnsureWriteAccessAsync())
            {
                toasts.Show("Без доступа к памяти журнал некуда сохранить", ToastKind.Error);
                return;
            }

            await platform.RequestNotificationPermissionAsync();

            lock (_log) _log.Clear();
            _ok = _failed = _skipped = _warnings = _logChars = 0;
            _logTruncated = _completed = false;
            _runLog = new BoundedFileLog(Path.Combine(FileSystem.AppDataDirectory, "logs", "diagnostics-last.log"));
            _previousDiagnostic = _runLog.Read();
            _runLog.Reset();
            _cover = null;
            _coverMime = null;
            Summary = null;
            Result = null;
            Status = "Запускаю…";
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            // Тот же foreground-сервис, что у скачивания: свёрнутое приложение Android не остановит
            var hold = downloads.KeepServiceAlive("Диагностика E2E: запуск…");
            started = true;
            Changed?.Invoke();

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunAsync(ct);
                }
                catch (Exception ex)
                {
                    Line($"# Диагностика прервалась: {ex}");
                }
                finally
                {
                    hold.Dispose();
                    Interlocked.Exchange(ref _cts, null)?.Dispose();
                    Volatile.Write(ref _busy, 0);
                    Changed?.Invoke();
                }
            });
        }
        finally
        {
            if (!started)
            {
                Volatile.Write(ref _busy, 0);
            }
        }
    }

    /// <summary>Остановить: текущий шаг прерывается, остальные пропускаются, журнал всё равно сохраняется.</summary>
    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* Сценарий успел завершиться до нажатия. */ }
    }

    /// <summary>Очищает только внутренние журналы. Книги, история и экспортированные TXT остаются.</summary>
    public bool TryClearLogs(out string error)
    {
        error = "Дождитесь окончания скачивания или диагностики";
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        try
        {
            if (!downloads.TryClearLogs(() =>
                {
                    new BoundedFileLog(Path.Combine(FileSystem.AppDataDirectory, "logs", "diagnostics-last.log")).Delete();
                    StartupLog.Clear();
                    lock (_log)
                    {
                        _runLog = null;
                        _log.Clear();
                        _previousDiagnostic = [];
                        _ok = _failed = _skipped = _warnings = _logChars = 0;
                        _logTruncated = _completed = false;
                        Summary = Status = null;
                        Result = null;
                    }
                })) return false;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "Не удалось удалить все внутренние журналы. Попробуйте ещё раз";
            return false;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            Changed?.Invoke();
        }
    }

    /// <summary>Сохраняет журнал последнего скачивания файлом в папку загрузок (с шапкой о сборке).</summary>
    public async Task<SavedFile> SaveDownloadLogAsync()
    {
        var lines = Header("журнал скачивания").Concat(downloads.Log)
            .Concat(StartupLog.FailedLines().Select(l => "# Предыдущий неудачный запуск: " + l));
        return await SaveTextAsync($"E2E-download-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt", lines);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        foreach (var line in Header("журнал диагностики"))
        {
            Line(line);
        }

        Line("# Обозначения: [ok] — проверка прошла, [FAIL] — ошибка, [warn] — ограничение или подозрение, [skip] — пропущена, [i] — сведения.");
        Line("# HTTP-ответ и успешный TLS не доказывают работу загрузчика. Проверка примеров не гарантирует работу всех книг сайта.");

        var steps = new (string Name, Func<CancellationToken, Task> Run)[]
        {
            ("Устройство и приложение", StepEnvironmentAsync),
            ("Сохранённые журналы", StepHistoryAsync),
            ("FB2, EPUB, Unicode и изображения без сети", StepFormatsAsync),
            ("Встроенные корневые сертификаты", StepRootsAsync),
            ("Фоновый сервис", StepServiceAsync),
            ("Хранилище: запись, удаление, отмена", StepStorageAsync),
            ("Сертификаты и доступность сайтов", StepSitesAsync),
            ("Обход блокировок по IP", StepBypassAsync),
            ("Мосты, встроенные корни и подмена", StepOldDeviceAsync),
            ("Пробная книга и регрессии загрузчиков", StepTestDownloadAsync),
            ("Миниатюра обложки", StepThumbnailAsync)
        };

        for (var i = 0; i < steps.Length; i++)
        {
            if (ct.IsCancellationRequested)
            {
                _skipped += steps.Length - i;
                Line("# Остановлено — остальные шаги пропущены");
                break;
            }

            await RunStepAsync(i + 1, steps.Length, steps[i].Name, steps[i].Run, ct);
        }

        _completed = !ct.IsCancellationRequested;
        Summary = $"Проверок {_ok + _failed + _warnings}: прошло {_ok}, ошибок {_failed}, предупреждений {_warnings}, пропущено {_skipped} — за {total.Elapsed:mm\\:ss}" +
                  (ct.IsCancellationRequested ? " (остановлено)" : string.Empty);
        Line(string.Empty);
        Line($"# Итог: {Summary}");
        Line($"# Конец: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        Status = "Сохраняю журнал…";
        Changed?.Invoke();
        try
        {
            Result = await SaveTextAsync($"E2E-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt", ReportLines);
            if (Result == null) throw new IOException("хранилище не вернуло сохранённый файл");
        }
        catch (Exception ex)
        {
            Summary += $". Журнал не сохранился в папку ({ex.Message}) — скопируйте его кнопкой";
        }

        Status = ct.IsCancellationRequested ? "Остановлено" : "Готово";
        var where = Result != null ? $"Журнал: {storage.DestinationLabel}/{Result.Name}" : "Журнал можно скопировать в настройках";
        var passed = _completed && _failed == 0 && _warnings == 0 && _skipped == 0 && Result != null;
        var outcome = !_completed ? "остановлена, проверка неполная"
            : _failed > 0 ? $"ошибок {_failed}"
            : _warnings > 0 || _skipped > 0 ? "завершена с предупреждениями или пропусками"
            : Result == null ? "журнал не сохранён" : "выполненные проверки прошли";
        toasts.Show($"Диагностика: {outcome}. {where}", passed ? ToastKind.Success : _failed > 0 ? ToastKind.Error : ToastKind.Info, 8);
        if (!platform.IsInForeground)
        {
            platform.NotifyFinished("Диагностика E2E завершена", $"{Summary}. {where}");
        }
    }

    private async Task RunStepAsync(int number, int count, string name, Func<CancellationToken, Task> run, CancellationToken ct)
    {
        Status = $"Шаг {number} из {count}: {name}";
        platform.UpdateBackgroundWork($"Диагностика E2E — {Status}");
        Changed?.Invoke();

        Line(string.Empty);
        Line($"== {number}. {name} ==");
        var sw = Stopwatch.StartNew();
        try
        {
            await run(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Skip("шаг прерван: диагностику остановили");
        }
        catch (Exception ex)
        {
            Fail($"шаг упал: {ex}");
        }

        Info($"шаг занял {sw.Elapsed.TotalSeconds:0.0} с");
    }

    // ---------------------------------------------------------------- шаги

    private Task StepHistoryAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_previousDiagnostic.Count > 0 && !_previousDiagnostic.Any(line => line.StartsWith("# Конец:", StringComparison.Ordinal)))
        {
            Warn("прошлая диагностика прервалась до итоговой записи; ниже последние 100 сохранённых строк");
            foreach (var line in _previousDiagnostic.TakeLast(100)) Line("# Предыдущая диагностика: " + line);
        }
        var startup = StartupLog.FailedLines();
        if (startup.Count > 0)
        {
            Warn($"есть журнал прошлого неудачного запуска: {startup.Count} строк; ниже последние 150");
            foreach (var line in startup.TakeLast(150)) Line("# Предыдущий неудачный запуск: " + line);
        }
        else Info("журнала прошлого неудачного запуска нет; это не воспроизводит возможный сбой старта");

        var previous = downloads.Log;
        Info($"последний журнал скачивания: {previous.Count} строк; ниже последние 100, отдельный полный экспорт остаётся доступен");
        foreach (var line in previous.TakeLast(100)) Line("# Предыдущее скачивание: " + line);
        Info("исторические строки не относятся к текущим сетевым проверкам; журналы скачивания и запуска не изменены");
        return Task.CompletedTask;
    }

    private async Task StepFormatsAsync(CancellationToken ct)
    {
        var work = Path.Combine(FileSystem.CacheDirectory, "diagnostics", "formats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            foreach (var check in await DiagnosticBookChecks.RunSelfTestAsync(work, ct)) Report(check);
            var path = Path.Combine(work, "probe.log");
            var log = new BoundedFileLog(path, 16000);
            log.Append("Проверка журнала: Кириллица 🐈");
            log.Append("data:image/png;base64," + new string('A', 10000));
            var restored = new BoundedFileLog(path, 16000).Read();
            Check(restored.Count == 2 && restored[0].Contains("Кириллица 🐈") && !restored[1].Contains("AAAA"),
                "журнал на диске: повторное чтение, Unicode и удаление base64");
        }
        finally
        {
            TryDelete(work);
            Check(!Directory.Exists(work), "временные файлы локальной проверки удалены");
        }
    }

    private void Report(DiagnosticCheck check)
    {
        if (check.Warning) Warn(check.Message);
        else Check(check.Passed, check.Message);
    }

    private Task StepEnvironmentAsync(CancellationToken ct)
    {
        foreach (var (key, value) in platform.DescribeDevice())
        {
            Info($"{key}: {value}");
        }

        if (!platform.IsIgnoringBatteryOptimizations)
        {
            Info("Снять ограничение батареи: «Настройки» → «Скачивание» → «Работа в фоне ограничена»");
        }

        Info(Safe(() => $".NET: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, " +
                        $"процесс {RuntimeInformation.ProcessArchitecture}, ядер {Environment.ProcessorCount}"));
        Info(Safe(() => $"Время: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} (UTC {DateTime.UtcNow:HH:mm:ss}), пояс {TimeZoneInfo.Local.Id}, " +
                        $"язык {CultureInfo.CurrentCulture.Name}"));
        Info(Safe(() => $"Память процесса: управляемая {GC.GetTotalMemory(false) / 1048576} МБ, всего {Environment.WorkingSet / 1048576} МБ"));
        foreach (var line in DescribeSettings(settings, storage))
        {
            Info(line);
        }

        Info(Safe(() =>
        {
            var sites = SupportedSites.Sites;
            return $"Загрузчиков: {GetterResolver.GetterTypes.Count}; сайтов {sites.Count}: работают {sites.Count(s => s.Available)}, " +
                   $"отключены {sites.Count(s => s.DisabledReason != null)}";
        }));
        Info($"Без карточки до скачивания: {string.Join(", ", BookPipeline.NoPreviewGetters.Select(n => n.Split('.')[^1]))}");
        Ok("сведения собраны");
        return Task.CompletedTask;
    }

    private Task StepRootsAsync(CancellationToken ct)
    {
        var roots = CertificatePolicy.Roots;
        if (roots.Count == 0)
        {
            Fail("встроенных корней нет — на старых устройствах цепочки не достроятся");
            return Task.CompletedTask;
        }

        foreach (var root in roots)
        {
            Info($"{root.GetNameInfo(X509NameType.SimpleName, false)} — до {root.NotAfter:yyyy-MM-dd}, " +
                 $"SHA-256 {Convert.ToHexString(SHA256.HashData(root.RawData))[..16]}…");
        }

        Ok($"встроенных корней: {roots.Count}");
        var expiring = roots.Where(r => r.NotAfter < DateTime.Now.AddYears(1)).ToList();
        Check(expiring.Count == 0, expiring.Count == 0
            ? "все корни действуют ещё минимум год"
            : $"скоро истекают: {string.Join(", ", expiring.Select(r => r.GetNameInfo(X509NameType.SimpleName, false)))}");
        return Task.CompletedTask;
    }

    private async Task StepServiceAsync(CancellationToken ct)
    {
        // Сервис попросили при запуске диагностики; startForeground срабатывает чуть позже
        for (var i = 0; i < 50 && !platform.IsBackgroundWorkRunning; i++)
        {
            await Task.Delay(100, ct);
        }

        var state = platform.DescribeDevice().FirstOrDefault(kv => kv.Key == "Фоновый сервис").Value;
        Check(platform.IsBackgroundWorkRunning, platform.IsBackgroundWorkRunning
            ? $"foreground-сервис работает ({state})"
            : $"foreground-сервис не запустился за 5 с ({state}) — свёрнутое приложение Android может остановить");
        Info($"Приложение на экране: {YesNo(platform.IsInForeground)}");
    }

    private async Task StepStorageAsync(CancellationToken ct)
    {
        var root = Path.Combine(FileSystem.CacheDirectory, "diagnostics", "storage-" + Guid.NewGuid().ToString("N"));
        var probeDir = Path.Combine(root, "probe");
        var bigDir = Path.Combine(root, "big");
        var stamp = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        var cleanup = new List<SavedFile>();
        try
        {
            Directory.CreateDirectory(probeDir);
            await File.WriteAllTextAsync(Path.Combine(probeDir, $"E2E-проверка-{stamp}.txt"),
                "Проверка записи E2E. Файл удаляется сразу после проверки.", ct);
            Info($"папка сохранения: {storage.DestinationLabel}");

            var sw = Stopwatch.StartNew();
            var expectedBytes = Directory.GetFiles(probeDir).Sum(path => new FileInfo(path).Length);
            StorageProgress previous = null;
            var progressValid = true;
            var updates = 0;
            var saved = await storage.SaveTreeAsync(probeDir, f => { cleanup.Add(f); return f; }, ct,
                new InlineStorageProgress(p =>
                {
                    updates++;
                    progressValid &= p.TotalFiles == 1 && p.TotalBytes == expectedBytes &&
                                     p.CompletedFiles >= (previous?.CompletedFiles ?? 0) && p.CompletedFiles <= p.TotalFiles &&
                                     p.CopiedBytes >= (previous?.CopiedBytes ?? 0) && p.CopiedBytes <= p.TotalBytes;
                    previous = p;
                }));
            Check(saved.Count == 1, saved.Count == 1
                ? $"записан «{saved[0].Name}» ({saved[0].Size} байт, {saved[0].Mime}) за {sw.ElapsedMilliseconds} мс → {saved[0].Uri}"
                : $"записано файлов: {saved.Count}, ожидался 1");
            Check(progressValid && updates >= 3 && previous?.CompletedFiles == 1 && previous.CopiedBytes == expectedBytes,
                $"прогресс сохранения: {updates} отчётов, {previous?.CompletedFiles}/1 файлов, {previous?.CopiedBytes}/{expectedBytes} байт; счётчики не убывают");
            foreach (var file in saved)
            {
                Check(storage.Exists(file), "файл виден после записи");
                Check(storage.Delete(file), "файл удалён");
                Check(!storage.Exists(file), "после удаления файла нет");
            }

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    var leaked = await storage.SaveTreeAsync(probeDir, f => f, cancelled.Token);
                    Fail($"сохранение, отменённое заранее, всё же записало {leaked.Count} файл(ов)");
                    foreach (var file in leaked)
                    {
                        storage.Delete(file);
                    }
                }
                catch (OperationCanceledException)
                {
                    Ok("сохранение, отменённое заранее, ничего не записало");
                }
            }

            // Сначала небольшой файл, затем большой: отменяем из отчёта о записи второго,
            // чтобы проверить откат уже готового файла независимо от скорости устройства.
            Directory.CreateDirectory(bigDir);
            var firstPath = Path.Combine(bigDir, $"0-E2E-проверка-отката-{stamp}.txt");
            await File.WriteAllTextAsync(firstPath, "Этот файл должен исчезнуть при отмене следующего.", ct);
            var firstBytes = new FileInfo(firstPath).Length;
            var bigName = $"1-E2E-проверка-отмены-{stamp}.bin";
            await using (var big = File.Create(Path.Combine(bigDir, bigName)))
            {
                big.SetLength(2L * 1024 * 1024);
            }

            using (var midway = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var completed = new List<SavedFile>();
                var cancelledDuringCopy = false;
                sw.Restart();
                try
                {
                    var finished = await storage.SaveTreeAsync(bigDir, f =>
                    {
                        completed.Add(f);
                        cleanup.Add(f);
                        return f;
                    }, midway.Token, new InlineStorageProgress(p =>
                    {
                        if (p.CompletedFiles == 1 && p.CopiedBytes > firstBytes && p.CopiedBytes < p.TotalBytes)
                        {
                            cancelledDuringCopy = true;
                            midway.Cancel();
                        }
                    }));
                    Fail($"ожидалась отмена посреди второго файла, но сохранение вернуло {finished.Count} файл(ов)");
                    foreach (var file in finished)
                    {
                        storage.Delete(file);
                    }
                }
                catch (OperationCanceledException) when (cancelledDuringCopy && !ct.IsCancellationRequested)
                {
                    Ok($"копирование второго файла прервано из отчёта о прогрессе через {sw.ElapsedMilliseconds} мс");
                    Check(completed.Count == 1 && completed.All(f => !storage.Exists(f)),
                        $"откат после отмены: первый файл удалён (полностью записано до отмены: {completed.Count})");
                    Info($"Недописанный файл «{bigName}» тоже не должен оставаться в папке; его URI интерфейс хранилища не возвращает, отсутствие проверяется вручную.");
                }
            }

            ct.ThrowIfCancellationRequested();
            var expectedError = new InvalidOperationException("Пробная ошибка диагностики после копирования");
            SavedFile beforeError = null;
            try
            {
                await storage.SaveTreeAsync(probeDir, f =>
                {
                    beforeError = f;
                    cleanup.Add(f);
                    throw expectedError;
                }, ct);
                Fail("хранилище не передало ошибку подготовки сохранённого файла");
            }
            catch (InvalidOperationException ex) when (ReferenceEquals(ex, expectedError))
            {
                Check(beforeError != null && !storage.Exists(beforeError), "откат после ошибки: скопированный файл удалён");
            }
        }
        finally
        {
            // Только файлы этого сценария; повторное удаление уже откатившихся файлов безвредно.
            foreach (var file in cleanup) storage.Delete(file);
            TryDelete(root);
        }
    }

    // Обычный Progress<T> откладывает колбэк: для отмены точно посреди копирования он не подходит.
    private sealed class InlineStorageProgress(Action<StorageProgress> report) : IProgress<StorageProgress>
    {
        public void Report(StorageProgress value) => report(value);
    }

    private async Task StepSitesAsync(CancellationToken ct)
    {
        var targets = SupportedSites.Sites.Where(s => s.Available).Select(s => s.Url).Distinct().ToList();
        Info($"сайтов: {targets.Count}; по {SiteParallelism} одновременно, таймаут {ProbeTimeout.TotalSeconds:0} с; " +
             $"прокси {MaskProxy(settings.Proxy)} (как у ядра)");
        Info("этот шаг проверяет соединение и сертификаты; HTTP 403/404/429/5xx отмечается предупреждением, а загрузчики проверяются отдельно");

        using var gate = new SemaphoreSlim(SiteParallelism);
        var results = await Task.WhenAll(targets.Select(async url =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var result = await TlsProbe.ProbeAsync(new Uri(url), null, settings.Proxy, ProbeTimeout, ct);
                // Не ответил за 20 с — пробуем ещё раз с таймаутом из настроек (до 120 с): так качает само приложение.
                // Отличает медленный сайт от недоступного
                var longTimeout = TimeSpan.FromSeconds(Math.Min(settings.Timeout, 120));
                if (result.TimedOut && longTimeout > ProbeTimeout)
                {
                    var first = result.Outcome;
                    result = await TlsProbe.ProbeAsync(new Uri(url), null, settings.Proxy, longTimeout, ct);
                    result.Lines.Insert(1, $"      Повтор с таймаутом {longTimeout.TotalSeconds:0} с (из настроек); первая попытка: {first}");
                }

                // Дополнительная проба при доступном TCP. Различие результатов не доказывает
                // конкретную причину: влиять могут сеть, VPN и временное состояние сервера.
                if (!result.Ok && result.TcpConnected)
                {
                    var tls12 = await TlsProbe.ProbeAsync(new Uri(url), null, settings.Proxy, ProbeTimeout, ct, System.Security.Authentication.SslProtocols.Tls12);
                    result.Tls12HttpStatusCode = tls12.HttpStatusCode;
                    result.Lines.Insert(1, tls12.Ok
                        ? $"      Проба TLS 1.2: ОТКРЫЛСЯ — {tls12.Outcome}. Результат отличается; причина может быть в сети или временном состоянии сервера"
                        : $"      Проба TLS 1.2: тоже не открылся — {tls12.Outcome}");
                }

                Report(result);
                return result;
            }
            finally
            {
                gate.Release();
            }
        }));

        Info(TlsProbe.DescribeSummary(results));
    }

    private async Task StepBypassAsync(CancellationToken ct)
    {
        foreach (var (ip, host, why) in BypassTargets)
        {
            ct.ThrowIfCancellationRequested();
            Info($"{why}: https://{ip}/ с Host: {host}");
            var result = await TlsProbe.ProbeAsync(new Uri($"https://{ip}/"), host, settings.Proxy, ProbeTimeout, ct);
            Report(result);
            if (result.TlsSeen)
            {
                Info(result.SystemErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
                    ? "  .NET сверял имя сертификата с IP (Host для TLS не используется) — имя сверила политика по Host"
                    : "  .NET сверял имя сертификата с Host — подмены имени нет");
            }
        }
    }

    private async Task StepOldDeviceAsync(CancellationToken ct)
    {
        // Мосты Let's Encrypt: сайты, не присылающие Root YE/YR, должны доходить до ISRG Root X1 системой Android
        var bridges = CertificatePolicy.Bridges;
        Check(bridges.Count > 0, $"мостовых сертификатов: {bridges.Count} " +
                                 $"({string.Join("; ", bridges.Select(b => $"{b.GetNameInfo(X509NameType.SimpleName, false)} ← {b.GetNameInfo(X509NameType.SimpleName, true)}"))})");

        // Старый Android без ISRG Root X1: цепочку author.today (Let's Encrypt) — только по встроенным корням
        var sample = await TlsProbe.ProbeAsync(new Uri("https://author.today/"), null, settings.Proxy, ProbeTimeout, ct);
        if (sample.Leaf == null)
        {
            Skip($"не удалось получить сертификат author.today: {sample.Outcome}");
        }
        else
        {
            var reason = CertificatePolicy.TryChainWithBundledRoots(sample.Leaf, sample.Sent);
            Check(reason == null, reason == null
                ? "цепочка author.today строится по одним встроенным корням — на Android без ISRG Root X1 сайты Let's Encrypt откроются"
                : $"по встроенным корням цепочка не строится: {reason}");
        }

        // И обратное: подставной сертификат на то же имя политика не пропускает
        var forged = TlsProbe.CheckForgedRejected("author.today");
        Check(forged != null, forged != null
            ? $"подставной сертификат отклонён: {forged}"
            : "ПОДСТАВНОЙ СЕРТИФИКАТ ПРИНЯТ — проверка сертификатов не работает");
    }

    private async Task StepTestDownloadAsync(CancellationToken ct)
    {
        var selected = string.IsNullOrWhiteSpace(settings.DebugTestUrl) ? DefaultTestUrl : settings.DebugTestUrl;
        var probes = new (string Label, string Url, int Start, int End)[]
        {
            ("Пробная книга", selected, 1, TestChapters),
            ("Rulate: граница 100–101", "https://tl.rulate.ru/book/129557", 100, 101),
            ("Bllate: постраничное оглавление и открытые главы", "https://bllate.org/book/23172", 1, 2),
            ("Litmarket: названия и диапазон", "https://litmarket.ru/books/devochki-iz-proshloy-zhizni-1", 1, 2),
            ("RanobeLib: содержимое глав", "https://ranobelib.me/ru/29609--mikkakan-no-koufuku", 1, 2)
        };
        Info("5 коротких примеров, до 10 глав; FB2 и EPUB из одного ответа. Полные книги не запрашиваются намеренно, но некоторые сайты возвращают весь текст одним ответом.");
        Info("Bllate 23172: главы 100–101 закрыты без входа, поэтому проверяются открытые 1–2 после сбора оглавления. Диапазон 100–101 проверяется отдельно на Rulate.");
        Info("Совпадение количества глав не доказывает их позиции в оглавлении: для анализа сохраняются сообщения загрузчика и последнее название главы. Пробы рассчитаны на текстовые книги.");
        Info("Вход не выполняется. Ошибка доступа не доказывает дефект приложения; первые две главы не доказывают полноту всей книги. Через 3 минуты запрашивается отмена примера; завершение обработки может занять время.");
        foreach (var probe in probes)
        {
            ct.ThrowIfCancellationRequested();
            Status = probe.Label;
            platform.UpdateBackgroundWork("Диагностика E2E — " + Status);
            Changed?.Invoke();
            await RunDownloadProbeAsync(probe.Label, probe.Url, probe.Start, probe.End, ct);
        }
    }

    private async Task RunDownloadProbeAsync(string label, string text, int start, int end, CancellationToken ct)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0)
        {
            Fail($"{label}: нужна ссылка HTTP/HTTPS без логина и пароля в адресе");
            return;
        }
        if (!GetterResolver.IsSupported(url))
        {
            Skip($"{label}: для {url.Host} нет включённого загрузчика в этой сборке");
            return;
        }

        var work = Path.Combine(FileSystem.CacheDirectory, "diagnostics", "download-" + Guid.NewGuid().ToString("N"));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(3));
        var options = new Options
        {
            Url = [url.ToString()], Format = ["fb2", "epub"],
            Timeout = settings.Timeout, Delay = settings.Delay, DelayMode = settings.DelayMode,
            CustomDelaySeconds = settings.CustomDelaySeconds,
            Proxy = string.IsNullOrWhiteSpace(settings.Proxy) ? null : settings.Proxy.Trim(),
            Start = start, End = end, NoImage = !settings.Images, Additional = false, Cover = false,
            BookNamePattern = settings.BookNamePattern,
            SavePath = Path.Combine(work, "out"), TempPath = Path.Combine(work, "tmp")
        };
        Info($"{label}: {url}; позиции {start}–{end}, FB2 + EPUB, картинки {YesNo(settings.Images)}, без входа; файлы временные");
        Action<string> trace = line => Line("  tls | " + line);
        CertificatePolicy.Traced += trace;
        var sw = Stopwatch.StartNew();
        try
        {
            var logger = new CallbackLogger(line => Line("  ядро | " + line), message => Warn($"{label}: {message}"));
            // Необязательное превью отключено: не запрашиваем оглавление и обложку второй раз.
            var result = await BookPipeline.RunAsync(options, logger, null, limit.Token);
            _cover ??= result.Info.Cover;
            Info($"{label}: за {sw.Elapsed.TotalSeconds:0.0} с получено глав {result.Info.Chapters}, последняя «{result.Info.LastChapter}», обложка {result.Info.Cover?.Length ?? 0} байт");
            Check(result.Info.Chapters > 0, $"{label}: есть непустые главы");
            if (result.Info.Chapters != end - start + 1)
                Warn($"{label}: запрошено {end - start + 1}, получено {result.Info.Chapters}; возможны короткая книга, закрытые главы или ошибка диапазона — см. журнал ядра");
            else Ok($"{label}: количество глав соответствует диапазону {start}–{end}");
            var books = result.Files.Where(f => f.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)).ToList();
            Check(books.Count == 2 && books.Select(Path.GetExtension).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
                $"{label}: созданы оба формата, файлов книги {books.Count}, дополнительных {result.Files.Count - books.Count}");
            foreach (var file in books)
            {
                limit.Token.ThrowIfCancellationRequested();
                Info($"{label}: {Path.GetExtension(file)} — {new FileInfo(file).Length} байт");
                foreach (var check in DiagnosticBookChecks.CheckFile(file, result.Info.Chapters)) Report(check);
            }
            limit.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            Warn($"{label}: проверка не завершена за 3 минуты; это ограничение диагностики, а не доказательство неработоспособности сайта");
        }
        catch (Exception ex)
        {
            Fail($"{label}: скачивание не удалось за {sw.Elapsed.TotalSeconds:0.0} с: {ex}");
            if (CertificatePolicy.LastRejection != null) Info($"последний отказ по сертификату: {CertificatePolicy.LastRejection}");
        }
        finally
        {
            CertificatePolicy.Traced -= trace;
            TryDelete(work);
            Check(!Directory.Exists(work), $"{label}: временные книги удалены");
        }
    }

    private Task StepThumbnailAsync(CancellationToken ct)
    {
        if (_cover == null)
        {
            Skip("у пробной книги нет обложки — проверять нечего");
            return Task.CompletedTask;
        }

        var sw = Stopwatch.StartNew();
        var path = platform.SaveThumbnail(_cover, CoverThumbnails.NewPreviewPath());
        if (path == null)
        {
            Fail($"миниатюра не получилась (исходник {_cover.Length} байт, {_coverMime ?? "тип неизвестен"})");
            return Task.CompletedTask;
        }

        try
        {
            var size = new FileInfo(path).Length;
            Ok($"миниатюра {size} байт за {sw.ElapsedMilliseconds} мс (исходник {_cover.Length} байт, {_coverMime ?? "тип неизвестен"})");

            var url = CoverThumbnails.UrlFor(path);
            Check(url != null, $"адрес для интерфейса: {url ?? "нет"}");

            var provider = new CoverFileProvider(new NullFileProvider());
            var served = provider.GetFileInfo(url ?? string.Empty);
            Check(served.Exists && served.Length == size, served.Exists
                ? $"встроенный браузер получит файл по адресу ({served.Length} байт)"
                : "по адресу миниатюры файл не находится");
            Check(!provider.GetFileInfo("_covers/../books.json").Exists, "файлы вне папок миниатюр не отдаются");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // кэш подчистится при запуске
            }
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- проверка одного адреса

    /// <summary>Строки проверки — одним блоком (сайты проверяются параллельно) и в счётчики.</summary>
    private void Report(TlsProbeResult result)
    {
        if (result.HttpBlocked)
        {
            Interlocked.Increment(ref _warnings);
        }
        else if (result.Ok)
        {
            Interlocked.Increment(ref _ok);
        }
        else
        {
            Interlocked.Increment(ref _failed);
        }

        lock (_log)
        {
            for (var index = 0; index < result.Lines.Count; index++)
            {
                var line = result.Lines[index];
                if (index == 0 && result.Tls12HttpStatusCode.HasValue)
                    line += $"; отдельная проба TLS 1.2: HTTP {result.Tls12HttpStatusCode}, результат штатного режима не изменён";
                AddLine(line);
            }
        }

        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- журнал и общее

    /// <summary>Шапка журнала: сборка, устройство в двух словах и пометка для ИИ-агентов.</summary>
    private IEnumerable<string> Header(string what)
    {
        yield return $"# E2E для Android — {what}";
        yield return $"# Сценарий диагностики {ScenarioVersion}";
        yield return $"# Версия {platform.AppVersion} ({BuildInfo.Kind(platform.AppVersion)}), сборка {platform.BuildNumber}, " +
                     $"коммит {BuildInfo.Commit}, ядро Elib2Ebook {BuildInfo.CoreVersion}";
        yield return $"# {BuildInfo.AgentNote(platform.AppVersion)}";
        yield return $"# Записано: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}";
    }

    /// <summary>Настройки одной-двумя строками — без паролей и адреса прокси с логином.</summary>
    public static IEnumerable<string> DescribeSettings(AppSettings s, IBookStorage storage)
    {
        yield return $"Настройки: формат по умолчанию {s.TextFormat}, авто-CBZ {YesNo(s.AutoFormat)}, картинки {YesNo(s.Images)}, " +
                     $"обложка файлом {YesNo(s.Cover)}, доп. файлы {YesNo(s.Additional)} [{string.Join(",", s.AdditionalTypes)}], " +
                     $"шаблон имени «{s.BookNamePattern}»";
        yield return $"Настройки: таймаут {s.Timeout} с, режим задержки {s.DelayMode}, своя задержка {s.CustomDelaySeconds} с, прокси {MaskProxy(s.Proxy)}, " +
                     $"не проверять сертификаты {YesNo(s.AllowInsecureTls)}, подробный журнал {YesNo(s.DebugLog)}, " +
                     $"пропускать диалог скачивания {YesNo(s.AutoStartOnShare)}";
        yield return $"Сохранение: {storage.DestinationLabel}";
    }

    /// <summary>Прокси без логина и пароля: схема, хост, порт.</summary>
    public static string MaskProxy(string proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy))
        {
            return "нет";
        }

        return Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}:{uri.Port}{(string.IsNullOrEmpty(uri.UserInfo) ? "" : " (с логином)")}"
            : "задан (не разобрать)";
    }

    private async Task<SavedFile> SaveTextAsync(string fileName, IEnumerable<string> lines)
    {
        var dir = Path.Combine(FileSystem.CacheDirectory, "diagnostics", "out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllLinesAsync(Path.Combine(dir, fileName), LogText.CleanLines(lines), new UTF8Encoding(false));
            var saved = await storage.SaveTreeAsync(dir, f =>
            {
                f.BookTitle = Path.GetFileNameWithoutExtension(fileName);
                return f;
            }, CancellationToken.None);
            return saved.FirstOrDefault();
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private void Ok(string text) => Mark("ok", text, ref _ok);

    private void Fail(string text) => Mark("FAIL", text, ref _failed);

    private void Warn(string text) => Mark("warn", text, ref _warnings);

    private void Skip(string text) => Mark("skip", text, ref _skipped);

    private void Check(bool condition, string text)
    {
        if (condition)
        {
            Ok(text);
        }
        else
        {
            Fail(text);
        }
    }

    private void Info(string text) => Line("[i] " + text);

    private void Mark(string kind, string text, ref int counter)
    {
        Interlocked.Increment(ref counter);
        Line($"[{kind}] {text}");
    }

    private void Line(string text)
    {
        lock (_log)
        {
            AddLine(text);
        }
    }

    /// <summary>Строка с отметкой времени; вызывать под lock (_log).</summary>
    private void AddLine(string text)
    {
        text = LogText.Clean(text);
        var line = text.Length == 0 || text.StartsWith('#') ? text : $"{DateTime.Now:HH:mm:ss.fff} {text}";
        _runLog?.Append(line);
        _log.Add(line);
        _logChars += line.Length;
        while (_log.Count > MaxLines || _logChars > MaxLogChars)
        {
            // Шапка остаётся, подробности вытесняются старыми вперёд; свежий итог не теряется.
            var index = Math.Min(16, _log.Count - 1);
            _logChars -= _log[index].Length;
            _log.RemoveAt(index);
            _logTruncated = true;
        }
    }

    private static string Safe(Func<string> value)
    {
        try
        {
            return value();
        }
        catch (Exception ex)
        {
            return $"не узнать: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string YesNo(bool value) => value ? "да" : "нет";

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch
        {
            // подчистится при запуске (DownloadManager.CleanupLeftovers)
        }
    }
}
