using System.Net;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.ExternalServices.Litnet.Getters;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitnetPreviewTests
{
    private static readonly Uri BookUrl = new("https://litnet.com/ru/book/test-b123");

    [Fact]
    public async Task PreviewDefersSeriesUntilAfterTextAndReusesMetadataAndIndependentCover()
    {
        var backend = new Backend();
        var options = new Options { NoChapters = true };
        using var config = AfAuditSupport.Config(backend.Handler, options);
        using var getter = new LitnetGetter(config);
        await getter.Init();
        string previewCover;

        using (var preview = await getter.GetPreview(BookUrl))
        {
            Assert.Equal("Книга", preview.Title);
            Assert.Empty(preview.Chapters);
            Assert.Null(preview.Seria);
            Assert.Equal(Backend.CoverBytes, preview.Cover.Content);
            previewCover = preview.Cover.FilePath;
            Assert.Equal(["metadata", "cover"], backend.Requests);
        }
        Assert.False(File.Exists(previewCover));
        options.NoChapters = false;

        using var book = await getter.Get(BookUrl);

        Assert.Equal(["metadata", "cover", "toc", "text", "series"], backend.Requests);
        Assert.Equal("<p>Текст главы</p>", book.Chapters.Single().Content);
        Assert.Equal("Проверенный цикл", book.Seria.Name);
        Assert.Equal("2", book.Seria.Number);
        Assert.NotEqual(previewCover, book.Cover.FilePath);
        Assert.Equal(Backend.CoverBytes, book.Cover.Content);
    }

    [Fact]
    public async Task OrdinaryMetadataOnlyDownloadStillObtainsSeries()
    {
        var backend = new Backend();
        using var config = AfAuditSupport.Config(backend.Handler, new Options { NoChapters = true });
        using var getter = new LitnetGetter(config);
        await getter.Init();

        using var book = await getter.Get(BookUrl);

        Assert.Empty(book.Chapters);
        Assert.Equal("Проверенный цикл", book.Seria.Name);
        Assert.Equal(["metadata", "cover", "series"], backend.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RejectedTextAfterPreviewDoesNotStartOptionalSeriesRequest(HttpStatusCode status)
    {
        var backend = new Backend { TextStatus = status };
        var options = new Options { NoChapters = true };
        using var config = AfAuditSupport.Config(backend.Handler, options);
        using var getter = new LitnetGetter(config);
        await getter.Init();
        using (var preview = await getter.GetPreview(BookUrl)) { }
        options.NoChapters = false;

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUrl));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(["metadata", "cover", "toc", "text"], backend.Requests);
    }

    [Fact]
    public async Task OptionalSeriesRateLimitAfterTextStillPropagatesWithoutRetry()
    {
        var backend = new Backend { SeriesStatus = HttpStatusCode.TooManyRequests };
        var options = new Options { NoChapters = true };
        using var config = AfAuditSupport.Config(backend.Handler, options);
        using var getter = new LitnetGetter(config);
        await getter.Init();
        using (var preview = await getter.GetPreview(BookUrl)) { }
        options.NoChapters = false;

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUrl));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(["metadata", "cover", "toc", "text", "series"], backend.Requests);
    }

    [Fact]
    public async Task DefaultPreviewCallsTheExistingGetterWithoutChangingOptions()
    {
        var options = new Options { NoChapters = true };
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => throw new InvalidOperationException("Сеть не нужна")), options);
        using var getter = new ExistingGetter(config);

        using var preview = await getter.GetPreview(BookUrl);

        Assert.Equal(1, getter.Calls);
        Assert.Equal("Карточка существующего загрузчика", preview.Title);
        Assert.True(options.NoChapters);
    }

    private sealed class ExistingGetter(BookGetterConfig config) : GetterBase(config)
    {
        protected override Uri SystemUrl => new("https://example.test/");
        public int Calls { get; private set; }

        public override Task<Book> Get(Uri url)
        {
            Calls++;
            return Task.FromResult(new Book(url) { Title = "Карточка существующего загрузчика" });
        }
    }

    private sealed class Backend
    {
        public static readonly byte[] CoverBytes = [137, 80, 78, 71, 13, 10, 26, 10, 1];
        public List<string> Requests { get; } = [];
        public HttpStatusCode TextStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode SeriesStatus { get; init; } = HttpStatusCode.OK;
        public HttpMessageHandler Handler => new AfAuditSupport.Handler(Send);

        private HttpResponseMessage Send(HttpRequestMessage request)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/book/get/123":
                    Requests.Add("metadata");
                    return AfAuditSupport.Json(new
                    {
                        title = "Книга", url = BookUrl.AbsoluteUri, author_name = "Автор", lang = "ru",
                        cover = "https://publiccdn.litnet.com/cover.png", cycle_priority = 2
                    });
                case "/cover.png":
                    Requests.Add("cover");
                    var image = new ByteArrayContent(CoverBytes);
                    image.Headers.ContentType = new("image/png");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = image };
                case "/v1/book/contents":
                    Requests.Add("toc");
                    return AfAuditSupport.Json(new[] { new { id = 1, title = "Глава 1", access = true } });
                case "/v1/book/text/get-chapters":
                    Requests.Add("text");
                    return LitnetArchiveTestSupport.Response([(1L, "[\"<p>Текст главы</p>\"]")], TextStatus);
                case "/ru/book/test-b123":
                    Requests.Add("series");
                    return new HttpResponseMessage(SeriesStatus)
                    {
                        Content = new StringContent("<div class='book-view-info-coll'><span class='meta-name'>Цикл: </span>" +
                            "<a href='/ru/author/novels?sort=cycles&amp;scroll-to=test'>Проверенный цикл</a></div>")
                    };
                default:
                    throw new InvalidOperationException("Неожиданный тестовый запрос");
            }
        }

    }
}
