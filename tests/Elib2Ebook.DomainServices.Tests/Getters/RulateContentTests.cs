using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Rulate.Getters;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class RulateContentTests
{
    private const string ChapterUrl = "https://tl.rulate.ru/book/129557/123";

    [Theory]
    [InlineData("<p>http://tl.rulate.ru/book/129557/123</p>")]
    [InlineData("<p>https://tl.rulate.ru/book/129557/123</p>")]
    [InlineData("<p> https://tl.rulate.ru/book/129557/123 </p>\n<!-- конец --> <p>&nbsp;<br></p><br>")]
    [InlineData("<p><a href='https://tl.rulate.ru/book/129557/123'>https://tl.rulate.ru/book/129557/123</a></p>")]
    public async Task RemovesOnlyStandaloneTrailingLinkToDownloadedChapter(string tail)
    {
        using var getter = CreateGetter("<p>Текст главы.</p>" + tail);
        using var book = await getter.Get(new Uri("https://tl.rulate.ru/book/129557"));

        var chapter = Assert.Single(book.Chapters);
        Assert.Contains("<p>Текст главы.</p>", chapter.Content);
        Assert.DoesNotContain("/book/129557/123", chapter.Content);
    }

    [Theory]
    [InlineData("<p>https://tl.rulate.ru/book/129557/123</p><p>Продолжение главы.</p>")]
    [InlineData("<p>Источник: https://tl.rulate.ru/book/129557/123</p>")]
    [InlineData("<p>https://tl.rulate.ru/book/129557/124</p>")]
    [InlineData("<p>https://tl.rulate.ru/book/999/123</p>")]
    [InlineData("<p>https://other.example/book/129557/123</p>")]
    [InlineData("<p>https://tl.rulate.ru.evil.example/book/129557/123</p>")]
    [InlineData("<p>https://tl.rulate.ru:8443/book/129557/123</p>")]
    [InlineData("<p>https://user@tl.rulate.ru/book/129557/123</p>")]
    [InlineData("<p>https://tl.rulate.ru/book/129557/123?source=author</p>")]
    [InlineData("<p>https://tl.rulate.ru/book/129557/123#notes</p>")]
    [InlineData("<p><span>https://tl.rulate.ru/book/129557/123</span></p>")]
    [InlineData("<p><a href='https://other.example/'>https://tl.rulate.ru/book/129557/123</a></p>")]
    [InlineData("<p><a href='https://tl.rulate.ru/book/129557/123'>Ссылка автора</a></p>")]
    [InlineData("<div><p>https://tl.rulate.ru/book/129557/123</p></div>")]
    public async Task PreservesAuthorLinksAndNonmatchingContent(string tail)
    {
        var body = "<p>Текст главы.</p>" + tail;
        using var getter = CreateGetter(body);
        using var book = await getter.Get(new Uri("https://tl.rulate.ru/book/129557"));

        Assert.Equal(body, Assert.Single(book.Chapters).Content);
    }

    [Theory]
    [InlineData("<p>https://tl.rulate.ru/book/129557/123<img src='https://tl.rulate.ru/image.png'></p>")]
    [InlineData("<p>https://tl.rulate.ru/book/129557/123</p><p><img src='https://tl.rulate.ru/image.png'></p>")]
    public async Task PreservesLinkWhenParagraphContainsOrPrecedesImage(string tail)
    {
        using var getter = CreateGetter("<p>Текст главы.</p>" + tail);
        using var book = await getter.Get(new Uri("https://tl.rulate.ru/book/129557"));

        var chapter = Assert.Single(book.Chapters);
        Assert.Contains(ChapterUrl, chapter.Content);
        Assert.Single(chapter.Images);
        Assert.Contains("<img", chapter.Content);
    }

    [Fact]
    public async Task UsesActualGetterHostForMirror()
    {
        using var getter = CreateGetter("<p>Текст главы.</p><p>http://bllate.org/book/129557/123</p>", mirror: true);
        using var book = await getter.Get(new Uri("https://bllate.org/book/129557"));

        Assert.Equal("<p>Текст главы.</p>", Assert.Single(book.Chapters).Content);
    }

    private static RulateGetterBase CreateGetter(string content, bool mirror = false)
    {
        var config = new BookGetterConfig(new Options(), new HttpClient(new FakeHandler(content)),
            new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        return mirror ? new BllateGetter(config) : new RulateGetter(config);
    }

    [Theory]
    [InlineData("tl.rulate.ru")]
    [InlineData("mlate.ru")]
    [InlineData("erolate.com")]
    public async Task MirrorRemovesExactChapterFooterFromKnownSiblingDomain(string footerHost)
    {
        using var getter = CreateGetter($"<p>Текст главы.</p><p>http://{footerHost}/book/129557/123</p>", mirror: true);
        using var book = await getter.Get(new Uri("https://bllate.org/book/129557"));
        Assert.Equal("<p>Текст главы.</p>", Assert.Single(book.Chapters).Content);
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

            var response = request.RequestUri.AbsolutePath.EndsWith("/readyajax")
                ? JsonSerializer.Serialize(new { can_read = true, content = "<div class='content-text'>" + content + "</div>" })
                : "<h1>Книга</h1><div id='Chapters'><table><tr data-id='123'><td class='t'>Глава 1</td></tr></table></div>";
            return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request, new StringContent(response)));
        }
    }
}
