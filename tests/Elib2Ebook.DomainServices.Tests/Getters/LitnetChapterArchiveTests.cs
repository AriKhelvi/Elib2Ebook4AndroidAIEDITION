using System.Text;
using Elib2Ebook.ExternalServices.Litnet.Getters;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitnetChapterArchiveTests
{
    private const string Chapter = "[\"<p>Текст главы</p>\"]";

    [Fact]
    public async Task ReadsBinaryAesGzipRowsFromNonSeekableZipAndPreservesRequestedOrder()
    {
        var bytes = LitnetArchiveTestSupport.Archive([
            ("22.gz", LitnetArchiveTestSupport.Encrypt("[\"<p>Вторая</p>\"]")),
            ("11.gz", LitnetArchiveTestSupport.Encrypt(Chapter))]);
        using var stream = new NonSeekableStream(bytes);

        var result = await LitnetChapterArchive.ReadAsync(stream, [11, 22], LitnetArchiveTestSupport.AppKey, default);

        Assert.Equal([11L, 22L], result.Keys);
        Assert.Equal(Chapter, result[11]);
        Assert.Equal("[\"<p>Вторая</p>\"]", result[22]);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData("../11.gz")]
    [InlineData("dir/11.gz")]
    [InlineData("11.txt")]
    [InlineData("011.gz")]
    [InlineData("-1.gz")]
    [InlineData("0.gz")]
    [InlineData("999999999999999999999.gz")]
    public async Task RejectsInvalidEntryNamesWithoutExtractingThem(string name)
    {
        using var stream = new MemoryStream(LitnetArchiveTestSupport.Archive([(name, LitnetArchiveTestSupport.Encrypt(Chapter))]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LitnetChapterArchive.ReadAsync(stream, [11], LitnetArchiveTestSupport.AppKey, default));
        Assert.Contains("Неполная книга не будет сохранена", error.Message);
        Assert.DoesNotContain(name, error.Message);
    }

    [Fact]
    public async Task DuplicateRequestedEntryFailsRatherThanSilentlyChoosingOne()
    {
        var payload = LitnetArchiveTestSupport.Encrypt(Chapter);
        using var stream = new MemoryStream(LitnetArchiveTestSupport.Archive([("11.gz", payload), ("11.gz", payload)]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LitnetChapterArchive.ReadAsync(stream, [11], LitnetArchiveTestSupport.AppKey, default));
        Assert.Contains("повторно", error.Message);
    }

    [Fact]
    public async Task UnexpectedIdFailsEvenWhenRequestedChapterIsPresent()
    {
        var payload = LitnetArchiveTestSupport.Encrypt(Chapter);
        using var stream = new MemoryStream(LitnetArchiveTestSupport.Archive([("11.gz", payload), ("22.gz", payload)]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LitnetChapterArchive.ReadAsync(stream, [11], LitnetArchiveTestSupport.AppKey, default));
        Assert.Contains("незапрошенную главу 22", error.Message);
    }

    [Fact]
    public async Task MissingEntryFailsInsteadOfReturningTheAvailableSubset()
    {
        using var stream = new MemoryStream(LitnetArchiveTestSupport.Archive([("11.gz", LitnetArchiveTestSupport.Encrypt(Chapter))]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LitnetChapterArchive.ReadAsync(stream, [11, 22], LitnetArchiveTestSupport.AppKey, default));
        Assert.Contains("не вернул 1 из 2", error.Message);
    }

    [Fact]
    public async Task RejectsInvalidZipWithoutEchoingItsContents()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("private response contents"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LitnetChapterArchive.ReadAsync(stream, [11], LitnetArchiveTestSupport.AppKey, default));
        Assert.DoesNotContain("private", error.ToString());
    }

    [Theory]
    [InlineData("cipher")]
    [InlineData("gzip")]
    [InlineData("json")]
    [InlineData("rows")]
    public async Task RejectsCorruptInnerContentWithoutReturningEarlierChapters(string kind)
    {
        var broken = kind switch
        {
            "cipher" => new byte[48],
            "gzip" => LitnetArchiveTestSupport.EncryptBytes(Encoding.UTF8.GetBytes("not-gzip")),
            "json" => LitnetArchiveTestSupport.Encrypt("private non-json response"),
            _ => LitnetArchiveTestSupport.Encrypt("[null]")
        };
        using var stream = new MemoryStream(LitnetArchiveTestSupport.Archive([
            ("11.gz", LitnetArchiveTestSupport.Encrypt(Chapter)), ("22.gz", broken)]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LitnetChapterArchive.ReadAsync(stream, [11, 22], LitnetArchiveTestSupport.AppKey, default));
        Assert.Contains("главы 22", error.Message);
        Assert.DoesNotContain("private", error.ToString());
    }

    [Fact]
    public async Task EnforcesEncryptedEntryLimitBeforeDecryptingZipBomb()
    {
        using var stream = new MemoryStream(LitnetArchiveTestSupport.Archive([
            ("11.gz", new byte[LitnetChapterArchive.MaxEncryptedEntryBytes + 1])]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LitnetChapterArchive.ReadAsync(stream, [11], LitnetArchiveTestSupport.AppKey, default));
        Assert.Contains("размер", error.Message);
    }

    [Fact]
    public async Task EnforcesDecodedEntryLimitAfterGzipExpansion()
    {
        var expanded = "[\"" + new string('x', LitnetChapterArchive.MaxDecodedEntryBytes) + "\"]";
        using var stream = new MemoryStream(LitnetArchiveTestSupport.Archive([("11.gz", LitnetArchiveTestSupport.Encrypt(expanded))]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LitnetChapterArchive.ReadAsync(stream, [11], LitnetArchiveTestSupport.AppKey, default));
        Assert.Contains("Неполная книга не будет сохранена", error.Message);
    }

    [Fact]
    public async Task CancellationDuringInputReadPropagatesAndDoesNotDisposeCallerStream()
    {
        using var cancel = new CancellationTokenSource();
        var bytes = LitnetArchiveTestSupport.Archive([("11.gz", LitnetArchiveTestSupport.Encrypt(Chapter))]);
        using var stream = new NonSeekableStream(bytes, cancel.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LitnetChapterArchive.ReadAsync(stream, [11], LitnetArchiveTestSupport.AppKey, cancel.Token));
        Assert.True(stream.CanRead);
    }

    private sealed class NonSeekableStream(byte[] bytes, Action afterRead = null) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = base.ReadAsync(buffer, cancellationToken);
            afterRead?.Invoke();
            return result;
        }
    }
}
