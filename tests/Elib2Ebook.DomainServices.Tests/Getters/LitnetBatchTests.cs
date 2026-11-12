using System.Net;
using System.Text.Json;
using System.Web;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Litnet.Getters;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitnetBatchTests
{
    private static readonly Uri BookUri = new("https://litnet.com/ru/book/test-b123");

    [Fact]
    public async Task RequestsAll205ChaptersInBoundedBatchesAndKeepsTocOrder()
    {
        var handler = new FakeHandler();
        using var getter = CreateGetter(new Options(), handler);
        using var book = await getter.Get(BookUri);

        Assert.Equal(new[] { 100, 100, 5 }, handler.Batches.Select(batch => batch.Length));
        Assert.Equal(Enumerable.Range(1, 205).Select(i => (long)i), handler.Batches.SelectMany(batch => batch));
        Assert.Equal(Enumerable.Range(1, 205).Select(i => $"Глава {i}"), book.Chapters.Select(chapter => chapter.Title));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedRangeContainsOnlySelectedChapters(bool byName)
    {
        var handler = new FakeHandler();
        var options = byName
            ? new Options { StartName = "Глава 99", EndName = "Глава 102" }
            : new Options { Start = 99, End = 102 };
        using var getter = CreateGetter(options, handler);
        using var book = await getter.Get(BookUri);

        Assert.Equal(new long[] { 99, 100, 101, 102 }, Assert.Single(handler.Batches));
        Assert.Equal(new[] { "Глава 99", "Глава 100", "Глава 101", "Глава 102" }, book.Chapters.Select(chapter => chapter.Title));
    }

    [Fact]
    public async Task MissingChapterIdStopsDownloadInsteadOfCreatingEmptyChapter()
    {
        var handler = new FakeHandler { OmitLastChapter = true };
        using var getter = CreateGetter(new Options(), handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(BookUri));
        Assert.Contains("не вернул 1 из 100", error.Message);
        Assert.Single(handler.Batches);
    }

    [Fact]
    public async Task PreviewDoesNotRequestContentsOrChapterBatches()
    {
        var handler = new FakeHandler();
        using var getter = CreateGetter(new Options { NoChapters = true }, handler);
        using var book = await getter.Get(BookUri);

        Assert.Empty(book.Chapters);
        Assert.Empty(handler.Batches);
        Assert.Equal(0, handler.ContentsRequests);
    }

    private static LitnetGetter CreateGetter(Options options, HttpMessageHandler handler)
        => new(new BookGetterConfig(options, new HttpClient(handler), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance));

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<long[]> Batches { get; } = [];
        public int ContentsRequests { get; private set; }
        public bool OmitLastChapter { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            object result;
            switch (uri.AbsolutePath)
            {
                case "/v1/book/get/123":
                    result = new { title = "Книга", url = BookUri.ToString(), author_name = "Автор", lang = "ru" };
                    break;
                case "/v1/book/contents":
                    ContentsRequests++;
                    result = Enumerable.Range(1, 205).Select(i => new { id = i, title = $"Глава {i}" });
                    break;
                case "/v1/book/get-chapters-texts/":
                    var ids = HttpUtility.ParseQueryString(uri.Query).GetValues("chapter_ids[]")!.Select(long.Parse).ToArray();
                    Batches.Add(ids);
                    // Сервер может вернуть главы в другом порядке и обрезать большой запрос.
                    result = ids.Take(OmitLastChapter ? ids.Length - 1 : 100).Reverse().Select(id => new { id, text = (string)null });
                    break;
                case "/ru/book/test-b123":
                    result = new { };
                    break;
                default:
                    throw new InvalidOperationException($"Неожиданный путь {uri.AbsolutePath}");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(result)), RequestMessage = request
            });
        }
    }
}
