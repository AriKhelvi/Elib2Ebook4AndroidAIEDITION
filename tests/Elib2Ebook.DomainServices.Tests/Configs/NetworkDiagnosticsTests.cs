using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Misc;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Configs;

public class NetworkDiagnosticsTests
{
    [Fact]
    public async Task FailureTraceHasTimingAndStatusWithoutPrefetchingBodyOrExposingSecrets()
    {
        var log = new Capture();
        const string body = "{\"password\":\"response-secret\",\"text\":\"private-book-text\"}";
        var content = new DeferredContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var transport = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = content };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "sid=cookie-secret");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });
        using var client = Client(log, transport);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://private-user:private-password@example.org/v1/user/path-secret?user_token=query-secret&odd-secret=value-secret#fragment-secret")
        { Content = new StringContent("password=request-secret") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "header-secret");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(0, content.Reads);
        var output = string.Join("\n", log.Debug);
        Assert.Contains("method=POST", output);
        Assert.Contains("status=403", output);
        Assert.Contains("content_type=application/json", output);
        Assert.Contains("retry_after=30s", output);
        Assert.Contains("elapsed_ms=", output);
        Assert.Contains("queue_ms=", output);
        Assert.Contains("network_ms=", output);
        Assert.DoesNotContain("secret", output);
        Assert.DoesNotContain("private-", output);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, content.Reads);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task DisabledDebugDoesNotProduceNetworkTraceOrReadTheBody()
    {
        var log = new Capture { Enabled = false };
        var content = new DeferredContent("never-prefetched");
        using var client = Client(log, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));

        using var response = await client.GetAsync("https://example.org/book/123", HttpCompletionOption.ResponseHeadersRead);

        Assert.Empty(log.Debug);
        Assert.Equal(0, content.Reads);
    }

    [Fact]
    public async Task RedirectTraceContainsSafeDestinationAndBothStatuses()
    {
        var log = new Capture();
        var count = 0;
        using var client = Client(log, new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(++count == 1 ? HttpStatusCode.Redirect : HttpStatusCode.OK);
            if (count == 1) response.Headers.Location = new Uri("/login?token=redirect-secret", UriKind.Relative);
            return Task.FromResult(response);
        }));

        using var response = await client.GetAsync("https://example.org/book/123");

        var output = string.Join("\n", log.Debug);
        Assert.Equal(2, count);
        Assert.Contains("status=302", output);
        Assert.Contains("status=200", output);
        Assert.Contains("перенаправление", output);
        Assert.Contains("redirect=1", output);
        Assert.DoesNotContain("redirect-secret", output);
    }

    [Theory]
    [InlineData("ftp://example.org/private-secret")]
    [InlineData("mailto:private-secret@example.org")]
    public async Task DiagnosticsDoNotFollowOrBreakRejectedRedirectProtocols(string location)
    {
        var log = new Capture();
        var transport = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        });
        using var client = Client(log, transport);

        using var response = await client.GetAsync("https://example.org/book/123");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, transport.Calls);
        Assert.Contains(log.Debug, line => line.Contains("перенаправление остановлено"));
        Assert.DoesNotContain(log.Debug, line => line.Contains("private-secret"));
    }

    [Fact]
    public async Task CancellationIsPreservedWithoutASecondRequest()
    {
        var log = new Capture();
        using var cancellation = new CancellationTokenSource();
        var transport = new Handler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = Client(log, transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://example.org/book/123", cancellation.Token));

        Assert.Equal(1, transport.Calls);
        Assert.Contains(log.Debug, line => line.Contains("cancelled=True"));
    }

    [Fact]
    public async Task TransportExceptionIsPreservedButItsMessageIsNeverLogged()
    {
        var log = new Capture();
        var error = new HttpRequestException("token=transport-secret");
        using var client = Client(log, new Handler((_, _) => throw error));

        var actual = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.org/api/book"));

        Assert.Same(error, actual);
        Assert.Contains(log.Debug, line => line.Contains("error=HttpRequestException"));
        Assert.DoesNotContain(log.Debug, line => line.Contains("transport-secret"));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("SEND")]
    public async Task RetryTraceIdentifiesAttemptWithoutLeakingRequestOrResponse(string mode)
    {
        var log = new Capture();
        var calls = 0;
        using var client = Client(log, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(
            ++calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK))));
        using var config = new BookGetterConfig(new Options(), client, new CookieContainer(), TempFolderFactory.Create(null), log);
        var url = new Uri("https://example.org/api/book?token=retry-secret");
        using var content = new StringContent("password=body-secret");
        using var response = mode switch
        {
            "GET" => await client.GetWithTriesAsync(url, TimeSpan.FromMilliseconds(1)),
            "POST" => await client.PostWithTriesAsync(url, content, TimeSpan.FromMilliseconds(1)),
            _ => await client.SendWithTriesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), TimeSpan.FromMilliseconds(1))
        };

        Assert.Equal(2, calls);
        var retry = Assert.Single(log.Debug, line => line.Contains("HTTP повтор"));
        Assert.Contains("attempt=2/5", retry);
        Assert.Contains("status=503", retry);
        Assert.DoesNotContain("secret", retry);
    }

    [Theory]
    [InlineData("{\"secret-key\":\"private-value\", broken}", "shape=object")]
    [InlineData("<html>cf-chl captcha private-value</html>", "cloudflare=True")]
    public async Task JsonParseFailureReportsOnlyStructureWithoutResponseTextOrPropertyNames(string payload, string expected)
    {
        var log = new Capture();
        using var client = Client(log, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(payload, Encoding.UTF8, "application/json") })));

        var error = await Assert.ThrowsAsync<Exception>(() => client.GetFromJsonWithTriesAsync<object>(new Uri("https://example.org/api/book?token=url-secret")));

        var output = error + string.Join("\n", log.Debug);
        Assert.DoesNotContain("private-value", output);
        Assert.DoesNotContain("secret-key", output);
        Assert.DoesNotContain("url-secret", output);
        Assert.Contains(expected, string.Join("\n", log.Debug));
    }

    [Fact]
    public void UriAndPayloadDescriptionsAreBoundedAndNeverExposeDynamicValues()
    {
        var value = new string('s', 15_000);
        var uri = DownloadDiagnostics.SafeUri(new Uri("https://example.org/book/" + value + "?" + value + "=" + value));
        var payload = DownloadDiagnostics.DescribePayload("{\"" + value + "\":\"" + value + "\"}");

        Assert.True(uri.Length < 1000);
        Assert.True(payload.Length < 500);
        Assert.DoesNotContain(new string('s', 20), uri + payload);
        Assert.Contains("sample_chars=8192", payload);
    }

    [Theory]
    [InlineData("user_token")]
    [InlineData("utoken")]
    [InlineData("sign")]
    [InlineData("email")]
    [InlineData("device_id")]
    [InlineData("uid")]
    public void SharedLogRedactionAlsoCoversSiteSpecificCredentials(string name)
    {
        Assert.DoesNotContain("sensitive-value", LogText.Clean($"https://example.org/?{name}=sensitive-value"));
        Assert.DoesNotContain("sensitive-value", LogText.Clean($"{{\"{name}\":\"sensitive-value\"}}"));
        var encoded = string.Concat(name.Select(ch => "%" + ((int)ch).ToString("x2")));
        Assert.DoesNotContain("sensitive-value", LogText.Clean($"https://example.org/?{encoded}=sensitive-value"));
    }

    private static HttpClient Client(ILogger log, HttpMessageHandler handler)
        => new(new RequestPolicyHandler(new RequestPacer(new Options { DelayMode = 1 }), log, handler));

    private sealed class Capture : ILogger
    {
        public bool Enabled { get; init; } = true;
        public List<string> Debug { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level != LogLevel.Debug || Enabled;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception error, Func<TState, Exception, string> format)
        {
            if (level == LogLevel.Debug) Debug.Add(format(state, error));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return send(request, token);
        }
    }

    private sealed class DeferredContent(string value) : HttpContent
    {
        public int Reads { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
        {
            Reads++;
            return stream.WriteAsync(Encoding.UTF8.GetBytes(value)).AsTask();
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
