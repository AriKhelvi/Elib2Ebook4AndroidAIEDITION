// ============================================================================
// E2E для Android — модель сохранённого файла (книга, обложка, доп. файл)
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

namespace E2E.Droid.Services;

/// <summary>Файл, сохранённый в общую папку (MediaStore или выбранную через SAF).</summary>
public sealed class SavedFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>content://-URI файла.</summary>
    public string Uri { get; set; }

    public string Name { get; set; }

    /// <summary>Подпапка внутри папки сохранения («» для книг, «Автор - Книга/...» для доп. файлов).</summary>
    public string Folder { get; set; } = string.Empty;

    public string Mime { get; set; }

    public long Size { get; set; }

    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;

    public string BookTitle { get; set; }

    public string Author { get; set; }

    public string SourceUrl { get; set; }

    public string Extension => Path.GetExtension(Name)?.TrimStart('.').ToLowerInvariant() ?? string.Empty;

    /// <summary>Книга в одном из форматов, а не обложка или дополнительный файл.</summary>
    public bool IsBook => string.IsNullOrEmpty(Folder) && Extension is "epub" or "fb2" or "cbz" or "txt" or "json";
}
