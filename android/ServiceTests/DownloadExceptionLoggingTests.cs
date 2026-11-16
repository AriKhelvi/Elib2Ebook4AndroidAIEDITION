using System.Reflection;
using E2E.Droid.Services;
using Elib2Ebook.DomainServices.Misc;

namespace ServiceTests;

public sealed class DownloadExceptionLoggingTests : IDisposable
{
    private static readonly Type JobType = typeof(DownloadManager).GetNestedType("Job", BindingFlags.NonPublic)!;
    private static readonly MethodInfo WriteException = typeof(DownloadManager).GetMethod("WriteException", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "e2e-exception-log-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingPlatform _platform = new();
    private readonly DownloadManager _manager;
    private readonly object _job;
    private string LogPath => Path.Combine(_root, "data", "logs", "download.log");

    public DownloadExceptionLoggingTests()
    {
        FileSystem.CacheDirectory = Path.Combine(_root, "cache");
        FileSystem.AppDataDirectory = Path.Combine(_root, "data");
        _manager = new DownloadManager(null, null, _platform, null, null);
        _job = Activator.CreateInstance(JobType, new DownloadRequest())!;
        typeof(DownloadManager).GetField("_job", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_manager, _job);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongExceptionKeepsFinalBuilderFrameOnDiskAndShortStatus(bool debug)
    {
        JobType.GetProperty("Debug")!.SetValue(_job, debug);
        const string summary = "Не удалось собрать архив книги";
        const string builderFrame = "   at Elib2Ebook.DomainServices.Builders.CbzBuilder.BuildInternal(Book book, String fileName)";
        var frames = Enumerable.Range(0, 80).Select(index => $"   at System.IO.Compression.ZipArchive.CreateEntryForFile{index:D3}(String fileName)");
        var detail = "System.IO.IOException: " + summary + "\r\n" + string.Join("\r\n", frames) + "\r\n" + builderFrame;
        Assert.True(detail.IndexOf(builderFrame, StringComparison.Ordinal) > LogText.MaxLineLength);

        Write(summary, detail);

        Assert.Contains(_manager.Log, line => line.Contains("System.IO.IOException", StringComparison.Ordinal));
        Assert.Contains(_manager.Log, line => line.EndsWith(builderFrame, StringComparison.Ordinal));
        Assert.Contains(builderFrame, File.ReadAllText(LogPath));
        Assert.All(_manager.Log, line => { Assert.DoesNotContain('\n', line); Assert.DoesNotContain('\r', line); });
        Assert.Equal(summary, _manager.Status);
        Assert.Equal(summary, Assert.Single(_platform.Notifications));
    }

    [Fact]
    public void MultilineSecretsAreRemovedBeforeMemoryAndDiskPersistence()
    {
        const string detail = """
            System.IO.IOException: Не удалось собрать архив
            Authorization: Bearer bearer-secret
             folded-header-secret
            HTTP 403
            {"password":
              "multiline-password-secret",
            "status": 403}
            -----BEGIN PRIVATE KEY-----
            private-key-secret
            -----END PRIVATE KEY-----
            https://reader:userinfo-secret@example.test/book/42?sid=query-secret&chapter=7
               at Elib2Ebook.DomainServices.Builders.CbzBuilder.BuildInternal(Book book, String fileName)
            """;

        Write("Ошибка: token=status-secret\n continued-status-secret", detail);

        var allOutput = string.Join('\n', _manager.Log) + File.ReadAllText(LogPath) + _manager.Status + string.Join('\n', _platform.Notifications);
        foreach (var secret in new[] { "bearer-secret", "folded-header-secret", "multiline-password-secret", "private-key-secret", "userinfo-secret", "query-secret", "status-secret", "continued-status-secret" })
            Assert.DoesNotContain(secret, allOutput);
        Assert.Contains(LogText.Hidden, allOutput);
        Assert.Contains("HTTP 403", allOutput);
        Assert.Contains("chapter=7", allOutput);
        Assert.Contains("CbzBuilder.BuildInternal", allOutput);
        Assert.DoesNotContain('\n', _manager.Status);
    }

    [Fact]
    public void ExceptionHasBoundedLinesWithoutDiscardingLaterFramesAfterOneLongLine()
    {
        var frames = Enumerable.Range(0, 200).Select(index => $"   at Frame{index:D3}()");
        var detail = "System.IO.IOException: " + new string('x', 5000) + "\n" + string.Join('\n', frames);

        Write("Ошибка сборки", detail);

        // Одна строка статуса и не более 128 строк самого исключения.
        Assert.Equal(129, _manager.Log.Count);
        Assert.Contains(_manager.Log, line => line.EndsWith("at Frame126()", StringComparison.Ordinal));
        Assert.DoesNotContain(_manager.Log, line => line.Contains("Frame127", StringComparison.Ordinal));
        Assert.All(_manager.Log, line => Assert.InRange(line.Length, 1, LogText.MaxLineLength + "00:00:00 ".Length));
        Assert.Equal(129, File.ReadAllLines(LogPath).Length);
        Assert.Equal("Ошибка сборки", _manager.Status);
    }

    // Воспроизводим длинный текст исключения без сети и зависимости от оптимизации реального стека.
    private void Write(string summary, string detail)
        => WriteException.Invoke(_manager, [_job, summary, new DetailedException(detail)]);

    private sealed class DetailedException(string detail) : Exception("Ошибка сборки")
    {
        public override string ToString() => detail;
    }

    public void Dispose()
    {
        ((CancellationTokenSource)JobType.GetProperty("Cts")!.GetValue(_job)!).Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class CapturingPlatform : IPlatformServices
    {
        public List<string> Notifications { get; } = [];
        public string AppVersion => "0.2.2";
        public string BuildNumber => "1";
        public bool IsInForeground => true;
        public bool SupportsDynamicColor => false;
        public bool IsBackgroundWorkRunning => true;
        public bool IsIgnoringBatteryOptimizations => true;
        public Task RequestNotificationPermissionAsync() => Task.CompletedTask;
        public void StartBackgroundWork(string text) { }
        public void UpdateBackgroundWork(string text) => Notifications.Add(text);
        public void StopBackgroundWork() { }
        public void NotifyFinished(string title, string text) { }
        public void ApplyTheme(bool dark, string background) { }
        public IReadOnlyDictionary<string, string> GetDynamicColorScheme(bool dark) => null;
        public string SaveThumbnail(byte[] image, string path) => null;
        public IReadOnlyList<KeyValuePair<string, string>> DescribeDevice() => [];
        public Task<bool> RequestIgnoreBatteryOptimizationsAsync() => Task.FromResult(true);
    }
}
