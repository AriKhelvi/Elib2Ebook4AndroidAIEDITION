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
// «== N. шаг ==» — начало шага, «[ok]/[FAIL]/[skip]» — проверки, «[i]» — сведения, «ядро |», «tls |» — строки
// ядра и проверок сертификатов во время пробного скачивания. Паролей и логинов в журнале нет.

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Elib2Ebook.DomainServices.Configs;
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
    /// <summary>Книга для пробного скачивания по умолчанию — та же, что в android/CoreSmoke.</summary>
    public const string DefaultTestUrl = "https://author.today/work/212721";

    private const int TestChapters = 2;
    private const int MaxLines = 50000;
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

    /// <summary>Обложка пробной книги — для шага с миниатюрой.</summary>
    private byte[] _cover;
    private string _coverMime;

    /// <summary>Интерфейс перерисовывается. Может вызываться из фонового потока!</summary>
    public event Action Changed;

    public bool IsRunning => Volatile.Read(ref _busy) == 1;

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
            _ok = _failed = _skipped = 0;
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
    public void Cancel() => _cts?.Cancel();

    /// <summary>Сохраняет журнал последнего скачивания файлом в папку загрузок (с шапкой о сборке).</summary>
    public async Task<SavedFile> SaveDownloadLogAsync()
    {
        var lines = Header("журнал скачивания").Concat(downloads.Log);
        return await SaveTextAsync($"E2E-download-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt", lines);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        foreach (var line in Header("журнал диагностики"))
        {
            Line(line);
        }

        Line("# Обозначения: [ok] — проверка прошла, [FAIL] — нет, [skip] — пропущена, [i] — сведения.");

        var steps = new (string Name, Func<CancellationToken, Task> Run)[]
        {
            ("Устройство и приложение", StepEnvironmentAsync),
            ("Встроенные корневые сертификаты", StepRootsAsync),
            ("Фоновый сервис", StepServiceAsync),
            ("Хранилище: запись, удаление, отмена", StepStorageAsync),
            ("Сертификаты и доступность сайтов", StepSitesAsync),
            ("Обход блокировок по IP", StepBypassAsync),
            ("Мосты, встроенные корни и подмена", StepOldDeviceAsync),
            ("Пробное скачивание", StepTestDownloadAsync),
            ("Миниатюра обложки", StepThumbnailAsync)
        };

        for (var i = 0; i < steps.Length; i++)
        {
            if (ct.IsCancellationRequested)
            {
                Line("# Остановлено — остальные шаги пропущены");
                break;
            }

            await RunStepAsync(i + 1, steps.Length, steps[i].Name, steps[i].Run, ct);
        }

        Summary = $"Проверок {_ok + _failed}: прошло {_ok}, ошибок {_failed}, пропущено {_skipped} — за {total.Elapsed:mm\\:ss}" +
                  (ct.IsCancellationRequested ? " (остановлено)" : string.Empty);
        Line(string.Empty);
        Line($"# Итог: {Summary}");
        Line($"# Конец: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        Status = "Сохраняю журнал…";
        Changed?.Invoke();
        try
        {
            Result = await SaveTextAsync($"E2E-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt", Log);
        }
        catch (Exception ex)
        {
            Summary += $". Журнал не сохранился в папку ({ex.Message}) — скопируйте его кнопкой";
        }

        Status = ct.IsCancellationRequested ? "Остановлено" : "Готово";
        var where = Result != null ? $"Журнал: {storage.DestinationLabel}/{Result.Name}" : "Журнал можно скопировать в настройках";
        toasts.Show($"Диагностика: {(_failed == 0 ? "всё в порядке" : $"ошибок {_failed}")}. {where}", _failed == 0 ? ToastKind.Success : ToastKind.Error, 8);
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
            Info("шаг прерван: диагностику остановили");
        }
        catch (Exception ex)
        {
            Fail($"шаг упал: {ex}");
        }

        Info($"шаг занял {sw.Elapsed.TotalSeconds:0.0} с");
    }

    // ---------------------------------------------------------------- шаги

    private Task StepEnvironmentAsync(CancellationToken ct)
    {
        foreach (var (key, value) in platform.DescribeDevice())
        {
            Info($"{key}: {value}");
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
        var root = Path.Combine(FileSystem.CacheDirectory, "diagnostics");
        var probeDir = Path.Combine(root, "probe");
        var bigDir = Path.Combine(root, "big");
        var stamp = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        try
        {
            Directory.CreateDirectory(probeDir);
            await File.WriteAllTextAsync(Path.Combine(probeDir, $"E2E-проверка-{stamp}.txt"),
                "Проверка записи E2E. Файл удаляется сразу после проверки.", ct);
            Info($"папка сохранения: {storage.DestinationLabel}");

            var sw = Stopwatch.StartNew();
            var saved = await storage.SaveTreeAsync(probeDir, f => f, ct);
            Check(saved.Count == 1, saved.Count == 1
                ? $"записан «{saved[0].Name}» ({saved[0].Size} байт, {saved[0].Mime}) за {sw.ElapsedMilliseconds} мс → {saved[0].Uri}"
                : $"записано файлов: {saved.Count}, ожидался 1");
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

            // Отмена посреди копирования: 32 МБ не успевают скопироваться за 30 мс
            Directory.CreateDirectory(bigDir);
            await using (var big = File.Create(Path.Combine(bigDir, $"E2E-проверка-отмены-{stamp}.bin")))
            {
                big.SetLength(32L * 1024 * 1024);
            }

            using (var midway = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
            {
                sw.Restart();
                try
                {
                    var finished = await storage.SaveTreeAsync(bigDir, f => f, midway.Token);
                    Skip($"32 МБ скопировались за {sw.ElapsedMilliseconds} мс раньше отмены — откат не проверен");
                    foreach (var file in finished)
                    {
                        storage.Delete(file);
                    }
                }
                catch (OperationCanceledException)
                {
                    Ok($"копирование 32 МБ прервано через {sw.ElapsedMilliseconds} мс (недописанный файл хранилище удаляет само)");
                }
            }
        }
        finally
        {
            TryDelete(probeDir);
            TryDelete(bigDir);
        }
    }

    private async Task StepSitesAsync(CancellationToken ct)
    {
        var targets = SupportedSites.Sites.Where(s => s.Available).Select(s => s.Url).Distinct().ToList();
        Info($"сайтов: {targets.Count}; по {SiteParallelism} одновременно, таймаут {ProbeTimeout.TotalSeconds:0} с; " +
             $"прокси {MaskProxy(settings.Proxy)} (как у ядра)");

        using var gate = new SemaphoreSlim(SiteParallelism);
        var results = await Task.WhenAll(targets.Select(async url =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var result = await TlsProbe.ProbeAsync(new Uri(url), null, settings.Proxy, ProbeTimeout, ct);
                Report(result);
                return result;
            }
            finally
            {
                gate.Release();
            }
        }));

        Info($"итого: прошли {results.Count(r => r.Ok)}, отказ по сертификату {results.Count(r => r.TlsRejected)}, " +
             $"недоступны по сети {results.Count(r => !r.Ok && !r.TlsRejected)}, " +
             $"цепочку достроила политика {results.Count(r => r.ChainCompleted)}");
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
        var text = string.IsNullOrWhiteSpace(settings.DebugTestUrl) ? DefaultTestUrl : settings.DebugTestUrl;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url))
        {
            Fail($"ссылка «{text}» не разобрана");
            return;
        }

        if (!GetterResolver.IsSupported(url))
        {
            Fail($"для {url.Host} нет загрузчика");
            return;
        }

        var work = Path.Combine(FileSystem.CacheDirectory, "diagnostics", "download");
        TryDelete(work);
        var options = new Options
        {
            Url = [url.ToString()],
            Format = ["epub"],
            Timeout = settings.Timeout,
            Delay = settings.Delay,
            Proxy = string.IsNullOrWhiteSpace(settings.Proxy) ? null : settings.Proxy.Trim(),
            End = TestChapters,
            BookNamePattern = settings.BookNamePattern,
            SavePath = Path.Combine(work, "out"),
            TempPath = Path.Combine(work, "tmp")
        };
        Info($"книга {url}: первые {TestChapters} главы в EPUB, во временную папку (в загрузки не сохраняется), без входа на сайт");

        Action<string> trace = line => Line("  tls | " + line);
        CertificatePolicy.Traced += trace;
        var sw = Stopwatch.StartNew();
        try
        {
            var logger = new CallbackLogger(line => Line("  ядро | " + line));
            var result = await BookPipeline.RunAsync(options, logger, preview =>
            {
                _cover = preview.Cover;
                _coverMime = preview.CoverMime;
                Info($"карточка через {sw.ElapsedMilliseconds} мс: «{preview.Title}» / {preview.Author}, серия «{preview.Seria}», " +
                     $"обложка {(preview.Cover == null ? "нет" : $"{preview.Cover.Length} байт {preview.CoverMime}")}, " +
                     $"аннотация {preview.Annotation?.Length ?? 0} симв.");
            }, ct);

            _cover ??= result.Info.Cover;
            Ok($"скачано за {sw.Elapsed.TotalSeconds:0.0} с: «{result.Title}» / {result.Author}, глав {result.Info.Chapters}, " +
               $"последняя «{result.Info.LastChapter}», обложка {result.Info.Cover?.Length ?? 0} байт");
            Check(result.Files.Count > 0, $"файлов: {result.Files.Count}");
            foreach (var file in result.Files)
            {
                Info($"файл {Path.GetRelativePath(result.OutputDir, file)} — {new FileInfo(file).Length} байт");
                if (file.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
                {
                    CheckEpub(file);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail($"скачивание не удалось за {sw.Elapsed.TotalSeconds:0.0} с: {ex}");
            if (CertificatePolicy.LastRejection != null)
            {
                Info($"последний отказ по сертификату: {CertificatePolicy.LastRejection}");
            }
        }
        finally
        {
            CertificatePolicy.Traced -= trace;
            TryDelete(work);
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
        if (result.Ok)
        {
            Interlocked.Increment(ref _ok);
        }
        else
        {
            Interlocked.Increment(ref _failed);
        }

        lock (_log)
        {
            foreach (var line in result.Lines)
            {
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
        yield return $"# Версия {platform.AppVersion} ({BuildInfo.Kind(platform.AppVersion)}), сборка {platform.BuildNumber}, " +
                     $"коммит {BuildInfo.Commit}, ядро Elib2Ebook {BuildInfo.CoreVersion}";
        yield return $"# {BuildInfo.AgentNote}";
        yield return $"# Записано: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}";
    }

    /// <summary>Настройки одной-двумя строками — без паролей и адреса прокси с логином.</summary>
    public static IEnumerable<string> DescribeSettings(AppSettings s, IBookStorage storage)
    {
        yield return $"Настройки: формат по умолчанию {s.TextFormat}, авто-CBZ {YesNo(s.AutoFormat)}, картинки {YesNo(s.Images)}, " +
                     $"обложка файлом {YesNo(s.Cover)}, доп. файлы {YesNo(s.Additional)} [{string.Join(",", s.AdditionalTypes)}], " +
                     $"шаблон имени «{s.BookNamePattern}»";
        yield return $"Настройки: таймаут {s.Timeout} с, пауза {s.Delay} с, прокси {MaskProxy(s.Proxy)}, " +
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

    private void CheckEpub(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var mimetype = zip.GetEntry("mimetype");
        string content = null;
        if (mimetype != null)
        {
            using var reader = new StreamReader(mimetype.Open());
            content = reader.ReadToEnd();
        }

        Check(content == "application/epub+zip", $"EPUB: mimetype «{content ?? "нет"}»");
        var pages = zip.Entries.Count(e => e.FullName.EndsWith("html", StringComparison.OrdinalIgnoreCase));
        Check(pages > 0, $"EPUB: страниц {pages}, всего записей {zip.Entries.Count}");
    }

    private async Task<SavedFile> SaveTextAsync(string fileName, IEnumerable<string> lines)
    {
        var dir = Path.Combine(FileSystem.CacheDirectory, "diagnostics", "out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllLinesAsync(Path.Combine(dir, fileName), lines, new UTF8Encoding(false));
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
        if (_log.Count < MaxLines)
        {
            _log.Add(text.Length == 0 || text.StartsWith('#') ? text : $"{DateTime.Now:HH:mm:ss.fff} {text}");
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
