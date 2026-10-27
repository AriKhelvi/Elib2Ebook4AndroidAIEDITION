// ============================================================================
// E2E для Android — какие форматы имеют смысл для сайта и автовыбор CBZ для комиксов
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

namespace E2E.Droid.Services;

/// <summary>
/// Форматы книги и их совместимость с сайтом.
/// </summary>
/// <remarks>
/// Как устроены сборщики ядра (src/Elib2Ebook.DomainServices/Builders):
/// <list type="bullet">
/// <item>CBZ — архив только из картинок глав. Для манги и комиксов это лучший формат, а у текстовой книги
/// в него попадут разве что иллюстрации — сам текст пропадёт.</item>
/// <item>TXT — только текст. У манги и комиксов глава — это картинки, и TXT получится пустым.</item>
/// <item>EPUB и FB2 годятся для всего (у комиксов это книга из картинок).</item>
/// </list>
/// Различать приходится по домену, а не по загрузчику: один загрузчик ядра иногда обслуживает и мангу, и ранобэ
/// (LibSocial — mangalib и ranobelib, Renovels — remanga и renovels).
/// </remarks>
public static class FormatSupport
{
    public static readonly IReadOnlyList<string> All = ["epub", "fb2", "cbz", "txt"];

    /// <summary>
    /// Домены, где книга — это картинки (манга, манхва, комиксы). Список ручной: при добавлении сайта-комикса
    /// в ядро его нужно дописать сюда (иначе просто не будет автовыбора CBZ — скачивание работает и так).
    /// </summary>
    private static readonly HashSet<string> ComicHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "acomics.ru",
        "mangalib.me",
        "mangalib.org",
        "hentailib.me",
        "slashlib.me",
        "v2.slashlib.me",
        "mangamammy.ru",
        "manga.ovh",
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
        return ComicHosts.Contains(host);
    }

    /// <summary>Почему формат не подходит для такого сайта; null — подходит.</summary>
    public static string Problem(string format, bool comic) => format switch
    {
        "txt" when comic => "В комиксах нет текста — TXT получится пустым",
        "cbz" when !comic => "CBZ — это только картинки: текст книги в него не попадёт",
        _ => null
    };

    /// <summary>Формат по ссылке для автовыбора: CBZ для комиксов, иначе null (остаётся выбранный пользователем).</summary>
    public static string Auto(string url) => IsComic(url) ? "cbz" : null;

    /// <summary>Подходящий формат: выбранный, а если он не годится для сайта — запасной.</summary>
    public static string Fit(string format, bool comic) => Problem(format, comic) == null ? format : comic ? "cbz" : "epub";
}
