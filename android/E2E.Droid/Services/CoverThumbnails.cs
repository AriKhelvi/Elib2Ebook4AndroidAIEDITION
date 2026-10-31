// ============================================================================
// E2E для Android — миниатюры обложек: где лежат файлы и по какому адресу их видит интерфейс
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

namespace E2E.Droid.Services;

/// <summary>
/// Миниатюры обложек — маленькие JPEG (~240 px по высоте, см. <see cref="IPlatformServices.SaveThumbnail"/>),
/// которые интерфейс показывает файлом по адресу <c>/_covers/…</c>, а не data:-URL. Base64 обложки весит
/// сотни килобайт, держится в памяти строкой и при каждой отрисовке уходит через мост Blazor во встроенный браузер.
/// </summary>
/// <remarks>
/// Встроенный браузер сам не читает личную папку приложения — файлы ему отдаёт CoverFileProvider
/// (см. AppBlazorWebView.cs), и только из двух папок ниже, только с именами, которые выдаёт этот класс.
/// </remarks>
public static class CoverThumbnails
{
    /// <summary>Начало адреса миниатюр (относительно корня встроенного сайта).</summary>
    private const string UrlPrefix = "_covers/";

    /// <summary>Подпапка адреса для миниатюр карточки идущего скачивания.</summary>
    private const string PreviewSegment = "preview/";

    private const string Extension = ".jpg";

    /// <summary>Миниатюры записей «Книг»: covers/&lt;Id&gt;.jpg в личной папке; удаляются вместе с записью (HistoryStore).</summary>
    public static string RecordsDir => Path.Combine(FileSystem.AppDataDirectory, "covers");

    /// <summary>Миниатюры карточки во время скачивания: в кэше, чистятся при новом задании и при запуске приложения.</summary>
    public static string PreviewsDir => Path.Combine(FileSystem.CacheDirectory, "covers-preview");

    /// <summary>Куда сохранить миниатюру записи «Книг».</summary>
    public static string RecordPath(string recordId) => Path.Combine(RecordsDir, recordId + Extension);

    /// <summary>Новый файл для миниатюры карточки (имя каждый раз новое — браузер не покажет старую из кэша).</summary>
    public static string NewPreviewPath() => Path.Combine(PreviewsDir, Guid.NewGuid().ToString("N") + Extension);

    /// <summary>Адрес для &lt;img src&gt;; null — миниатюры нет (или файл лежит не в папке миниатюр).</summary>
    public static string UrlFor(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var name = Path.GetFileName(path);
        var dir = Path.GetDirectoryName(path);
        var url = SameDir(dir, RecordsDir) ? "/" + UrlPrefix + name
            : SameDir(dir, PreviewsDir) ? "/" + UrlPrefix + PreviewSegment + name
            : null;

        return url != null && IsSafeName(name) && File.Exists(path) ? url : null;
    }

    /// <summary>
    /// Обратное к <see cref="UrlFor"/>: путь запроса встроенного браузера → файл миниатюры.
    /// </summary>
    /// <param name="subpath">Путь относительно корня сайта (как его передаёт BlazorWebView), с «/» в начале или без.</param>
    /// <param name="path">Файл миниатюры; null — адрес про миниатюры, но имя недопустимое.</param>
    /// <returns>false — запрос не про миниатюры (его обслуживает wwwroot).</returns>
    public static bool TryResolve(string subpath, out string path)
    {
        path = null;
        var relative = (subpath ?? string.Empty).TrimStart('/');
        if (!relative.StartsWith(UrlPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        relative = relative[UrlPrefix.Length..];
        var dir = RecordsDir;
        if (relative.StartsWith(PreviewSegment, StringComparison.Ordinal))
        {
            relative = relative[PreviewSegment.Length..];
            dir = PreviewsDir;
        }

        // Только имя файла: никаких подпапок и «..» — наружу не уходит ничего, кроме самих миниатюр
        if (IsSafeName(relative))
        {
            path = Path.Combine(dir, relative);
        }

        return true;
    }

    /// <summary>Имя, которое могли выдать мы сами: латиница, цифры, «-», «_» и .jpg (Id записей — Guid "N").</summary>
    private static bool IsSafeName(string name)
    {
        if (name is not { Length: > 4 } || !name.EndsWith(Extension, StringComparison.Ordinal))
        {
            return false;
        }

        var stem = name.AsSpan(0, name.Length - Extension.Length);
        foreach (var c in stem)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameDir(string a, string b)
        => a != null && string.Equals(Path.GetFullPath(a).TrimEnd('/'), Path.GetFullPath(b).TrimEnd('/'), StringComparison.Ordinal);
}
