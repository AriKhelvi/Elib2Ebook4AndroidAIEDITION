using System.Reflection;
using E2E.Droid.Services;

namespace ServiceTests;

public sealed class DiagnosticsRunnerTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "e2e-diagnostics-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingStorage _storage = new();
    private readonly FakePlatform _platform = new();
    private readonly ToastService _toasts = new();

    public DiagnosticsRunnerTests()
    {
        FileSystem.CacheDirectory = Path.Combine(_root, "cache");
        FileSystem.AppDataDirectory = Path.Combine(_root, "data");
        Preferences.Default = new FakePreferences();
    }

    [Fact]
    public async Task CancellationBeforeFirstStepStillSavesAnIncompleteReportWithoutSuccessToast()
    {
        var runner = CreateRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Ни одного шага с сетью: воспроизводим отмену до запуска фонового задания.
        await InvokeAsync(runner, "RunAsync", cancellation.Token);

        Assert.NotNull(runner.Result);
        Assert.Equal("Остановлено", runner.Status);
        Assert.Contains("останов", runner.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("останов", _storage.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("# Проблем нет", _storage.Text);
        Assert.DoesNotContain("всё в порядке", _storage.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_toasts.Toasts, toast => toast.Kind == ToastKind.Success);
        Assert.NotEmpty(_toasts.Toasts);
        Assert.False(_storage.CancellationWasRequested);
        Assert.False((bool)typeof(DiagnosticsRunner).GetField("_completed", PrivateInstance)!.GetValue(runner)!);
    }

    [Fact]
    public void WarningIsIncludedInTheShortReportAndDoesNotBecomeSuccessfulCheck()
    {
        var runner = CreateRunner();

        Invoke(runner, "Warn", "Не получена часть глав: требуется доступ на сайте");

        Assert.Contains(runner.ReportLines, line => line.StartsWith("#   ") && line.Contains("[warn]") && line.Contains("Не получена часть глав"));
        Assert.DoesNotContain(runner.ReportLines, line => line == "# Проблем нет");
        Assert.Equal(1, typeof(DiagnosticsRunner).GetField("_warnings", PrivateInstance)!.GetValue(runner));
        Assert.Equal(0, typeof(DiagnosticsRunner).GetField("_ok", PrivateInstance)!.GetValue(runner));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public void AcceptedTlsWithDeniedHttpAccessIsReportedAsWarning(int status)
    {
        var runner = CreateRunner();
        var probe = new TlsProbeResult { Ok = true, HttpStatusCode = status };
        probe.Lines.Add($"[warn] TLS прошёл; HTTP {status}");
        var report = typeof(DiagnosticsRunner).GetMethod("Report", PrivateInstance, null, [typeof(TlsProbeResult)], null)!;

        report.Invoke(runner, [probe]);

        Assert.Equal(1, typeof(DiagnosticsRunner).GetField("_warnings", PrivateInstance)!.GetValue(runner));
        Assert.Equal(0, typeof(DiagnosticsRunner).GetField("_ok", PrivateInstance)!.GetValue(runner));
        Assert.Contains(runner.ReportLines, line => line.StartsWith("#   ") && line.Contains($"HTTP {status}"));
    }

    [Fact]
    public void AlternativeTlsResponseIsVisibleInShortReportWithoutHidingDefaultFailure()
    {
        var runner = CreateRunner();
        var probe = new TlsProbeResult { Tls12HttpStatusCode = 200 };
        probe.Lines.Add("[FAIL] example.test: штатное TLS-соединение оборвано");
        var report = typeof(DiagnosticsRunner).GetMethod("Report", PrivateInstance, null, [typeof(TlsProbeResult)], null)!;

        report.Invoke(runner, [probe]);

        Assert.Equal(1, typeof(DiagnosticsRunner).GetField("_failed", PrivateInstance)!.GetValue(runner));
        Assert.Equal(0, typeof(DiagnosticsRunner).GetField("_ok", PrivateInstance)!.GetValue(runner));
        Assert.Contains(runner.ReportLines, line => line.StartsWith("#   ") && line.Contains("[FAIL]") && line.Contains("TLS 1.2: HTTP 200"));
    }

    [Fact]
    public void DiagnosticLogStripsEmbeddedPayloadAndKeepsRecentLinesWithinMemoryLimit()
    {
        var runner = CreateRunner();
        Invoke(runner, "Line", "Картинка data:image/png;base64," + new string('A', 5_000_000));

        var first = Assert.Single(runner.Log);
        Assert.DoesNotContain("AAAA", first);
        Assert.True(first.Length < 2500);

        for (var index = 0; index < 1000; index++)
            Invoke(runner, "Line", $"Строка {index}: " + new string('Я', 4000));
        Invoke(runner, "Line", "Последняя диагностическая запись");

        Assert.True(runner.Log.Sum(line => line.Length) < 1_000_000);
        Assert.Contains("Последняя диагностическая запись", runner.Log.Last());
        Assert.DoesNotContain(runner.Log, line => line.Contains("AAAA"));
        Assert.Contains(runner.ReportLines, line => line.StartsWith('#') && line.Contains("огранич", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HistoryIncludesFailedStartupAndPreviousDownloadWithoutChangingTheirFiles()
    {
        var logs = Path.Combine(FileSystem.AppDataDirectory, "logs");
        Directory.CreateDirectory(logs);
        var downloadPath = Path.Combine(logs, "download.log");
        const string downloadText = "Ранее скачивание завершилось HTTP 403\n";
        await File.WriteAllTextAsync(downloadPath, downloadText);
        await File.WriteAllTextAsync(Path.Combine(logs, "startup.log"), "Прошлый запуск: WebView не открылся\n");
        StartupLog.Begin(logs);
        StartupLog.MarkReady();
        var failedPath = Path.Combine(logs, "startup-failed.log");
        var previousFailure = await File.ReadAllTextAsync(failedPath);
        var runner = CreateRunner();

        await InvokeAsync(runner, "StepHistoryAsync", CancellationToken.None);

        Assert.Contains(runner.ReportLines, line => line.Contains("WebView не открылся"));
        Assert.Contains(runner.ReportLines, line => line.Contains("Ранее скачивание завершилось HTTP 403"));
        Assert.Equal(downloadText, await File.ReadAllTextAsync(downloadPath));
        Assert.Equal(previousFailure, await File.ReadAllTextAsync(failedPath));
        Assert.Null(runner.Result);
        Assert.Null(_storage.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryRecoversOnlyAnUnfinishedDiagnostic(bool finished)
    {
        var runner = CreateRunner();
        IReadOnlyList<string> previous = finished
            ? ["# Начало", "Сетевой шаг прошлой диагностики", "# Конец: 2026-11-12"]
            : ["# Начало", "Сетевой шаг прошлой диагностики"];
        typeof(DiagnosticsRunner).GetField("_previousDiagnostic", PrivateInstance)!.SetValue(runner, previous);

        await InvokeAsync(runner, "StepHistoryAsync", CancellationToken.None);

        Assert.Equal(!finished, runner.ReportLines.Any(line => line.Contains("Сетевой шаг прошлой диагностики")));
        Assert.Equal(!finished, runner.ReportLines.Any(line => line.StartsWith("#   ") && line.Contains("прошлая диагностика прервалась")));
    }

    private DiagnosticsRunner CreateRunner()
    {
        var settings = new AppSettings();
        var downloads = new DownloadManager(settings, _storage, _platform, null, _toasts);
        return new DiagnosticsRunner(settings, _storage, _platform, downloads, _toasts);
    }

    [Fact]
    public async Task ExportOfOldDownloadLogRemovesCredentials()
    {
        var logs = Path.Combine(FileSystem.AppDataDirectory, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "download.log"),
            "HTTP 403 https://example.test/book?%73%49%64=sample-secret&chapter=101\nAuthorization: Bearer old-secret\n");

        await CreateRunner().SaveDownloadLogAsync();

        Assert.DoesNotContain("sample-secret", _storage.Text);
        Assert.DoesNotContain("old-secret", _storage.Text);
        Assert.Contains("HTTP 403", _storage.Text);
        Assert.Contains("chapter=101", _storage.Text);
    }

    [Fact]
    public void ClearRemovesOnlyInternalLogsAndRejectsLateCancelledJobMessages()
    {
        var logs = Path.Combine(FileSystem.AppDataDirectory, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "startup.log"), "Старый сбой запуска");
        StartupLog.Begin(logs);
        StartupLog.MarkReady();
        File.WriteAllText(Path.Combine(logs, "diagnostics-last.log"), "Старая диагностика");
        var book = Path.Combine(FileSystem.AppDataDirectory, "book.fb2");
        var history = Path.Combine(FileSystem.AppDataDirectory, "history.json");
        var exported = Path.Combine(logs, "E2E-diagnostics-exported.txt");
        foreach (var path in new[] { book, history, exported }) File.WriteAllText(path, "Сохранённые данные");
        var settings = new AppSettings();
        var downloads = new DownloadManager(settings, _storage, _platform, null, _toasts);
        var runner = new DiagnosticsRunner(settings, _storage, _platform, downloads, _toasts);
        var job = typeof(DownloadManager).GetField("_job", PrivateInstance)!.GetValue(downloads);
        var append = typeof(DownloadManager).GetMethod("AppendLog", PrivateInstance)!;
        append.Invoke(downloads, [job, "Старый журнал скачивания"]);
        Invoke(runner, "Warn", "Старая диагностика в памяти");

        Assert.True(runner.TryClearLogs(out var error), error);
        append.Invoke(downloads, [job, "Запоздалая строка отменённого задания"]);

        Assert.Empty(downloads.Log);
        Assert.Empty(runner.Log);
        Assert.Empty(StartupLog.FailedLines());
        Assert.Null(runner.Summary);
        Assert.Null(runner.Result);
        foreach (var name in new[] { "download.log", "diagnostics-last.log", "startup.log", "startup-failed.log" })
            Assert.False(File.Exists(Path.Combine(logs, name)));
        foreach (var path in new[] { book, history, exported }) Assert.Equal("Сохранённые данные", File.ReadAllText(path));
        Assert.True(runner.TryClearLogs(out error), error);
    }

    [Fact]
    public async Task ClearIsBlockedWhileDownloadWaitsForPermission()
    {
        var permission = new TaskCompletionSource<bool>();
        _storage.WriteAccess = permission.Task;
        var settings = new AppSettings();
        var downloads = new DownloadManager(settings, _storage, _platform, null, _toasts);
        var runner = new DiagnosticsRunner(settings, _storage, _platform, downloads, _toasts);
        var starting = downloads.StartAsync(new DownloadRequest { Urls = ["https://example.test/book/1"] });
        try
        {
            Assert.True(downloads.IsBusy);
            Assert.False(downloads.IsRunning);
            Assert.False(runner.CanClearLogs);
            Assert.False(runner.TryClearLogs(out var error));
            Assert.NotNull(error);
        }
        finally { permission.SetResult(false); }
        Assert.False(await starting);
        Assert.True(runner.CanClearLogs);
    }

    [Fact]
    public void ClearRechecksDiagnosticActivityWithoutDiscardingItsLog()
    {
        var runner = CreateRunner();
        Invoke(runner, "Line", "Текущий сценарий");
        Assert.True(runner.CanClearLogs);
        // Сценарий успел стартовать после проверки состояния кнопкой.
        typeof(DiagnosticsRunner).GetField("_busy", PrivateInstance)!.SetValue(runner, 1);
        Assert.False(runner.TryClearLogs(out _));
        Assert.False(runner.CanClearLogs);
        Assert.NotEmpty(runner.Log);
    }

    // Проверяем настоящий сценарий без добавления тестовых переключателей в приложение.
    private static object Invoke(DiagnosticsRunner runner, string method, params object[] arguments)
        => typeof(DiagnosticsRunner).GetMethod(method, PrivateInstance)!.Invoke(runner, arguments)!;

    private static Task InvokeAsync(DiagnosticsRunner runner, string method, params object[] arguments)
        => (Task)Invoke(runner, method, arguments);

    public void Dispose()
    {
        StartupLog.MarkReady();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakePreferences : IPreferences
    {
        private readonly Dictionary<string, object> _values = [];
        public T Get<T>(string key, T defaultValue, string sharedName = null)
            => _values.TryGetValue(key, out var value) ? (T)value : defaultValue;
        public void Set<T>(string key, T value, string sharedName = null) => _values[key] = value;
    }

    private sealed class CapturingStorage : IBookStorage
    {
        public string DestinationLabel => "Тестовая папка";
        public string Text { get; private set; }
        public bool CancellationWasRequested { get; private set; }
        public Task<bool> WriteAccess { get; set; } = Task.FromResult(true);

        public async Task<IReadOnlyList<SavedFile>> SaveTreeAsync(string sourceDir, Func<SavedFile, SavedFile> decorate,
            CancellationToken cancellationToken = default, IProgress<StorageProgress> progress = null)
        {
            CancellationWasRequested = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            var path = Assert.Single(Directory.EnumerateFiles(sourceDir, "*.txt"));
            Text = await File.ReadAllTextAsync(path, cancellationToken);
            return [decorate(new SavedFile { Name = Path.GetFileName(path), Uri = "content://test/report", Size = new FileInfo(path).Length })];
        }

        public Task<bool> EnsureWriteAccessAsync() => WriteAccess;
        public Task<bool> PickFolderAsync() => Task.FromResult(false);
        public void ResetFolder() { }
        public bool Exists(SavedFile file) => true;
        public bool Delete(SavedFile file) => true;
        public bool Open(SavedFile file) => true;
        public void Share(IReadOnlyCollection<SavedFile> files) { }
    }

    private sealed class FakePlatform : IPlatformServices
    {
        public string AppVersion => "0.2.1-dev.test";
        public string BuildNumber => "123";
        public bool IsInForeground => true;
        public bool SupportsDynamicColor => false;
        public bool IsBackgroundWorkRunning => true;
        public bool IsIgnoringBatteryOptimizations => true;
        public Task RequestNotificationPermissionAsync() => Task.CompletedTask;
        public void StartBackgroundWork(string text) { }
        public void UpdateBackgroundWork(string text) { }
        public void StopBackgroundWork() { }
        public void NotifyFinished(string title, string text) { }
        public void ApplyTheme(bool dark, string background) { }
        public IReadOnlyDictionary<string, string> GetDynamicColorScheme(bool dark) => null;
        public string SaveThumbnail(byte[] image, string path) => null;
        public IReadOnlyList<KeyValuePair<string, string>> DescribeDevice() => [];
        public Task<bool> RequestIgnoreBatteryOptimizationsAsync() => Task.FromResult(true);
    }
}
