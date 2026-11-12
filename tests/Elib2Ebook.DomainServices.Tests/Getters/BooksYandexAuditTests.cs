using System.Net;
using System.Web;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.BooksYandex.Getters;
using Elib2Ebook.ExternalServices.BooksYandex.Types;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class BooksYandexAuditTests
{
    [Fact]
    public async Task SerialReadsAllEpisodePagesAndReportsUnavailableEpisodes()
    {
        var pages = new List<int>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            var page = int.Parse(HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!);
            pages.Add(page);
            return AfAuditSupport.Json(new { episodes = Enumerable.Range(1, 205).Skip((page - 1) * 100).Take(100)
                .Select(i => new { uuid = i.ToString(), title = $"Эпизод {i}", can_be_read = false }) });
        });
        var logger = new AfAuditSupport.AuditLogger();
        using var getter = new TestGetter(AfAuditSupport.Config(handler, logger: logger));
        using var book = new Book(new Uri("https://books.yandex.ru/serials/book"));
        Assert.Empty(await getter.Chapters(book, Serial()));
        Assert.Equal(new[] { 1, 2, 3, 4 }, pages);
        Assert.Equal(205, logger.Warnings.Count);
    }

    [Fact]
    public async Task RepeatedEpisodePageStopsInsteadOfLooping()
    {
        var handler = new AfAuditSupport.Handler(_ => AfAuditSupport.Json(new
        { episodes = new[] { new { uuid = "episode", title = "Эпизод", can_be_read = false } } }));
        using var getter = new TestGetter(AfAuditSupport.Config(handler));
        using var book = new Book(new Uri("https://books.yandex.ru/serials/book"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Chapters(book, Serial()));
        Assert.Contains("повторили страницу", error.Message);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task EpisodeDownloadFailureStopsInsteadOfBeingSkipped()
    {
        var handler = new AfAuditSupport.Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/episodes")
            ? AfAuditSupport.Json(new { episodes = HttpUtility.ParseQueryString(request.RequestUri.Query)["page"] == "1"
                ? new[] { new { uuid = "episode", title = "Эпизод", can_be_read = true } }
                : [] })
            : AfAuditSupport.Json(new { }, HttpStatusCode.Forbidden));
        using var getter = new TestGetter(AfAuditSupport.Config(handler));
        using var book = new Book(new Uri("https://books.yandex.ru/serials/book"));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Chapters(book, Serial()));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task PreviewDoesNotFetchFullEpubOrEpisodes()
    {
        var handler = new AfAuditSupport.Handler(_ => throw new InvalidOperationException("Предпросмотр не должен загружать главы"));
        using var getter = new TestGetter(AfAuditSupport.Config(handler, new Options { NoChapters = true }));
        using var book = new Book(new Uri("https://books.yandex.ru/serials/book"));
        Assert.Empty(await getter.Chapters(book, Serial()));
        Assert.Equal(0, handler.Calls);
    }

    private static BooksYandexResponse Serial() => new() { Book = new BooksYandexBook { UUID = "book", SourceType = "serial" } };

    private sealed class TestGetter(BookGetterConfig config) : BooksYandexBooksGetter(config)
    {
        public Task<IEnumerable<Chapter>> Chapters(Book book, BooksYandexResponse response) => FillChapters(book, response);
    }
}
