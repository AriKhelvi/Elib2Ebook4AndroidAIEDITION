// ============================================================================
// E2E для Android — конвейер скачивания одной книги через ядро Elib2Ebook (без MAUI)
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================
//
// Этот файл НЕ зависит от MAUI/Android, поэтому он же подключён в android/CoreSmoke и проверяется на ПК.
// Логика повторяет Program.cs консольной утилиты (src/Elib2EbookCli) и Home.razor веб-версии:
//
//   BookGetterConfig.GetDefault  → HttpClient с cookies, прокси, таймаутом (как в CLI)
//   GetterResolver.Create        → загрузчик нужного сайта по ссылке
//   getter.Init / Authorize      → подготовка сессии, вход по логину (если задан)
//   getter.Get (NoChapters=true) → быстрое «превью»: название, автор, обложка — для карточки в интерфейсе.
//                                  Необязательное: его ошибки не роняют скачивание, а для сайтов, где
//                                  такой проход всё равно качает всю книгу, оно отключено (NoPreviewGetters)
//   getter.Get                   → полная книга со всеми главами и картинками
//   BuilderProvider.Get(format)  → сборка EPUB/FB2/CBZ/TXT в рабочую папку задания
//   AdditionaFileBuilder         → доп. файлы (аудио, вложения), если включены в настройках
//
// Готовые файлы остаются в options.SavePath; копированием в «Загрузки» занимается DownloadManager.

using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc;
using Microsoft.Extensions.Logging;

namespace E2E.Droid.Services;

/// <summary>
/// Краткие сведения о книге для карточки на главном экране. Это отдельный «снимок», а не сам <see cref="Book"/>:
/// временные файлы книги (в том числе обложка) удаляются сразу после сборки, а карточка должна жить дольше.
/// </summary>
/// <param name="Cover">Обложка как есть (байты с сайта); null — обложки нет. Интерфейс показывает не её,
/// а уменьшенную копию файлом (см. CoverThumbnails) — без base64 и data:-URL.</param>
/// <param name="CoverMime">Тип картинки обложки (image/jpeg, image/png…); null — обложки нет.</param>
public sealed record BookPreview(
    string Url,
    string Title,
    string Author,
    string AuthorUrl,
    string Seria,
    string SeriaUrl,
    string Annotation,
    byte[] Cover,
    string CoverMime)
{
    /// <summary>Снимает данные с книги; обложка копируется байтами (временные файлы книги скоро удалятся).</summary>
    public static BookPreview From(Book book)
    {
        // Обложки бывают не у всех книг — тогда Cover == null и карточка покажет заглушку
        byte[] cover = null;
        string mime = null;
        var content = book.Cover?.Content;
        if (content is { Length: > 0 })
        {
            cover = content;
            // Тип картинки — по расширению временного файла; если не знаем — JPEG (декодер всё равно разберётся)
            mime = (book.Cover.Extension ?? string.Empty).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                _ => "image/jpeg"
            };
        }

        // «Серия #3» — так же, как в ShortBook.razor веб-версии
        var seria = book.Seria == null
            ? null
            : book.Seria.Name + (string.IsNullOrWhiteSpace(book.Seria.Number) ? string.Empty : $" #{book.Seria.Number}");

        return new BookPreview(
            book.Url?.ToString(),
            book.Title,
            book.Author?.Name,
            book.Author?.Url?.ToString(),
            seria,
            book.Seria?.Url?.ToString(),
            book.Annotation,
            cover,
            mime);
    }
}

