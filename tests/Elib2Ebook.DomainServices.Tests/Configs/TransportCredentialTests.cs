using System.Net;
using System.Net.Http.Headers;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Configs;

public sealed class TransportCredentialTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "e2e-header-scope-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] SecretHeaders =
    [
        "Authorization", "Proxy-Authorization", "Token", "auth-token", "access-token", "Session-Id",
        "X-CSRF-TOKEN", "X-XSRF-TOKEN", "X-Future-Session", "Cookie"
    ];

    [Theory]
    [InlineData("https://images.invalid/cover.png")]
    [InlineData("https://cdn.books.invalid/cover.png")]
    [InlineData("https://books.invalid:444/cover.png")]
    [InlineData("http://books.invalid/cover.png")]
    public async Task ExternalImageStripsDefaultAndExplicitCredentials(string url)
    {
        var calls = 0;
        using var config = Config((request, _) =>
        {
            calls++;
            AssertNoSecrets(request);
            Assert.False(request.Headers.Contains("X-Explicit-Session"));
            Assert.Null(request.Headers.Host);
            Assert.Equal("E2E-test", request.Headers.UserAgent.ToString());
            Assert.Equal("https://books.invalid/", request.Headers.Referrer!.AbsoluteUri);
            return Task.FromResult(ImageResponse(request));
        });
        using var getter = new ImageGetter(config, explicitHeaders: true);

        using var image = await getter.Image(new Uri(url));

        Assert.NotNull(image);
        Assert.Equal(1, calls);
        Assert.All(SecretHeaders, name => Assert.True(config.Client.DefaultRequestHeaders.Contains(name)));
    }

    [Fact]
    public async Task SameOriginImageKeepsCredentialsAndExplicitHeaders()
    {
        using var config = Config((request, _) =>
        {
            Assert.All(SecretHeaders, name => Assert.True(request.Headers.Contains(name)));
            Assert.True(request.Headers.Contains("X-Explicit-Session"));
            Assert.Contains("private-path", request.Headers.Referrer!.AbsoluteUri);
            return Task.FromResult(ImageResponse(request));
        });
        using var getter = new ImageGetter(config, explicitHeaders: true);

        using var image = await getter.Image(new Uri("https://BOOKS.invalid:443/cover.png"));

        Assert.NotNull(image);
    }

    [Theory]
    [InlineData("https://books.invalid/cover.png", true)]
    [InlineData("https://cdn.books.invalid/cover.png", false)]
    public async Task ImageIpRoutingPreservesOnlyTheImagesOwnHost(string url, bool authenticated)
    {
        using var config = Config((request, _) =>
        {
            Assert.Equal("192.0.2.1", request.RequestUri!.Host);
            Assert.Equal(new Uri(url).Host, request.Headers.Host);
            if (authenticated) Assert.All(SecretHeaders, name => Assert.True(request.Headers.Contains(name)));
            else AssertNoSecrets(request);
            return Task.FromResult(ImageResponse(request));
        });
        using var getter = new ImageGetter(config, routeToIp: true);

        using var image = await getter.Image(new Uri(url));

        Assert.NotNull(image);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedExternalImageDoesNotRemoveSiteCredentials(bool cancelled)
    {
        var calls = 0;
        using var config = Config((request, _) =>
        {
            calls++;
            if (request.RequestUri!.Host != "books.invalid")
            {
                AssertNoSecrets(request);
                if (cancelled) throw new OperationCanceledException();
                throw new HttpRequestException("Синтетический отказ", null, HttpStatusCode.Forbidden);
            }
            Assert.All(SecretHeaders, name => Assert.True(request.Headers.Contains(name)));
            return Task.FromResult(ImageResponse(request));
        });
        using var getter = new ImageGetter(config);
        var external = new Uri("https://images.invalid/failure.png");
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => getter.Image(external));
        else Assert.Null(await getter.Image(external));

        using var image = await getter.Image(new Uri("https://books.invalid/private.png"));

        Assert.NotNull(image);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("https://other.invalid/next")]
    [InlineData("https://cdn.books.invalid/next")]
    [InlineData("https://books.invalid:444/next")]
    public async Task CrossOriginRedirectDoesNotRestoreCredentialsOnFurtherRedirects(string target)
    {
        var calls = 0;
        using var config = Config((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.All(SecretHeaders, name => Assert.True(request.Headers.Contains(name)));
                return Task.FromResult(Redirect(request, target));
            }
            AssertNoSecrets(request);
            Assert.Null(request.Headers.Host);
            Assert.Equal("E2E-test", request.Headers.UserAgent.ToString());
            Assert.Equal("https://books.invalid/", request.Headers.Referrer!.AbsoluteUri);
            return Task.FromResult(calls switch
            {
                2 => Redirect(request, "/again"),
                3 => Redirect(request, "https://books.invalid/returned"),
                _ => ImageResponse(request)
            });
        });

        using var response = await config.Client.GetAsync("https://books.invalid/start");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, calls);
        Assert.All(SecretHeaders, name => Assert.True(config.Client.DefaultRequestHeaders.Contains(name)));
    }

    [Fact]
    public async Task SameOriginRedirectRetainsCustomSiteHeaders()
    {
        var calls = 0;
        using var config = Config((request, _) =>
        {
            if (++calls == 1) return Task.FromResult(Redirect(request, "/next"));
            // Authorization уже снимался со всех redirects; остальные заголовки своего сайта остаются.
            Assert.Null(request.Headers.Authorization);
            Assert.All(SecretHeaders.Where(name => name != "Authorization"), name => Assert.True(request.Headers.Contains(name)));
            Assert.Equal("books.invalid", request.Headers.Host);
            return Task.FromResult(ImageResponse(request));
        });

        using var response = await config.Client.GetAsync("https://books.invalid/start");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task HttpToHttpsRedirectIsAlsoAnOriginChange()
    {
        var calls = 0;
        using var config = Config((request, _) =>
        {
            if (++calls == 1) return Task.FromResult(Redirect(request, "https://books.invalid/next"));
            AssertNoSecrets(request);
            return Task.FromResult(ImageResponse(request));
        });

        using var response = await config.Client.GetAsync("http://books.invalid/start");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task RedirectCannotReplayLoginBodyToAnotherOrigin(HttpStatusCode status)
    {
        var calls = 0;
        using var config = Config((request, _) =>
        {
            calls++;
            var response = Redirect(request, "https://other.invalid/receive");
            response.StatusCode = status;
            response.Content = new StringContent("redirect response remains readable");
            return Task.FromResult(response);
        });
        using var body = new StringContent("password=synthetic-password");

        using var response = await config.Client.PostAsync("https://books.invalid/login", body);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("redirect response remains readable", await response.Content.ReadAsStringAsync());
        Assert.Equal("password=synthetic-password", await body.ReadAsStringAsync());
        Assert.Equal(1, calls);
    }

    private BookGetterConfig Config(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        Directory.CreateDirectory(_dir);
        var options = new Options { DelayMode = 1 };
        var client = new HttpClient(new RequestPolicyHandler(new RequestPacer(options), null, new FakeHandler(send)));
        foreach (var name in SecretHeaders)
            client.DefaultRequestHeaders.TryAddWithoutValidation(name, name == "Authorization" ? "Bearer synthetic-token" : "synthetic-value");
        client.DefaultRequestHeaders.Host = "books.invalid";
        client.DefaultRequestHeaders.UserAgent.ParseAdd("E2E-test");
        client.DefaultRequestHeaders.Referrer = new Uri("https://user:synthetic@books.invalid/private-path?sid=synthetic#fragment");
        return new BookGetterConfig(options, client, new CookieContainer(), new TempFolder(_dir), NullLogger.Instance);
    }

    private static void AssertNoSecrets(HttpRequestMessage request) =>
        Assert.All(SecretHeaders, name => Assert.False(request.Headers.Contains(name), name));

    private static HttpResponseMessage ImageResponse(HttpRequestMessage request)
    {
        var content = new ByteArrayContent([137, 80, 78, 71]);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return TestHttpResponse.Create(HttpStatusCode.OK, request, content);
    }

    private static HttpResponseMessage Redirect(HttpRequestMessage request, string target)
    {
        var response = TestHttpResponse.Create(HttpStatusCode.Redirect, request);
        response.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class ImageGetter(BookGetterConfig config, bool explicitHeaders = false, bool routeToIp = false) : GetterBase(config)
    {
        protected override Uri SystemUrl => new("https://books.invalid/");
        public Task<TempFile> Image(Uri uri) => SaveImage(uri);
        public override Task<Book> Get(Uri url) => throw new NotSupportedException();
        protected override HttpRequestMessage GetImageRequestMessage(Uri uri)
        {
            var message = base.GetImageRequestMessage(uri);
            if (explicitHeaders) message.Headers.TryAddWithoutValidation("X-Explicit-Session", "synthetic-explicit");
            if (routeToIp)
            {
                message.RequestUri = new Uri("https://192.0.2.1" + uri.PathAndQuery);
                message.Headers.Host = uri.Host;
            }
            return message;
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
