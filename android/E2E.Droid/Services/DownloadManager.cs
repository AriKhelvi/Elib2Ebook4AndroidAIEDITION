// ============================================================================
// E2E для Android — очередь скачивания: запуск, отмена, журнал, сохранение результата
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================
//
// Жизненный цикл одного «задания» (нажатие «Скачать», объект Job):
//
//   StartAsync  → спросить разрешения, создать Job, удержать foreground-сервис, уйти в фон (Task.Run)
//   RunAsync    → для каждой ссылки: BookPipeline в рабочую папку cache/jobs/<guid> → копия в «Загрузки»
//                 (или выбранную папку) → запись в историю → удаление рабочей папки
//   Finish      → отпустить сервис, показать тост и (если приложение свёрнуто) уведомление «Книга готова»
//
// Отмена не ждёт ядро: интерфейс освобождается сразу, а «хвост» задания дорабатывает в фоне вхолостую.
// Он пишет только в свой Job (журнал, файлы, карточка) и видит Job.Cancelled — поэтому до следующего
// задания ему не дотянуться. Запись в хранилище после отмены не начинается, а начатая — прерывается
// (токен отмены) и откатывается; foreground-сервис держится, пока она не закончится (см. HoldService).

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

    /// <summary>
    /// Свой формат для отдельных ссылок — когда в одной пачке и манга (CBZ), и ранобэ (текстовый формат);
    /// ссылкам, которых тут нет, достаётся Formats.
    /// </summary>
    public IReadOnlyDictionary<string, string> FormatByUrl { get; init; }

    public IReadOnlyList<string> FormatsFor(string url)
        => FormatByUrl != null && FormatByUrl.TryGetValue(url, out var format) ? [format] : Formats;

    public string Login { get; init; }
    public string Password { get; init; }

    /// <summary>«Скачать без картинок» (--no-image). Для CBZ не применяется — там нужны именно картинки.</summary>
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

    /// <summary>
    /// Защищает смену задания, State, Job.Cancelled и счётчик удержаний сервиса — всё, что должно
    /// меняться согласованно между UI-потоком (StartAsync, Cancel) и фоновым (RunAsync).
    /// </summary>
    private readonly object _sync = new();

    /// <summary>Текущее (или последнее) задание. Интерфейс читает всё отсюда; пустое — до первого скачивания.</summary>
    private volatile Job _job = new(new DownloadRequest());

    /// <summary>
    /// 1 — StartAsync уже идёт (ждёт разрешений). Защита от двойного старта: между проверкой IsRunning
    /// и State = Running есть await-ы, и второе нажатие успело бы проскочить проверку.
    /// </summary>
    private int _starting;

    /// <summary>
    /// Сколько причин держать foreground-сервис: идущее задание (одна, см. Job.HoldsService) плюс каждая
    /// идущая запись в хранилище (см. WriteResultAsync). Меняется только под _sync.
    /// </summary>
    private int _serviceHolds;

    /// <summary>1 — обновление интерфейса уже запланировано (см. NotifyChanged).</summary>
    private int _changePending;

    /// <summary>Когда последний раз обновляли текст в шторке. Только под _sync.</summary>
    private DateTime _lastNotificationUpdate;

    /// <summary>Интерфейс подписывается и перерисовывается. Может вызываться из фонового потока!</summary>
    public event Action Changed;

    public DownloadState State { get; private set; }

    public bool IsRunning => State == DownloadState.Running;

    /// <summary>Последняя строка журнала — показывается в блоке прогресса, как на кнопке веб-версии.</summary>
    public string Status => _job.Status;

    /// <summary>Карточка книги (название, автор, обложка) — появляется до того, как скачаются главы.</summary>
    public BookPreview Preview => _job.Preview;

    /// <summary>Адрес миниатюры обложки карточки (/_covers/…, см. CoverThumbnails); null — обложки нет.</summary>
    public string PreviewCoverUrl => _job.PreviewCoverUrl;

    /// <summary>Номер текущей ссылки (с 1) при скачивании нескольких книг.</summary>
    public int Current => _job.Current;

    /// <summary>
    /// Сколько глав текущей книги уже загружено — по строкам журнала «Загружаю главу …» (GetterBase ядра).
    /// Общего числа глав ядро не сообщает, поэтому полоса прогресса бегущая, а рядом — этот счётчик.
    /// </summary>
    public int ChaptersLoaded => Volatile.Read(ref _job.Chapters);

    public int Total => _job.Request.Urls.Count;

    // Коллекции читаются из UI-потока, а пишутся из фонового — поэтому отдаём копии под lock

    public IReadOnlyList<string> Log => Copy(_job.Log);

    /// <summary>Файлы, сохранённые в текущем/последнем задании (книги, обложки, доп. файлы).</summary>
    public IReadOnlyList<SavedFile> Saved => Copy(_job.Saved);

    /// <summary>Короткие сообщения об ошибках по каждой не скачавшейся ссылке.</summary>
    public IReadOnlyList<string> Errors => Copy(_job.Errors);

    /// <summary>
    /// Запускает задание. Вызывать из UI-потока (запрос разрешения показывает системный диалог).
    /// Повторный вызов, пока идёт запуск или скачивание, ничего не делает.
    /// </summary>
    /// <returns>true — задание запущено; false — уже идёт другое, нет ссылок или нет доступа к памяти.</returns>
    public async Task<bool> StartAsync(DownloadRequest request)
    {
        if (request.Urls.Count == 0 || IsRunning)
        {
            return false;
        }

        // Атомарно занимаем запуск: двойное нажатие «Скачать» (или «Скачать заново» + авто-старт по ссылке
        // из «Поделиться») иначе запустило бы два задания, пока первое ждёт разрешений
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            // Пока запуск занят, Running может выставить только этот вызов — но проверяем ещё раз
            // на случай, если предыдущий запуск закончился между первой проверкой и CompareExchange
            if (IsRunning)
            {
                return false;
            }

            // Android 7–9: без разрешения на запись книгу некуда сохранить — не начинаем зря
            if (!await storage.EnsureWriteAccessAsync())
            {
                toasts.Show("Без доступа к памяти книги некуда сохранить. Разрешите доступ или выберите папку в настройках", ToastKind.Error, 8);
                return false;
            }

            // Android 13+: без разрешения не будет видно прогресса в шторке (сама загрузка всё равно пойдёт)
            await platform.RequestNotificationPermissionAsync();

            var job = new Job(request);
            lock (_sync)
            {
                _job = job;
                State = DownloadState.Running;
                job.HoldsService = true;
                HoldService(job.Status);
            }

            Changed?.Invoke();
            _ = Task.Run(() => RunAsync(job));
            return true;
        }
        finally
        {
            // Освобождаем запуск только после State = Running: следующий вызов увидит IsRunning
            Volatile.Write(ref _starting, 0);
        }
    }

    /// <summary>Отмена: интерфейс освобождается сразу, ядро дорабатывает «вхолостую» в фоне.</summary>
    public void Cancel()
    {
        Job job;
        lock (_sync)
        {
            if (!IsRunning)
            {
                return;
            }

            job = _job;
            // До Cts.Cancel(): к моменту, когда хвост задания увидит отмену, флаг уже стоит,
            // и все его записи в журнал, шторку и историю отбрасываются
            job.Cancelled = true;
            job.Status = "Отменено";
            State = DownloadState.Cancelled;
            ReleaseJobHold(job);

            // Сервис ещё держит начатая запись в хранилище: она прервётся и откатится, тогда он и остановится
            if (_serviceHolds > 0)
            {
                platform.UpdateBackgroundWork("Отменяю: прерываю сохранение…");
            }
        }

        AppendLog(job, "Скачивание отменено");

        // Ядро может ещё несколько секунд дожидаться повторов запросов — не ждём его,
        // а отпускаем задание доработать в фоне; его результаты будут проигнорированы
        job.Cts.Cancel();
        Changed?.Invoke();
    }

    /// <summary>Убирает результаты прошлого скачивания с главного экрана.</summary>
    public void Dismiss()
    {
        lock (_sync)
        {
            if (IsRunning)
            {
                return;
            }

            State = DownloadState.Idle;
        }

        Changed?.Invoke();
    }

    /// <summary>Фоновая часть задания. Что бы ни случилось, заканчивается Finish — «вечного» скачивания не будет.</summary>
    private async Task RunAsync(Job job)
    {
        try
        {
            await DownloadAllAsync(job);
        }
        catch (Exception ex)
        {
            // Ошибки отдельных книг ловятся в DownloadAllAsync; сюда попадает только непредвиденное
            if (!job.Cancelled)
            {
                lock (job.Errors) job.Errors.Add(ex.Message);
                Write(job, $"Непредвиденная ошибка: {ex}");
            }
        }
        finally
        {
            Finish(job);
        }
    }

    /// <summary>По очереди скачивает все ссылки задания.</summary>
    private async Task DownloadAllAsync(Job job)
    {
        var request = job.Request;
        var ct = job.Cts.Token;

        // Рабочие папки — в кэше приложения: Android может их чистить сам, а мы удаляем их после каждой книги
        var jobsRoot = Path.Combine(FileSystem.CacheDirectory, "jobs");

        // Миниатюры карточек прошлого задания больше не на экране (интерфейс читает уже этот Job)
        TryDelete(CoverThumbnails.PreviewsDir);

        for (var i = 0; i < request.Urls.Count && !job.Cancelled; i++)
        {
            var url = request.Urls[i];
            var workDir = Path.Combine(jobsRoot, Guid.NewGuid().ToString("N"));
            job.Current = i + 1;
            SetPreview(job, null);
            Interlocked.Exchange(ref job.Chapters, 0);

            // Карточка именно этой книги — для записи о неудаче (в job.Preview её может сменить следующая)
            BookPreview preview = null;

            try
            {
                Write(job, $"Начинаю генерацию книги {url}");
                var options = BuildOptions(request, url, workDir);
                var logger = new CallbackLogger(line => Write(job, line));

                var result = await BookPipeline.RunAsync(options, logger, p =>
                {
                    preview = p;
                    if (!job.Cancelled)
                    {
                        SetPreview(job, p);
                        NotifyChanged();
                    }
                }, ct);

                // Отменили, пока ядро работало: сохранять уже нечего
                ct.ThrowIfCancellationRequested();

                Write(job, $"Сохраняю в {storage.DestinationLabel}");
                await WriteResultAsync(job, async () =>
                {
                    // Копируем всё, что собрало ядро (книги, обложку, доп. файлы), в общую папку.
                    // Отмена посреди копирования откатывает файлы этой книги (см. IBookStorage.SaveTreeAsync)
                    var saved = await storage.SaveTreeAsync(result.OutputDir, f =>
                    {
                        f.BookTitle = result.Title;
                        f.Author = result.Author;
                        f.SourceUrl = url;
                        return f;
                    }, ct);

                    if (saved.Count == 0)
                    {
                        throw new InvalidOperationException("Ядро не создало ни одного файла");
                    }

                    // Файлы уже в папке — запись о них нужна, даже если отмену нажали в последний миг:
                    // иначе книга осталась бы в «Загрузках», но пропала из вкладки «Книги»
                    var record = NewRecord(url, request);
                    record.Files = saved.Where(f => string.IsNullOrEmpty(f.Folder)).ToList();
                    FillRecord(record, result.Info);
                    history.Add(record);
                    lock (job.Saved) job.Saved.AddRange(saved);
                    Write(job, $"Готово: {result.Title} ({saved.Count} файл(ов))");
                });
            }
            catch (OperationCanceledException) when (job.Cancelled)
            {
                return;
            }
            catch (Exception ex)
            {
                if (job.Cancelled)
                {
                    return; // после отмены ядро падает с чем угодно — это не ошибка книги
                }

                // Ошибка одной книги не останавливает остальные ссылки.
                // TaskCanceledException без отмены = сработал таймаут HttpClient.
                var message = ex is TaskCanceledException
                    ? "Сервер не успевает ответить. Попробуйте увеличить таймаут в настройках"
                    : ex.Message;
                lock (job.Errors) job.Errors.Add($"{url}: {message}");
                // В журнал — полный текст исключения со стеком: пригодится для отчёта об ошибке
                Write(job, $"Генерация книги {url} завершилась с ошибкой. {ex}");

                // Неудачная загрузка тоже попадает в «Книги» — с пометкой и кнопкой «Скачать заново»
                await WriteResultAsync(job, () =>
                {
                    var failed = NewRecord(url, request);
                    failed.Error = message;
                    FillRecord(failed, preview);
                    history.Add(failed);
                    return Task.CompletedTask;
                });
            }
            finally
            {
                TryDelete(workDir);
            }
        }
    }

    /// <summary>
    /// Карточка книги для интерфейса: обложка уменьшается в файл-миниатюру в кэше и показывается по адресу
    /// (CoverThumbnails), а не data:-URL. Миниатюра прошлой карточки этого задания больше не нужна — удаляется.
    /// </summary>
    private void SetPreview(Job job, BookPreview preview)
    {
        var thumbnail = preview?.Cover == null ? null : platform.SaveThumbnail(preview.Cover, CoverThumbnails.NewPreviewPath());
        var old = job.PreviewCoverPath;

        // Сначала адрес, потом карточка: интерфейс, увидевший новую карточку, увидит и её обложку
        job.PreviewCoverPath = thumbnail;
        job.PreviewCoverUrl = CoverThumbnails.UrlFor(thumbnail);
        job.Preview = preview;

        if (old != null)
        {
            TryDeleteFile(old);
        }
    }

    /// <summary>
    /// Запись результата (файлы, история, миниатюра обложки). Не начинается, если задание уже отменено,
    /// а начатая держит foreground-сервис до своего конца — даже если отмену нажмут посреди записи:
    /// иначе Android мог бы убить свёрнутое приложение на середине файла.
    /// </summary>
    /// <returns>false — задание отменено, ничего не записано.</returns>
    private async Task<bool> WriteResultAsync(Job job, Func<Task> write)
    {
        lock (_sync)
        {
            if (job.Cancelled)
            {
                return false;
            }

            // Задание не отменено — значит, его удержание ещё действует и сервис уже запущен: просто считаем
            _serviceHolds++;
        }

        try
        {
            await write();
            return true;
        }
        finally
        {
            lock (_sync)
            {
                ReleaseService();
            }
        }
    }

    /// <summary>Итог задания: статус, тост, уведомление. Для отменённого — только отпускает сервис.</summary>
    private void Finish(Job job)
    {
        var saved = Copy(job.Saved);
        var errors = Copy(job.Errors);

        lock (_sync)
        {
            ReleaseJobHold(job);
            // Отменённое задание итогов не подводит: интерфейс уже показывает «Отменено» (или следующее задание)
            if (job.Cancelled)
            {
                return;
            }

            State = saved.Count > 0 ? DownloadState.Done : DownloadState.Failed;
            job.Status = errors.Count == 0 ? "Готово" : "Завершено с ошибками";
        }

        Changed?.Invoke();

        var books = saved.Where(f => f.IsBook).ToList();
        var title = books.FirstOrDefault()?.BookTitle ?? job.Preview?.Title;
        var total = job.Request.Urls.Count;

        string text;
        if (errors.Count == 0)
        {
            text = total > 1 ? $"Готово книг: {total}" : $"Книга «{title}» сохранена";
            toasts.Show(text, ToastKind.Success);
        }
        else
        {
            text = saved.Count > 0 ? $"Часть книг не скачалась: {errors.Count} из {total}" : errors[0];
            toasts.Show(text, ToastKind.Error, 10);
        }

        // Если приложение на экране, хватает тоста; если свёрнуто — напоминаем уведомлением
        if (!platform.IsInForeground)
        {
            platform.NotifyFinished(errors.Count == 0 ? "Книга готова" : "Скачивание завершилось с ошибкой", text);
        }
    }

    /// <summary>
    /// Ещё одна причина держать foreground-сервис (он не даёт Android убить процесс, пока приложение свёрнуто).
    /// Первая запускает сервис, остальные только обновляют текст в шторке. Вызывать под _sync.
    /// </summary>
    private void HoldService(string text)
    {
        if (_serviceHolds++ == 0)
        {
            platform.StartBackgroundWork(text);
        }
        else
        {
            platform.UpdateBackgroundWork(text);
        }
    }

    /// <summary>Последняя причина ушла — останавливаем сервис. Вызывать под _sync.</summary>
    private void ReleaseService()
    {
        if (--_serviceHolds == 0)
        {
            platform.StopBackgroundWork();
        }
    }

    /// <summary>Снимает удержание сервиса самим заданием (в Cancel или Finish — что раньше). Вызывать под _sync.</summary>
    private void ReleaseJobHold(Job job)
    {
        if (job.HoldsService)
        {
            job.HoldsService = false;
            ReleaseService();
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
            Format = request.FormatsFor(url),
            Login = NullIfEmpty(request.Login),
            Password = NullIfEmpty(request.Password),
            Proxy = NullIfEmpty(settings.Proxy),
            Timeout = settings.Timeout,
            Delay = settings.Delay,
            NoImage = request.NoImage && !request.FormatsFor(url).Contains("cbz"),
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

    private static BookRecord NewRecord(string url, DownloadRequest request) => new()
    {
        Url = url,
        Formats = request.FormatsFor(url).ToList(),
        NoImage = request.NoImage
    };

    /// <summary>Сведения о скачанной книге — из того, что вернуло ядро.</summary>
    private void FillRecord(BookRecord record, BookInfo info)
    {
        record.Title = info.Title;
        record.Author = info.Author;
        record.AuthorUrl = info.AuthorUrl;
        record.CoAuthors = info.CoAuthors.ToList();
        record.SeriaName = info.SeriaName;
        record.SeriaNumber = info.SeriaNumber;
        record.SeriaUrl = info.SeriaUrl;
        record.Annotation = info.Annotation;
        record.Chapters = info.Chapters;
        record.LastChapter = info.LastChapter;
        record.CoverPath = info.Cover == null ? null : platform.SaveThumbnail(info.Cover, CoverThumbnails.RecordPath(record.Id));
    }

    /// <summary>Не скачалось — берём что успели узнать из карточки книги (если ядро до неё дошло).</summary>
    private void FillRecord(BookRecord record, BookPreview preview)
    {
        if (preview == null)
        {
            return;
        }

        record.Title = preview.Title;
        record.Author = preview.Author;
        record.AuthorUrl = preview.AuthorUrl;
        record.SeriaName = preview.Seria;
        record.SeriaUrl = preview.SeriaUrl;
        record.Annotation = preview.Annotation;
        // Битая картинка — SaveThumbnail вернёт null, запись будет без обложки
        record.CoverPath = preview.Cover == null ? null : platform.SaveThumbnail(preview.Cover, CoverThumbnails.RecordPath(record.Id));
    }

    /// <summary>Строка журнала от ядра или от нас: в журнал, в статус и (не чаще раза в секунду) в шторку.</summary>
    private void Write(Job job, string line)
    {
        if (job.Cancelled || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        AppendLog(job, line);
        job.Status = line.Length > 300 ? line[..300] + "…" : line;
        if (line.StartsWith("Загружаю главу", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref job.Chapters);
        }

        lock (_sync)
        {
            // Под _sync и с повторной проверкой: после Cancel хвост задания не перепишет текст в шторке.
            // Обновлять уведомление чаще раза в секунду бессмысленно, а Android начинает такие обновления пропускать
            if (!job.Cancelled && DateTime.UtcNow - _lastNotificationUpdate > TimeSpan.FromSeconds(1))
            {
                _lastNotificationUpdate = DateTime.UtcNow;
                var total = job.Request.Urls.Count;
                var prefix = total > 1 ? $"[{job.Current}/{total}] " : string.Empty;
                platform.UpdateBackgroundWork(prefix + job.Status);
            }
        }

        NotifyChanged();
    }

    private static void AppendLog(Job job, string line)
    {
        lock (job.Log)
        {
            job.Log.Add($"{DateTime.Now:HH:mm:ss} {line}");
            if (job.Log.Count > MaxLogLines)
            {
                job.Log.RemoveRange(0, job.Log.Count - MaxLogLines);
            }
        }
    }

    private static IReadOnlyList<T> Copy<T>(List<T> list)
    {
        lock (list)
        {
            return list.ToList();
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

    /// <summary>
    /// При запуске приложения удаляет рабочие папки и миниатюры карточек, оставшиеся от прошлого (например, после краша).
    /// </summary>
    public static void CleanupLeftovers()
    {
        TryDelete(Path.Combine(FileSystem.CacheDirectory, "jobs"));
        TryDelete(CoverThumbnails.PreviewsDir);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // подчистится при следующем запуске (CleanupLeftovers)
        }
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

    /// <summary>
    /// Одно задание (нажатие «Скачать»): запрос, отмена и всё, что видит интерфейс. Фоновая часть пишет
    /// только сюда, поэтому «хвост» отменённого задания не может испортить журнал, файлы или карточку следующего.
    /// </summary>
    private sealed class Job(DownloadRequest request)
    {
        public DownloadRequest Request { get; } = request;

        /// <summary>Ядро токен не принимает — BookPipeline по нему закрывает HttpClient; хранилище обрывает копирование.</summary>
        public CancellationTokenSource Cts { get; } = new();

        /// <summary>Задание отменено. Ставится только под DownloadManager._sync и раньше Cts.Cancel().</summary>
        public volatile bool Cancelled;

        /// <summary>Задание держит foreground-сервис (снимается в Cancel или Finish). Только под _sync.</summary>
        public bool HoldsService;

        public volatile string Status = "Подготовка…";
        public volatile BookPreview Preview;
        public volatile int Current;

        /// <summary>Миниатюра обложки карточки: адрес для интерфейса и файл в кэше (его удаляем при смене карточки).</summary>
        public volatile string PreviewCoverUrl;
        public string PreviewCoverPath;

        /// <summary>Счётчик глав текущей книги (через Interlocked).</summary>
        public int Chapters;

        // Пишутся из фонового потока, читаются из UI — только под lock на сам список
        public readonly List<string> Log = [];
        public readonly List<SavedFile> Saved = [];
        public readonly List<string> Errors = [];
    }
}
