// ============================================================================
// E2E для Android — очередь скачивания: запуск, отмена, журнал, сохранение результата
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================
//
// Жизненный цикл одного «задания» (нажатие «Скачать»):
//
//   StartAsync  → спросить разрешение на уведомления, запустить foreground-сервис, уйти в фон (Task.Run)
//   RunAsync    → для каждой ссылки: BookPipeline в рабочую папку cache/jobs/<guid> → копия в «Загрузки»
//                 (или выбранную папку) → запись в историю → удаление рабочей папки
//   Finish      → остановить сервис, показать тост и (если приложение свёрнуто) уведомление «Книга готова»
//
// Отмена не ждёт ядро: задание получает новый номер (_jobId), а «хвост» старого задания доработает
// в фоне и будет проигнорирован (все его колбэки сверяют свой jobId с текущим).

using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Configs;

namespace E2E.Droid.Services;

/// <summary>Что скачивать — собирается из формы на главном экране.</summary>
public sealed class DownloadRequest
{
    /// <summary>Одна или несколько ссылок; каждая скачивается отдельно, со своим загрузчиком.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    /// <summary>epub, fb2, cbz, txt — как ключ --format консольной версии.</summary>
    public IReadOnlyList<string> Formats { get; init; } = [];

    public string Login { get; init; }
    public string Password { get; init; }

    /// <summary>«Скачать без картинок» (--no-image).</summary>
    public bool NoImage { get; init; }

    /// <summary>Диапазон глав по номерам (--start/--end); отрицательные числа считаются с конца.</summary>
    public int? Start { get; init; }
    public int? End { get; init; }

    /// <summary>Диапазон глав по названиям (--start-name/--end-name).</summary>
    public string StartName { get; init; }
    public string EndName { get; init; }
}

public enum DownloadState
{
    /// <summary>Ничего не происходит, результатов на экране нет.</summary>
    Idle,
    Running,
    /// <summary>Закончено, хотя бы одна книга сохранена.</summary>
    Done,
    /// <summary>Закончено, ничего не сохранено.</summary>
    Failed,
    Cancelled
}

