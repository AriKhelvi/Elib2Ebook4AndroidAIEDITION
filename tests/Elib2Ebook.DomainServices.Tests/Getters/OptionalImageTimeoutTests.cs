using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class OptionalImageTimeoutTests
{
    private static readonly Uri ImageUrl = new("https://cdn.example.test/stalled.png");
    private static readonly byte[] ImageBytes = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly TimeSpan TestBudget = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OptionalImageTimeoutSkipsOnlyThatImageAndDisposesBody(bool stalledBody, bool shorterClientTimeout)
    {
        var body = new StalledStream();
        var handler = StallHandler(stalledBody, body);
        var logger = new CaptureLogger();
        using var config = Config(handler, logger, clientTimeout: shorterClientTimeout ? TimeSpan.FromMilliseconds(40) : null);
        using var getter = new ImageGetter(config);

        var image = await getter.Image(ImageUrl).WaitAsync(TestDeadline);

        Assert.Null(image);
        Assert.Equal(1, handler.Calls);
        Assert.False(config.CancellationToken.IsCancellationRequested);
        Assert.Contains("stalled.png", Assert.Single(logger.Warnings));
        Assert.DoesNotContain(logger.Messages, line => line.StartsWith("Загружена картинка"));
        Assert.Empty(Directory.EnumerateFiles(config.TempFolder.Path));
        if (stalledBody)
        {
            Assert.True(body.Entered.Task.IsCompleted);
            Assert.True(body.Disposed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserCancellationPropagatesWithoutOptionalImageWarning(bool stalledBody)
    {
        var body = new StalledStream();
        var headersEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = StallHandler(stalledBody, body, headersEntered);
        var logger = new CaptureLogger();
        using var cancel = new CancellationTokenSource();
        using var config = Config(handler, logger);
        config.CancellationToken = cancel.Token;
        using var getter = new ImageGetter(config, TimeSpan.FromSeconds(3));
        var pending = getter.Image(ImageUrl);
        await (stalledBody ? body.Entered.Task : headersEntered.Task).WaitAsync(TestDeadline);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TestDeadline));

        Assert.Empty(logger.Warnings);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.EnumerateFiles(config.TempFolder.Path));
        if (stalledBody) Assert.True(body.Disposed);
    }

    [Fact]
    public async Task UnrecognizedCancellationIsNotMisreportedAsOptionalImageTimeout()
    {
        var handler = new Handler((_, _) => Task.FromException<HttpResponseMessage>(new OperationCanceledException("Отмена транспортом")));
        var logger = new CaptureLogger();
        using var config = Config(handler, logger);
        using var getter = new ImageGetter(config);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await getter.Image(ImageUrl).WaitAsync(TestDeadline));

        Assert.IsNotType<TimeoutException>(error.InnerException);
        Assert.False(config.CancellationToken.IsCancellationRequested);
        Assert.Empty(logger.Warnings);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UserCancellationInterruptsRetryPauseBeforeAnotherImageRequest()
    {
        var responseDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new DisposalNotificationContent(responseDisposed)
        }));
        var logger = new CaptureLogger();
        using var cancel = new CancellationTokenSource();
        using var config = Config(handler, logger);
        config.CancellationToken = cancel.Token;
        using var getter = new ImageGetter(config);
        var pending = getter.Image(ImageUrl);
        await responseDisposed.Task.WaitAsync(TestDeadline);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TestDeadline));

        Assert.Empty(logger.Warnings);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredComicImageTimeoutPropagatesInsteadOfSkippingPage(bool stalledBody)
    {
        var body = new StalledStream();
        var handler = StallHandler(stalledBody, body);
        var logger = new CaptureLogger();
        using var config = Config(handler, logger, clientTimeout: TestBudget);
        using var getter = new ImageGetter(config);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await getter.RequiredImage(ImageUrl).WaitAsync(TestDeadline));

        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.EnumerateFiles(config.TempFolder.Path));
        if (stalledBody) Assert.True(body.Disposed);
    }

    [Fact]
    public async Task RequiredImageDoesNotUseOptionalImageBudget()
    {
        var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            return ImageResponse();
        });
        var logger = new CaptureLogger();
        using var config = Config(handler, logger);
        using var getter = new ImageGetter(config);

        using var image = await getter.RequiredImage(ImageUrl).WaitAsync(TestDeadline);

        Assert.NotNull(image);
        Assert.Equal(ImageBytes, image.Content);
        Assert.Empty(logger.Warnings);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RateLimitIsNotMaskedByOptionalImageTimeoutEvenWhenErrorBodyStalls(bool stalledErrorBody)
    {
        var body = new StalledStream();
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = stalledErrorBody ? new StreamContent(body) : new StringContent("rate limit")
        }));
        var logger = new CaptureLogger();
        using var config = Config(handler, logger);
        using var getter = new ImageGetter(config);

        var error = await Assert.ThrowsAsync<HttpRequestException>(async () => await getter.Image(ImageUrl).WaitAsync(TestDeadline));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(logger.Warnings);
        if (stalledErrorBody)
        {
            Assert.False(body.Entered.Task.IsCompleted);
            Assert.True(body.Disposed);
        }
    }

    [Fact]
    public async Task ChapterRetainsTextAndSuccessfulImageAfterAnotherImageTimesOut()
    {
        var body = new StalledStream();
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri.AbsolutePath == "/stalled.png"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = ImageContent(body) }
            : ImageResponse()));
        var logger = new CaptureLogger();
        using var config = Config(handler, logger);
        using var getter = new ImageGetter(config);
        var document = new HtmlDocument();
        document.LoadHtml("<p>Текст до иллюстрации</p><img src='https://cdn.example.test/stalled.png'><p>Текст после иллюстрации</p><img src='https://cdn.example.test/good.png'>");

        var images = (await getter.Images(document).WaitAsync(TestDeadline)).ToArray();
        try
        {
            var image = Assert.Single(images);
            Assert.Equal(ImageBytes, image.Content);
            Assert.Equal("/good.png", image.Url.AbsolutePath);
            Assert.Contains("Текст до иллюстрации", document.DocumentNode.InnerText);
            Assert.Contains("Текст после иллюстрации", document.DocumentNode.InnerText);
            var node = Assert.Single(document.DocumentNode.Descendants("img"));
            Assert.Equal(image.FullName, node.GetAttributeValue("src", ""));
            Assert.DoesNotContain("stalled.png", document.DocumentNode.OuterHtml);
            Assert.Single(logger.Warnings);
            Assert.Equal(2, handler.Calls);
            Assert.True(body.Disposed);
        }
        finally { foreach (var image in images) image.Dispose(); }
    }

    [Fact]
    public async Task ConfiguredRequestDelayDoesNotConsumeOptionalImageNetworkBudget()
    {
        var clock = TimeSpan.Zero;
        TimeSpan? waited = null;
        var pacer = new RequestPacer(() => TimeSpan.FromMinutes(2), () => clock, async (delay, ct) =>
        {
            waited = delay;
            // Реальная очередь длиннее тестового сетевого лимита, но вместо двух минут ждём 250 мс.
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            clock += delay;
        });
        var handler = new Handler((_, _) => Task.FromResult(ImageResponse()));
        var logger = new CaptureLogger();
        using var config = Config(handler, logger, pacer);
        using var getter = new ImageGetter(config);
        using var first = await getter.Image(new Uri("https://cdn.example.test/first.png")).WaitAsync(TestDeadline);

        using var second = await getter.Image(new Uri("https://cdn.example.test/second.png")).WaitAsync(TestDeadline);

        Assert.Equal(TimeSpan.FromMinutes(2), waited);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(ImageBytes, second.Content);
        Assert.Equal(2, handler.Calls);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task RedirectsShareOneImageNetworkBudgetInsteadOfStartingItAgain()
    {
        var handler = new Handler(async (request, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            if (request.RequestUri.AbsolutePath == "/stalled.png")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://other-cdn.example.test/final.png");
                return redirect;
            }
            return ImageResponse();
        });
        var logger = new CaptureLogger();
        using var config = Config(handler, logger);
        using var getter = new ImageGetter(config, TimeSpan.FromMilliseconds(400));

        var image = await getter.Image(ImageUrl).WaitAsync(TestDeadline);

        Assert.Null(image);
        Assert.Equal(2, handler.Calls);
        Assert.Single(logger.Warnings);
        Assert.Empty(Directory.EnumerateFiles(config.TempFolder.Path));
    }

    [Fact]
    public async Task RetrySharesImageNetworkBudgetInsteadOfStartingItAgain()
    {
        var attempts = 0;
        var handler = new Handler(async (_, ct) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            if (attempt == 1) throw new HttpRequestException("Временный сетевой сбой");
            return ImageResponse();
        });
        var logger = new CaptureLogger();
        using var config = Config(handler, logger);
        using var getter = new ImageGetter(config, TimeSpan.FromMilliseconds(400));

        var image = await getter.Image(ImageUrl).WaitAsync(TestDeadline);

        Assert.Null(image);
        Assert.Equal(2, handler.Calls);
        Assert.Single(logger.Warnings);
        Assert.Empty(Directory.EnumerateFiles(config.TempFolder.Path));
    }

    private static BookGetterConfig Config(Handler handler, CaptureLogger logger, RequestPacer pacer = null,
        TimeSpan? clientTimeout = null)
    {
        var options = new Options { DelayMode = 1 };
        var client = new HttpClient(new RequestPolicyHandler(pacer ?? new RequestPacer(options), logger, handler))
        {
            Timeout = clientTimeout ?? TimeSpan.FromSeconds(4)
        };
        return new BookGetterConfig(options, client, new CookieContainer(), TempFolderFactory.Create(null), logger);
    }

    private static Handler StallHandler(bool body, StalledStream stream, TaskCompletionSource headersEntered = null) => new(async (_, ct) =>
    {
        headersEntered?.TrySetResult();
        if (body) return new HttpResponseMessage(HttpStatusCode.OK) { Content = ImageContent(stream) };
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new InvalidOperationException("Недостижимый ответ зависшего запроса");
    });

    private static HttpResponseMessage ImageResponse()
    {
        var content = new ByteArrayContent(ImageBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpContent ImageContent(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return content;
    }

    private sealed class ImageGetter(BookGetterConfig config, TimeSpan? timeout = null) : GetterBase(config)
    {
        protected override Uri SystemUrl => new("https://example.test");
        protected override TimeSpan OptionalImageTimeout => timeout ?? TestBudget;
        public Task<TempFile> Image(Uri uri) => SaveImage(uri);
        public Task<TempFile> RequiredImage(Uri uri) => SaveRequiredImage(uri);
        public Task<IEnumerable<TempFile>> Images(HtmlDocument document) => GetImages(document, SystemUrl);
        public override Task<Book> Get(Uri url) => throw new NotSupportedException();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return send(request, cancellationToken);
        }
    }

    private sealed class CaptureLogger : ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ConcurrentQueue<string> Warnings { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception error, Func<TState, Exception, string> format)
        {
            var line = format(state, error);
            Messages.Enqueue(line);
            if (level >= LogLevel.Warning) Warnings.Enqueue(line);
        }
    }

    private sealed class DisposalNotificationContent(TaskCompletionSource disposed) : ByteArrayContent([])
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            disposed.TrySetResult();
        }
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
