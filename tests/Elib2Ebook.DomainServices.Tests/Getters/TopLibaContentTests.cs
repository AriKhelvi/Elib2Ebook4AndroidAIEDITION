using System.Net;
using System.Net.Http.Headers;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.TopLiba.Getters;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class TopLibaContentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovesOnlyExtractedTitleAndPreservesInternalHeadingsAndImage(bool mirror)
    {
        const string content = "<h1 class='capter-title'>Глава 1</h1><p>Текст.</p>"
            + "<h1 id='part'>Часть вторая</h1><h2>Авторский подзаголовок</h2>"
            + "<h1 class='capter-title'>Заголовок внутри главы</h1><img src='/image.png'>";
        using var getter = CreateGetter(content, mirror);
        using var book = await getter.Get(BookUrl(mirror));

        var chapter = Assert.Single(book.Chapters);
        var doc = chapter.Content.AsHtmlDoc();
        Assert.Equal("Глава 1", chapter.Title);
        Assert.DoesNotContain(">Глава 1</h1>", chapter.Content);
        Assert.Equal(2, doc.QuerySelectorAll("h1").Count());
        Assert.Equal("Часть вторая", doc.GetTextBySelector("h1#part"));
        Assert.Equal("Авторский подзаголовок", doc.GetTextBySelector("h2"));
        Assert.Equal("Заголовок внутри главы", doc.GetTextBySelector("h1.capter-title"));
        Assert.Single(chapter.Images);
        Assert.Single(doc.QuerySelectorAll("img"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesAnchorsFromExtractedTitle(bool mirror)
    {
        const string content = "<h1 class='capter-title' id='chapter'>"
            + "<a name='old-chapter'></a><span id='nested'>Глава 1</span></h1>"
            + "<p><a href='#chapter'>К началу</a><a href='#old-chapter'>Старый якорь</a>"
            + "<a href='#nested'>Вложенный якорь</a></p>";
        using var getter = CreateGetter(content, mirror);
        using var book = await getter.Get(BookUrl(mirror));

        var chapter = Assert.Single(book.Chapters);
        var doc = chapter.Content.AsHtmlDoc();
        Assert.Equal("Глава 1", chapter.Title);
        Assert.Empty(doc.QuerySelectorAll("h1"));
        Assert.NotNull(doc.QuerySelector("[id='chapter']"));
        Assert.NotNull(doc.QuerySelector("a[name='old-chapter']"));
        Assert.NotNull(doc.QuerySelector("[id='nested']"));
        Assert.Equal(3, doc.QuerySelectorAll("a[href]").Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsAllHeadingsWhenSiteTitleIsMissing(bool mirror)
    {
        const string content = "<h1>Книга</h1><p>Текст.</p><h1>Часть вторая</h1><h3>Подраздел</h3>";
        using var getter = CreateGetter(content, mirror);
        using var book = await getter.Get(BookUrl(mirror));

        var chapter = Assert.Single(book.Chapters);
        Assert.Equal("Книга", chapter.Title);
        Assert.Equal(content, chapter.Content);
    }

    private static Uri BookUrl(bool mirror) => new($"https://{(mirror ? "bigliba" : "topliba")}.com/books/7");

    private static TopLibaGetterBase CreateGetter(string content, bool mirror)
    {
        var config = new BookGetterConfig(new Options(), new HttpClient(new FakeHandler(content)),
            new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        return mirror ? new BiglibaGetter(config) : new TopLibaGetter(config);
    }

    private sealed class FakeHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/image.png")
            {
                var image = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
                image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request, image));
            }

            var response = request.RequestUri.AbsolutePath switch
            {
                "/books/7" => "<meta name='_token' content='test'><h1 itemprop='name'>Книга</h1>"
                    + "<h2 itemprop='author'><a href='/authors/1'>Автор</a></h2>",
                "/reader/7" => "<script>var reader = { capters: [\"1\"] };</script>",
                "/reader/7/chapter" when request.Method == HttpMethod.Post => content,
                _ => throw new InvalidOperationException($"Неожиданный запрос: {request.Method} {request.RequestUri}")
            };
            return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request, new StringContent(response)));
        }
    }
}
