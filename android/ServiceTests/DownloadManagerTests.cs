using System.Reflection;
using E2E.Droid.Services;

namespace ServiceTests;

public sealed class DownloadManagerTests : IDisposable
{
    private static readonly Type JobType = typeof(DownloadManager).GetNestedType("Job", BindingFlags.NonPublic)!;
    private static readonly FieldInfo CurrentJob = typeof(DownloadManager).GetField("_job", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo DownloadAll = typeof(DownloadManager).GetMethod("DownloadAllAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "e2e-download-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<object> _jobs = [];

    public DownloadManagerTests()
    {
        FileSystem.CacheDirectory = Path.Combine(_root, "cache");
        FileSystem.AppDataDirectory = Path.Combine(_root, "data");
    }

    [Fact]
    public void SavedFilesSummaryDistinguishesBookFromItsCover()
    {
        SavedFile[] files =
        [
            new() { Name = "Книга.epub" },
            new() { Name = "Cover.jpg", Folder = "Книга/Images" },
        ];

        Assert.Equal("файлов книги: 1; дополнительных файлов: 1", DownloadManager.DescribeSavedFiles(files));
    }

    [Fact]
    public void SavedFilesSummaryCountsFormatsButNotRootCoverOrNestedText()
    {
        SavedFile[] files =
        [
            new() { Name = "Книга.epub" },
            new() { Name = "Книга.fb2" },
            new() { Name = "Книга_cover.jpg" },
            new() { Name = "Примечания.txt", Folder = "Книга/Other" },
        ];

        Assert.Equal("файлов книги: 2; дополнительных файлов: 2", DownloadManager.DescribeSavedFiles(files));
    }

    [Fact]
    public async Task CancelledWorkerStartingLateKeepsNewJobsPreview()
    {
        var manager = CreateManager();
        var oldJob = NewJob(manager);
        manager.Cancel();
        var currentJob = NewJob(manager);
        var path = CreatePreview(currentJob);
        var url = manager.PreviewCoverUrl;

        // Воспроизводим очередь Task.Run: старое задание начинает работу только после того,
        // как отмена освободила интерфейс и следующее задание уже создало свою миниатюру.
        await RunWorker(manager, oldJob);

        Assert.Equal("новая обложка", await File.ReadAllTextAsync(path));
        Assert.Equal(url, manager.PreviewCoverUrl);
        Assert.True(new CoverFileProvider(new Microsoft.Extensions.FileProviders.NullFileProvider()).GetFileInfo(url).Exists);
    }

    [Fact]
    public async Task ReplacedWorkerWithoutCancellationKeepsNewJobsPreview()
    {
        var manager = CreateManager();
        var oldJob = NewJob(manager);
        var currentJob = NewJob(manager);
        var path = CreatePreview(currentJob);

        await RunWorker(manager, oldJob);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CancelledCurrentWorkerKeepsItsPreview()
    {
        var manager = CreateManager();
        var job = NewJob(manager);
        var path = CreatePreview(job);
        manager.Cancel();

        await RunWorker(manager, job);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CurrentWorkerRemovesStalePreviewsButKeepsSavedBookCovers()
    {
        var manager = CreateManager();
        var oldJob = NewJob(manager);
        var stalePreview = CreatePreview(oldJob);
        var job = NewJob(manager);
        var savedCover = CoverThumbnails.RecordPath(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(CoverThumbnails.RecordsDir);
        await File.WriteAllTextAsync(savedCover, "обложка сохранённой книги");

        await RunWorker(manager, job);

        Assert.False(File.Exists(stalePreview));
        Assert.Equal("обложка сохранённой книги", await File.ReadAllTextAsync(savedCover));
    }

    [Fact]
    public async Task RateLimitSkipsSameSiteAndMirrorWithHistoryButOtherSiteAndNewJobStillRun()
    {
        using var fixture = RateLimitFixture.Use();
        Preferences.Default = new RateLimitFixture.Preferences();
        var history = new HistoryStore();
        var manager = new DownloadManager(new AppSettings(), new RateLimitFixture.Storage(),
            new RateLimitFixture.Platform(), history, new ToastService());
        var first = RateLimitFixture.Site + "/limited";
        var mirror = RateLimitFixture.Mirror + "/next";
        var other = RateLimitFixture.Other + "/next";
        var same = RateLimitFixture.Site + "/last";
        var job = NewJob(manager, new DownloadRequest { Urls = [first, mirror, other, same], Formats = ["fb2"] });

        await RunWorker(manager, job);

        Assert.Contains(first, RateLimitFixture.Requests);
        Assert.Contains(other, RateLimitFixture.Requests);
        Assert.DoesNotContain(mirror, RateLimitFixture.Requests);
        Assert.DoesNotContain(same, RateLimitFixture.Requests);
        Assert.Equal(4, history.Records.Count);
        Assert.All(history.Records.Where(record => record.Url == mirror || record.Url == same), record =>
        {
            Assert.Contains("Пропущено", record.Error);
            Assert.Contains("HTTP 429", record.Error);
            Assert.Empty(record.Files);
        });
        Assert.False(Assert.Single(history.Records, record => record.Url == other).Failed);
        Assert.Contains(manager.Log, line => line.Contains(mirror) && line.Contains("Пропущено"));
        Assert.Single(manager.Saved);

        var nextJob = NewJob(manager, new DownloadRequest { Urls = [mirror], Formats = ["fb2"] });
        await RunWorker(manager, nextJob);
        Assert.Contains(mirror, RateLimitFixture.Requests);
        Assert.Single(manager.Saved);
        Assert.Empty(manager.Errors);
    }

    // Пустой запрос останавливает сценарий сразу после подготовки кэша: ни сети, ни Android API.
    // Через reflection управляем порядком фоновых заданий, не добавляя тестовые переключатели в приложение.
    private static DownloadManager CreateManager() => new(null, null, null, null, null);

    private object NewJob(DownloadManager manager, DownloadRequest request = null)
    {
        var job = Activator.CreateInstance(JobType, request ?? new DownloadRequest())!;
        _jobs.Add(job);
        CurrentJob.SetValue(manager, job);
        typeof(DownloadManager).GetProperty(nameof(DownloadManager.State))!.SetValue(manager, DownloadState.Running);
        return job;
    }

    private static string CreatePreview(object job)
    {
        Directory.CreateDirectory(CoverThumbnails.PreviewsDir);
        var path = CoverThumbnails.NewPreviewPath();
        File.WriteAllText(path, "новая обложка");
        JobType.GetField("PreviewCoverPath")!.SetValue(job, path);
        JobType.GetField("PreviewCoverUrl")!.SetValue(job, CoverThumbnails.UrlFor(path));
        return path;
    }

    private static Task RunWorker(DownloadManager manager, object job)
        => (Task)DownloadAll.Invoke(manager, [job])!;

    public void Dispose()
    {
        foreach (var job in _jobs)
            ((CancellationTokenSource)JobType.GetProperty("Cts")!.GetValue(job)!).Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
