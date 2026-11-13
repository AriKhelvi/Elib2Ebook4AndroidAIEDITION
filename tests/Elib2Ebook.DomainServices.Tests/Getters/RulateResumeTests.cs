using System.Net;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Rulate.Getters;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class RulateResumeTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "e2e-resume-test-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri BookUri = new("https://tl.rulate.ru/book/129557");

    [Theory]
    [InlineData("tl.rulate.ru")]
    [InlineData("bllate.org")]
    public async Task RetryReusesCompletedTextButRechecksTocAndRetriesFailedChapter(string host)
    {
        var bookUri = new Uri($"https://{host}/book/129557");
        var first = new FakeHandler { ForbiddenChapter = "2" };
        using (var getter = Getter(first, host: host))
            await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(bookUri));
        Assert.Equal(new[] { "1", "2" }, first.Chapters);

        var second = new FakeHandler();
        using var retry = Getter(second, host: host);
        using var book = await retry.Get(bookUri);

        Assert.Equal(1, second.TocRequests);
        Assert.Equal(new[] { "2", "3" }, second.Chapters);
        Assert.Equal(new[] { "Глава 1", "Глава 2", "Глава 3" }, book.Chapters.Select(chapter => chapter.Title));
        Assert.All(book.Chapters, chapter => Assert.Contains("Текст", chapter.Content));
    }

    [Fact]
    public async Task CacheIsDisabledByDefault()
    {
        var first = new FakeHandler { ForbiddenChapter = "2" };
        using (var getter = Getter(first, cache: false))
            await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUri));
        var second = new FakeHandler();
        using var retry = Getter(second, cache: false);
        using var book = await retry.Get(BookUri);
        Assert.Equal(new[] { "1", "2", "3" }, second.Chapters);
        Assert.False(Directory.Exists(_cache));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CachedTextDoesNotOverrideClosedOrUnknownCurrentAccess(bool closed)
    {
        var first = new FakeHandler { ForbiddenChapter = "2" };
        using (var getter = Getter(first))
            await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUri));
        var second = new FakeHandler { FirstAccess = closed ? "closed" : "unknown" };
        using var retry = Getter(second);
        using var book = await retry.Get(BookUri);

        Assert.Equal(closed ? new[] { "2", "3" } : new[] { "1", "2", "3" }, second.Chapters);
        Assert.Equal(closed ? 2 : 3, book.Chapters.Count());
    }

    [Fact]
    public async Task ContradictoryReadActionDoesNotReuseCachedText()
    {
        var first = new FakeHandler { ForbiddenChapter = "2" };
        using (var getter = Getter(first))
            await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUri));
        var second = new FakeHandler { FirstAccess = "conflict" };
        using var retry = Getter(second);
        using var book = await retry.Get(BookUri);

        Assert.Equal(new[] { "1", "2", "3" }, second.Chapters);
        Assert.Equal(3, book.Chapters.Count());
    }

    [Fact]
    public async Task ClosedDuplicateOnAnotherTocPageDoesNotReuseCachedText()
    {
        var first = new FakeHandler { ForbiddenChapter = "2" };
        using (var getter = Getter(first))
            await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUri));
        var second = new FakeHandler { ClosedDuplicateOnSecondPage = true };
        using var retry = Getter(second);
        using var book = await retry.Get(BookUri);

        Assert.Equal(2, second.TocRequests);
        Assert.Equal(new[] { "1", "2", "3", "4" }, second.Chapters);
        Assert.Equal(new[] { "Глава 1", "Глава 2", "Глава 3", "Глава 4" }, book.Chapters.Select(chapter => chapter.Title));
    }

    [Fact]
    public async Task CorruptCachedTextIsDownloadedAgain()
    {
        var first = new FakeHandler { ForbiddenChapter = "2" };
        using (var getter = Getter(first))
            await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUri));
        await File.WriteAllTextAsync(Assert.Single(Directory.GetFiles(_cache)), "{broken");
        var second = new FakeHandler();
        using var retry = Getter(second);
        using var book = await retry.Get(BookUri);
        Assert.Equal(new[] { "1", "2", "3" }, second.Chapters);
        Assert.Equal(3, book.Chapters.Count());
    }

    [Fact]
    public async Task CancellationAfterHttpResponseDoesNotCommitUnfinishedChapter()
    {
        using var cancellation = new CancellationTokenSource();
        var first = new FakeHandler { OnFirstChapter = cancellation.Cancel };
        using (var getter = Getter(first, ct: cancellation.Token))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => getter.Get(BookUri));
        Assert.False(Directory.Exists(_cache) && Directory.EnumerateFiles(_cache).Any());

        var second = new FakeHandler();
        using var retry = Getter(second);
        using var book = await retry.Get(BookUri);
        Assert.Equal(new[] { "1", "2", "3" }, second.Chapters);
    }

    private RulateGetterBase Getter(FakeHandler handler, bool cache = true, string host = "tl.rulate.ru", CancellationToken ct = default)
    {
        var options = new Options { NoImage = true, ChapterCachePath = cache ? _cache : null };
        var config = new BookGetterConfig(options, new HttpClient(handler), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance)
        { CancellationToken = ct };
        return host == "bllate.org" ? new BllateGetter(config) : new RulateGetter(config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public string ForbiddenChapter { get; init; }
        public string FirstAccess { get; init; } = "open";
        public Action OnFirstChapter { get; init; }
        public bool ClosedDuplicateOnSecondPage { get; init; }
        public int TocRequests { get; private set; }
        public List<string> Chapters { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (!uri.AbsolutePath.EndsWith("/readyajax"))
            {
                TocRequests++;
                if (ClosedDuplicateOnSecondPage && uri.Query.Length > 0)
                {
                    const string secondPage = "<h1>Книга</h1><div id='Chapters'><table>" +
                        "<tr data-id='1'><td class='t'>Другое название</td><td><span class='disabled' title='Вы не можете скачивать перевод этой главы.'>читать</span></td></tr>" +
                        "<tr data-id='4'><td class='t'>Глава 4</td><td><a title='Получить готовый перевод.'>читать</a></td></tr></table></div>";
                    return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request, new StringContent(secondPage)));
                }
                var rows = Enumerable.Range(1, 3).Select(id =>
                {
                    var access = id == 1 ? FirstAccess : "open";
                    var action = access is "closed" or "conflict"
                        ? "<span class='disabled' title='Вы не можете скачивать перевод этой главы.'>читать</span>" : "";
                    if (access is "open" or "conflict") action += "<a title='Получить готовый перевод.'>читать</a>";
                    return $"<tr data-id='{id}'><td class='t'>Глава {id}</td><td>{action}</td></tr>";
                });
                return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request,
                    new StringContent("<h1>Книга</h1><div id='Chapters'><table>" + string.Concat(rows) + "</table></div>" +
                        (ClosedDuplicateOnSecondPage ? "<a href='?Chapter_page=2'>2</a>" : ""))));
            }
            var chapter = uri.Segments[^2].TrimEnd('/');
            Chapters.Add(chapter);
            if (chapter == "1") OnFirstChapter?.Invoke();
            return Task.FromResult(TestHttpResponse.Create(chapter == ForbiddenChapter ? HttpStatusCode.Forbidden : HttpStatusCode.OK,
                request, new StringContent(JsonSerializer.Serialize(new { can_read = true, content = $"<div class='content-text'><p>Текст {chapter}</p></div>" }))));
        }
    }
}