/// <summary>Сведения о скачанной книге для вкладки «Книги»: описание, сколько глав и до какой, обложка.</summary>
public sealed record BookInfo(
    string Url,
    string Title,
    string Author,
    string AuthorUrl,
    IReadOnlyList<string> CoAuthors,
    string SeriaName,
    string SeriaNumber,
    string SeriaUrl,
    string Annotation,
    int Chapters,
    string LastChapter,
    byte[] Cover)
{
    /// <summary>Аннотации бывают огромными (с картинками и стихами) — в историю кладём не больше этого.</summary>
    private const int MaxAnnotation = 8000;

    public static BookInfo From(Book book)
    {
        // Глава без текста (IsValid == false) не скачалась или вне выбранного диапазона
        var chapters = book.Chapters.Where(c => c.IsValid).ToList();
        var annotation = book.Annotation;
        if (annotation is { Length: > MaxAnnotation })
        {
            annotation = annotation[..MaxAnnotation] + "…";
        }

        return new BookInfo(
            book.Url?.ToString(),
            book.Title,
            book.Author?.Name,
            book.Author?.Url?.ToString(),
            (book.CoAuthors ?? []).Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
            book.Seria?.Name,
            book.Seria?.Number,
            book.Seria?.Url?.ToString(),
            annotation,
            chapters.Count,
            chapters.LastOrDefault()?.Title,
            book.Cover?.Content is { Length: > 0 } cover ? cover : null);
    }
}

/// <summary>Результат задания: сведения о книге (для истории) и список созданных файлов в рабочей папке.</summary>
public sealed record BookJobResult(BookInfo Info, string OutputDir, IReadOnlyList<string> Files)
{
    public string Title => Info.Title;

    public string Author => Info.Author;
}

/// <summary>
/// Тот же конвейер, что в Elib2EbookCli/Elib2EbookWeb: Init → Authorize → Get → Build по форматам.
/// Не зависит от MAUI, поэтому проверяется и на десктопе (android/CoreSmoke).
/// </summary>
public static class BookPipeline
{
    /// <summary>
    /// Загрузчики, у которых быстрый проход без глав (NoChapters) всё равно скачивает книгу целиком: файл книги,
    /// аудио или весь текст. Для них карточку до скачивания не показываем — иначе книга качалась бы дважды.
    /// Имена сверяет с загрузчиками ядра android/CoreSmoke: если в ядре класс переименуют, проверка упадёт.
    /// </summary>
    public static readonly IReadOnlySet<string> NoPreviewGetters = new HashSet<string>(StringComparer.Ordinal)
    {
        // NoChapters не проверяет вовсе — качает все посты
        "Elib2Ebook.ExternalServices.Boosty.BoostyGetter",
        // Сначала качают файл книги (epub, fb3), аудио или все эпизоды сериала и только потом смотрят на NoChapters
        "Elib2Ebook.ExternalServices.BooksYandex.Getters.BooksYandexBooksGetter",
        "Elib2Ebook.ExternalServices.Litres.LitresGetter",
        "Elib2Ebook.ExternalServices.MyBook.MyBookGetter",
        // Весь текст книги приходит одним запросом (/reader/blocks) ещё до проверки NoChapters
        "Elib2Ebook.ExternalServices.Litmarket.LitmarketGetter"
    };

    /// <summary>Показывать ли карточку книги до скачивания для этого загрузчика (см. <see cref="NoPreviewGetters"/>).</summary>
    public static bool SupportsPreview(Type getterType) => !NoPreviewGetters.Contains(getterType.FullName ?? string.Empty);

