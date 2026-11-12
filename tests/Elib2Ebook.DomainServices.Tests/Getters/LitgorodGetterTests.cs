using System.Net;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Litgorod;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitgorodGetterTests
{
    [Fact]
    public async Task PreviewThenDownloadAndAnotherBookReplaceReferer()
    {
        var handler = new FakeHandler();
        var options = new Options { NoChapters = true };
        var config = new BookGetterConfig(options, new HttpClient(handler), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        using var getter = new LitgorodGetter(config);
        var bookUri = new Uri("https://litgorod.ru/books/view/64003");

        using var preview = await getter.Get(bookUri);
        using var repeatedPreview = await getter.Get(bookUri);
        options.NoChapters = false;
        using var book = await getter.Get(bookUri);
        options.NoChapters = true;
        using var another = await getter.Get(new Uri("https://litgorod.ru/books/view/64004"));

        Assert.Empty(preview.Chapters);
        Assert.Empty(repeatedPreview.Chapters);
        Assert.Single(book.Chapters);
        Assert.Contains("Текст главы", book.Chapters.Single().Content);
        Assert.Equal(new[] { bookUri.ToString(), bookUri.ToString(), bookUri.ToString(), bookUri.ToString(), "https://litgorod.ru/books/view/64004" }, handler.Referers);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> Referers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Referers.Add(Assert.Single(request.Headers.GetValues("Referer")));
            var chapter = WebUtility.HtmlEncode(JsonSerializer.Serialize(new { explodedParagraphs = new[] { "<p>Текст главы</p>" } }));
            var content = request.RequestUri!.AbsolutePath.StartsWith("/reader/")
                ? $"<book-reader :current_chapter=\"{chapter}\"></book-reader>"
                : "<div class='b-book_item__name'><h1>Книга</h1></div>" +
                  "<div class='b-book_item__author'><a href='/authors/1'>Автор</a></div>" +
                  "<div class='b-tab__content'><ul class='list-unstyled'><li><a href='/reader/64003/1'>Глава 1</a></li></ul></div>";
            return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request, new StringContent(content)));
        }
    }
}
