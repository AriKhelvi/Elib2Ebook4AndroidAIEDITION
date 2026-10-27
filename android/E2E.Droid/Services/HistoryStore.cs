// ============================================================================
// E2E для Android — история загрузок для вкладки «Книги» (books.json)
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Text.Json;

namespace E2E.Droid.Services;

/// <summary>
/// Записи о скачанных и не скачавшихся книгах (books.json в личной папке приложения), новые сверху.
/// </summary>
/// <remarks>
/// До версии 0.2 история была просто списком файлов (history.json). При первом запуске он переводится
/// в записи о книгах — файлы одной загрузки (одна ссылка, сохранены подряд) собираются в одну запись,
/// а старый файл переименовывается в history.v1.json (на случай, если что-то пойдёт не так).
/// </remarks>
public sealed class HistoryStore
{
    private const int MaxRecords = 300;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "books.json");
    private readonly string _legacyPath = Path.Combine(FileSystem.AppDataDirectory, "history.json");
    private readonly object _sync = new();
    private List<BookRecord> _records;

    public event Action Changed;

    public IReadOnlyList<BookRecord> Records
    {
        get
        {
            lock (_sync)
            {
                return Load().ToList();
            }
        }
    }

    public BookRecord Get(string id)
    {
        lock (_sync)
        {
            return Load().FirstOrDefault(r => r.Id == id);
        }
    }

    public void Add(BookRecord record)
    {
        lock (_sync)
        {
            var records = Load();
            records.Insert(0, record);
            foreach (var old in records.Skip(MaxRecords).ToList())
            {
                DeleteCover(old);
                records.Remove(old);
            }

            Save(records);
        }

        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (_sync)
        {
            var records = Load();
            foreach (var record in records.Where(r => r.Id == id).ToList())
            {
                DeleteCover(record);
                records.Remove(record);
            }

            Save(records);
        }

        Changed?.Invoke();
    }

    /// <summary>Убирает файл из записи (файл удалили); запись без файлов и без ошибки исчезает целиком.</summary>
    public void RemoveFile(string recordId, string fileId)
    {
        lock (_sync)
        {
            var records = Load();
            var record = records.FirstOrDefault(r => r.Id == recordId);
            if (record == null)
            {
                return;
            }

            record.Files.RemoveAll(f => f.Id == fileId);
            if (record.Files.Count == 0 && !record.Failed)
            {
                DeleteCover(record);
                records.Remove(record);
            }

            Save(records);
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_sync)
        {
            foreach (var record in Load())
            {
                DeleteCover(record);
            }

            Save([]);
        }

        Changed?.Invoke();
    }

    private List<BookRecord> Load()
    {
        if (_records != null)
        {
            return _records;
        }

        try
        {
            if (File.Exists(_path))
            {
                _records = JsonSerializer.Deserialize<List<BookRecord>>(File.ReadAllText(_path), JsonOptions) ?? [];
            }
            else if (File.Exists(_legacyPath))
            {
                _records = MigrateLegacy();
                Save(_records);
                File.Move(_legacyPath, Path.Combine(FileSystem.AppDataDirectory, "history.v1.json"), true);
            }
            else
            {
                _records = [];
            }
        }
        catch
        {
            _records = [];
        }

        return _records;
    }

    /// <summary>Старый формат: файлы шли подряд, новые сверху — файлы одной загрузки соседствуют и делят SourceUrl.</summary>
    private List<BookRecord> MigrateLegacy()
    {
        var files = JsonSerializer.Deserialize<List<SavedFile>>(File.ReadAllText(_legacyPath), JsonOptions) ?? [];
        var records = new List<BookRecord>();
        BookRecord current = null;

        foreach (var file in files)
        {
            var sameDownload = current != null
                               && current.Url == file.SourceUrl
                               && (current.Date - file.SavedAt).Duration() < TimeSpan.FromMinutes(5);
            if (!sameDownload)
            {
                current = new BookRecord
                {
                    Url = file.SourceUrl,
                    Title = file.BookTitle,
                    Author = file.Author,
                    Date = file.SavedAt
                };
                records.Add(current);
            }

            current.Files.Add(file);
            if (file.IsBook && !current.Formats.Contains(file.Extension))
            {
                current.Formats.Add(file.Extension);
            }
        }

        return records;
    }

    private void Save(List<BookRecord> records)
    {
        _records = records;
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(records, JsonOptions));
        File.Move(tmp, _path, true);
    }

    private static void DeleteCover(BookRecord record)
    {
        try
        {
            if (!string.IsNullOrEmpty(record.CoverPath) && File.Exists(record.CoverPath))
            {
                File.Delete(record.CoverPath);
            }
        }
        catch
        {
            // миниатюра в личной папке — не страшно, если останется
        }
    }
}
