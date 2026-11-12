using System.Net;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.LitLife;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitLifeContentTests
{
    [Fact]
    public async Task RemovesRepeatedPageTitleButPreservesInternalSubheadings()
    {
        using var getter = CreateGetter(
            "<div id='read_online_ad'>Реклама</div><h4>Глава 1</h4><p>Начало.</p><h4>Первая встреча</h4><p>Продолжение.</p>",
            "<h4>Глава 1</h4><p>Вторая страница.</p><h4>Глава 1</h4><p>Внутренний заголовок.</p>");

        using var book = await getter.Get(new Uri("https://litlife.club/books/123"));
        var chapter = Assert.Single(book.Chapters);

        Assert.Equal("Глава 1", chapter.Title);
        Assert.Equal("<p>Начало.</p><h4>Первая встреча</h4><p>Продолжение.</p><p>Вторая страница.</p><h4>Глава 1</h4><p>Внутренний заголовок.</p>", chapter.Content);
    }

    [Theory]
    [InlineData("<h4>Другой заголовок</h4><p>Текст.</p>")]
    [InlineData("<p>Глава 1</p><p>Авторский абзац.</p><h4>Подзаголовок</h4>")]
    [InlineData("<p>Эпиграф.</p><h4>Глава 1</h4><p>Текст.</p>")]
    public async Task PreservesNonmatchingHeadingsAndOrdinaryTitleParagraphs(string content)
    {
        using var getter = CreateGetter(content);
        using var book = await getter.Get(new Uri("https://litlife.club/books/123"));

        Assert.Equal(content, Assert.Single(book.Chapters).Content);
    }

    private static LitLifeGetter CreateGetter(params string[] pages)
    {
        var config = new BookGetterConfig(new Options(), new HttpClient(new FakeHandler(pages)),
            new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        return new LitLifeGetter(config);
    }

    private sealed class FakeHandler(string[] pages) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            string content;
            if (url.AbsolutePath == "/books/123")
            {
                content = "<div class='card-header'><h2 itemprop='name'>Книга</h2></div>" +
                          "<div class='card-body'><h3 itemprop='author'><a class='author' href='/authors/1'>Автор</a></h3></div>";
            }
            else if (url.AbsolutePath == "/books/123/sections/list_go_to")
            {
                content = "<ul><li><a href='/books/123/read/1'>Глава 1</a></li></ul>";
            }
            else
            {
                Assert.Equal("/books/123/read/1", url.AbsolutePath);
                var page = string.IsNullOrEmpty(url.Query) ? 1 : int.Parse(url.Query.Split('=')[1]);
                content = "<div class='book_text'>" + pages[page - 1] + "</div>";
                if (pages.Length > 1)
                {
                    content += $"<div class='card-body'><ul class='pagination'><li><a class='page-link'>{pages.Length}</a></li></ul></div>";
                }
            }

            return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request, new StringContent(content)));
        }
    }
}
