using E2E.Droid.Services;

namespace ServiceTests;

public sealed class DiagnosticReservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "e2e-reservation-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DownloadRequest Request = new() { Urls = ["https://example.test/book/1"] };

    public DiagnosticReservationTests()
    {
        FileSystem.CacheDirectory = Path.Combine(_root, "cache");
        FileSystem.AppDataDirectory = Path.Combine(_root, "data");
    }

    [Fact]
    public async Task ReservationBlocksDownloadsAndLogClearingBeforeAnyPermissionRequest()
    {
        var storage = new PermissionStorage();
        var toasts = new ToastService();
        var manager = new DownloadManager(null, storage, null, null, toasts);
        Assert.True(manager.TryReserveDiagnostics(out var lease));
        using (lease)
        {
            Assert.True(manager.IsBusy);
            Assert.False(manager.TryReserveDiagnostics(out var second));
            Assert.Null(second);
            Assert.False(await manager.StartAsync(Request));
            Assert.False(manager.TryClearLogs(() => throw new InvalidOperationException("Не должна начаться очистка")));
            Assert.Equal(0, storage.Requests);
            Assert.Contains(toasts.Toasts, toast => toast.Text.Contains("диагностика"));
        }
        Assert.False(manager.IsBusy);
    }

    [Fact]
    public async Task LostFolderAccessShowsErrorWithoutStartingDownloadOrBlockingRetry()
    {
        var storage = new PermissionStorage();
        storage.Permission.SetException(new InvalidOperationException("Доступ к выбранной папке утрачен"));
        var toasts = new ToastService();
        var manager = new DownloadManager(null, storage, null, null, toasts);

        Assert.False(await manager.StartAsync(Request));
        Assert.False(await manager.StartAsync(Request));

        Assert.False(manager.IsBusy);
        Assert.False(manager.IsRunning);
        Assert.Equal(2, storage.Requests);
        Assert.All(toasts.Toasts, toast =>
        {
            Assert.Equal(ToastKind.Error, toast.Kind);
            Assert.Contains("Доступ к выбранной папке утрачен", toast.Text);
        });
    }

    [Fact]
    public async Task LostLogFolderAccessDoesNotCrashDiagnosticsOrKeepDownloadReservation()
    {
        var books = new PermissionStorage();
        var logs = new PermissionStorage();
        logs.Permission.SetException(new InvalidOperationException("Доступ к выбранной папке утрачен"));
        var toasts = new ToastService();
        var manager = new DownloadManager(null, books, null, null, toasts);
        var runner = new DiagnosticsRunner(null, books, null, manager, toasts, logs);

        await runner.StartAsync();
        await runner.StartAllSitesAsync();

        Assert.False(runner.IsRunning);
        Assert.False(manager.IsBusy);
        Assert.Equal(2, logs.Requests);
        Assert.Equal(0, books.Requests);
        Assert.All(toasts.Toasts, toast => Assert.Equal(ToastKind.Error, toast.Kind));
        Assert.True(manager.TryReserveDiagnostics(out var lease));
        lease.Dispose();
    }

    [Fact]
    public async Task DownloadAwaitingPermissionBlocksDiagnosticsUntilDenied()
    {
        var storage = new PermissionStorage();
        var manager = new DownloadManager(null, storage, null, null, new ToastService());
        var starting = manager.StartAsync(Request);
        try
        {
            Assert.False(starting.IsCompleted);
            Assert.Equal(1, storage.Requests);
            Assert.True(manager.IsBusy);
            Assert.False(manager.TryReserveDiagnostics(out var lease));
            Assert.Null(lease);
        }
        finally { storage.Permission.TrySetResult(false); }
        Assert.False(await starting);
        Assert.True(manager.TryReserveDiagnostics(out var next));
        next.Dispose();
    }

    [Fact]
    public void DisposingAnOldLeaseTwiceDoesNotReleaseTheNextReservation()
    {
        var manager = new DownloadManager(null, null, null, null, new ToastService());
        Assert.True(manager.TryReserveDiagnostics(out var first));
        first.Dispose();
        Assert.True(manager.TryReserveDiagnostics(out var second));
        first.Dispose();
        Assert.True(manager.IsBusy);
        Assert.False(manager.TryReserveDiagnostics(out _));
        second.Dispose();
        Assert.False(manager.IsBusy);
    }

    [Fact]
    public async Task CancelledWorkerTailBlocksOnlyDiagnosticsAndPreservesQuickDownloadRetry()
    {
        var storage = new PermissionStorage();
        var manager = new DownloadManager(null, storage, null, null, new ToastService());
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var workers = typeof(DownloadManager).GetField("_activeWorkers", flags)!;
        typeof(DownloadManager).GetProperty(nameof(DownloadManager.State))!.SetValue(manager, DownloadState.Cancelled);
        workers.SetValue(manager, 1);

        Assert.False(manager.IsBusy);
        Assert.False(manager.TryReserveDiagnostics(out _));
        var retry = manager.StartAsync(Request);
        Assert.Equal(1, storage.Requests);
        storage.Permission.TrySetResult(false);
        Assert.False(await retry);
        Assert.False(manager.TryReserveDiagnostics(out _));

        workers.SetValue(manager, 0);
        Assert.True(manager.TryReserveDiagnostics(out var lease));
        lease.Dispose();
    }

    [Fact]
    public async Task ConcurrentReservationAndDownloadNeverBothReachPermissionPhase()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var storage = new PermissionStorage();
            var manager = new DownloadManager(null, storage, null, null, new ToastService());
            using var ready = new ManualResetEventSlim();
            var invoked = new TaskCompletionSource<Task<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
            IDisposable lease = null;
            var download = Task.Run(() =>
            {
                ready.Wait();
                var pending = manager.StartAsync(Request);
                invoked.SetResult(pending);
                return pending;
            });
            var reserve = Task.Run(() => { ready.Wait(); return manager.TryReserveDiagnostics(out lease); });
            ready.Set();
            await invoked.Task;
            var reserved = await reserve;
            try
            {
                Assert.False(reserved && storage.Requests > 0);
                Assert.True(reserved || storage.Requests == 1);
            }
            finally
            {
                storage.Permission.TrySetResult(false);
                await download;
                lease?.Dispose();
            }
            Assert.False(manager.IsBusy);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class PermissionStorage : ILogStorage
    {
        public TaskCompletionSource<bool> Permission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests { get; private set; }
        public string DestinationLabel => "тестовая папка";
        public Task<bool> EnsureWriteAccessAsync() { Requests++; return Permission.Task; }
        public Task<bool> PickFolderAsync() => throw new NotSupportedException();
        public void ResetFolder() => throw new NotSupportedException();
        public bool Exists(SavedFile file) => throw new NotSupportedException();
        public bool Delete(SavedFile file) => throw new NotSupportedException();
        public bool Open(SavedFile file) => throw new NotSupportedException();
        public void Share(IReadOnlyCollection<SavedFile> files) => throw new NotSupportedException();
        public Task<IReadOnlyList<SavedFile>> SaveTreeAsync(string sourceDir, Func<SavedFile, SavedFile> decorate,
            CancellationToken cancellationToken = default, IProgress<StorageProgress> progress = null) => throw new NotSupportedException();
    }
}
