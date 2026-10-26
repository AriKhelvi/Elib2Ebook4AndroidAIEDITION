// ============================================================================
// E2E для Android — список скачанных файлов для вкладки «Книги» (history.json)
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Text.Json;

namespace E2E.Droid.Services;

/// <summary>Список скачанных файлов (history.json в личной папке приложения).</summary>
public sealed class HistoryStore
{
    private const int MaxItems = 500;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "history.json");
    private readonly object _sync = new();
    private List<SavedFile> _items;

    public event Action Changed;

    public IReadOnlyList<SavedFile> Items
    {
        get
        {
            lock (_sync)
            {
                return Load().ToList();
            }
        }
    }

    public void AddRange(IEnumerable<SavedFile> files)
    {
        lock (_sync)
        {
            var items = Load();
            items.InsertRange(0, files);
            if (items.Count > MaxItems)
            {
                items.RemoveRange(MaxItems, items.Count - MaxItems);
            }

            Save(items);
        }

        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (_sync)
        {
            var items = Load();
            items.RemoveAll(i => i.Id == id);
            Save(items);
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_sync)
        {
            Save([]);
        }

        Changed?.Invoke();
    }

    private List<SavedFile> Load()
    {
        if (_items != null)
        {
            return _items;
        }

        try
        {
            _items = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<SavedFile>>(File.ReadAllText(_path), JsonOptions) ?? []
                : [];
        }
        catch
        {
            _items = [];
        }

        return _items;
    }

    private void Save(List<SavedFile> items)
    {
        _items = items;
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(items, JsonOptions));
        File.Move(tmp, _path, true);
    }
}
