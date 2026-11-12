using System.Net;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Libbox;
using Elib2Ebook.ExternalServices.Libking;
using Elib2Ebook.ExternalServices.Libst;
using Elib2Ebook.ExternalServices.Litgorod;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LibPaginationAuditTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(125)]
    public async Task LibboxReadsSinglePageAndExpandingArrowPaginator(int pages)
    {
        var read = new List<int>();
        using var getter = new LibboxGetter(Config(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/book/1")
                return Html("<h1 class='product__title'>Книга</h1><div class='author'><a href='/author/a'>Автор</a></div>");
            var page = request.RequestUri.Query.Length == 0 ? 1 : int.Parse(request.RequestUri.Query.Split('=')[1]);
            if (request.RequestUri.Query.Length > 0) read.Add(page);
            // Каждая страница открывает только следующий номер; текст ссылки — стрелка.
            var next = page < pages ? $"<ul class='pagination'><a class='page-numbers' href='?page_book={page + 1}'>→</a></ul>" : "";
            return Html($"<div class='entry-content'><p>Текст страницы {page}.</p></div>{next}");
        }));
        using var book = await getter.Get(new Uri("https://libbox.ru/book/1"));
        Assert.Equal(Enumerable.Range(1, pages), read);
        Assert.Contains($"Текст страницы {pages}.", Assert.Single(book.Chapters).Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task LibstDoesNotTreatHttpFailureAsEndOfChapter(HttpStatusCode failure)
    {
        using var getter = new LibstGetter(Config(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("BookView")) return Html(LibstCard);
            return request.RequestUri.Query.EndsWith("page=1") ? Json("[\"Первая часть\"]") : new HttpResponseMessage(failure);
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(new Uri("https://libst.ru/Detail/BookView/1")));
        Assert.Equal(failure, error.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.NoContent, "")]
    [InlineData(HttpStatusCode.OK, "[]")]
    public async Task LibstStopsAtExplicitEndAfterText(HttpStatusCode end, string body)
    {
        var requests = 0;
        using var getter = new LibstGetter(Config(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("BookView")) return Html(LibstCard);
            requests++;
            return requests == 1 ? Json("[\"Текст главы\"]") : new HttpResponseMessage(end) { Content = new StringContent(body) };
        }));
        using var book = await getter.Get(new Uri("https://libst.ru/Detail/BookView/1"));
        Assert.Equal(2, requests);
        Assert.Contains("Текст главы", Assert.Single(book.Chapters).Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.OK)]
    public async Task LitgorodCannotSilentlyDropBlockedOrChangedChapter(HttpStatusCode status)
    {
        using var getter = new LitgorodGetter(Config(request =>
            request.RequestUri!.AbsolutePath.StartsWith("/reader/")
                ? new HttpResponseMessage(status) { Content = new StringContent("<html>Нет текста главы</html>") }
                : Html("<div class='b-book_item__name'><h1>Книга</h1></div><div class='b-book_item__author'><a href='/author/1'>Автор</a></div>" +
                    "<div class='b-tab__content'><ul class='list-unstyled'><li><a href='/reader/1/1'>Глава</a></li></ul></div>")));
        if (status == HttpStatusCode.OK)
            await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://litgorod.ru/books/view/1")));
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(new Uri("https://litgorod.ru/books/view/1")));
    }

    [Fact]
    public async Task LibkingRejectsNextPageCycleInsteadOfRepeatingBookForever()
    {
        var requests = 0;
        using var getter = new LibkingGetter(Config(_ =>
        {
            requests++;
            return Html("<div itemprop='name'>Книга</div><div id='book'><p>Текст</p></div><a class='load_text' href='/book#next'>Далее</a>");
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://libking.ru/book")));
        Assert.Contains("повторяет", error.Message);
        Assert.Equal(2, requests); // Карточка и первая страница; повтор не отправляется.
    }

    private const string LibstCard = "<h1>Книга</h1><h2 class='author_name'><a href='/author/1'>Автор</a></h2>" +
        "<span class='mt-action-author' onclick='ChapID=123'>Глава 1</span>";

    private static BookGetterConfig Config(Func<HttpRequestMessage, HttpResponseMessage> response)
        => new(new Options { NoImage = true }, new HttpClient(new FakeHandler(response)), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage Json(string body) => Html(body);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var result = response(request);
            result.RequestMessage = request;
            return Task.FromResult(result);
        }
    }
}
