namespace E2E.Droid.Services;

/// <summary>Отдельный журнал от начала Android Application до первого кадра Blazor.</summary>
public static class StartupLog
{
    private static readonly object Sync = new();
    private static BoundedFileLog _current;
    private static string _failedPath;

    public static void Begin(string directory)
    {
        lock (Sync)
        {
            if (_current != null) return;
            var path = Path.Combine(directory, "startup.log");
            _failedPath = Path.Combine(directory, "startup-failed.log");
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(path)) File.Move(path, _failedPath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _current = new BoundedFileLog(path, 256 * 1024);
            Write("Начало запуска приложения");
        }
    }

    public static void Write(string text)
    {
        lock (Sync) _current?.Append($"{DateTimeOffset.Now:O} {text}");
    }

    public static IReadOnlyList<string> FailedLines()
    {
        lock (Sync) return _failedPath == null ? [] : new BoundedFileLog(_failedPath, 256 * 1024).Read();
    }

    public static void MarkReady()
    {
        lock (Sync)
        {
            if (_current == null) return;
            // Прошлый неудачный запуск остаётся доступен для отчёта. Успешный удаляется автоматически.
            try { File.Delete(_current.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _current = null;
        }
    }

    /// <summary>Удаляет текущий и предыдущий внутренние журналы, не затрагивая сохранённые отчёты.</summary>
    public static void Clear()
    {
        lock (Sync)
        {
            if (_current != null) _current.Delete();
            if (_failedPath != null)
            {
                File.Delete(_failedPath);
                File.Delete(Path.Combine(Path.GetDirectoryName(_failedPath)!, "startup.log"));
            }
        }
    }
}
