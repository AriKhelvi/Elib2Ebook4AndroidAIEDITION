using System.Net;
using System.Reflection;
using E2E.Droid.Services;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Microsoft.Extensions.Logging.Abstractions;

namespace ServiceTests;

public sealed class BookPreviewRateLimitTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RateLimitedPreviewStopsBeforeFullGetAndRestoresOptions(bool additional)
    {
        var options = new Options { Additional = additional };
        using var getter = Create(options, HttpStatusCode.TooManyRequests);

        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await Preview(getter, options);
            await getter.Get(new Uri("https://example.test/book"));
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(1, getter.Calls);
        Assert.False(options.NoChapters);
        Assert.Equal(additional, options.Additional);
    }

    [Fact]
    public async Task MissingOptionalPreviewStillAllowsFullGet()
    {
        var options = new Options { Additional = true };
        using var getter = Create(options, HttpStatusCode.NotFound);

        await Preview(getter, options);
        using var book = await getter.Get(new Uri("https://example.test/book"));

        Assert.Equal(2, getter.Calls);
        Assert.False(options.NoChapters);
        Assert.True(options.Additional);
    }

    private static Task Preview(GetterBase getter, Options options) => (Task)typeof(BookPipeline)
        .GetMethod("TryPreviewAsync", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, [getter, new Uri("https://example.test/book"), options, NullLogger.Instance,
            (Action<BookPreview>)(_ => throw new InvalidOperationException("Карточка не должна появиться")), CancellationToken.None])!;

    private static PreviewGetter Create(Options options, HttpStatusCode status) => new(
        new BookGetterConfig(options, new HttpClient(), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance)) { Status = status };

    private sealed class PreviewGetter(BookGetterConfig config) : GetterBase(config)
    {
        protected override Uri SystemUrl => new("https://example.test");
        public HttpStatusCode Status { get; init; }
        public int Calls { get; private set; }
        public override Task<Book> Get(Uri url)
        {
            Calls++;
            if (Config.Options.NoChapters)
            {
                Assert.False(Config.Options.Additional);
                return Task.FromException<Book>(new HttpRequestException("HTTP " + (int)Status, null, Status));
            }
            return Task.FromResult(new Book(url) { Title = "Книга" });
        }
    }
}
