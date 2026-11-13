using System.Net;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.BookInBook;
using Elib2Ebook.ExternalServices.Neobook;
using Elib2Ebook.ExternalServices.Novelxo.Getters;
using Elib2Ebook.ExternalServices.Renovels.Getters;
using Elib2Ebook.ExternalServices.StrokiMts;
using Elib2Ebook.ExternalServices.TopLiba.Getters;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class GetterAuditSafetyTests
{
    [Fact]
    public async Task BookInBookRepeatedPageFailsWithoutRequestingForeverOrReturningPartialChapter()
    {
        var handler = new StubHandler(uri => uri.AbsolutePath == "/book"
            ? "<a class='chapters-form__chapter' href='/read?id=1&chapter=1'>Глава</a>"
            : "<script id='PAGE_TEXT' type='application/json'>\"Текст страницы\"</script>");
        using var getter = new BookInBookGetter(Config(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://bookinbook.ru/book?id=1")));

        Assert.Contains("повторил текст", error.Message);
        Assert.Equal(2, handler.Requests.Count(uri => uri.AbsolutePath == "/read"));
    }

    [Fact]
    public async Task BookInBookHttp403DoesNotBecomeChapterTextOrEndOfBook()
    {
        var handler = new StubHandler(uri => uri.AbsolutePath == "/book"
            ? "<a class='chapters-form__chapter' href='/read?id=1&chapter=1'>Глава</a>"
            : "Access denied", uri => uri.AbsolutePath == "/read" ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
        using var getter = new BookInBookGetter(Config(handler));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(new Uri("https://bookinbook.ru/book?id=1")));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Theory]
    [InlineData(-1, "[{\"data\":{\"html\":\"<p>Текст</p>\"}}]")]
    [InlineData(1, "[{\"data\":{\"html\":\"<p>Текст</p>\"}}]")]
    [InlineData(0, "[]")]
    [InlineData(0, "null")]
    public async Task NeobookInvalidChapterIndexFailsClearlyInsteadOfCrashOrSilentEmptyChapter(int index, string chapters)
    {
        var handler = NeobookHandler($"{{\"active_chapter_index\":{index},\"chapters\":{chapters}}}");
        using var getter = new NeobookGetter(Config(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://neobook.org/book/test/")));

        Assert.Contains("неверный индекс", error.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"data\":{\"html\":\"  \"}}")]
    public async Task NeobookMissingChapterTextDoesNotBecomeSuccessfulEmptyChapter(string chapter)
    {
        var handler = NeobookHandler($"{{\"active_chapter_index\":0,\"chapters\":[{chapter}]}}");
        using var getter = new NeobookGetter(Config(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://neobook.org/book/test/")));

        Assert.Contains("не предоставил текст", error.Message);
    }

    [Fact]
    public async Task NeobookValidLastIndexLoadsRequestedText()
    {
        var handler = NeobookHandler("{\"active_chapter_index\":1,\"chapters\":[{\"data\":{\"html\":\"<p>Другой текст</p>\"}},{\"data\":{\"html\":\"<p>Нужный текст</p>\"}}]}");
        using var getter = new NeobookGetter(Config(handler));

        using var book = await getter.Get(new Uri("https://neobook.org/book/test/"));

        Assert.Contains("Нужный текст", Assert.Single(book.Chapters).Content);
        Assert.DoesNotContain("Другой текст", Assert.Single(book.Chapters).Content);
    }

    [Theory]
    [InlineData("/book/chapter1")]
    [InlineData("#next")]
    public async Task NovelxoRepeatedNextLinkFailsWithoutReturningDuplicatedBook(string next)
    {
        var handler = new StubHandler(uri => uri.AbsolutePath == "/book"
            ? "<div class='actions'><a class='read' href='/book/chapter1'>Читать</a></div>"
            : NovelxoPage("Первая", $"<a class='next' href='{next}'>Далее</a>"));
        using var getter = new NovelxoGetter(Config(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://novelxo.com/book")));

        Assert.Contains("повторил ссылку", error.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NovelxoFiniteNextChainKeepsChapterOrderAndAppliesRange(bool range)
    {
        var handler = new StubHandler(uri => uri.AbsolutePath switch
        {
            "/book" => "<h1>Книга</h1><div class='actions'><a class='read' href='/book/chapter1'>Читать</a></div>",
            "/book/chapter1" => NovelxoPage("Первая", "<a class='next' href='/book/chapter2'>Далее</a>"),
            _ => NovelxoPage("Вторая", "")
        });
        using var getter = new NovelxoGetter(Config(handler, options: range ? new Options { Start = 2, End = 2 } : new Options()));

        using var book = await getter.Get(new Uri("https://novelxo.com/book"));

        Assert.Equal(range ? new[] { "Вторая" } : new[] { "Первая", "Вторая" }, book.Chapters.Select(chapter => chapter.Title));
        Assert.All(book.Chapters, chapter => Assert.True(chapter.IsValid));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TopLibaAndBiglibaWarnAboutExplicitlyBlockedParts(bool mirror)
    {
        var handler = new StubHandler(uri => uri.AbsolutePath switch
        {
            "/books/1" => "<h1 itemprop='name'>Книга</h1><h2 itemprop='author'><a href='/author/1'>Автор</a></h2><meta name='_token' content='test'>",
            "/reader/1" => "<script>capters: [\"1\"]</script>",
            _ => "{\"status\":\"error\"}"
        });
        var log = new WarningLogger();
        using TopLibaGetterBase getter = mirror ? new BiglibaGetter(Config(handler, log)) : new TopLibaGetter(Config(handler, log));

        using var book = await getter.Get(new Uri($"https://{(mirror ? "bigliba.com" : "topliba.com")}/books/1"));

        Assert.Empty(book.Chapters);
        Assert.Contains("Часть 1", Assert.Single(log.Warnings));
    }

    [Fact]
    public async Task TopLibaHttp403FailsInsteadOfTreatingResponseAsBookContent()
    {
        var handler = new StubHandler(uri => uri.AbsolutePath switch
        {
            "/books/1" => "<h1 itemprop='name'>Книга</h1><meta name='_token' content='test'>",
            "/reader/1" => "<script>capters: [\"1\"]</script>",
            _ => "Access denied"
        }, uri => uri.AbsolutePath.EndsWith("/chapter") ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
        using var getter = new TopLibaGetter(Config(handler));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(new Uri("https://topliba.com/books/1")));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task RenovelsRepeatedFullPageStopsBeforeDownloadingAnyChapter()
    {
        var page = JsonSerializer.Serialize(new { results = Enumerable.Range(1, 40).Select(id => new { id, name = $"Глава {id}" }) });
        var handler = new StubHandler(uri => uri.AbsolutePath == "/api/v2/titles/test/" ? RenovelsMetadata : page);
        using var getter = new RenovelsGetter(Config(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://renovels.org/novel/test/main")));

        Assert.Contains("повторил страницу 2", error.Message);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenovelsSlicesBeforeSkippingPaidChaptersAndDeduplicatesOverlappingPages(bool accessibleDuplicate)
    {
        var handler = new StubHandler(uri =>
        {
            if (uri.AbsolutePath == "/api/v2/titles/test/") return RenovelsMetadata;
            if (uri.Query.Length != 0)
            {
                var ids = uri.Query.EndsWith("page=1") ? Enumerable.Range(41, 40).Reverse()
                    : uri.Query.EndsWith("page=2") ? Enumerable.Range(2, 40).Reverse() : new[] { 1 };
                return JsonSerializer.Serialize(new { results = ids.Select(id => new { id, name = $"Глава {id}", chapter = id.ToString(), tome = 1, is_paid = id is 41 or 42,
                    is_bought = id == 42 || (accessibleDuplicate && id == 41 && uri.Query.EndsWith("page=2")) }) });
            }
            return JsonSerializer.Serialize(new { content = "<p>Текст главы.</p>" });
        });
        var log = new WarningLogger();
        using var getter = new RenovelsGetter(Config(handler, log, new Options { Start = 40, End = 43, NoImage = true }));

        using var book = await getter.Get(new Uri("https://renovels.org/novel/test/main"));

        Assert.Equal(accessibleDuplicate ? new[] { "40", "41", "42", "43" } : new[] { "40", "42", "43" }, handler.Requests.Where(uri => uri.Query.Length == 0 && uri.AbsolutePath.Contains("/chapters/"))
            .Select(uri => uri.Segments[^1].TrimEnd('/')));
        Assert.Equal(accessibleDuplicate ? 4 : 3, book.Chapters.Count());
        if (accessibleDuplicate) Assert.Empty(log.Warnings);
        else Assert.Contains("Глава 41", Assert.Single(log.Warnings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrokiWarnsOnlyWhenPreviewIsChosenInsteadOfFullFile(bool full)
    {
        var handler = StrokiHandler(full ? "{\"full\":[{\"fileId\":1}],\"preview\":{\"fileId\":2}}" : "{\"full\":[],\"preview\":{\"fileId\":2}}");
        var log = new WarningLogger();
        using var getter = new StrokiMtsGetter(Config(handler, log));

        using var book = await getter.Get(new Uri("https://stroki.mts.ru/book/test-1"));

        Assert.Equal(!full, log.Warnings.Any(message => message.Contains("ознакомительный отрывок")));
        Assert.Contains(handler.Requests, uri => uri.AbsolutePath.EndsWith(full ? "/link/1" : "/link/2"));
    }

    [Fact]
    public async Task StrokiMissingFullAndPreviewFailsBeforeFileRequest()
    {
        var handler = StrokiHandler("{\"full\":[],\"preview\":null}");
        using var getter = new StrokiMtsGetter(Config(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://stroki.mts.ru/book/test-1")));

        Assert.Contains("ни полного файла", error.Message);
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.Contains("/link/"));
    }

    private const string RenovelsMetadata = "{\"main_name\":\"Книга\",\"cover\":{},\"branches\":[{\"id\":1,\"publishers\":[]}]}";

    private static StubHandler NeobookHandler(string chapterData) => new(uri => uri.AbsolutePath == "/reader/"
        ? $"<script>var data = {chapterData};\n</script>"
        : "<script>var postData = {\"title\":\"Книга\",\"user\":{\"username\":\"author\",\"firstname\":\"Автор\",\"lastname\":\"Книги\"},\"chapters\":[{\"token\":\"1\",\"title\":\"Глава\"}]};\n</script>");

    private static string NovelxoPage(string title, string next)
        => $"<h2 class='chapter-title'>{title}</h2><div class='readerbody-wg'><p>Текст главы.</p></div><div class='readernav-wg'>{next}</div>";

    private static StubHandler StrokiHandler(string files) => new(uri => uri.AbsolutePath switch
    {
        "/api/books/multi/1" => "{\"data\":{\"items\":[{\"textBook\":{\"id\":1,\"title\":\"Книга\",\"imageUrl\":{},\"authors\":[],\"contentInfo\":{\"fileType\":\"PDF\"}}}]}}",
        "/api/books/files" => $"{{\"data\":{files}}}",
        "/api/books/files/data/link/1" or "/api/books/files/data/link/2" => "{\"data\":{\"url\":\"https://stroki.mts.ru/file.pdf\"}}",
        _ => "sample file"
    });

    private static BookGetterConfig Config(HttpMessageHandler handler, ILogger logger = null, Options options = null)
        => new(options ?? new Options { NoImage = true }, new HttpClient(handler), new CookieContainer(), TempFolderFactory.Create(null), logger ?? new WarningLogger());

    private sealed class StubHandler(Func<Uri, string> content, Func<Uri, HttpStatusCode> status = null) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(TestHttpResponse.Create(status?.Invoke(request.RequestUri!) ?? HttpStatusCode.OK, request,
                new StringContent(content(request.RequestUri!))));
        }
    }

    private sealed class WarningLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
