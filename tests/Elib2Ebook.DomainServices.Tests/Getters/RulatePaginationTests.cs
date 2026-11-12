using System.Net;
using System.Text.Json;
using System.Web;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Rulate.Getters;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class RulatePaginationTests
{
    private static readonly Uri BookUri = new("https://bllate.org/book/23172");

    [Theory]
    [InlineData("bllate.org")]
    [InlineData("tl.rulate.ru")]
    public async Task BothReportedDomainsDownloadPastChapter100(string host)
    {
        var handler = new FakeHandler(uri => uri.Query.Length == 0
            ? Page(1, 100, "?Chapter_page=2") : Page(101, 102));
        var config = Config(new Options { NoImage = true }, handler);
        using RulateGetterBase getter = host == "bllate.org" ? new BllateGetter(config) : new RulateGetter(config);
        using var book = await getter.Get(new Uri($"https://{host}/book/23172?bt_page=home"));

        Assert.Equal(102, book.Chapters.Count());
        Assert.Equal("Глава 102", book.Chapters.Last().Title);
        Assert.All(handler.Requests, request => Assert.Equal(host, request.Host));
    }

    [Theory]
    [InlineData("all", 1, 205)]
    [InlineData("range", 99, 102)]
    [InlineData("tail", 204, 205)]
    [InlineData("names", 101, 103)]
    public async Task CollectsAllPagesBeforeSlicingAndDownloadsEachChapterOnce(string selection, int first, int last)
    {
        var handler = new FakeHandler(uri => uri.Query switch
        {
            "" => Page(1, 100,
                "?Chapter_page=3", "?Chapter_page=2", "?Chapter_page=1",
                "https://other.example/book/23172?Chapter_page=4", "/book/999?Chapter_page=4",
                "https://bllate.org.other.example/book/23172?Chapter_page=4",
                "https://bllate.org:8443/book/23172?Chapter_page=4", "http://bllate.org/book/23172?Chapter_page=4"),
            "?Chapter_page=2" => Page(100, 200, "?Chapter_page=3", "?Chapter_page=1"),
            "?Chapter_page=3" => Page(201, 205, "?Chapter_page=2"),
            _ => throw new InvalidOperationException($"Неожиданный запрос {uri}")
        });
        var options = new Options { NoImage = true };
        switch (selection)
        {
            case "range": options.Start = 99; options.End = 102; break;
            case "tail": options.Start = -2; break;
            case "names": options.StartName = "Глава 101"; options.EndName = "Глава 103"; break;
        }

        using var getter = new BllateGetter(Config(options, handler));
        using var book = await getter.Get(new Uri(BookUri + "?bt_page=home&bt_block=ad"));

        Assert.Equal(Enumerable.Range(first, last - first + 1).Select(i => $"Глава {i}"), book.Chapters.Select(c => c.Title));
        Assert.Equal(new[] { "", "?Chapter_page=2", "?Chapter_page=3" }, handler.Requests.Take(3).Select(u => u.Query));
        Assert.Equal(last - first + 1, handler.Requests.Count(u => u.AbsolutePath.EndsWith("/readyajax")));
        Assert.All(handler.Requests, uri => Assert.Equal("bllate.org", uri.Host));
        Assert.All(book.Chapters, chapter => Assert.Contains("Текст", chapter.Content));
    }

    [Fact]
    public async Task LastPageLinkAlsoSchedulesIntermediatePages()
    {
        var handler = new FakeHandler(uri => uri.Query switch
        {
            "" => Page(1, 1, "?Chapter_page=3"),
            "?Chapter_page=3" => Page(3, 3, "?Chapter_page=2"),
            "?Chapter_page=2" => Page(2, 2, "?Chapter_page=3"),
            _ => throw new InvalidOperationException()
        });
        using var getter = new BllateGetter(Config(new Options(), handler));
        using var book = await getter.Get(BookUri);

        Assert.Equal(new[] { "Глава 1", "Глава 2", "Глава 3" }, book.Chapters.Select(chapter => chapter.Title));
        Assert.Equal(new[] { "", "?Chapter_page=2", "?Chapter_page=3" }, handler.Requests.Take(3).Select(uri => uri.Query));
    }

    [Theory]
    [InlineData("first-next-last")]
    [InlineData("window")]
    [InlineData("first-last")]
    public async Task ShortenedPagerCollectsEveryPageInOrder(string pager)
    {
        const int lastPage = 23;
        var handler = new FakeHandler(uri =>
        {
            var value = HttpUtility.ParseQueryString(uri.Query)["Chapter_page"];
            var page = value == null ? 1 : int.Parse(value);
            var links = pager switch
            {
                "first-next-last" => new[] { 1, Math.Min(page + 1, lastPage), lastPage },
                "window" => Enumerable.Range(Math.Max(1, page - 4), Math.Min(10, lastPage - Math.Max(1, page - 4) + 1)).ToArray(),
                _ => new[] { 1, lastPage }
            };
            return Page(page, page, links.Reverse().Select(number => $"?Chapter_page={number}").ToArray());
        });
        using var getter = new BllateGetter(Config(new Options(), handler));
        using var book = await getter.Get(BookUri);

        Assert.Equal(Enumerable.Range(1, lastPage).Select(page => $"Глава {page}"), book.Chapters.Select(chapter => chapter.Title));
        Assert.Equal(Enumerable.Range(1, lastPage).Select(page => page == 1 ? "" : $"?Chapter_page={page}"), handler.Requests.Take(lastPage).Select(uri => uri.Query));
        Assert.Equal(lastPage * 2, handler.Requests.Count);
    }

    [Fact]
    public async Task PreviewDoesNotRequestOtherPagesOrChapters()
    {
        var handler = new FakeHandler(_ => Page(1, 100, "?Chapter_page=2"));
        using var getter = new BllateGetter(Config(new Options { NoChapters = true }, handler));
        using var book = await getter.Get(BookUri);

        Assert.Empty(book.Chapters);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("<h1>Доступ ограничен</h1>")]
    [InlineData("<div id='Chapters'></div>")]
    [InlineData("<div id='Chapters'><table><tr data-id='1'><td class='t'>Глава 1</td></tr></table></div>")]
    public async Task InvalidOrRepeatedPageStopsBeforeSavingPartialBook(string secondPage)
    {
        var handler = new FakeHandler(uri => uri.Query == "" ? Page(1, 1, "?Chapter_page=2") : secondPage);
        using var getter = new BllateGetter(Config(new Options(), handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(BookUri));
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.EndsWith("/readyajax"));
    }

    [Fact]
    public async Task FailedPageDoesNotProducePartialBook()
    {
        var handler = new FakeHandler(uri => Page(1, 1, "?Chapter_page=2"), HttpStatusCode.Forbidden);
        using var getter = new BllateGetter(Config(new Options(), handler));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUri));
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.EndsWith("/readyajax"));
    }

    private static BookGetterConfig Config(Options options, HttpMessageHandler handler)
        => new(options, new HttpClient(handler), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);

    private static string Page(int first, int last, params string[] links)
        => "<h1>Книга</h1><div id='Chapters'><table>" +
           string.Concat(Enumerable.Range(first, last - first + 1).Select(i => $"<tr data-id='{i}'><td class='t'>Глава {i}</td></tr>")) +
           "</table></div>" + string.Concat(links.Select(href => $"<a href='{href}'>Страница</a>"));

    private sealed class FakeHandler(Func<Uri, string> page, HttpStatusCode nextPageStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var chapter = uri.AbsolutePath.EndsWith("/readyajax");
            var content = chapter
                ? JsonSerializer.Serialize(new { can_read = true, content = "<div class='content-text'><p>Текст</p></div>" })
                : page(uri);
            return Task.FromResult(new HttpResponseMessage(uri.Query.Length > 0 ? nextPageStatus : HttpStatusCode.OK)
            {
                Content = new StringContent(content), RequestMessage = request
            });
        }
    }
}