/// <summary>
/// Очередь скачивания: живёт всё время работы приложения (переход между вкладками её не прерывает),
/// держит foreground-сервис, пока идёт загрузка, и складывает готовые файлы в общую папку.
/// Зарегистрирована в DI как singleton (см. MauiProgram).
/// </summary>
public sealed class DownloadManager(
    AppSettings settings,
    IBookStorage storage,
    IPlatformServices platform,
    HistoryStore history,
    ToastService toasts)
{
    /// <summary>Журнал ограничен, чтобы книга на тысячу глав не съела память.</summary>
    private const int MaxLogLines = 3000;

    private readonly List<string> _log = [];
    private readonly List<SavedFile> _saved = [];
    private readonly List<string> _errors = [];
    private CancellationTokenSource _cts;

    /// <summary>Номер текущего задания. Колбэки отменённых заданий видят «чужой» номер и ничего не делают.</summary>
    private int _jobId;

    /// <summary>1 — обновление интерфейса уже запланировано (см. NotifyChanged).</summary>
    private int _changePending;

    private DateTime _lastNotificationUpdate;

    /// <summary>Интерфейс подписывается и перерисовывается. Может вызываться из фонового потока!</summary>
    public event Action Changed;

    public DownloadState State { get; private set; }

    public bool IsRunning => State == DownloadState.Running;

    /// <summary>Последняя строка журнала — показывается в блоке прогресса, как на кнопке веб-версии.</summary>
    public string Status { get; private set; }

    /// <summary>Карточка книги (название, автор, обложка) — появляется до того, как скачаются главы.</summary>
    public BookPreview Preview { get; private set; }

    /// <summary>Номер текущей ссылки (с 1) при скачивании нескольких книг.</summary>
    public int Current { get; private set; }

    public int Total { get; private set; }

    // Коллекции читаются из UI-потока, а пишутся из фонового — поэтому отдаём копии под lock

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

    /// <summary>Файлы, сохранённые в текущем/последнем задании (книги, обложки, доп. файлы).</summary>
    public IReadOnlyList<SavedFile> Saved
    {
        get
        {
            lock (_saved)
            {
                return _saved.ToList();
            }
        }
    }

    /// <summary>Короткие сообщения об ошибках по каждой не скачавшейся ссылке.</summary>
    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_errors)
            {
                return _errors.ToList();
            }
        }
    }

    /// <summary>Запускает задание. Вызывать из UI-потока (запрос разрешения показывает системный диалог).</summary>
    public async Task StartAsync(DownloadRequest request)
    {
        if (IsRunning || request.Urls.Count == 0)
        {
            return;
        }

        // Android 13+: без разрешения не будет видно прогресса в шторке (сама загрузка всё равно пойдёт)
        await platform.RequestNotificationPermissionAsync();

        var jobId = Interlocked.Increment(ref _jobId);
        _cts = new CancellationTokenSource();
        lock (_log) _log.Clear();
        lock (_saved) _saved.Clear();
        lock (_errors) _errors.Clear();
        Preview = null;
        Status = "Подготовка…";
        Current = 0;
        Total = request.Urls.Count;
        State = DownloadState.Running;
        Changed?.Invoke();

        // Foreground-сервис не даёт Android убить процесс, пока приложение свёрнуто
        platform.StartBackgroundWork(Status);
        var ct = _cts.Token;
        _ = Task.Run(() => RunAsync(jobId, request, ct));
    }

    /// <summary>Отмена: интерфейс освобождается сразу, ядро дорабатывает «вхолостую» в фоне.</summary>
    public void Cancel()
    {
        if (!IsRunning)
        {
            return;
        }

        // Ядро может ещё несколько секунд дожидаться повторов запросов — не ждём его,
        // а отпускаем задание доработать в фоне; его результаты будут проигнорированы.
        _cts?.Cancel();
        Interlocked.Increment(ref _jobId);
        AppendLog("Скачивание отменено");
        Status = "Отменено";
        State = DownloadState.Cancelled;
        platform.StopBackgroundWork();
        Changed?.Invoke();
    }

    /// <summary>Убирает результаты прошлого скачивания с главного экрана.</summary>
    public void Dismiss()
    {
        if (IsRunning)
        {
            return;
        }

        State = DownloadState.Idle;
        Preview = null;
        Changed?.Invoke();
    }

    /// <summary>Фоновая часть задания: по очереди скачивает все ссылки.</summary>
    private async Task RunAsync(int jobId, DownloadRequest request, CancellationToken ct)
    {
        // Рабочие папки — в кэше приложения: Android может их чистить сам, а мы удаляем их после каждой книги
        var jobsRoot = Path.Combine(FileSystem.CacheDirectory, "jobs");

        for (var i = 0; i < request.Urls.Count && !ct.IsCancellationRequested; i++)
        {
            var url = request.Urls[i];
            var workDir = Path.Combine(jobsRoot, Guid.NewGuid().ToString("N"));
            if (jobId == _jobId)
            {
                Current = i + 1;
                Preview = null;
            }

            try
            {
                Write(jobId, $"Начинаю генерацию книги {url}");
                var options = BuildOptions(request, url, workDir);
                var logger = new CallbackLogger(line => Write(jobId, line));

                var result = await BookPipeline.RunAsync(options, logger, preview =>
                {
                    if (jobId == _jobId)
                    {
                        Preview = preview;
                        NotifyChanged();
                    }
                }, ct);

                if (jobId != _jobId)
                {
                    return; // задание отменили, пока ядро работало
                }

                // Копируем всё, что собрало ядро (книги, обложку, доп. файлы), в общую папку
                Write(jobId, $"Сохраняю в {storage.DestinationLabel}");
                var saved = await storage.SaveTreeAsync(result.OutputDir, f =>
                {
                    f.BookTitle = result.Title;
                    f.Author = result.Author;
                    f.SourceUrl = url;
                    return f;
                });

                if (saved.Count == 0)
                {
                    throw new InvalidOperationException("Ядро не создало ни одного файла");
                }

                // В историю («Книги») попадают только файлы из корня: книги и обложки, без россыпи доп. файлов
                history.AddRange(saved.Where(f => string.IsNullOrEmpty(f.Folder)));
                lock (_saved) _saved.AddRange(saved);
                Write(jobId, $"Готово: {result.Title} ({saved.Count} файл(ов))");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (jobId != _jobId)
                {
                    return;
                }

                // Ошибка одной книги не останавливает остальные ссылки.
                // TaskCanceledException без отмены = сработал таймаут HttpClient.
                var message = ex is TaskCanceledException
                    ? "Сервер не успевает ответить. Попробуйте увеличить таймаут в настройках"
                    : ex.Message;
                lock (_errors) _errors.Add($"{url}: {message}");
                // В журнал — полный текст исключения со стеком: пригодится для отчёта об ошибке
                Write(jobId, $"Генерация книги {url} завершилась с ошибкой. {ex}");
            }
            finally
            {
                TryDelete(workDir);
            }
        }

        if (jobId != _jobId)
        {
            return;
        }

        Finish();
    }

    /// <summary>Итог задания: статус, тост, уведомление.</summary>
    private void Finish()
    {
        var saved = Saved;
        var errors = Errors;
        var books = saved.Where(f => f.IsBook).ToList();
        var title = books.FirstOrDefault()?.BookTitle ?? Preview?.Title;

        State = saved.Count > 0 ? DownloadState.Done : DownloadState.Failed;
        Status = errors.Count == 0 ? "Готово" : "Завершено с ошибками";
        platform.StopBackgroundWork();
        Changed?.Invoke();

        string text;
        if (errors.Count == 0)
        {
            text = Total > 1 ? $"Готово книг: {Total}" : $"Книга «{title}» сохранена";
            toasts.Show(text, ToastKind.Success);
        }
        else
        {
            text = saved.Count > 0 ? $"Часть книг не скачалась: {errors.Count} из {Total}" : errors[0];
            toasts.Show(text, ToastKind.Error, 10);
        }

        // Если приложение на экране, хватает тоста; если свёрнуто — напоминаем уведомлением
        if (!platform.IsInForeground)
        {
            platform.NotifyFinished(errors.Count == 0 ? "Книга готова" : "Скачивание завершилось с ошибкой", text);
        }
    }

    /// <summary>
    /// Опции ядра для одной ссылки — аналог разбора командной строки в Elib2EbookCli.
    /// ВАЖНО: у new Options() таймаут равен 0 (120 подставляет только парсер CLI), поэтому всё задаём явно.
    /// </summary>
    private Options BuildOptions(DownloadRequest request, string url, string workDir)
    {
        var additionalTypes = settings.AdditionalTypes
            .Select(t => Enum.TryParse<AdditionalTypeEnum>(t, true, out var type) ? (AdditionalTypeEnum?)type : null)
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .ToList();

        return new Options
        {
            Url = [url],
            Format = request.Formats,
            Login = NullIfEmpty(request.Login),
            Password = NullIfEmpty(request.Password),
            Proxy = NullIfEmpty(settings.Proxy),
            Timeout = settings.Timeout,
            Delay = settings.Delay,
            NoImage = request.NoImage,
            Start = request.Start,
            End = request.End,
            StartName = NullIfEmpty(request.StartName),
            EndName = NullIfEmpty(request.EndName),
            Cover = settings.Cover,
            Additional = settings.Additional,
            AdditionalTypes = additionalTypes,
            BookNamePattern = settings.BookNamePattern,
            SavePath = Path.Combine(workDir, "out"), // сюда билдеры пишут готовые файлы
            TempPath = Path.Combine(workDir, "tmp")  // сюда ядро складывает скачанные картинки
        };
    }

    /// <summary>Строка журнала от ядра или от нас: в журнал, в статус и (не чаще раза в секунду) в шторку.</summary>
    private void Write(int jobId, string line)
    {
        if (jobId != _jobId || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        AppendLog(line);
        Status = line.Length > 300 ? line[..300] + "…" : line;

        // Обновлять уведомление чаще раза в секунду бессмысленно, а Android начинает такие обновления пропускать
        if (DateTime.UtcNow - _lastNotificationUpdate > TimeSpan.FromSeconds(1))
        {
            _lastNotificationUpdate = DateTime.UtcNow;
            var prefix = Total > 1 ? $"[{Current}/{Total}] " : string.Empty;
            platform.UpdateBackgroundWork(prefix + Status);
        }

        NotifyChanged();
    }

    private void AppendLog(string line)
    {
        lock (_log)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss} {line}");
            if (_log.Count > MaxLogLines)
            {
                _log.RemoveRange(0, _log.Count - MaxLogLines);
            }
        }
    }

    /// <summary>
    /// Ядро пишет в лог очень часто (каждая глава и картинка) — склеиваем обновления UI:
    /// не больше одной перерисовки за 200 мс.
    /// </summary>
    private void NotifyChanged()
    {
        if (Interlocked.Exchange(ref _changePending, 1) == 1)
        {
            return;
        }

        _ = Task.Delay(200).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _changePending, 0);
            Changed?.Invoke();
        });
    }

    /// <summary>При запуске приложения удаляет рабочие папки, оставшиеся от прошлого (например, после краша).</summary>
    public static void CleanupLeftovers()
    {
        TryDelete(Path.Combine(FileSystem.CacheDirectory, "jobs"));
    }

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
            // временные файлы подчистятся при следующем запуске
        }
    }

    private static string NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
