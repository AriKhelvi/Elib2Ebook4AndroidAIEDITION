using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Litres;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class LitresAnonymousDownloadTests
{
    private static readonly Uri BookUrl = new("https://www.litres.ru/123");
    private const string MetadataPath = "/foundation/api/arts/123";
    private const string ExcerptPath = "/pub/t/123.fb3";
    private const string SyntheticSession = "test-user-session";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnonymousDownloadUsesPublicExcerptWithoutSessionOrFullBookRequests(bool authorize, bool additional)
    {
        var requests = new List<Uri>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            requests.Add(request.RequestUri);
            Assert.False(request.Headers.Contains("Session-Id"));
            Assert.Equal(HttpMethod.Get, request.Method);
            return request.RequestUri.AbsolutePath switch
            {
                MetadataPath => Metadata(),
                ExcerptPath => Excerpt(),
                _ => throw new InvalidOperationException("Анонимная загрузка запросила непубличный маршрут: " + request.RequestUri.AbsolutePath)
            };
        });
        var logger = new AfAuditSupport.AuditLogger();
        using var config = AfAuditSupport.Config(handler, new Options { Additional = additional, AuthCachePath = "" }, logger);
        using var getter = new LitresGetter(config);
        if (authorize) await getter.Authorize();

        using var book = await getter.Get(BookUrl);

        Assert.Contains("Публичный отрывок", Assert.Single(book.Chapters).Content);
        Assert.Equal(new[] { MetadataPath, ExcerptPath }, requests.Select(uri => uri.AbsolutePath));
        Assert.Equal("catalit.litres.ru", requests[1].Host);
        Assert.Empty(requests[1].Query);
        Assert.False(config.Client.DefaultRequestHeaders.Contains("Session-Id"));
        Assert.Contains(logger.Warnings, warning => warning.Contains("ознакомительный отрывок") && warning.Contains("Полнота книги не подтверждена"));
    }

    [Fact]
    public async Task RemovingCredentialsClearsPreviouslyAuthenticatedSessionAndSelectsExcerpt()
    {
        var loginRequests = 0;
        var downloadRequests = new List<string>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            if (request.RequestUri.AbsolutePath == "/foundation/api/auth/login")
            {
                loginRequests++;
                Assert.False(request.Headers.Contains("Session-Id"));
                return Login();
            }
            if (request.RequestUri.AbsolutePath == "/foundation/api/users/me/detailed")
            {
                Assert.Equal(SyntheticSession, Assert.Single(request.Headers.GetValues("Session-Id")));
                return Profile();
            }
            Assert.False(request.Headers.Contains("Session-Id"));
            downloadRequests.Add(request.RequestUri.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                MetadataPath => Metadata(),
                ExcerptPath => Excerpt(),
                _ => throw new InvalidOperationException("Старый сеанс не должен использоваться после удаления логина")
            };
        });
        var options = Credentials();
        using var config = AfAuditSupport.Config(handler, options);
        using var getter = new LitresGetter(config);
        await getter.Authorize();
        options.Login = null;
        options.Password = null;

        await getter.Authorize();
        using var book = await getter.Get(BookUrl);

        Assert.Equal(1, loginRequests);
        Assert.Equal(new[] { MetadataPath, ExcerptPath }, downloadRequests);
        Assert.Single(book.Chapters);
        Assert.False(config.Client.DefaultRequestHeaders.Contains("Session-Id"));
    }

    [Fact]
    public async Task AuthenticatedDownloadStillUsesConfirmedUserSessionForFullBook()
    {
        var paths = new List<string>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            var path = request.RequestUri.AbsolutePath;
            paths.Add(path);
            if (path == "/foundation/api/auth/login") return Login();
            Assert.Equal(SyntheticSession, Assert.Single(request.Headers.GetValues("Session-Id")));
            if (path == "/foundation/api/users/me/detailed") return Profile();
            if (path == MetadataPath) return Metadata();
            Assert.Equal("/pages/download_book_j", path);
            var response = Excerpt();
            response.Headers.AcceptRanges.Add("bytes");
            return response;
        });
        var logger = new AfAuditSupport.AuditLogger();
        using var config = AfAuditSupport.Config(handler, Credentials(), logger);
        using var getter = new LitresGetter(config);

        await getter.Authorize();
        using var book = await getter.Get(BookUrl);

        Assert.Single(book.Chapters);
        Assert.Contains("/pages/download_book_j", paths);
        Assert.DoesNotContain(ExcerptPath, paths);
        Assert.DoesNotContain(logger.Warnings, warning => warning.Contains("ознакомительный отрывок"));
    }

    [Fact]
    public async Task AuthenticatedFallbackDisposesRejectedResponsesAndWarnsThatOnlyExcerptWasDownloaded()
    {
        var rejected = new List<TrackedContent>();
        var logger = new AfAuditSupport.AuditLogger();
        var handler = new AfAuditSupport.Handler(request =>
        {
            var path = request.RequestUri.AbsolutePath;
            if (path == "/foundation/api/auth/login") return Login();
            if (path == "/foundation/api/users/me/detailed") return Profile();
            if (path == MetadataPath) return Metadata();
            if (path == ExcerptPath) return Excerpt();
            Assert.StartsWith("/pages/download_", path);
            var content = new TrackedContent("Полная книга недоступна");
            rejected.Add(content);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = content };
        });
        using var config = AfAuditSupport.Config(handler, Credentials(), logger);
        using var getter = new LitresGetter(config);

        await getter.Authorize();
        using var book = await getter.Get(BookUrl);

        Assert.Contains("Публичный отрывок", Assert.Single(book.Chapters).Content);
        Assert.Equal(6, rejected.Count);
        Assert.All(rejected, response => Assert.True(response.Disposed));
        Assert.Contains(logger.Warnings, warning => warning.Contains("ознакомительный отрывок") && warning.Contains("Полнота книги не подтверждена"));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    public async Task ExcerptHttpFailureIsPreservedAndNeverParsedAsAnEmptyBook(int status)
    {
        var rejectedBody = new TrackedContent("Отказ в выдаче отрывка");
        var handler = new AfAuditSupport.Handler(request =>
        {
            Assert.False(request.Headers.Contains("Session-Id"));
            if (request.RequestUri.AbsolutePath == MetadataPath) return Metadata();
            Assert.Equal(ExcerptPath, request.RequestUri.AbsolutePath);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = rejectedBody };
        });
        using var config = AfAuditSupport.Config(handler);
        using var getter = new LitresGetter(config);
        await getter.Authorize();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUrl));

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.True(rejectedBody.Disposed);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CancellingPublicExcerptRequestStopsDownloadInsteadOfReturningAnEmptyBook()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new WaitingExcerptHandler();
        using var config = AfAuditSupport.Config(handler);
        config.CancellationToken = cancellation.Token;
        using var getter = new LitresGetter(config);
        await getter.Authorize();

        var download = getter.Get(BookUrl);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, handler.Calls);
    }

    private static Options Credentials() => new() { Login = "reader@example.test", Password = "test-password", AuthCachePath = "" };

    private static HttpResponseMessage Login() => AfAuditSupport.Json(new { payload = new { data = new { sid = SyntheticSession } } });
    private static HttpResponseMessage Profile() => AfAuditSupport.Json(new { payload = new { data = new { } } });
    private static HttpResponseMessage Metadata() => AfAuditSupport.Json(new { payload = new { data = new { id = 123, title = "Тестовая книга", art_type = 0 } } });

    private static HttpResponseMessage Excerpt()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var body = zip.CreateEntry("fb3/body.xml").Open())
            body.Write(Encoding.UTF8.GetBytes("<fb3-body><section><title>Глава</title><p>Публичный отрывок.</p></section></fb3-body>"));
        var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "sample.fb3" };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class TrackedContent(string body) : StringContent(body)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class WaitingExcerptHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.False(request.Headers.Contains("Session-Id"));
            if (request.RequestUri.AbsolutePath == MetadataPath)
                return SetRequest(Metadata(), request);
            Assert.Equal(ExcerptPath, request.RequestUri.AbsolutePath);
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Запрос должен быть отменён");
        }

        private static HttpResponseMessage SetRequest(HttpResponseMessage response, HttpRequestMessage request)
        {
            response.RequestMessage = request;
            return response;
        }
    }
}
