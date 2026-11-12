using System.Web;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Boosty;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class BoostyAuditTests
{
    [Fact]
    public async Task CursorTraversesMoreThan100PostsAndReportsLockedPosts()
    {
        var offsets = new List<int>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var offset = int.Parse(query["offset"] ?? "0");
            offsets.Add(offset);
            return AfAuditSupport.Json(new
            {
                data = Enumerable.Range(offset * 10 + 1, 10).Select(i => new { id = new Guid(i, 0, 0, new byte[8]), title = $"Пост {i}", hasAccess = false }),
                extra = new { isLast = offset == 10, offset = (offset + 1).ToString() }
            });
        });
        var logger = new AfAuditSupport.AuditLogger();
        using var getter = new BoostyGetter(AfAuditSupport.Config(handler, logger: logger));
        var chapters = await AfAuditSupport.Invoke<IEnumerable<Chapter>>(getter, "FillChapters", new Uri("https://boosty.to/test"), "test");
        Assert.Equal(Enumerable.Range(0, 11), offsets);
        Assert.Equal(110, chapters.Count());
        Assert.Equal(110, logger.Warnings.Count);
    }

    [Fact]
    public async Task RepeatedCursorOrPostsStopInsteadOfLooping()
    {
        var id = Guid.NewGuid();
        var handler = new AfAuditSupport.Handler(_ => AfAuditSupport.Json(new
        {
            data = new[] { new { id, title = "Пост", hasAccess = false } },
            extra = new { isLast = false, offset = "same" }
        }));
        using var getter = new BoostyGetter(AfAuditSupport.Config(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AfAuditSupport.Invoke<IEnumerable<Chapter>>(getter, "FillChapters", new Uri("https://boosty.to/test"), "test"));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task PreviewDoesNotFetchEntireBlog()
    {
        var handler = new AfAuditSupport.Handler(_ => throw new InvalidOperationException("Предпросмотр не должен скачивать блог"));
        using var getter = new BoostyGetter(AfAuditSupport.Config(handler, new Options { NoChapters = true }));
        Assert.Empty(await AfAuditSupport.Invoke<IEnumerable<Chapter>>(getter, "FillChapters", new Uri("https://boosty.to/test"), "test"));
        Assert.Equal(0, handler.Calls);
    }
}
