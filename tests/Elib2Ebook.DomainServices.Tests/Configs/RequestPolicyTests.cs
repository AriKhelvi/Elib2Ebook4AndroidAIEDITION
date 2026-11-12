using System.Net;
using System.Net.Http.Headers;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;

namespace Elib2Ebook.DomainServices.Tests.Configs;

public class RequestPolicyTests
{
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(2, 0, 0.6)]
    [InlineData(2, 1, 1.5)]
    [InlineData(3, 0, 5)]
    [InlineData(3, 1, 10)]
    public void PresetIntervalsStayWithinChosenRange(int mode, double sample, double expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), RequestPacer.GetInterval(new Options { DelayMode = mode }, sample));
    }

    [Theory]
    [InlineData(0, 0.01)]
    [InlineData(0.25, 0.25)]
    [InlineData(121, 120)]
    public void CustomIntervalSupportsFractionsAndBounds(double seconds, double expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), RequestPacer.GetInterval(new Options { DelayMode = 4, CustomDelaySeconds = seconds }, 0.5));
    }

    [Fact]
    public void CommandLineDelayRemainsCompatible()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), RequestPacer.GetInterval(new Options { Delay = 7 }, 0));
        Assert.Equal(TimeSpan.Zero, RequestPacer.GetInterval(new Options(), 0));
    }

    [Fact]
    public async Task ParallelRequestsAcrossTwoClientsHaveSpacedStarts()
    {
        const int requestCount = 20;
        var clock = new Clock();
        var pacer = clock.Pacer();
        var starts = new List<TimeSpan>();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Send(HttpRequestMessage _, CancellationToken token)
        {
            lock (starts)
            {
                starts.Add(clock.Now);
                if (starts.Count == requestCount) allStarted.SetResult();
            }
            // Ответы ещё не получены: проверяется интервал начал, а не пауза после загрузки.
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        using var first = Client(pacer, Send);
        using var second = Client(pacer, Send);

        var responses = await Task.WhenAll(Enumerable.Range(0, requestCount)
            .Select(i => (i % 2 == 0 ? first : second).GetAsync($"https://example.org/{i}")));
        foreach (var response in responses) response.Dispose();

        Assert.Equal(Enumerable.Range(0, requestCount).Select(i => TimeSpan.FromSeconds(i)), starts);
    }

    [Fact]
    public async Task SynchronousDispatchFailureReleasesTheNextRequest()
    {
        var clock = new Clock();
        var pacer = clock.Pacer();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pacer.SendAsync<int>(() => throw new InvalidOperationException("Ошибка отправки"), CancellationToken.None));

        var result = await pacer.SendAsync(() => Task.FromResult(42), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(42, result);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Now);
    }

    [Fact]
    public async Task RelativeRedirectIsPacedAndDoesNotForwardAuthorization()
    {
        var clock = new Clock();
        var paths = new List<string>();
        var starts = new List<TimeSpan>();
        using var client = Client(clock.Pacer(), (request, _) =>
        {
            paths.Add(request.RequestUri.AbsolutePath);
            starts.Add(clock.Now);
            if (paths.Count == 1)
            {
                Assert.NotNull(request.Headers.Authorization);
                var moved = new HttpResponseMessage(HttpStatusCode.Redirect);
                moved.Headers.Location = new Uri("next", UriKind.Relative);
                return Task.FromResult(moved);
            }
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test");

        using var response = await client.GetAsync("https://example.org/book/start");

        Assert.Equal(new[] { "/book/start", "/book/next" }, paths);
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1) }, starts);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/next", "example.org")]
    [InlineData("https://other.example/next", null)]
    public async Task RedirectPreservesManualHostOnlyForSameAddress(string target, string expectedHost)
    {
        var calls = 0;
        using var client = Client(new Clock().Pacer(), (request, _) =>
        {
            if (++calls == 1)
            {
                Assert.Equal("example.org", request.Headers.Host);
                var moved = new HttpResponseMessage(HttpStatusCode.Redirect);
                moved.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
                return Task.FromResult(moved);
            }
            Assert.Equal(expectedHost, request.Headers.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://127.0.0.1/start");
        request.Headers.Host = "example.org";
        using var response = await client.SendAsync(request);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.SeeOther, "GET", false)]
    [InlineData(HttpStatusCode.TemporaryRedirect, "POST", true)]
    public async Task RedirectRespectsMethodAndContent(HttpStatusCode redirectStatus, string expectedMethod, bool hasContent)
    {
        var calls = 0;
        using var client = Client(new Clock().Pacer(), async (request, _) =>
        {
            if (++calls == 1)
            {
                var moved = new HttpResponseMessage(redirectStatus);
                moved.Headers.Location = new Uri("/next", UriKind.Relative);
                return moved;
            }
            Assert.Equal(expectedMethod, request.Method.Method);
            Assert.Equal(hasContent, request.Content != null);
            Assert.Equal(hasContent, request.Headers.TransferEncodingChunked == true);
            if (hasContent) Assert.Equal("body", await request.Content.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.org/start") { Content = new StringContent("body") };
        request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AccessErrorsAreNotRetriedOrParsedAsBookHtml(HttpStatusCode status)
    {
        var calls = 0;
        using var client = Client(new Clock().Pacer(), (_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(status) { Content = new StringContent("<html>access denied</html>") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetHtmlDocWithTriesAsync(new Uri("https://example.org/book")));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, calls);
        if (status == HttpStatusCode.TooManyRequests) Assert.Contains("30 сек", error.Message);
    }

    [Fact]
    public async Task CancellationWhileWaitingDoesNotSendAnotherRequest()
    {
        var calls = 0;
        var pacer = new RequestPacer(() => TimeSpan.FromSeconds(1), () => TimeSpan.Zero,
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        using var client = Client(pacer, (_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var first = await client.GetAsync("https://example.org/first");
        using var cancellation = new CancellationTokenSource();
        var waiting = client.GetAsync("https://example.org/second", cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RedirectFailureDisposesOwnedResourcesButKeepsCallerContent()
    {
        using var body = new TrackedContent("тело запроса");
        using var redirectBody = new TrackedContent("перенаправление");
        using var redirectResponse = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = redirectBody };
        redirectResponse.Headers.Location = new Uri("/next", UriKind.Relative);
        HttpRequestMessage redirected = null;
        var calls = 0;
        using var client = Client(new Clock().Pacer(), (request, _) =>
        {
            if (++calls == 1)
            {
                return Task.FromResult(redirectResponse);
            }
            redirected = request;
            Assert.True(redirectBody.IsDisposed);
            throw new InvalidOperationException("Ошибка отправки перенаправленного запроса");
        });
        using var original = new HttpRequestMessage(HttpMethod.Post, "https://example.org/start") { Content = body };

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(original));

        Assert.True(redirectBody.IsDisposed);
        Assert.NotNull(redirected);
        Assert.Throws<ObjectDisposedException>(() => redirected.Method = HttpMethod.Get);
        Assert.False(body.IsDisposed);
        Assert.Equal("тело запроса", await body.ReadAsStringAsync());
    }

    [Fact]
    public async Task CancellationDuringRedirectDisposesRequestAndKeepsCallerContent()
    {
        using var body = new TrackedContent("не закрывать");
        using var redirectBody = new TrackedContent("перенаправление");
        using var redirectResponse = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = redirectBody };
        redirectResponse.Headers.Location = new Uri("/next", UriKind.Relative);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpRequestMessage redirected = null;
        var calls = 0;
        using var client = Client(new Clock().Pacer(), async (request, token) =>
        {
            if (++calls == 1)
            {
                return redirectResponse;
            }
            redirected = request;
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Ожидалась отмена");
        });
        using var original = new HttpRequestMessage(HttpMethod.Post, "https://example.org/start") { Content = body };
        var sending = client.SendAsync(original, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

        Assert.True(redirectBody.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => redirected.Method = HttpMethod.Get);
        Assert.False(body.IsDisposed);
        Assert.Equal("не закрывать", await body.ReadAsStringAsync());
    }

    [Fact]
    public async Task Redirect307UsesIndependentContentAndReturnsLiveFinalResponse()
    {
        using var body = new TrackedContent("тело");
        body.Headers.ContentType = new MediaTypeHeaderValue("application/custom");
        using var redirectBody = new TrackedContent("перенаправление");
        using var finalBody = new TrackedContent("результат");
        using var redirectResponse = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = redirectBody };
        redirectResponse.Headers.Location = new Uri("/next", UriKind.Relative);
        using var finalResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = finalBody };
        HttpRequestMessage redirected = null;
        var calls = 0;
        using var client = Client(new Clock().Pacer(), async (request, _) =>
        {
            if (++calls == 1)
            {
                return redirectResponse;
            }
            redirected = request;
            Assert.NotSame(body, request.Content);
            Assert.Equal("application/custom", request.Content.Headers.ContentType!.MediaType);
            Assert.Equal("тело", await request.Content.ReadAsStringAsync());
            return finalResponse;
        });
        using var original = new HttpRequestMessage(HttpMethod.Post, "https://example.org/start") { Content = body };

        using var final = await client.SendAsync(original);

        Assert.True(redirectBody.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => redirected.Method = HttpMethod.Get);
        Assert.False(body.IsDisposed);
        Assert.False(finalBody.IsDisposed);
        Assert.Equal("результат", await final.Content.ReadAsStringAsync());
        Assert.Equal("тело", await body.ReadAsStringAsync());
    }

    private sealed class TrackedContent(string text) : ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text))
    {
        public bool IsDisposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private static HttpClient Client(RequestPacer pacer, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new RequestPolicyHandler(pacer, null, new FakeHandler(send)));

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class Clock
    {
        public TimeSpan Now;
        public RequestPacer Pacer() => new(() => TimeSpan.FromSeconds(1), () => Now, async (duration, token) =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            Now += duration;
        });
    }
}
