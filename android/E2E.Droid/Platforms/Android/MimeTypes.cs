// ============================================================================
// E2E для Android — MIME-типы книг для Android
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using Android.Webkit;

namespace E2E.Droid;

internal static class MimeTypes
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["epub"] = "application/epub+zip",
        ["fb2"] = "application/x-fictionbook+xml",
        ["cbz"] = "application/vnd.comicbook+zip",
        ["txt"] = "text/plain",
        ["json"] = "application/json",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["png"] = "image/png",
        ["webp"] = "image/webp",
        ["gif"] = "image/gif",
        ["mp3"] = "audio/mpeg",
        ["m4a"] = "audio/mp4",
        ["pdf"] = "application/pdf",
        ["zip"] = "application/zip"
    };

    /// <summary>Тип для Intent «Открыть/Поделиться» — по нему читалки находят файл.</summary>
    public static string ForIntent(string fileName)
    {
        var ext = Extension(fileName);
        return Known.GetValueOrDefault(ext)
               ?? MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext)
               ?? "application/octet-stream";
    }

    /// <summary>
    /// Тип для записи в MediaStore/SAF. Если система не знает расширение (fb2), а мы передадим «свой» тип,
    /// MediaStore допишет к имени «правильное» расширение (book.fb2.xml) — поэтому для неизвестных octet-stream.
    /// </summary>
    public static string ForStorage(string fileName)
        => MimeTypeMap.Singleton?.GetMimeTypeFromExtension(Extension(fileName)) ?? "application/octet-stream";

    private static string Extension(string fileName)
        => Path.GetExtension(fileName)?.TrimStart('.').ToLowerInvariant() ?? string.Empty;
}
