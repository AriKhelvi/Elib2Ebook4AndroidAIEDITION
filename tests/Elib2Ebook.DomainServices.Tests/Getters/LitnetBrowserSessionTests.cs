using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Litnet.Getters;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitnetBrowserSessionTests
{
    private static readonly Uri BookUrl = new("https://litnet.com/ru/book/browser-test-b123");
    private const string FirstToken = "first-browser-session-token";
    private const string SecondToken = "refreshed-browser-session-token";
    private const string FirstDevice = "11111111-1111-4111-8111-111111111111";
    private const string SecondDevice = "22222222-2222-4222-8222-222222222222";

    [Fact]
    public async Task ProviderSessionSkipsLegacyLoginAndSignsEveryApiWithItsOwnTokenAndDevice()
    {
        var backend = new Backend();
        var providerCalls = new List<string>();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(backend.Send), CreateOptions(providerCalls));
        using var getter = new LitnetGetter(config);
        await getter.Init();

        await getter.Authorize();
        Assert.Empty(backend.Requests);
        using var book = await getter.GetPreview(BookUrl);

        Assert.Equal([null], providerCalls);
        Assert.Equal(["metadata", "toc", "archive"], backend.Requests.Select(request => request.Route));
        Assert.All(backend.Requests, request => Assert.Equal(FirstToken, request.Token));
        Assert.Single(book.Chapters);
        Assert.Equal("<p>Текст главы</p>", book.Chapters.Single().Content);
    }

    [Theory]
    [InlineData("metadata", HttpStatusCode.Unauthorized)]
    [InlineData("toc", HttpStatusCode.Unauthorized)]
    [InlineData("archive", HttpStatusCode.Unauthorized)]
    [InlineData("metadata", HttpStatusCode.Forbidden)]
    [InlineData("toc", HttpStatusCode.Forbidden)]
    [InlineData("archive", HttpStatusCode.Forbidden)]
    public async Task RejectedBrowserTokenRefreshesOnceAndRetriesOnlyCurrentRequest(string rejectedRoute, HttpStatusCode status)
    {
        var backend = new Backend { RejectedRoute = rejectedRoute, RejectStatus = status, RejectJson = "{\"error_code\":105}", RejectCount = 1 };
        var providerCalls = new List<string>();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(backend.Send), CreateOptions(providerCalls));
        using var getter = new LitnetGetter(config);
        await getter.Init();
        await getter.Authorize();

        using var book = await getter.GetPreview(BookUrl);

        Assert.Equal([null, FirstToken], providerCalls);
        Assert.Equal(2, backend.Requests.Count(request => request.Route == rejectedRoute));
        Assert.Equal(4, backend.Requests.Count);
        var rejectedIndex = backend.Requests.FindIndex(request => request.Route == rejectedRoute);
        Assert.Equal(FirstToken, backend.Requests[rejectedIndex].Token);
        Assert.All(backend.Requests.Skip(rejectedIndex + 1), request =>
        {
            Assert.Equal(SecondToken, request.Token);
            Assert.Equal(SecondDevice, request.Device);
        });
        Assert.Single(book.Chapters);
    }

    [Theory]
    [InlineData("metadata", HttpStatusCode.Forbidden)]
    [InlineData("archive", HttpStatusCode.Forbidden)]
    [InlineData("metadata", HttpStatusCode.TooManyRequests)]
    [InlineData("archive", HttpStatusCode.TooManyRequests)]
    public async Task AccessDeniedOrRateLimitNeverRefreshesOrRetries(string rejectedRoute, HttpStatusCode status)
    {
        var backend = new Backend { RejectedRoute = rejectedRoute, RejectStatus = status, RejectJson = "{\"error_code\":2}", RejectCount = 1 };
        var providerCalls = new List<string>();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(backend.Send), CreateOptions(providerCalls));
        using var getter = new LitnetGetter(config);
        await getter.Init();
        await getter.Authorize();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.GetPreview(BookUrl));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal([null], providerCalls);
        Assert.Single(backend.Requests, request => request.Route == rejectedRoute);
        Assert.Equal(rejectedRoute, backend.Requests.Last().Route);
    }

    [Theory]
    [InlineData("{\"error_code\":\"105\"}")]
    [InlineData("{\"error_code\":null}")]
    [InlineData("{\"error_code\":{\"code\":105}}")]
    [InlineData("<html>Forbidden</html>")]
    public async Task InvalidErrorCodeShapePreservesHttp403WithoutRefreshing(string payload)
    {
        var backend = new Backend { RejectedRoute = "metadata", RejectStatus = HttpStatusCode.Forbidden, RejectJson = payload, RejectCount = 1 };
        var providerCalls = new List<string>();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(backend.Send), CreateOptions(providerCalls));
        using var getter = new LitnetGetter(config);
        await getter.Init();
        await getter.Authorize();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.GetPreview(BookUrl));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal([null], providerCalls);
        Assert.Single(backend.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task TwiceRejectedTokenFailsAndDoesNotStartAnotherRefreshOnFollowingApiCall(HttpStatusCode status)
    {
        var backend = new Backend { RejectedRoute = "metadata", RejectStatus = status, RejectJson = "{\"error_code\":105}", RejectCount = 2 };
        var providerCalls = new List<string>();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(backend.Send), CreateOptions(providerCalls));
        using var getter = new LitnetGetter(config);
        await getter.Init();
        await getter.Authorize();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.GetPreview(BookUrl));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal([null, FirstToken], providerCalls);
        Assert.Equal(2, backend.Requests.Count);

        await Assert.ThrowsAsync<HttpRequestException>(() => getter.GetPreview(BookUrl));
        Assert.Equal([null, FirstToken], providerCalls);
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task RemovedBrowserSessionStopsWithoutLegacyLoginFallback()
    {
        var backend = new Backend { RejectedRoute = "metadata", RejectStatus = HttpStatusCode.Unauthorized, RejectCount = 1 };
        var providerCalls = new List<string>();
        var options = CreateOptions(providerCalls);
        options.LitnetSessionProvider = (rejected, ct) =>
        {
            providerCalls.Add(rejected);
            return Task.FromResult(rejected == null ? new BrowserAuthSession(FirstToken, FirstDevice) : null);
        };
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(backend.Send), options);
        using var getter = new LitnetGetter(config);
        await getter.Init();
        await getter.Authorize();

        await Assert.ThrowsAsync<InvalidOperationException>(() => getter.GetPreview(BookUrl));

        Assert.Equal([null, FirstToken], providerCalls);
        Assert.Single(backend.Requests);
    }

    private static Options CreateOptions(List<string> calls) => new()
    {
        // Наличие старых реквизитов не должно запускать парольный вход поверх браузерного.
        Login = "unused-legacy@example.test",
        Password = "unused-legacy-password",
        LitnetSessionProvider = (rejectedToken, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add(rejectedToken);
            return Task.FromResult(rejectedToken == null
                ? new BrowserAuthSession(FirstToken, FirstDevice)
                : new BrowserAuthSession(SecondToken, SecondDevice));
        }
    };

    private sealed record ObservedRequest(string Route, string Token, string Device);

    private sealed class Backend
    {
        public List<ObservedRequest> Requests { get; } = [];
        public string RejectedRoute { get; init; }
        public HttpStatusCode RejectStatus { get; init; }
        public string RejectJson { get; init; } = "{}";
        public int RejectCount { get; init; }
        private int rejected;

        public HttpResponseMessage Send(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            var route = uri.AbsolutePath switch
            {
                "/v1/book/get/123" => "metadata",
                "/v1/book/contents" => "toc",
                "/v1/book/text/get-chapters" => "archive",
                _ => throw new InvalidOperationException("Неожиданный запрос: браузерная сессия не должна вызывать legacy login или другой endpoint.")
            };
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("sapi.litnet.com", uri.Host);
            Assert.Equal("sapi.litnet.com", request.Headers.Host);
            var query = HttpUtility.ParseQueryString(uri.Query);
            Assert.Null(query["login"]);
            Assert.Null(query["password"]);
            var token = Assert.Single(query.GetValues("user_token") ?? []);
            var device = Assert.Single(query.GetValues("device_id") ?? []);
            Assert.Equal(token == FirstToken ? FirstDevice : SecondDevice, device);
            Assert.Contains(token, new[] { FirstToken, SecondToken });
            var sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(device + LitnetArchiveTestSupport.AppKey + token))).ToLowerInvariant();
            Assert.Equal(sign, Assert.Single(query.GetValues("sign") ?? []));
            Requests.Add(new ObservedRequest(route, token, device));
            if (route == RejectedRoute && rejected++ < RejectCount)
                return new(RejectStatus) { Content = new StringContent(RejectJson, Encoding.UTF8, "application/json") };
            return route switch
            {
                "metadata" => AfAuditSupport.Json(new { title = "Книга", url = BookUrl.AbsoluteUri, author_name = "Автор", lang = "ru", adult_only = true }),
                "toc" => AfAuditSupport.Json(new[] { new { id = 11, title = "Глава 1", access = true } }),
                _ => LitnetArchiveTestSupport.Response([(11L, "[\"<p>Текст главы</p>\"]")])
            };
        }
    }
}
