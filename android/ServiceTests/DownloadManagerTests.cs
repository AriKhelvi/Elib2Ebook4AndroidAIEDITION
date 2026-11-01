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

    // Пустой запрос останавливает сценарий сразу после подготовки кэша: ни сети, ни Android API.
    // Через reflection управляем порядком фоновых заданий, не добавляя тестовые переключатели в приложение.
    private static DownloadManager CreateManager() => new(null, null, null, null, null);

    private object NewJob(DownloadManager manager)
    {
        var job = Activator.CreateInstance(JobType, new DownloadRequest())!;
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
