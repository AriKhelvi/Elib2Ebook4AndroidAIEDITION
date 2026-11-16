using System.IO.Compression;
using System.Reflection;
using E2E.Droid.Services;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Configs;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ServiceTests;

public sealed class TemporaryWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "e2e-workspace-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousCache = FileSystem.CacheDirectory;
    private readonly string _previousData = FileSystem.AppDataDirectory;

    public TemporaryWorkspaceTests()
    {
        FileSystem.CacheDirectory = Path.Combine(_root, "cache");
        FileSystem.AppDataDirectory = Path.Combine(_root, "data");
    }

    [Fact]
    public void LateStartupCleanupDeletesPreviousSessionsAndLegacyFilesButKeepsActiveWorkAndUserData()
    {
        var oldSession = Path.Combine(TemporaryWorkspace.RootDirectory, Guid.NewGuid().ToString("N"));
        var oldFiles = new[] { "jobs/book/tmp/page.jpg", "diagnostics/probe/book.fb2", "covers-preview/old.jpg" }
            .Select(name => WriteFile(Path.Combine(oldSession, name))).ToList();
        foreach (var directory in new[] { "jobs", "diagnostics", "covers-preview" })
            oldFiles.Add(WriteFile(Path.Combine(FileSystem.CacheDirectory, directory, "legacy-file")));

        var preview = WriteFile(CoverThumbnails.NewPreviewPath());
        var active = new[]
        {
            WriteFile(Path.Combine(TemporaryWorkspace.JobsDirectory, "running/tmp/page.jpg")),
            WriteFile(Path.Combine(TemporaryWorkspace.JobsDirectory, "running/out/book.cbz")),
            WriteFile(Path.Combine(TemporaryWorkspace.DiagnosticsDirectory, "running/tmp/page.png")),
            preview,
            WriteFile(CoverThumbnails.RecordPath("saved-book")),
            WriteFile(Path.Combine(FileSystem.AppDataDirectory, "history.json")),
            WriteFile(Path.Combine(FileSystem.AppDataDirectory, "chapter-cache/book/chapter")),
            WriteFile(Path.Combine(FileSystem.AppDataDirectory, "auth-cache/session")),
            WriteFile(Path.Combine(FileSystem.CacheDirectory, "chapter-cache/legacy-chapter")),
            WriteFile(Path.Combine(TemporaryWorkspace.RootDirectory, "not-a-session/keep"))
        };
        var url = CoverThumbnails.UrlFor(preview);

        // Фоновая стартовая очистка получила процессор уже после начала всех трёх видов работы.
        DownloadManager.CleanupLeftovers();
        DownloadManager.CleanupLeftovers();

        Assert.All(oldFiles, path => Assert.False(File.Exists(path), path));
        Assert.False(Directory.Exists(oldSession));
        Assert.All(active, path => Assert.Equal("сохранить", File.ReadAllText(path)));
        Assert.StartsWith("/_covers/preview/", url);
        Assert.Equal(url, CoverThumbnails.UrlFor(preview));
        var provider = new CoverFileProvider(new NullFileProvider());
        Assert.True(provider.GetFileInfo(url).Exists);
        Assert.Equal(Path.GetFullPath(preview), provider.GetFileInfo(url).PhysicalPath);
        Assert.False(provider.GetFileInfo("/_covers/preview/../../history.json").Exists);
    }

    [Fact]
    public void LookingUpWorkspaceAndCleaningAnEmptyInstallationDoesNotCreateSessionDirectories()
    {
        var session = TemporaryWorkspace.SessionDirectory;
        _ = TemporaryWorkspace.JobsDirectory;
        _ = TemporaryWorkspace.DiagnosticsDirectory;
        _ = CoverThumbnails.NewPreviewPath();

        DownloadManager.CleanupLeftovers();

        Assert.False(Directory.Exists(session));
        Assert.False(Directory.Exists(TemporaryWorkspace.RootDirectory));
    }

    [Fact]
    public async Task CacheRemovalAndLateCleanupKeepPagesAndCoverReadableDuringRealCbzBuild()
    {
        var work = Path.Combine(TemporaryWorkspace.JobsDirectory, Guid.NewGuid().ToString("N"));
        var input = Path.Combine(work, "tmp");
        var output = Path.Combine(work, "out");
        Directory.CreateDirectory(input);
        // Содержимое намеренно различается: проверяем побайтовую упаковку без перекодирования.
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xe0, 0, 4, 0x42, 0x31, 0xff, 0xd9];
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        byte[] cover = [0xff, 0xd8, 0xff, 0xe0, 0, 4, 0x43, 0x32, 0xff, 0xd9];
        using var first = await TempFile.Create(new Uri("https://example.test/1.jpg"), input, Guid.NewGuid() + ".jpg", jpeg);
        using var second = await TempFile.Create(new Uri("https://example.test/2.png"), input, Guid.NewGuid() + ".png", png);
        using var book = new Book(new Uri("https://example.test/comic"))
        {
            Title = "Comic",
            Author = new Author("Автор"),
            Cover = await TempFile.Create(new Uri("https://example.test/cover.jpg"), input, Guid.NewGuid() + ".jpg", cover),
            Chapters = [new Chapter { Title = "Глава", Images = [first, second] }]
        };
        WriteFile(Path.Combine(FileSystem.CacheDirectory, "unrelated-cache/file"));

        DownloadManager.CleanupLeftovers();
        // Моделируем независимую очистку Android-кэша после скачивания страниц и перед сборкой.
        Directory.Delete(FileSystem.CacheDirectory, recursive: true);
        await new CbzBuilder(new Options { SavePath = output, BookNamePattern = "{Book.Title}", Cover = true }, NullLogger.Instance).Build(book);

        using var archive = ZipFile.OpenRead(Path.Combine(output, "Comic.cbz"));
        Assert.Equal(new[] { "1.jpg", "2.png" }, archive.Entries.Select(entry => entry.FullName));
        Assert.Equal(jpeg, await ReadEntryAsync(archive.Entries[0]));
        Assert.Equal(png, await ReadEntryAsync(archive.Entries[1]));
        Assert.Equal(cover, await File.ReadAllBytesAsync(Path.Combine(output, "Comic_cover.jpg")));
        Assert.True(File.Exists(first.FilePath));
        Assert.True(File.Exists(second.FilePath));
        Assert.True(File.Exists(book.Cover.FilePath));
    }

    [Fact]
    public async Task DiagnosticReportExportSurvivesLateCleanupAndRemovesItsOwnWorkspaceAfterSaving()
    {
        var storage = new ReportStorage();
        var runner = new DiagnosticsRunner(null, storage, null, null, null, storage);
        var method = typeof(DiagnosticsRunner).GetMethod("SaveTextAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var saved = await (Task<SavedFile>)method.Invoke(runner, ["report.txt", new[] { "Проверка" }])!;

        Assert.NotNull(saved);
        Assert.Equal("Проверка", storage.Text.Trim());
        Assert.False(Directory.Exists(storage.Source));
    }

    private static string WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "сохранить");
        return path;
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry)
    {
        await using var stream = entry.Open();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    public void Dispose()
    {
        FileSystem.CacheDirectory = _previousCache;
        FileSystem.AppDataDirectory = _previousData;
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class ReportStorage : ILogStorage
    {
        public string Source { get; private set; }
        public string Text { get; private set; }
        public string DestinationLabel => "Тест";

        public async Task<IReadOnlyList<SavedFile>> SaveTreeAsync(string sourceDir, Func<SavedFile, SavedFile> decorate,
            CancellationToken cancellationToken = default, IProgress<StorageProgress> progress = null)
        {
            Source = sourceDir;
            Assert.Equal(TemporaryWorkspace.DiagnosticsDirectory, Path.GetDirectoryName(sourceDir));
            WriteFile(Path.Combine(FileSystem.CacheDirectory, "unrelated-cache/file"));
            DownloadManager.CleanupLeftovers();
            Directory.Delete(FileSystem.CacheDirectory, recursive: true);
            var path = Assert.Single(Directory.EnumerateFiles(sourceDir));
            Text = await File.ReadAllTextAsync(path, cancellationToken);
            return [decorate(new SavedFile { Name = Path.GetFileName(path), Uri = "content://test/report" })];
        }

        public Task<bool> EnsureWriteAccessAsync() => Task.FromResult(true);
        public Task<bool> PickFolderAsync() => throw new NotSupportedException();
        public void ResetFolder() => throw new NotSupportedException();
        public bool Exists(SavedFile file) => throw new NotSupportedException();
        public bool Delete(SavedFile file) => throw new NotSupportedException();
        public bool Open(SavedFile file) => throw new NotSupportedException();
        public void Share(IReadOnlyCollection<SavedFile> files) => throw new NotSupportedException();
    }
}
