using System.Reflection;
using E2E.Droid.Services;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ServiceTests;

public class DownloadResumeSettingsTests
{
    [Fact]
    public async Task UpgradeAndNewManagerKeepCompletedTextDespiteLegacyDisabledSetting()
    {
        var previous = Preferences.Default;
        var previousCache = FileSystem.CacheDirectory;
        var previousData = FileSystem.AppDataDirectory;
        var root = Path.Combine(Path.GetTempPath(), "resume-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Preferences.Default = new MemoryPreferences();
            Preferences.Default.Set("ResumeDownloads", false);
            FileSystem.CacheDirectory = Path.Combine(root, "cache");
            FileSystem.AppDataDirectory = Path.Combine(root, "data");
            var book = new Uri("https://tl.rulate.ru/book/1");
            var legacy = Path.Combine(FileSystem.CacheDirectory, "chapter-cache");
            await new ChapterTextCache(legacy, NullLogger.Instance).WriteAsync(book, "1", null, "глава до обновления");
            var settings = new AppSettings();
            var manager = new DownloadManager(settings, null, null, null, null);
            var method = typeof(DownloadManager).GetMethod("BuildOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var request = new DownloadRequest { Urls = ["https://tl.rulate.ru/book/1"] };
            var firstWork = Path.Combine(FileSystem.CacheDirectory, "jobs", "first");
            var retryWork = Path.Combine(FileSystem.CacheDirectory, "jobs", "retry");
            var first = (Options)method.Invoke(manager, [request, request.Urls[0], firstWork])!;
            await ChapterTextCache.MigrateAsync(legacy, first.ChapterCachePath, NullLogger.Instance);
            Directory.Delete(FileSystem.CacheDirectory, true);
            var newManager = new DownloadManager(new AppSettings(), null, null, null, null);
            var retry = (Options)method.Invoke(newManager, [request, request.Urls[0], retryWork])!;

            Assert.Equal(Path.Combine(FileSystem.AppDataDirectory, "chapter-cache"), retry.ChapterCachePath);
            Assert.Equal("глава до обновления", await new ChapterTextCache(retry.ChapterCachePath, NullLogger.Instance).ReadAsync(book, "1", null));
            Assert.Equal(first.ChapterCachePath, retry.ChapterCachePath);
            Assert.False(first.ChapterCachePath.StartsWith(firstWork, StringComparison.Ordinal));
            Assert.False(retry.ChapterCachePath.StartsWith(retryWork, StringComparison.Ordinal));
            Assert.NotEqual(first.TempPath, retry.TempPath);
            Assert.False(Preferences.Default.Get("ResumeDownloads", true));
            Assert.Null(new Options().ChapterCachePath);
        }
        finally
        {
            Preferences.Default = previous;
            FileSystem.CacheDirectory = previousCache;
            FileSystem.AppDataDirectory = previousData;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeparateCoverSettingReachesCoreAndRetryKeepsPreviousJobFilesSeparate(bool cover)
    {
        var previous = Preferences.Default;
        var previousCache = FileSystem.CacheDirectory;
        var previousData = FileSystem.AppDataDirectory;
        var root = Path.Combine(Path.GetTempPath(), "cover-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Preferences.Default = new MemoryPreferences();
            FileSystem.CacheDirectory = Path.Combine(root, "cache");
            FileSystem.AppDataDirectory = Path.Combine(root, "data");
            var settings = new AppSettings { Cover = cover, Additional = true };
            var manager = new DownloadManager(settings, null, null, null, null);
            var method = typeof(DownloadManager).GetMethod("BuildOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var request = new DownloadRequest { Urls = ["https://tl.rulate.ru/book/1"], Formats = ["epub", "fb2"] };
            var old = (Options)method.Invoke(manager, [request, request.Urls[0], Path.Combine(root, "old")])!;
            Directory.CreateDirectory(old.SavePath);
            File.WriteAllText(Path.Combine(old.SavePath, "Book_cover.png"), "старая обложка");
            var retry = (Options)method.Invoke(manager, [request, request.Urls[0], Path.Combine(root, "retry")])!;

            Assert.Equal(cover, retry.Cover);
            Assert.True(retry.Additional);
            Assert.False(retry.NoImage);
            Assert.Equal(new[] { "epub", "fb2" }, retry.Format);
            Assert.NotEqual(old.SavePath, retry.SavePath);
            Assert.False(Directory.Exists(retry.SavePath));
            Assert.True(File.Exists(Path.Combine(old.SavePath, "Book_cover.png")));
        }
        finally
        {
            Preferences.Default = previous;
            FileSystem.CacheDirectory = previousCache;
            FileSystem.AppDataDirectory = previousData;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class MemoryPreferences : IPreferences
    {
        private readonly Dictionary<string, object> _values = [];
        public T Get<T>(string key, T fallback, string sharedName = null) => _values.TryGetValue(key, out var value) ? (T)value : fallback;
        public void Set<T>(string key, T value, string sharedName = null) => _values[key] = value;
    }
}
