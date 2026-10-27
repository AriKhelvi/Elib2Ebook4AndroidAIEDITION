// ============================================================================
// E2E для Android — запись о скачанной (или не скачавшейся) книге для вкладки «Книги»
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Text.Json.Serialization;

namespace E2E.Droid.Services;

/// <summary>
/// Одна попытка скачать книгу: что за книга (для экрана «О книге»), какие файлы получились и с какими
/// параметрами качали (для «Скачать заново»). Неудачные попытки тоже записываются — с текстом ошибки.
/// </summary>
public sealed class BookRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Ссылка, по которой качали.</summary>
    public string Url { get; set; }

    public string Title { get; set; }

    public string Author { get; set; }

    public string AuthorUrl { get; set; }

    public List<string> CoAuthors { get; set; } = [];

    public string SeriaName { get; set; }

    public string SeriaNumber { get; set; }

    public string SeriaUrl { get; set; }

    /// <summary>Аннотация как пришла с сайта (HTML), обрезанная до разумной длины.</summary>
    public string Annotation { get; set; }

    /// <summary>Миниатюра обложки в личной папке приложения (covers/&lt;Id&gt;.jpg); null — обложки нет.</summary>
    public string CoverPath { get; set; }

    /// <summary>Сколько глав с текстом попало в книгу.</summary>
    public int Chapters { get; set; }

    /// <summary>Название последней скачанной главы — видно, до какого места книга.</summary>
    public string LastChapter { get; set; }

    /// <summary>Параметры загрузки — с ними же сработает «Скачать заново».</summary>
    public List<string> Formats { get; set; } = [];

    public bool NoImage { get; set; }

    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;

    /// <summary>Текст ошибки; не пусто — книгу скачать не удалось.</summary>
    public string Error { get; set; }

    /// <summary>Файлы книги в папке сохранения (без россыпи доп. файлов).</summary>
    public List<SavedFile> Files { get; set; } = [];

    [JsonIgnore]
    public bool Failed => !string.IsNullOrEmpty(Error);

    [JsonIgnore]
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : null;

    /// <summary>Название для списка: у неудачной загрузки его может не быть — тогда сайт.</summary>
    [JsonIgnore]
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : Host ?? Url;

    /// <summary>«Серия #3».</summary>
    [JsonIgnore]
    public string SeriaDisplay => string.IsNullOrWhiteSpace(SeriaName)
        ? null
        : SeriaName + (string.IsNullOrWhiteSpace(SeriaNumber) ? string.Empty : $" #{SeriaNumber}");
}
