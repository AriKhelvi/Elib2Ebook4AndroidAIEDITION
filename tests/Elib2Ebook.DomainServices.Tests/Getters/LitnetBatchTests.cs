using System.Net;
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

    [Theory]
    [InlineData(true, false, LogLevel.Information)]
    [InlineData(null, false, LogLevel.Information)]
    [InlineData(null, true, LogLevel.Information)]
    [InlineData(true, false, LogLevel.Debug)]
    [InlineData(null, true, LogLevel.Debug)]
    public async Task Incomplete25ChapterArchiveKeepsFailureAndReportsSafeAccessSummary(
        bool? access, bool omitAccessField, LogLevel minimumLevel)
    {
        const string token = "private-litnet-token";
        const string login = "private-litnet-login@example.test";
        const string password = "private-litnet-password";
        var providerCalls = 0;
        var options = new Options
        {
            Login = login,
            Password = password,
            LitnetSessionProvider = (_, _) =>
            {
                providerCalls++;
                return Task.FromResult(new BrowserAuthSession(token, "test-device"));
            }
        };
        var log = new ProgressLogger { MinimumLevel = minimumLevel };
        var handler = new FakeHandler
        {
            ChapterCount = 25, Access = _ => access, OmitAccessField = omitAccessField, ReturnedChapterLimit = 7
        };
        using var getter = new LitnetGetter(new BookGetterConfig(options, new HttpClient(handler),
            new CookieContainer(), TempFolderFactory.Create(null), log));
        await getter.Authorize();

        var error = await Assert.ThrowsAsync<LitnetIncompleteChapterArchiveException>(() => getter.Get(BookUri));

        Assert.Equal(25, error.RequestedCount);
        Assert.Equal(7, error.ReceivedCount);
        Assert.Equal(Enumerable.Range(8, 18).Select(id => (long)id), error.MissingChapterIds);
        Assert.Contains("Неполная книга не будет сохранена", error.Message);
        Assert.Equal(Enumerable.Range(1, 25).Select(id => (long)id), Assert.Single(handler.Batches));
        Assert.Equal(3, handler.Versions.Count); // Карточка, оглавление и один архив; повторов нет.
        Assert.Equal(1, providerCalls);
        Assert.Equal(0, handler.SeriesRequests);
        Assert.DoesNotContain(log.Lines, line => line.StartsWith("Загружаю главу"));
        var summary = Assert.Single(log.Lines, line => line.StartsWith("Litnet: неполный архив глав"));
        Assert.Contains("запрошено 25, получено 7, отсутствует 18", summary);
        Assert.Contains($"access=true — {(access == true ? 18 : 0)}", summary);
        Assert.Contains("access=false — 0", summary);
        Assert.Contains($"access=null (поле отсутствует или не задано) — {(access == null ? 18 : 0)}", summary);
        if (minimumLevel == LogLevel.Debug)
        {
            var details = Assert.Single(log.DebugLines, line => line.StartsWith("Litnet: отсутствующие главы"));
            Assert.Contains("показано 18 из 18", details);
            Assert.Contains($"позиция 8, ID 8, access={(access == true ? "true" : "null (поле отсутствует или не задано)")}", details);
        }
        else
        {
            Assert.Empty(log.DebugLines);
            Assert.DoesNotContain(log.Lines, line => line.Contains("ID 8"));
        }
        var diagnostics = string.Join("\n", log.Lines) + error;
        foreach (var privateValue in new[] { token, login, password, LitnetArchiveTestSupport.AppKey, "Текст главы" })
            Assert.DoesNotContain(privateValue, diagnostics);
    }

    [Fact]
    public async Task ExcerptOf25ChapterBookRequestsOnlySevenOpenChaptersAndReportsClosedRemainder()
    {
        var log = new ProgressLogger { MinimumLevel = LogLevel.Information };
        var handler = new FakeHandler { ChapterCount = 25, Access = id => id <= 7 };
        using var getter = new LitnetGetter(new BookGetterConfig(new Options(), new HttpClient(handler),
            new CookieContainer(), TempFolderFactory.Create(null), log));

        using var book = await getter.Get(BookUri);

        Assert.Equal(7, book.Chapters.Count());
        Assert.Equal(Enumerable.Range(1, 7).Select(id => (long)id), Assert.Single(handler.Batches));
        Assert.Contains(log.Warnings, line => line.Contains("доступно 7 глав, закрыто 18") && line.Contains("фрагмент"));
        Assert.DoesNotContain(log.Lines, line => line.Contains("неполный архив"));
    }

    [Theory]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Debug)]
    public async Task Complete25ChapterBookHasOneBatchAndAccessSummaryOnlyInDetailedLog(LogLevel minimumLevel)
    {
        var log = new ProgressLogger { MinimumLevel = minimumLevel };
        var handler = new FakeHandler { ChapterCount = 25, Access = _ => true };
        using var getter = new LitnetGetter(new BookGetterConfig(new Options(), new HttpClient(handler),
            new CookieContainer(), TempFolderFactory.Create(null), log));

        using var book = await getter.Get(BookUri);

        Assert.Equal(25, book.Chapters.Count());
        Assert.Equal(25, Assert.Single(handler.Batches).Length);
        Assert.Empty(log.Warnings);
        Assert.DoesNotContain(log.Lines, line => line.Contains("неполный архив"));
        if (minimumLevel == LogLevel.Debug)
            Assert.Contains(log.DebugLines, line => line.Contains("access=true — 25") && line.Contains("access=null (поле отсутствует или не задано) — 0"));
        else
            Assert.DoesNotContain(log.Lines, line => line.Contains("access="));
    }

    [Fact]
    public async Task MissingChapterDetailsKeepTocPositionsAfterRangeAndAccessFiltering()
    {
        var log = new ProgressLogger();
        var handler = new FakeHandler { ChapterCount = 25, Access = id => id <= 8 || id == 12, ReturnedChapterLimit = 1 };
        using var getter = new LitnetGetter(new BookGetterConfig(new Options { Start = 7, End = 12 },
            new HttpClient(handler), new CookieContainer(), TempFolderFactory.Create(null), log));

        var error = await Assert.ThrowsAsync<LitnetIncompleteChapterArchiveException>(() => getter.Get(BookUri));

        Assert.Equal(new long[] { 8, 12 }, error.MissingChapterIds);
        var details = Assert.Single(log.DebugLines, line => line.StartsWith("Litnet: отсутствующие главы"));
        Assert.Contains("позиция 8, ID 8", details);
        Assert.Contains("позиция 12, ID 12", details);
        Assert.DoesNotContain("позиция 2,", details);
        Assert.DoesNotContain("позиция 3,", details);
        Assert.Single(handler.Batches);
    }

    [Fact]
    public async Task MissingChapterDetailsAreBoundedAndDoNotStartNextBatch()
    {
        var log = new ProgressLogger();
        var handler = new FakeHandler { ChapterCount = 205, Access = _ => true, ReturnedChapterLimit = 0 };
        using var getter = new LitnetGetter(new BookGetterConfig(new Options { Start = 99 }, new HttpClient(handler),
            new CookieContainer(), TempFolderFactory.Create(null), log));

        var error = await Assert.ThrowsAsync<LitnetIncompleteChapterArchiveException>(() => getter.Get(BookUri));

        Assert.Equal(100, error.MissingChapterIds.Count);
        var details = Assert.Single(log.DebugLines, line => line.StartsWith("Litnet: отсутствующие главы"));
        Assert.Contains("показано 50 из 100", details);
        Assert.Equal(50, details.Split("ID ").Length - 1);
        Assert.Contains("позиция 99, ID 99", details);
        Assert.Contains("позиция 148, ID 148", details);
        Assert.DoesNotContain("ID 149", details);
        Assert.Single(handler.Batches);
        Assert.Equal(0, handler.SeriesRequests);
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

        var error = await Assert.ThrowsAsync<LitnetIncompleteChapterArchiveException>(() => getter.Get(BookUri));
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
        var handler = new FakeHandler { BrokenEntry = text == null ? [] : Encoding.UTF8.GetBytes(text), BrokenChapter = 4 };
        using var getter = CreateGetter(new Options(), handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(BookUri));

        Assert.Contains("главы 4", error.Message);
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
        var handler = new FakeHandler { BrokenChapter = 4, BrokenEntry = LitnetArchiveTestSupport.Encrypt(json) };
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

    private sealed class ProgressLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> DebugLines { get; } = [];
        public LogLevel MinimumLevel { get; init; } = LogLevel.Trace;
        public Action<string> OnLine { get; init; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= MinimumLevel;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var line = formatter(state, exception);
            Lines.Add(line);
            if (level >= LogLevel.Warning) Warnings.Add(line);
            if (level == LogLevel.Debug) DebugLines.Add(line);
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
        public bool OmitAccessField { get; init; }
        public HttpStatusCode BatchStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode CoverStatus { get; init; } = HttpStatusCode.OK;
        public int? RateLimitBatchNumber { get; init; }
        public bool OmitLastChapter { get; init; }
        public int? ReturnedChapterLimit { get; init; }
        public int ChapterCount { get; init; } = 205;
        public bool AdultOnly { get; init; }
        public long? BrokenChapter { get; init; }
        public byte[] BrokenEntry { get; init; }
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
                    result = OmitAccessField
                        ? Enumerable.Range(1, ChapterCount).Select(i => (object)new { id = i, title = $"Глава {i}" })
                        : Enumerable.Range(1, ChapterCount).Select(i => new { id = i, title = $"Глава {i}", access = Access(i) });
                    break;
                case "/v1/book/text/get-chapters":
                    var ids = HttpUtility.ParseQueryString(uri.Query).GetValues("chapter_ids[]")!.Select(long.Parse).ToArray();
                    Batches.Add(ids);
                    var status = Batches.Count == RateLimitBatchNumber ? HttpStatusCode.TooManyRequests : BatchStatus;
                    if (status != HttpStatusCode.OK)
                        return Task.FromResult(TestHttpResponse.Create(status, request, new StringContent("{}")));
                    BeforeBatch?.Invoke(Batches.Count);
                    // Сервер может вернуть главы в другом порядке и обрезать большой запрос.
                    var entries = ids.Take(ReturnedChapterLimit ?? (OmitLastChapter ? ids.Length - 1 : 100)).Reverse().Select(id =>
                        ($"{id}.gz", id == BrokenChapter ? BrokenEntry : LitnetArchiveTestSupport.Encrypt("[\"<p>Текст главы</p>\"]")));
                    var archive = new ByteArrayContent(LitnetArchiveTestSupport.Archive(entries));
                    archive.Headers.ContentType = new("application/zip");
                    return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request, archive));
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
