using System.Net;
using System.Text;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Litnet;
using Elib2Ebook.ExternalServices.Litnet.Getters;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitnetStreamTimeoutTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ArchiveTimeoutCoversStalledSuccessAndRejectedTokenBodies(HttpStatusCode status)
    {
        var stream = new StalledStream();
        using var config = ArchiveConfig(stream, status);
        config.Client.Timeout = TimeSpan.FromMilliseconds(100);
        using var getter = new LitnetGetter(config);
        await getter.Init();
        await getter.Authorize();

        var error = await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await getter.GetPreview(new Uri("https://litnet.com/book/123")).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.True(stream.Disposed);
        Assert.False(config.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task UserCancellationOfArchiveBodyIsNotReportedAsTimeout()
    {
        var stream = new StalledStream();
        using var cancel = new CancellationTokenSource();
        using var config = ArchiveConfig(stream, HttpStatusCode.OK);
        config.CancellationToken = cancel.Token;
        using var getter = new LitnetGetter(config);
        await getter.Init();
        await getter.Authorize();
        var download = getter.GetPreview(new Uri("https://litnet.com/book/123"));
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        Assert.IsNotType<TimeoutException>(error.InnerException);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task OAuthTimeoutCoversStalledBodyAfterSuccessfulHeaders()
    {
        var stream = new StalledStream();
        using var client = OAuthClient(new StreamContent(stream));
        client.Timeout = TimeSpan.FromMilliseconds(100);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await Exchange(client).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task OAuthUserCancellationRemainsCancellationAndDisposesBody()
    {
        var stream = new StalledStream();
        using var client = OAuthClient(new StreamContent(stream));
        using var cancel = new CancellationTokenSource();
        var exchange = Exchange(client, cancel.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);

        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuthRejectsOversizedKnownAndChunkedResponsesWithoutEchoingTokens(bool chunked)
    {
        var text = "{\"access_token\":\"private-token-marker" + new string('x', 256 * 1024) + "\",\"refresh_token\":\"private-refresh\"}";
        using var content = chunked
            ? (HttpContent)new StreamContent(new NonSeekableMemory(Encoding.UTF8.GetBytes(text)))
            : new StringContent(text, Encoding.UTF8, "application/json");
        using var client = OAuthClient(content);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Exchange(client));

        Assert.DoesNotContain("private-token-marker", error.ToString());
        Assert.DoesNotContain("private-refresh", error.ToString());
    }

    private static BookGetterConfig ArchiveConfig(Stream body, HttpStatusCode status)
    {
        var handler = new AfAuditSupport.Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/book/get/123" => AfAuditSupport.Json(new { title = "Книга", url = "https://litnet.com/book/123", author_name = "Автор", lang = "ru" }),
            "/v1/book/contents" => AfAuditSupport.Json(new[] { new { id = 11, title = "Глава", access = true } }),
            "/v1/book/text/get-chapters" => new HttpResponseMessage(status) { Content = new StreamContent(body) },
            _ => throw new InvalidOperationException("Лишний тестовый запрос")
        });
        return AfAuditSupport.Config(handler, new Options
        {
            LitnetSessionProvider = (_, _) => Task.FromResult(new BrowserAuthSession("test-token", "test-device"))
        });
    }

    private static HttpClient OAuthClient(HttpContent content) => new(new AfAuditSupport.Handler(_ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

    private static Task<LitnetOAuthSession> Exchange(HttpClient client, CancellationToken ct = default)
        => LitnetOAuth.ExchangeAsync(client, LitnetOAuth.CreateAuthorization("test-device"), "test-code", ct);

    private sealed class NonSeekableMemory(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class StalledStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
