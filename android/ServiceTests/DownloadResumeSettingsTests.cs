using System.Reflection;
using E2E.Droid.Services;
using Elib2Ebook.DomainServices.Configs;

namespace ServiceTests;

public class DownloadResumeSettingsTests
{
    [Fact]
    public void RetryUsesSharedCacheOutsideDisposableJobFolderAndCanBeDisabled()
    {
        var previous = Preferences.Default;
        var previousCache = FileSystem.CacheDirectory;
        var previousData = FileSystem.AppDataDirectory;
        try
        {
            Preferences.Default = new MemoryPreferences();
            FileSystem.CacheDirectory = Path.Combine(Path.GetTempPath(), "resume-settings-" + Guid.NewGuid().ToString("N"));
            FileSystem.AppDataDirectory = FileSystem.CacheDirectory;
            var settings = new AppSettings();
            var manager = new DownloadManager(settings, null, null, null, null);
            var method = typeof(DownloadManager).GetMethod("BuildOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var request = new DownloadRequest { Urls = ["https://tl.rulate.ru/book/1"] };
            var firstWork = Path.Combine(FileSystem.CacheDirectory, "jobs", "first");
            var retryWork = Path.Combine(FileSystem.CacheDirectory, "jobs", "retry");
            var first = (Options)method.Invoke(manager, [request, request.Urls[0], firstWork])!;
            var retry = (Options)method.Invoke(manager, [request, request.Urls[0], retryWork])!;

            Assert.True(settings.ResumeDownloads);
            Assert.Equal(first.ChapterCachePath, retry.ChapterCachePath);
            Assert.False(first.ChapterCachePath.StartsWith(firstWork, StringComparison.Ordinal));
            Assert.False(retry.ChapterCachePath.StartsWith(retryWork, StringComparison.Ordinal));
            Assert.NotEqual(first.TempPath, retry.TempPath);
            settings.ResumeDownloads = false;
            Assert.False(new AppSettings().ResumeDownloads);
            var disabled = (Options)method.Invoke(manager, [request, request.Urls[0], retryWork])!;
            Assert.Null(disabled.ChapterCachePath);
            Assert.Null(new Options().ChapterCachePath);
        }
        finally
        {
            Preferences.Default = previous;
            FileSystem.CacheDirectory = previousCache;
            FileSystem.AppDataDirectory = previousData;
        }
    }

    private sealed class MemoryPreferences : IPreferences
    {
        private readonly Dictionary<string, object> _values = [];
        public T Get<T>(string key, T fallback, string sharedName = null) => _values.TryGetValue(key, out var value) ? (T)value : fallback;
        public void Set<T>(string key, T value, string sharedName = null) => _values[key] = value;
    }
}
