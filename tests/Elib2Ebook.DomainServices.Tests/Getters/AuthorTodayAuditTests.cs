using System.Net;
using System.Web;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.AuthorToday;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class AuthorTodayAuditTests
{
    [Fact]
    public async Task All205ChaptersAreRequestedAndReturnedInTocOrder()
    {
        var batches = new List<long[]>();
        using var getter = Create(batches);
        using var book = await getter.Get(new Uri("https://author.today/work/123"));
        Assert.Equal(new[] { 100, 100, 5 }, batches.Select(b => b.Length));
        Assert.Equal(Enumerable.Range(1, 205).Select(i => $"Глава {i}"), book.Chapters.Select(c => c.Title));
    }

    [Fact]
    public async Task RequestedRangeIsAppliedBeforeSendingBatches()
    {
        var batches = new List<long[]>();
        using var getter = Create(batches, new Options { StartName = "Глава 99", EndName = "Глава 102" });
        using var book = await getter.Get(new Uri("https://author.today/work/123"));
        Assert.Equal(new long[] { 99, 100, 101, 102 }, Assert.Single(batches));
    }

    [Fact]
    public async Task MissingIdStopsInsteadOfSilentlyTruncatingBook()
    {
        using var getter = Create([], omit: true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://author.today/work/123")));
        Assert.Contains("не вернул 1 из 100", error.Message);
    }

    [Fact]
    public async Task ExplicitlyUnavailableChapterIsReported()
    {
        var logger = new AfAuditSupport.AuditLogger();
        using var getter = Create([], new Options { Start = 1, End = 1 }, logger: logger);
        using var book = await getter.Get(new Uri("https://author.today/work/123"));
        Assert.Contains("NotFound", Assert.Single(logger.Warnings));
        Assert.Single(book.Chapters);
    }

    private static AuthorTodayGetter Create(List<long[]> batches, Options options = null, bool omit = false, AfAuditSupport.AuditLogger logger = null)
    {
        var handler = new AfAuditSupport.Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/details"))
                return AfAuditSupport.Json(new { id = 123, title = "Книга", authorFIO = "Автор", authorUserName = "author",
                    chapters = Enumerable.Range(1, 205).Reverse().Select(i => new { id = i, title = $"Глава {i}", sortOrder = i }) });
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            var ids = query.AllKeys!.Where(k => k!.StartsWith("ids[")).Select(k => long.Parse(query[k]!)).ToArray();
            batches.Add(ids);
            return AfAuditSupport.Json(ids.Take(omit ? ids.Length - 1 : ids.Length).Reverse()
                .Select(id => new { id, title = $"Глава {id}", IsSuccessful = false, code = "NotFound" }));
        });
        return new AuthorTodayGetter(AfAuditSupport.Config(handler, options, logger));
    }
}
