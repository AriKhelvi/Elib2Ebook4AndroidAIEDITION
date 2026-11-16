namespace E2E.Droid.Services;

/// <summary>
/// Рабочие файлы текущего процесса: Android не должен очищать их вместе с кэшем
/// до завершения сборки книги. Остатки предыдущих запусков удаляются отдельно.
/// </summary>
internal static class TemporaryWorkspace
{
    private static readonly string SessionId = Guid.NewGuid().ToString("N");

    public static string RootDirectory => Path.Combine(FileSystem.AppDataDirectory, "work");
    public static string SessionDirectory => Path.Combine(RootDirectory, SessionId);
    public static string JobsDirectory => Path.Combine(SessionDirectory, "jobs");
    public static string DiagnosticsDirectory => Path.Combine(SessionDirectory, "diagnostics");
    public static string PreviewsDirectory => Path.Combine(SessionDirectory, "covers-preview");

    /// <summary>
    /// Можно вызывать фоном в любой момент: текущая сессия всегда исключена,
    /// даже если первое задание успело начаться раньше стартовой очистки.
    /// </summary>
    public static void CleanupPreviousSessions()
    {
        try
        {
            if (Directory.Exists(RootDirectory))
            {
                foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
                {
                    var name = Path.GetFileName(directory);
                    // Чужие файлы/каталоги не трогаем; наши сессии имеют только GUID-имена.
                    if (name != SessionId && Guid.TryParseExact(name, "N", out _))
                        TryDeleteChild(RootDirectory, directory);
                }
            }
        }
        catch
        {
            // Недоступный остаток можно убрать при следующем запуске приложения.
        }

        // Миграция: новые задания, диагностика и превью больше не пишут в эти папки.
        foreach (var name in new[] { "jobs", "diagnostics", "covers-preview" })
            TryDeleteChild(FileSystem.CacheDirectory, Path.Combine(FileSystem.CacheDirectory, name));
    }

    private static void TryDeleteChild(string parent, string directory)
    {
        try
        {
            var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var path = Path.GetFullPath(directory);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(path), root, comparison) ||
                string.Equals(path, Path.GetFullPath(SessionDirectory), comparison))
                return;

            var info = new DirectoryInfo(path);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                info.Delete(recursive: true);
        }
        catch
        {
            // Очистка остатков не должна мешать работающему приложению.
        }
    }
}
