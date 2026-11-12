using System.Net;
using System.Reflection;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.ExternalServices.Bookstab;
using Elib2Ebook.ExternalServices.BookRiver;
using Elib2Ebook.ExternalServices.DarkNovels;
using Elib2Ebook.ExternalServices.Freedom.Getters;
using HtmlAgilityPack;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class HttpChapterAuditTests
{
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task BookstabAccessFailureStopsAfterOneRequest(HttpStatusCode status)
    {
        var handler = new AfAuditSupport.Handler(_ => AfAuditSupport.Json(new { }, status));
        using var getter = new BookstabGetter(AfAuditSupport.Config(handler));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => AfAuditSupport.Invoke<HtmlDocument>(getter, "GetChapter", 1, "123"));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BookriverForbiddenChapterDoesNotReturnEmptySuccess()
    {
        using var getter = new BookriverGetter(AfAuditSupport.Config(new AfAuditSupport.Handler(_ => AfAuditSupport.Json(new { }, HttpStatusCode.Forbidden))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => AfAuditSupport.Invoke<HtmlDocument>(getter, "GetChapter", 1L));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task DarkNovelsBadRequestDoesNotReturnEmptySuccess()
    {
        using var getter = new DarkNovelsGetter(AfAuditSupport.Config(new AfAuditSupport.Handler(_ => AfAuditSupport.Json(new { }, HttpStatusCode.BadRequest))));
        var chapterType = typeof(DarkNovelsGetter).Assembly.GetType("Elib2Ebook.ExternalServices.DarkNovels.Types.DarkNovelsChapter")!;
        var chapter = Activator.CreateInstance(chapterType)!;
        chapterType.GetProperty("Id")!.SetValue(chapter, 1);
        var task = (Task)typeof(DarkNovelsGetter).GetMethod("FillChapter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(getter, ["123", chapter, new Chapter()])!;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => task);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
    }

    [Fact]
    public async Task FreedomPurchaseRequirementIsReported()
    {
        var logger = new AfAuditSupport.AuditLogger();
        using var getter = new FreedomGetter(AfAuditSupport.Config(new AfAuditSupport.Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("<div class='entry-content'><div class='single-notice'>Для чтения купите главу.</div></div>") }), logger: logger));
        var method = typeof(FreedomGetterBase).GetMethod("GetChapter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = await (Task<HtmlDocument>)method.Invoke(getter, [new Uri("https://ifreedom.su/chapter/1")])!;
        Assert.Null(result);
        Assert.Contains("требует покупки", Assert.Single(logger.Warnings));
    }
}
