// ============================================================================
// E2E для Android — какие форматы имеют смысл для сайта и автовыбор CBZ для комиксов
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

namespace E2E.Droid.Services;

/// <summary>
/// Политика форматов приложения: манга и комиксы — CBZ, текстовые книги — EPUB/FB2/TXT.
/// </summary>
/// <remarks>
/// Технические возможности сборщиков ядра не расширяют разрешённые форматы интерфейса.
/// Учитываем домен и путь: семейства загрузчиков обслуживают и мангу, и ранобэ,
/// а Яндекс Книги размещают комиксы и текстовые книги на одном домене.
/// </remarks>
public static class FormatSupport
{
    public static readonly IReadOnlyList<string> All = ["epub", "fb2", "cbz", "txt"];

    /// <summary>Форматы для текстовых книг — из них выбирается «текстовый формат по умолчанию».</summary>
    public static readonly IReadOnlyList<string> TextFormats = ["epub", "fb2", "txt"];

    /// <summary>
    /// Домены, где книга — это картинки (манга, манхва, комиксы). Список ручной: при добавлении сайта-комикса
    /// в ядро его нужно дописать сюда. Распознавание формата не добавляет поддержку зеркала в загрузчик.
    /// </summary>
    private static readonly HashSet<string> ComicHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "acomics.ru",
        "desu.me",
        "desu.uno",
        "mangalib.me",
        "mangalib.org",
        "hentailib.me",
        "slashlib.me",
        "v2.slashlib.me",
        "mangamammy.ru",
        "manga.ovh",
        "v1.manga.ovh",
        "readmanga.live",
        "remanga.org"
    };

    /// <summary>Ссылка ведёт на сайт с мангой или комиксами.</summary>
    public static bool IsComic(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return ComicHosts.Contains(host) ||
            (host.Equals("books.yandex.ru", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                 "comicbooks", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Почему формат не подходит для такого сайта; null — подходит.</summary>
    public static string Problem(string format, bool comic) => format switch
    {
        _ when !All.Contains(format) => "Неизвестный формат книги",
        _ when comic && format != "cbz" => "Манга и комиксы сохраняются только в CBZ",
        "cbz" when !comic => "CBZ — это только картинки: текст книги в него не попадёт",
        _ => null
    };

    /// <summary>
    /// Формат для ссылки — заново для каждой ссылки, без памяти о прошлой загрузке: манга и комиксы → CBZ
    /// без возможности отключения, всё остальное → текстовый формат по умолчанию из настроек.
    /// Так после манги в CBZ следующее ранобэ сразу получает EPUB/FB2/TXT.
    /// </summary>
    public static string ForUrl(string url, string textFormat) => Fit(textFormat, IsComic(url));

    /// <summary>Применяет те же ограничения к очереди, повторам из истории и прямому запуску конвейера.</summary>
    public static IReadOnlyList<string> ForDownload(string url, IEnumerable<string> formats)
    {
        if (IsComic(url)) return ["cbz"];
        var selected = (formats ?? []).Select(format => format?.Trim().ToLowerInvariant())
            .Where(TextFormats.Contains).Distinct().ToArray();
        return selected.Length > 0 ? selected : ["epub"];
    }

    /// <summary>Подходящий формат: выбранный, а если он не годится для сайта — запасной.</summary>
    public static string Fit(string format, bool comic) => Problem(format, comic) == null ? format : comic ? "cbz" : "epub";
}
