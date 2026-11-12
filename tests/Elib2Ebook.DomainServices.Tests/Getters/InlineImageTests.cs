using System.Net;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class InlineImageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "e2e-images-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EmbeddedPngIsDecodedWithoutHttpAndWithoutLoggingPayload()
    {
        Directory.CreateDirectory(_dir);
        var logger = new CaptureLogger();
        using var config = new BookGetterConfig(new Options(), new HttpClient(new ReplyHandler()), new CookieContainer(), new TempFolder(_dir), logger);
        using var getter = new ImageGetter(config);
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 1 };
        var payload = Convert.ToBase64String(bytes);
        using var file = await getter.Image(new Uri("data:image/png;base64," + payload));
        Assert.Equal(bytes, file.Content);
        Assert.Equal(".png", file.Extension);
        Assert.DoesNotContain(logger.Messages, l => l.Contains(payload));
        Assert.Contains(logger.Messages, l => l.StartsWith("Загружена картинка"));
    }

    [Fact]
    public async Task MissingImageIsAWarningWithoutSuccessMessage()
    {
        Directory.CreateDirectory(_dir);
        var logger = new CaptureLogger();
        using var config = new BookGetterConfig(new Options(), new HttpClient(new ReplyHandler()), new CookieContainer(), new TempFolder(_dir), logger);
        using var getter = new ImageGetter(config);
        Assert.Null(await getter.Image(new Uri("https://example.com/missing.png")));
        Assert.DoesNotContain(logger.Messages, l => l.StartsWith("Загружена картинка"));
        Assert.Contains(logger.Warnings, l => l.Contains("404"));
    }

    private sealed class ImageGetter(BookGetterConfig config) : GetterBase(config)
    {
        protected override Uri SystemUrl => new("https://example.com");
        public Task<TempFile> Image(Uri uri) => SaveImage(uri);
        public override Task<Book> Get(Uri url) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ContentTypeDeterminesExtensionForImageEndpoint()
    {
        Directory.CreateDirectory(_dir);
        using var config = new BookGetterConfig(new Options(), new HttpClient(new PngHandler()), new CookieContainer(), new TempFolder(_dir), new CaptureLogger());
        using var getter = new ImageGetter(config);
        using var image = await getter.Image(new Uri("https://example.com/image?id=1"));
        Assert.Equal(".png", image.Extension);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, image.Content);
    }

    private sealed class PngHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent([137, 80, 78, 71]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request, content));
        }
    }

    private sealed class ReplyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(TestHttpResponse.Create(HttpStatusCode.NotFound, request, new StringContent("not found")));
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public List<string> Warnings { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception ex, Func<TState, Exception, string> formatter)
        {
            var line = formatter(state, ex);
            Messages.Add(line);
            if (level >= LogLevel.Warning) Warnings.Add(line);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
