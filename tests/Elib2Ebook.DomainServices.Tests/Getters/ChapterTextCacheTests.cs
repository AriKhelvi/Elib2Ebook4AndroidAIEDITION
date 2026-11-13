using System.Text.Json.Nodes;
using Elib2Ebook.DomainServices.Misc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class ChapterTextCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "e2e-text-cache-test-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri Book = new("https://tl.rulate.ru/book/1");
    private ChapterTextCache Cache => new(_directory, NullLogger.Instance);

    [Fact]
    public async Task HostBookChapterAndAccountHaveSeparateEntries()
    {
        var mirror = new Uri("https://bllate.org/book/1");
        var other = new Uri("https://tl.rulate.ru/book/2");
        await Cache.WriteAsync(Book, "1", null, "основной");
        await Cache.WriteAsync(mirror, "1", null, "зеркало");
        await Cache.WriteAsync(other, "1", null, "другая книга");
        await Cache.WriteAsync(Book, "2", null, "другая глава");
        await Cache.WriteAsync(Book, "1", "private-login", "другой аккаунт");

        Assert.Equal("основной", await Cache.ReadAsync(Book, "1", null));
        Assert.Equal("зеркало", await Cache.ReadAsync(mirror, "1", null));
        Assert.Equal("другая книга", await Cache.ReadAsync(other, "1", null));
        Assert.Equal("другая глава", await Cache.ReadAsync(Book, "2", null));
        Assert.Equal("другой аккаунт", await Cache.ReadAsync(Book, "1", "private-login"));
        foreach (var file in Directory.GetFiles(_directory))
            Assert.DoesNotContain("private-login", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task ClearBookIgnoresQueryAndChapterSuffixButPreservesOtherBookAndAccount()
    {
        await Cache.WriteAsync(Book, "1", null, "первая");
        await Cache.WriteAsync(Book, "2", null, "вторая");
        var other = new Uri("https://tl.rulate.ru/book/2");
        await Cache.WriteAsync(other, "1", null, "другая книга");
        await Cache.WriteAsync(Book, "1", "reader", "другой аккаунт");

        await ChapterTextCache.ClearBookAsync(_directory, new Uri("https://tl.rulate.ru/book/1/123?Chapter_page=7"), null, NullLogger.Instance);

        Assert.Null(await Cache.ReadAsync(Book, "1", null));
        Assert.Null(await Cache.ReadAsync(Book, "2", null));
        Assert.Equal("другая книга", await Cache.ReadAsync(other, "1", null));
        Assert.Equal("другой аккаунт", await Cache.ReadAsync(Book, "1", "reader"));
    }

    [Fact]
    public async Task AlteredContentAndExpiredEntryAreNotReturned()
    {
        await Cache.WriteAsync(Book, "1", null, "исходный текст");
        var path = Assert.Single(Directory.GetFiles(_directory));
        var entry = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        entry["Html"] = "подменённый текст";
        await File.WriteAllTextAsync(path, entry.ToJsonString());
        Assert.Null(await Cache.ReadAsync(Book, "1", null));

        await Cache.WriteAsync(Book, "1", null, "новый текст");
        entry = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        entry["Created"] = DateTimeOffset.UtcNow - ChapterTextCache.Lifetime - TimeSpan.FromMinutes(1);
        await File.WriteAllTextAsync(path, entry.ToJsonString());
        Assert.Null(await Cache.ReadAsync(Book, "1", null));
    }

    [Fact]
    public async Task CancelledWriteLeavesNoEntryOrTemporaryFile()
    {
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cache.WriteAsync(Book, "1", null, "текст", ct.Token));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task OversizedTextIsNotStoredAndExpiredFilesArePruned()
    {
        await Cache.WriteAsync(Book, "1", null, new string('x', ChapterTextCache.MaxEntryBytes + 1));
        Assert.False(Directory.Exists(_directory));
        await Cache.WriteAsync(Book, "1", null, "старый текст");
        var old = Assert.Single(Directory.GetFiles(_directory));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - ChapterTextCache.Lifetime - TimeSpan.FromDays(1));
        await Cache.WriteAsync(Book, "2", null, "новый текст");
        Assert.False(File.Exists(old));
        Assert.Equal("новый текст", await Cache.ReadAsync(Book, "2", null));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task CapacityIsReservedBeforeAddingAnotherEntry()
    {
        Directory.CreateDirectory(_directory);
        for (var index = 0; index < ChapterTextCache.MaxBytes / ChapterTextCache.MaxEntryBytes; index++)
        {
            var path = Path.Combine(_directory, $"old-{index:D3}.chapter.json");
            using (var stream = File.Create(path)) stream.SetLength(ChapterTextCache.MaxEntryBytes);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        }

        await Cache.WriteAsync(Book, "1", null, "новая глава");

        Assert.Equal("новая глава", await Cache.ReadAsync(Book, "1", null));
        var files = new DirectoryInfo(_directory).GetFiles();
        Assert.True(files.Sum(file => file.Length) <= ChapterTextCache.MaxBytes);
        Assert.True(files.Length <= ChapterTextCache.MaxEntries);
    }

    [Fact]
    public async Task UnwritableCachePathDoesNotInterruptDownload()
    {
        Directory.CreateDirectory(_directory);
        var notDirectory = Path.Combine(_directory, "file");
        await File.WriteAllTextAsync(notDirectory, "занято");
        var cache = new ChapterTextCache(notDirectory, NullLogger.Instance);
        await cache.WriteAsync(Book, "1", null, "текст");
        Assert.Null(await cache.ReadAsync(Book, "1", null));
        Assert.Equal("занято", await File.ReadAllTextAsync(notDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