    /// <summary>Скачивает одну книгу (одну ссылку из options.Url) во все запрошенные форматы.</summary>
    /// <param name="options">Опции ядра; SavePath и TempPath должны указывать на рабочие папки задания.</param>
    /// <param name="logger">Куда писать журнал (строки ядра на русском: «Загружаю главу …»).</param>
    /// <param name="onPreview">
    /// Вызывается, как только известны название/автор/обложка; null — без превью. Может и не вызваться:
    /// превью необязательное (см. <see cref="TryPreviewAsync"/>).
    /// </param>
    /// <param name="ct">Отмена. Ядро токен не принимает, поэтому отмена реализована через закрытие HttpClient.</param>
    /// <exception cref="OperationCanceledException">Если загрузку отменили.</exception>
    public static async Task<BookJobResult> RunAsync(Options options, ILogger logger, Action<BookPreview> onPreview, CancellationToken ct)
    {
        Directory.CreateDirectory(options.SavePath);
        Directory.CreateDirectory(options.TempPath);

        var url = options.Url.First().AsUri();

        // Стандартная конфигурация ядра: HttpClient с CookieContainer, распаковкой gzip/brotli,
        // прокси из настроек и таймаутом options.Timeout (ВАЖНО: 0 в Options = ошибка, задаём явно)
        var config = BookGetterConfig.GetDefault(options, logger);

        // Ядро не принимает CancellationToken — обрываем запросы, освобождая HttpClient.
        // Текущий запрос упадёт с исключением, а дальше мы превратим его в OperationCanceledException.
        await using var registration = ct.Register(() =>
        {
            try
            {
                config.Client.CancelPendingRequests();
                config.Client.Dispose();
            }
            catch
            {
                // уже освобождён
            }
        });

        try
        {
            // Внимание: GetterBase.Dispose() освобождает весь config (HttpClient, временную папку)
            using var getter = GetterResolver.Create(config, url);

            logger.LogInformation($"Подключаюсь к {url.Host}");
            await getter.Init();
            await getter.Authorize();
            ct.ThrowIfCancellationRequested();

            if (onPreview != null && SupportsPreview(getter.GetType()))
            {
                await TryPreviewAsync(getter, url, options, logger, onPreview, ct);
            }

            // Полная загрузка: главы, картинки (если не выбрано «без картинок»), диапазон глав из options.Start/End
            using var book = await getter.Get(url);
            ct.ThrowIfCancellationRequested();

            // Один и тот же Book собирается во все выбранные форматы; файлы пишутся в options.SavePath
            foreach (var format in options.Format)
            {
                await BuilderProvider.Get(format, options, logger).Build(book);
                ct.ThrowIfCancellationRequested();
            }

            if (options.Additional)
            {
                // Дополнительные файлы кладутся в подпапку «Автор - Название/…»
                await new AdditionaFileBuilder(options, logger).Build(book);
            }

            var files = Directory.EnumerateFiles(options.SavePath, "*", SearchOption.AllDirectories).ToList();
            // Обложку забираем до выхода: временные файлы книги удалятся вместе с book
            return new BookJobResult(BookInfo.From(book), options.SavePath, files);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // После отмены ядро падает с чем угодно (ObjectDisposedException, HttpRequestException…) —
            // приводим к одному понятному типу
            throw new OperationCanceledException("Скачивание отменено", ex, ct);
        }
        finally
        {
            config.Dispose();
        }
    }

    /// <summary>
    /// Как в веб-версии: сначала быстрый проход без глав, чтобы сразу показать карточку книги.
    /// Карточка — только украшение: любая её ошибка (сайт не отдал обложку, таймаут, сбой разбора) пишется
    /// в журнал, а книга скачивается дальше. Пробрасывается только отмена.
    /// </summary>
    private static async Task TryPreviewAsync(GetterBase getter, Uri url, Options options, ILogger logger, Action<BookPreview> onPreview, CancellationToken ct)
    {
        // Доп. файлы (иллюстрации, аудио, оригиналы) карточке не нужны, а часть загрузчиков качает их,
        // не глядя на NoChapters (например, галерею author.today) — на время превью выключаем
        var additional = options.Additional;
        options.NoChapters = true;
        options.Additional = false;
        try
        {
            using var shortBook = await getter.Get(url);
            onPreview(BookPreview.From(shortBook));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation($"Не удалось получить карточку книги, скачиваю без неё: {ex.Message}");
        }
        finally
        {
            options.NoChapters = false;
            options.Additional = additional;
        }

        ct.ThrowIfCancellationRequested();
    }
}
