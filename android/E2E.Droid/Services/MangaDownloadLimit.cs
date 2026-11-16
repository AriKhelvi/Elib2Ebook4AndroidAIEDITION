using Elib2Ebook.DomainServices.Configs;

namespace E2E.Droid.Services;

/// <summary>Ограничение одного выбранного диапазона манги, независимо от формата файла.</summary>
public static class MangaDownloadLimit
{
    public const int MaxChapters = 1250;

    public static void Apply(BookGetterConfig config, Uri url)
    {
        var host = url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.Host[4..] : url.Host;
        // Acomics выбирает страницы одного выпуска, а не главы манги.
        config.ValidateSelectedChapterCount = FormatSupport.IsComic(url.AbsoluteUri) &&
            !host.Equals("acomics.ru", StringComparison.OrdinalIgnoreCase)
                ? EnsureWithinLimit : null;
    }

    public static void EnsureWithinLimit(int count)
    {
        if (count > MaxChapters) throw new MangaChapterLimitException(count);
    }
}

public sealed class MangaChapterLimitException(int selectedChapters) : InvalidOperationException(
    $"Выбрано {selectedChapters} глав манги. За одно скачивание можно загрузить не более {MangaDownloadLimit.MaxChapters}. " +
    "Выберите меньший диапазон глав (например, 1–1250), затем скачайте остальные отдельно.")
{
    public int SelectedChapters { get; } = selectedChapters;
}
