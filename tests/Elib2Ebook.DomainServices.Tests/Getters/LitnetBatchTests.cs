using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Litnet.Getters;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitnetBatchTests
{
    private static readonly Uri BookUri = new("https://litnet.com/ru/book/test-b123");

    [Fact]
    public async Task AnonymousAdultBookReportsTypedAccessRestrictionBeforeRequestingChapters()
    {
        var handler = new FakeHandler { AdultOnly = true };
        using var getter = CreateGetter(new Options(), handler);
        var error = await Assert.ThrowsAsync<SiteAccessException>(() => getter.Get(BookUri));
        Assert.Equal(SiteAccessReason.AgeConfirmationRequired, error.Reason);
        Assert.Empty(handler.Batches);
        Assert.Equal(0, handler.ContentsRequests);
    }

    [Fact]
    public async Task RequestsAll205ChaptersInBoundedBatchesAndKeepsTocOrder()
    {
        var handler = new FakeHandler();
        using var getter = CreateGetter(new Options(), handler);
        using var book = await getter.Get(BookUri);

        Assert.Equal(new[] { 100, 100, 5 }, handler.Batches.Select(batch => batch.Length));
        Assert.Equal(Enumerable.Range(1, 205).Select(i => (long)i), handler.Batches.SelectMany(batch => batch));
        Assert.Equal(Enumerable.Range(1, 205).Select(i => $"Глава {i}"), book.Chapters.Select(chapter => chapter.Title));
        Assert.All(book.Chapters, chapter => Assert.Equal("<p>Текст главы</p>", chapter.Content));
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(40, 1)]
    [InlineData(100, 1)]
    [InlineData(101, 2)]
    [InlineData(205, 3)]
    public async Task PreviewAndFullDownloadStayWithinRequestBudget(int chapters, int textRequests)
    {
        var options = new Options { NoChapters = true };
        var handler = new FakeHandler { ChapterCount = chapters, IncludeCover = true, Access = _ => true };
        using var getter = CreateGetter(options, handler);
        await getter.Init();
        using (var preview = await getter.Get(BookUri)) { }
        options.NoChapters = false;

        using var book = await getter.Get(BookUri);

        Assert.Equal(chapters, book.Chapters.Count());
        Assert.Equal(textRequests, handler.Batches.Count);
        Assert.Equal(1, handler.MetadataRequests);
        Assert.Equal(1, handler.CoverRequests);
        Assert.Equal(1, handler.SeriesRequests);
        Assert.Equal(1, handler.ContentsRequests);
        // Считаются все фактические HTTP-запросы. Authorize в этом тесте не вызывается.
        Assert.Equal(4 + textRequests, handler.Versions.Count);
    }

    [Fact]
    public async Task RateLimitOnSecondBatchDoesNotRequestThirdOrReturnPartialBook()
    {
        var handler = new FakeHandler { RateLimitBatchNumber = 2 };
        using var getter = CreateGetter(new Options(), handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUri));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(new[] { 100, 100 }, handler.Batches.Select(batch => batch.Length));
        Assert.Equal(0, handler.SeriesRequests);
    }

    [Fact]
    public async Task RateLimitedCoverStopsBeforeContentsAndText()
    {
        var handler = new FakeHandler { IncludeCover = true, CoverStatus = HttpStatusCode.TooManyRequests };
        using var getter = CreateGetter(new Options(), handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUri));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(1, handler.CoverRequests);
        Assert.Equal(0, handler.ContentsRequests);
        Assert.Equal(0, handler.SeriesRequests);
        Assert.Empty(handler.Batches);
    }

    [Fact]
    public async Task ExplicitlyClosedChaptersAreNotRequestedAndAvailableExcerptIsSaved()
    {
        var log = new ProgressLogger();
        var handler = new FakeHandler { ChapterCount = 40, Access = id => id <= 8 };
        using var getter = new LitnetGetter(new BookGetterConfig(new Options(), new HttpClient(handler),
            new CookieContainer(), TempFolderFactory.Create(null), log));

        using var book = await getter.Get(BookUri);

        Assert.Equal(Enumerable.Range(1, 8).Select(id => (long)id), Assert.Single(handler.Batches));
        Assert.Equal(8, book.Chapters.Count());
        Assert.Contains(log.Warnings, line => line.Contains("доступно 8 глав, закрыто 32") && line.Contains("фрагмент"));
    }

    [Fact]
    public async Task AccessFilterRunsAfterUserRangeAndKeepsOriginalChapterNumbers()
    {
        var handler = new FakeHandler { ChapterCount = 40, Access = id => id <= 8 || id == 12 };
        using var getter = CreateGetter(new Options { Start = 7, End = 12 }, handler);

        using var book = await getter.Get(BookUri);

        Assert.Equal(new long[] { 7, 8, 12 }, Assert.Single(handler.Batches));
        Assert.Equal(new[] { "Глава 7", "Глава 8", "Глава 12" }, book.Chapters.Select(chapter => chapter.Title));
    }

    [Fact]
    public async Task EntirelyClosedRangeReportsAccessErrorInsteadOfEmptyBook()
    {
        var handler = new FakeHandler { ChapterCount = 40, Access = id => id <= 8 };
        using var getter = CreateGetter(new Options { Start = 9 }, handler);

        var error = await Assert.ThrowsAsync<SiteAccessException>(() => getter.Get(BookUri));

        Assert.Equal(SiteAccessReason.AccessDenied, error.Reason);
        Assert.Empty(handler.Batches);
    }

    [Fact]
    public async Task PurchasedBookWithAccessToEveryChapterKeepsEverything()
    {
        var handler = new FakeHandler { ChapterCount = 40, Access = _ => true };
        using var getter = CreateGetter(new Options(), handler);

        using var book = await getter.Get(BookUri);

        Assert.Equal(40, book.Chapters.Count());
    }

    [Fact]
    public async Task RateLimitedTextRequestIsStillFailureRatherThanAnExcerpt()
    {
        var handler = new FakeHandler { ChapterCount = 40, Access = id => id <= 8, BatchStatus = HttpStatusCode.TooManyRequests };
        using var getter = CreateGetter(new Options(), handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUri));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Single(handler.Batches);
    }

    [Fact]
    public async Task PreviewAndFullBookShareNetworkMetadataButOwnIndependentCoverFiles()
    {
        var options = new Options { NoChapters = true };
        var handler = new FakeHandler { ChapterCount = 1, IncludeCover = true };
        using var getter = CreateGetter(options, handler);
        await getter.Init();
        string previewCover;
        using (var preview = await getter.Get(BookUri))
        {
            previewCover = preview.Cover.FilePath;
            Assert.True(File.Exists(previewCover));
        }
        Assert.False(File.Exists(previewCover));
        options.NoChapters = false;

        using var book = await getter.Get(BookUri);

        Assert.Single(book.Chapters);
        Assert.NotEqual(previewCover, book.Cover.FilePath);
        Assert.Equal(FakeHandler.CoverBytes, book.Cover.Content);
        Assert.Equal(1, handler.MetadataRequests);
        Assert.Equal(1, handler.SeriesRequests);
        Assert.Equal(1, handler.CoverRequests);
        Assert.All(handler.Versions, version => Assert.Equal(HttpVersion.Version11, version));
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

    [Fact]
    public async Task ShowsRequestProgressAndProcessesEachBatchBeforeRequestingTheNext()
    {
        var log = new ProgressLogger();
        var handler = new FakeHandler
        {
            ChapterCount = 102,
            BeforeBatch = number =>
            {
                Assert.Contains(log.Lines, line => line.StartsWith("Получаю текст глав Litnet"));
                Assert.Equal(number == 1 ? 0 : 100, log.Lines.Count(line => line.StartsWith("Загружаю главу")));
            }
        };
        using var getter = new LitnetGetter(new BookGetterConfig(new Options(), new HttpClient(handler),
            new CookieContainer(), TempFolderFactory.Create(null), log));

        using var book = await getter.Get(BookUri);

        Assert.Equal(102, book.Chapters.Count());
        Assert.Contains("Получаю текст глав Litnet 1–100 из 102", log.Lines);
        Assert.Contains("Получаю текст глав Litnet 101–102 из 102", log.Lines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad-encoding")]
    public async Task MissingOrMalformedTextStopsWithoutStartingTheNextBatch(string text)
    {
        var handler = new FakeHandler { BrokenText = text, BrokenChapter = 4 };
        using var getter = CreateGetter(new Options(), handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(BookUri));

        Assert.Contains("Глава 4", error.Message);
        Assert.Contains("Неполная книга не будет сохранена", error.Message);
        Assert.Single(handler.Batches);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[\"<p><br /></p>\"]")]
    public async Task EmptyOrMalformedDecryptedChapterIsNotReportedAsSuccess(string json)
    {
        var handler = new FakeHandler { BrokenChapter = 4, BrokenText = Encrypt(json) };
        using var getter = CreateGetter(new Options(), handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(BookUri));

        Assert.Contains("Неполная книга не будет сохранена", error.Message);
        Assert.Single(handler.Batches);
    }

    [Fact]
    public async Task EmptyContentsFailInsteadOfCreatingAnEmptyBook()
    {
        var handler = new FakeHandler { ChapterCount = 0 };
        using var getter = CreateGetter(new Options(), handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(BookUri));

        Assert.Contains("оглавление", error.Message);
        Assert.Empty(handler.Batches);
    }

    [Fact]
    public async Task CancellationAfterFirstBatchDoesNotRequestTheSecond()
    {
        using var cancellation = new CancellationTokenSource();
        var log = new ProgressLogger { OnLine = line => { if (line == "Загружаю главу \"Глава 100\"") cancellation.Cancel(); } };
        var handler = new FakeHandler();
        var config = new BookGetterConfig(new Options(), new HttpClient(handler), new CookieContainer(), TempFolderFactory.Create(null), log)
        { CancellationToken = cancellation.Token };
        using var getter = new LitnetGetter(config);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => getter.Get(BookUri));

        Assert.Single(handler.Batches);
    }

    private static string Encrypt(string json)
    {
        using var aes = Aes.Create();
        // Тест использует тот же публичный протокол API, не учётные данные пользователя.
        var key = (string)typeof(LitnetGetterBase).GetField("SECRET", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue();
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.IV = new byte[16];
        using var encryptor = aes.CreateEncryptor();
        var bytes = new byte[16].Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        return Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length));
    }

    private sealed class ProgressLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public List<string> Warnings { get; } = [];
        public Action<string> OnLine { get; init; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var line = formatter(state, exception);
            Lines.Add(line);
            if (level >= LogLevel.Warning) Warnings.Add(line);
            OnLine?.Invoke(line);
        }
    }

    private static LitnetGetter CreateGetter(Options options, HttpMessageHandler handler)
        => new(new BookGetterConfig(options, new HttpClient(handler), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance));

    private sealed class FakeHandler : HttpMessageHandler
    {
        public static readonly byte[] CoverBytes = [137, 80, 78, 71, 13, 10, 26, 10, 1];
        public List<long[]> Batches { get; } = [];
        public List<Version> Versions { get; } = [];
        public int ContentsRequests { get; private set; }
        public int MetadataRequests { get; private set; }
        public int SeriesRequests { get; private set; }
        public int CoverRequests { get; private set; }
        public bool IncludeCover { get; init; }
        public Func<int, bool?> Access { get; init; } = _ => null;
        public HttpStatusCode BatchStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode CoverStatus { get; init; } = HttpStatusCode.OK;
        public int? RateLimitBatchNumber { get; init; }
        public bool OmitLastChapter { get; init; }
        public int ChapterCount { get; init; } = 205;
        public bool AdultOnly { get; init; }
        public long? BrokenChapter { get; init; }
        public string BrokenText { get; init; }
        public Action<int> BeforeBatch { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Versions.Add(request.Version);
            object result;
            switch (uri.AbsolutePath)
            {
                case "/v1/book/get/123":
                    MetadataRequests++;
                    result = new { title = "Книга", url = BookUri.ToString(), author_name = "Автор", lang = "ru", adult_only = AdultOnly,
                        cover = IncludeCover ? "https://publiccdn.litnet.com/cover.png" : null };
                    break;
                case "/v1/book/contents":
                    ContentsRequests++;
                    result = Enumerable.Range(1, ChapterCount).Select(i => new { id = i, title = $"Глава {i}", access = Access(i) });
                    break;
                case "/v1/book/get-chapters-texts/":
                    var ids = HttpUtility.ParseQueryString(uri.Query).GetValues("chapter_ids[]")!.Select(long.Parse).ToArray();
                    Batches.Add(ids);
                    var status = Batches.Count == RateLimitBatchNumber ? HttpStatusCode.TooManyRequests : BatchStatus;
                    if (status != HttpStatusCode.OK)
                        return Task.FromResult(TestHttpResponse.Create(status, request, new StringContent("{}")));
                    BeforeBatch?.Invoke(Batches.Count);
                    // Сервер может вернуть главы в другом порядке и обрезать большой запрос.
                    result = ids.Take(OmitLastChapter ? ids.Length - 1 : 100).Reverse().Select(id => new
                    {
                        id, text = id == BrokenChapter ? BrokenText : Encrypt("[\"<p>Текст главы</p>\"]")
                    });
                    break;
                case "/ru/book/test-b123":
                    SeriesRequests++;
                    result = new { };
                    break;
                case "/cover.png":
                    Assert.Equal("publiccdn.litnet.com", uri.Host);
                    Assert.Equal("publiccdn.litnet.com", request.Headers.Host);
                    CoverRequests++;
                    if (CoverStatus != HttpStatusCode.OK)
                        return Task.FromResult(TestHttpResponse.Create(CoverStatus, request, new StringContent("{}")));
                    var image = new ByteArrayContent(CoverBytes);
                    image.Headers.ContentType = new("image/png");
                    return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request, image));
                default:
                    throw new InvalidOperationException($"Неожиданный путь {uri.AbsolutePath}");
            }

            return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request, new StringContent(JsonSerializer.Serialize(result))));
        }
    }
}
