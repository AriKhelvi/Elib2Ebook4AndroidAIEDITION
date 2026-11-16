using System.Net;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Litnet.Getters;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitnetSeriesTests
{
    private static readonly Uri BookUrl = new("https://litnet.com/ru/book/test-b123");

    [Theory]
    [InlineData(1, "1")]
    [InlineData(0, "")]
    public async Task PublicCycleMarkupSuppliesNameAndApiSuppliesNumber(int priority, string number)
    {
        const string html = "<div class='book-view-info-coll'><p><span class='meta-name'>Цикл: </span>" +
            "<a href='/ru/author/novels?sort=cycles&amp;scroll-to=test'>Миргородские былины</a></p></div>";
        var log = new WarningLogger();
        using var getter = Create(html, priority, log);

        using var book = await getter.Get(BookUrl);

        Assert.Equal("Миргородские былины", book.Seria.Name);
        Assert.Equal(number, book.Seria.Number);
        Assert.Equal("https://litnet.com/ru/author/novels?sort=cycles&scroll-to=test", book.Seria.Url.AbsoluteUri);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task NewBookCardFindsCycleByItsLabelWithoutLegacyContainer()
    {
        const string html = "<section class='new-book-card'><h2>Майя Марук</h2><p><span class='meta-name'>Цикл: </span>" +
            "<a href='/ru/maiya-maruk-u2799328/novels?sort=cycles&amp;scroll-to=Марсианские+Хроники'>Марсианские Хроники</a></p></section>";
        var log = new WarningLogger();
        using var getter = Create(html, 2, log);

        using var book = await getter.Get(BookUrl);

        Assert.Equal("Марсианские Хроники", book.Seria.Name);
        Assert.Equal("2", book.Seria.Number);
        Assert.Equal("litnet.com", book.Seria.Url.Host);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task UnrelatedCycleLinkIsNotUsedAsBookSeries()
    {
        const string html = "<aside><a href='/ru/another-author/novels?sort=cycles'>Другой цикл</a></aside>";
        var log = new WarningLogger();
        using var getter = Create(html, 2, log);

        using var book = await getter.Get(BookUrl);

        Assert.Null(book.Seria);
        Assert.Single(log.Warnings);
    }

    [Fact]
    public async Task BlockedOptionalSeriesPageWarnsWithoutDiscardingBook()
    {
        var log = new WarningLogger();
        using var getter = Create("<html>blocked</html>", 1, log, HttpStatusCode.Forbidden);

        using var book = await getter.Get(BookUrl);

        Assert.Equal("Книга", book.Title);
        Assert.Null(book.Seria);
        Assert.Contains("403", Assert.Single(log.Warnings));
    }

    [Fact]
    public async Task RateLimitedOptionalSeriesPropagatesToStopPreviewFallback()
    {
        using var getter = Create("", 1, new WarningLogger(), HttpStatusCode.TooManyRequests);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUrl));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public async Task MissingCycleIsDistinguishedFromExpectedButUnavailableSeries(int priority, bool warn)
    {
        var log = new WarningLogger();
        using var getter = Create("<h1>Книга</h1>", priority, log);

        using var book = await getter.Get(BookUrl);

        Assert.Null(book.Seria);
        Assert.Equal(warn ? 1 : 0, log.Warnings.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ApiFailureRemainsHttpErrorInsteadOfJsonOrNullReferenceFailure(HttpStatusCode status)
    {
        using var getter = Create("", 0, new WarningLogger(), apiStatus: status);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUrl));

        Assert.Equal(status, error.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RejectedLoginStopsOnceAndPreservesHttpStatus(HttpStatusCode status)
    {
        var requests = new List<Uri>();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request =>
        {
            requests.Add(request.RequestUri);
            Assert.Equal("api.litnet.com", request.Headers.Host);
            return new HttpResponseMessage(status) { Content = new StringContent("<html>Вход отклонён</html>") };
        }), new Options { Login = "reader@example.test", Password = "password" });
        using var getter = new LitnetGetter(config);

        var error = await Assert.ThrowsAsync<HttpRequestException>(getter.Authorize);

        Assert.Equal(status, error.StatusCode);
        var login = Assert.Single(requests);
        Assert.Equal("https", login.Scheme);
        Assert.Equal("/v1/user/find-by-login", login.AbsolutePath);
    }

    private static LitnetGetter Create(string html, int priority, ILogger log,
        HttpStatusCode pageStatus = HttpStatusCode.OK, HttpStatusCode apiStatus = HttpStatusCode.OK)
    {
        var options = new Options { NoChapters = true, NoImage = true };
        var handler = new Handler(html, priority, pageStatus, apiStatus);
        return new LitnetGetter(new BookGetterConfig(options, new HttpClient(handler), new CookieContainer(),
            TempFolderFactory.Create(null), log));
    }

    private sealed class Handler(string html, int priority, HttpStatusCode pageStatus, HttpStatusCode apiStatus) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var api = request.RequestUri!.AbsolutePath.StartsWith("/v1/");
            var content = api ? JsonSerializer.Serialize(new
            {
                title = "Книга", url = BookUrl.AbsoluteUri, author_name = "Автор", cycle_priority = priority,
            }) : html;
            return Task.FromResult(TestHttpResponse.Create(api ? apiStatus : pageStatus, request, new StringContent(content)));
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
