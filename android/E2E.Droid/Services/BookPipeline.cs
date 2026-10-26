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
//   getter.Get (NoChapters=true) → быстрое «превью»: название, автор, обложка — для карточки в интерфейсе
//   getter.Get                   → полная книга со всеми главами и картинками
//   BuilderProvider.Get(format)  → сборка EPUB/FB2/CBZ/TXT в рабочую папку задания
//   AdditionaFileBuilder         → доп. файлы (аудио, вложения), если включены в настройках
//
// Готовые файлы остаются в options.SavePath; копированием в «Загрузки» занимается DownloadManager.

using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Misc;
using Microsoft.Extensions.Logging;

namespace E2E.Droid.Services;

/// <summary>
/// Краткие сведения о книге для карточки на главном экране. Это отдельный «снимок», а не сам <see cref="Book"/>:
/// временные файлы книги (в том числе обложка) удаляются сразу после сборки, а карточка должна жить дольше.
/// </summary>
public sealed record BookPreview(
    string Url,
    string Title,
    string Author,
    string AuthorUrl,
    string Seria,
    string SeriaUrl,
    string Annotation,
    string CoverDataUrl)
{
    /// <summary>Снимает данные с книги; обложка превращается в data:-URL, чтобы показать её в WebView без файла.</summary>
    public static BookPreview From(Book book)
    {
        // Обложки бывают не у всех книг — тогда Cover == null и карточка покажет заглушку
        string cover = null;
        var content = book.Cover?.Content;
        if (content is { Length: > 0 })
        {
            // Тип картинки определяем по расширению временного файла; если не знаем — JPEG (браузер всё равно разберётся)
            var mime = (book.Cover.Extension ?? string.Empty).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                _ => "image/jpeg"
            };
            cover = $"data:{mime};base64,{Convert.ToBase64String(content)}";
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
            cover);
    }
}

/// <summary>Результат задания: название и автор (для истории) и список созданных файлов в рабочей папке.</summary>
public sealed record BookJobResult(string Title, string Author, string OutputDir, IReadOnlyList<string> Files);

/// <summary>
/// Тот же конвейер, что в Elib2EbookCli/Elib2EbookWeb: Init → Authorize → Get → Build по форматам.
/// Не зависит от MAUI, поэтому проверяется и на десктопе (android/CoreSmoke).
/// </summary>
public static class BookPipeline
{
    /// <summary>Скачивает одну книгу (одну ссылку из options.Url) во все запрошенные форматы.</summary>
    /// <param name="options">Опции ядра; SavePath и TempPath должны указывать на рабочие папки задания.</param>
    /// <param name="logger">Куда писать журнал (строки ядра на русском: «Загружаю главу …»).</param>
    /// <param name="onPreview">Вызывается, как только известны название/автор/обложка; null — без превью.</param>
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

            if (onPreview != null)
            {
                // Как в веб-версии: сначала быстрый проход без глав, чтобы сразу показать карточку книги
                options.NoChapters = true;
                using (var shortBook = await getter.Get(url))
                {
                    onPreview(BookPreview.From(shortBook));
                }

                options.NoChapters = false;
                ct.ThrowIfCancellationRequested();
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
            return new BookJobResult(book.Title, book.Author?.Name, options.SavePath, files);
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
}
