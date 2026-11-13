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
    public async Task AlteredContentAndUnsupportedFormatAreNotReturned()
    {
        await Cache.WriteAsync(Book, "1", null, "исходный текст");
        var path = Assert.Single(Directory.GetFiles(_directory));
        var entry = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        entry["Html"] = "подменённый текст";
        await File.WriteAllTextAsync(path, entry.ToJsonString());
        Assert.Null(await Cache.ReadAsync(Book, "1", null));

        await Cache.WriteAsync(Book, "1", null, "новый текст");
        entry = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        entry["Version"] = 2;
        await File.WriteAllTextAsync(path, entry.ToJsonString());
        Assert.Null(await Cache.ReadAsync(Book, "1", null));
    }

    [Fact]
    public async Task OldValidFormatSurvivesUpgradeAndReadRefreshesLru()
    {
        await Cache.WriteAsync(Book, "1", null, "готовая глава");
        var path = Assert.Single(Directory.GetFiles(_directory));
        var entry = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        entry["Created"] = DateTimeOffset.UtcNow.AddYears(-1);
        await File.WriteAllTextAsync(path, entry.ToJsonString());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddYears(-1));
        var before = DateTime.UtcNow.AddSeconds(-1);

        Assert.Equal("готовая глава", await new ChapterTextCache(_directory, NullLogger.Instance).ReadAsync(Book, "1", null));
        Assert.True(File.GetLastWriteTimeUtc(path) >= before);
        Assert.Equal(1, JsonNode.Parse(await File.ReadAllTextAsync(path))!["Version"]!.GetValue<int>());
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
    public async Task OversizedTextIsNotStoredButOldUsefulTextIsRetained()
    {
        await Cache.WriteAsync(Book, "1", null, new string('x', ChapterTextCache.MaxEntryBytes + 1));
        Assert.False(Directory.Exists(_directory));
        await Cache.WriteAsync(Book, "1", null, "старый текст");
        var old = Assert.Single(Directory.GetFiles(_directory));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddYears(-1));
        await Cache.WriteAsync(Book, "2", null, "новый текст");
        Assert.True(File.Exists(old));
        Assert.Equal("старый текст", await Cache.ReadAsync(Book, "1", null));
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

    [Fact]
    public async Task EntryLimitEvictsLeastRecentlyUsedAndKeepsJustReadText()
    {
        await Cache.WriteAsync(Book, "1", null, "прочитанная глава");
        var readPath = Assert.Single(Directory.GetFiles(_directory));
        await Cache.WriteAsync(Book, "2", null, "давно не использованная глава");
        var oldPath = Directory.GetFiles(_directory).Single(path => path != readPath);
        File.SetLastWriteTimeUtc(readPath, DateTime.UtcNow.AddYears(-2));
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddYears(-1));
        for (var i = 2; i < ChapterTextCache.MaxEntries; i++)
            await File.WriteAllTextAsync(Path.Combine(_directory, $"filler-{i}.chapter.json"), "x");
        Assert.NotNull(await Cache.ReadAsync(Book, "1", null));

        await Cache.WriteAsync(Book, "3", null, "новая глава");

        Assert.Null(await Cache.ReadAsync(Book, "2", null));
        Assert.Equal("прочитанная глава", await Cache.ReadAsync(Book, "1", null));
        Assert.Equal("новая глава", await Cache.ReadAsync(Book, "3", null));
        Assert.Equal(ChapterTextCache.MaxEntries, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public async Task MigrationKeepsNewValidEntryAndMovesOtherLegacyEntries()
    {
        var legacy = Path.Combine(_directory, "legacy");
        var oldCache = new ChapterTextCache(legacy, NullLogger.Instance);
        await oldCache.WriteAsync(Book, "1", null, "старая версия главы");
        await oldCache.WriteAsync(Book, "2", null, "завершённая глава до обновления");
        await Cache.WriteAsync(Book, "1", null, "новая версия главы");
        await File.WriteAllTextAsync(Path.Combine(legacy, "unrelated.txt"), "не трогать");
        await File.WriteAllTextAsync(Path.Combine(legacy, "unfinished.tmp"), "неполная запись");

        await ChapterTextCache.MigrateAsync(legacy, _directory, NullLogger.Instance);

        Assert.Equal("новая версия главы", await Cache.ReadAsync(Book, "1", null));
        Assert.Equal("завершённая глава до обновления", await Cache.ReadAsync(Book, "2", null));
        Assert.Empty(Directory.GetFiles(legacy, "*.chapter.json"));
        Assert.Equal("не трогать", await File.ReadAllTextAsync(Path.Combine(legacy, "unrelated.txt")));
        Assert.True(File.Exists(Path.Combine(legacy, "unfinished.tmp")));
    }

    [Fact]
    public async Task InterruptedOrUnavailableMigrationPreservesLegacyData()
    {
        var legacy = Path.Combine(_directory, "legacy");
        var oldCache = new ChapterTextCache(legacy, NullLogger.Instance);
        await oldCache.WriteAsync(Book, "1", null, "завершённая глава");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChapterTextCache.MigrateAsync(legacy, _directory, NullLogger.Instance, cancellation.Token));
        var blocked = Path.Combine(_directory, "blocked");
        await File.WriteAllTextAsync(blocked, "занято");
        await ChapterTextCache.MigrateAsync(legacy, blocked, NullLogger.Instance);
        Assert.Equal("завершённая глава", await oldCache.ReadAsync(Book, "1", null));
    }

    [Fact]
    public async Task MigrationRejectsDamagedLegacyEntryAndRepairsDamagedDestination()
    {
        var legacy = Path.Combine(_directory, "legacy");
        var oldCache = new ChapterTextCache(legacy, NullLogger.Instance);
        await oldCache.WriteAsync(Book, "1", null, "готовая глава");
        var source = Assert.Single(Directory.GetFiles(legacy));
        var target = Path.Combine(_directory, Path.GetFileName(source));
        await File.WriteAllTextAsync(target, "{broken");
        await ChapterTextCache.MigrateAsync(legacy, _directory, NullLogger.Instance);
        Assert.Equal("готовая глава", await Cache.ReadAsync(Book, "1", null));

        await oldCache.WriteAsync(Book, "2", null, "повреждённый источник");
        source = Assert.Single(Directory.GetFiles(legacy));
        await File.WriteAllTextAsync(source, "{broken");
        await ChapterTextCache.MigrateAsync(legacy, _directory, NullLogger.Instance);
        Assert.Null(await Cache.ReadAsync(Book, "2", null));
        Assert.True(File.Exists(source));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
