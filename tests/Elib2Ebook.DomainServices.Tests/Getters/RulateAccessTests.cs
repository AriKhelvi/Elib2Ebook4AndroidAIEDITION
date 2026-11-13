using System.Net;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Rulate.Getters;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class RulateAccessTests
{
    private const string Denied = "<span class='disabled' title='Вы не можете скачивать перевод этой главы.'>читать</span>";
    private const string Readable = "<a class='btn btn-small btn-info' href='/book/1/1/ready' title='Получить готовый перевод.'>читать</a>";
    private const string Price = "<input type='checkbox' name='chapter[]' data-price='6' data-cents='600'>";

    [Theory]
    [InlineData("tl.rulate.ru", false)]
    [InlineData("tl.rulate.ru", true)]
    [InlineData("bllate.org", false)]
    [InlineData("bllate.org", true)]
    public async Task SkipsExplicitlyDeniedRequestsAfterSlicingFullPagedToc(string host, bool range)
    {
        var handler = new FakeHandler(uri => uri.Query.Length == 0
            ? Page(Row(1, Readable), Row(2, Price + Denied)) + "<a href='?Chapter_page=2'>2</a>"
            : Page(Row(3, Readable), Row(4, Readable)));
        var log = new WarningLogger();
        var options = new Options { NoImage = true };
        if (range) { options.Start = 2; options.End = 3; }
        using var getter = CreateGetter(host, options, handler, log);

        using var book = await getter.Get(new Uri($"https://{host}/book/1"));

        var expected = range ? new[] { "3" } : new[] { "1", "3", "4" };
        Assert.Equal(expected, handler.RequestedChapterIds);
        Assert.Equal(expected.Select(id => $"Глава {id}"), book.Chapters.Select(chapter => chapter.Title));
        Assert.All(book.Chapters, chapter => Assert.Contains("Текст", chapter.Content));
        Assert.Equal(new[] { "", "?Chapter_page=2" }, handler.Requests.Take(2).Select(uri => uri.Query));
        var warning = Assert.Single(log.Warnings);
        Assert.Contains("Глава 2", warning);
        Assert.Contains($"https://{host}/book/1/2", warning);
        Assert.Contains("оглавление", warning);
    }

    [Fact]
    public async Task ReadableDuplicateOnLaterPagePreservesFirstTitleOrderAndSingleRequest()
    {
        var handler = new FakeHandler(uri => uri.Query.Length == 0
            ? Page(Row(1, Readable), Row(2, Denied)) + "<a href='?Chapter_page=2'>2</a>"
            : Page(Row(2, Readable).Replace("Глава 2", "Другое название"), Row(3, Readable)));
        var log = new WarningLogger();
        using var getter = CreateGetter("tl.rulate.ru", new Options(), handler, log);

        using var book = await getter.Get(new Uri("https://tl.rulate.ru/book/1"));

        Assert.Equal(new[] { "1", "2", "3" }, handler.RequestedChapterIds);
        Assert.Equal(new[] { "Глава 1", "Глава 2", "Глава 3" }, book.Chapters.Select(chapter => chapter.Title));
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task RangeContainingOnlyDeniedChapterDoesNotShiftToNextReadableChapter()
    {
        var handler = new FakeHandler(_ => Page(Row(1, Readable), Row(2, Denied), Row(3, Readable)));
        var log = new WarningLogger();
        using var getter = CreateGetter("tl.rulate.ru", new Options { Start = 2, End = 2 }, handler, log);

        using var book = await getter.Get(new Uri("https://tl.rulate.ru/book/1"));

        Assert.Empty(handler.RequestedChapterIds);
        Assert.Empty(book.Chapters);
        Assert.Single(log.Warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData(Price)]
    [InlineData(Price + Readable)]
    [InlineData("<span class='disabled' title='Перевод ещё не готов.'>читать</span>")]
    [InlineData("<span title='Вы не можете скачивать перевод этой главы.'>читать</span>")]
    public async Task PriceTitleLinkOrUnknownMarkupDoesNotSuppressApiRequest(string action)
    {
        var handler = new FakeHandler(_ => Page(Row(1, action)));
        var log = new WarningLogger();
        using var getter = CreateGetter("tl.rulate.ru", new Options(), handler, log);

        using var book = await getter.Get(new Uri("https://tl.rulate.ru/book/1"));

        Assert.Equal(new[] { "1" }, handler.RequestedChapterIds);
        Assert.True(Assert.Single(book.Chapters).IsValid);
        Assert.Empty(log.Warnings);
    }

    [Theory]
    [InlineData(Readable)]
    [InlineData("<input type='checkbox' name='download_chapter[]' value='1'>")]
    public async Task EnabledActionOverridesContradictoryDeniedMarker(string enabledAction)
    {
        var handler = new FakeHandler(_ => Page(Row(1, Price + Denied + enabledAction)));
        var log = new WarningLogger();
        using var getter = CreateGetter("tl.rulate.ru", new Options(), handler, log);

        using var book = await getter.Get(new Uri("https://tl.rulate.ru/book/1"));

        Assert.Equal(new[] { "1" }, handler.RequestedChapterIds);
        Assert.True(Assert.Single(book.Chapters).IsValid);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task DisabledControlsDoNotOverrideExplicitDeniedMarker()
    {
        const string disabledControls = "<input name='download_chapter[]' disabled>" +
            "<a class='btn disabled' title='Получить готовый перевод.'>читать</a>";
        var handler = new FakeHandler(_ => Page(Row(1, Denied + disabledControls)));
        var log = new WarningLogger();
        using var getter = CreateGetter("tl.rulate.ru", new Options(), handler, log);

        using var book = await getter.Get(new Uri("https://tl.rulate.ru/book/1"));

        Assert.Empty(handler.RequestedChapterIds);
        Assert.Empty(book.Chapters);
        Assert.Single(log.Warnings);
    }

    [Theory]
    [InlineData("tl.rulate.ru")]
    [InlineData("bllate.org")]
    public async Task Http403AfterReadableChapterStillFailsInsteadOfReturningPartialBook(string host)
    {
        var handler = new FakeHandler(_ => Page(Row(1, Readable), Row(2, Denied), Row(3, Readable), Row(4, Readable)), "3");
        var log = new WarningLogger();
        using var getter = CreateGetter(host, new Options(), handler, log);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(new Uri($"https://{host}/book/1")));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal(new[] { "1", "3" }, handler.RequestedChapterIds);
        Assert.Single(log.Warnings);
    }

    private static RulateGetterBase CreateGetter(string host, Options options, HttpMessageHandler handler, ILogger logger)
    {
        var config = new BookGetterConfig(options, new HttpClient(handler), new CookieContainer(), TempFolderFactory.Create(null), logger);
        return host == "bllate.org" ? new BllateGetter(config) : new RulateGetter(config);
    }

    private static string Page(params string[] rows)
        => "<h1>Книга</h1><div id='Chapters'><table>" + string.Concat(rows) + "</table></div>";

    private static string Row(int id, string action)
        => $"<tr data-id='{id}'><td class='t'><a href='/book/1/{id}/ready_new'>Глава {id}</a></td><td>{action}</td></tr>";

    private sealed class FakeHandler(Func<Uri, string> page, string forbiddenChapter = null) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public IEnumerable<string> RequestedChapterIds => Requests.Where(uri => uri.AbsolutePath.EndsWith("/readyajax"))
            .Select(uri => uri.Segments[^2].TrimEnd('/'));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var chapter = uri.AbsolutePath.EndsWith("/readyajax");
            var denied = chapter && uri.Segments[^2].TrimEnd('/') == forbiddenChapter;
            var body = chapter
                ? JsonSerializer.Serialize(new { can_read = true, content = "<div class='content-text'><p>Текст главы.</p></div>" })
                : page(uri);
            return Task.FromResult(TestHttpResponse.Create(denied ? HttpStatusCode.Forbidden : HttpStatusCode.OK,
                request, new StringContent(body)));
        }
    }

    private sealed class WarningLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (level >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
