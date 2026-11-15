using System.Net;
using System.Text;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.ExternalServices.LibSocial.Getters;
using Elib2Ebook.ExternalServices.RanobeHub;
using Elib2Ebook.ExternalServices.RanobesCom;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class Dev2ReaderRegressionTests
{
    private const string ModernBook = "<html data-site-version='modern'><h1>Книга</h1>" +
        "<div class='book-author-byline'><a class='is-author' href='/author/7'><span>АБ</span><strong>Автор</strong></a></div>" +
        "<div class='book-description-copy'><p>Описание</p></div></html>";

    [Fact]
    public async Task RanobesNormalBookDoesNotCallRemovedAntibotEndpointOrReloadCard()
    {
        var handler = new AfAuditSupport.Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/ranobe/177387-book.html", request.RequestUri.AbsolutePath);
            return Html("<h1 class='title'>Название<span> — альтернативное</span></h1><span itemprop='creator'>Автор</span>");
        });
        using var config = AfAuditSupport.Config(handler, new Options { NoChapters = true });
        using var getter = new RanobesComGetter(config);

        using var book = await getter.Get(new Uri("https://ranobes.com/ranobe/177387-book.html"));

        Assert.Equal("Название", book.Title);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(config.CookieContainer.GetCookies(new Uri("https://ranobes.com/")));
    }

    [Theory]
    [InlineData("<h1>Проверка браузера</h1><script>var h1 = 'challenge';</script>")]
    [InlineData("<h1 class='title'></h1>")]
    public async Task RanobesChallengeOrMissingCardNeverTriggersAntibotPost(string html)
    {
        var handler = new AfAuditSupport.Handler(_ => Html(html));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new RanobesComGetter(config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://ranobes.com/ranobe/177387-book.html")));

        Assert.Contains("карточку книги", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RanobeHubModernReaderCollectsAllPagesBeforeApplyingRangeAndPreservesHtml()
    {
        var pages = new List<int>();
        var chapterRequests = new List<string>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            var path = request.RequestUri.AbsolutePath;
            if (path == "/ranobe/947-test") return Html(ModernBook);
            if (path == "/api/books/947/chapters")
            {
                Assert.Contains("sort=asc", request.RequestUri.Query);
                Assert.Contains("limit=180", request.RequestUri.Query);
                var offset = request.RequestUri.Query.Contains("offset=180") ? 180 : 0;
                pages.Add(offset);
                return Toc(Enumerable.Range(offset + 1, offset == 0 ? 180 : 2), 182, offset == 0 ? 180 : null);
            }
            chapterRequests.Add(path);
            Assert.Equal("/ranobe/947-test/chapter/181", path);
            return Html("<div class='reader-navigation'>Лишний текст</div><div class='reader-content' id='reader-content-181'>" +
                "<h2 id='scene'>Сцена</h2><p>Проза &lt;герой&gt; &amp; продолжение</p></div><div>Реклама</div>");
        });
        using var config = AfAuditSupport.Config(handler, new Options { Start = 181, End = 181, NoImage = true });
        using var getter = new RanobeHubGetter(config);

        using var book = await getter.Get(new Uri("https://ranobehub.org/ranobe/947-test"));

        Assert.Equal(new[] { 0, 180 }, pages);
        Assert.Single(chapterRequests);
        Assert.Equal("Автор", book.Author.Name);
        Assert.Contains("Описание", book.Annotation);
        var chapter = Assert.Single(book.Chapters);
        Assert.Equal("Том 1. Глава 181", chapter.Title);
        Assert.Contains("&lt;герой&gt;", chapter.Content);
        Assert.Contains("Сцена", chapter.Content);
        Assert.DoesNotContain("Реклама", chapter.Content);
        Assert.DoesNotContain("Лишний текст", chapter.Content);
    }

    [Fact]
    public async Task RanobeHubMetadataOnlyDoesNotRequestToc()
    {
        var handler = new AfAuditSupport.Handler(_ => Html(ModernBook));
        using var config = AfAuditSupport.Config(handler, new Options { NoChapters = true });
        using var getter = new RanobeHubGetter(config);

        using var book = await getter.Get(new Uri("https://ranobehub.org/ranobe/947-test"));

        Assert.Equal("Книга", book.Title);
        Assert.Empty(book.Chapters);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"items\":[],\"total\":0,\"nextOffset\":null}")]
    [InlineData("{\"items\":[],\"total\":1,\"nextOffset\":0}")]
    [InlineData("<html>Проверка браузера</html>")]
    public async Task RanobeHubRejectsEmptyMalformedOrStuckModernToc(string body)
    {
        var handler = new AfAuditSupport.Handler(request => request.RequestUri.AbsolutePath.StartsWith("/ranobe/")
            ? Html(ModernBook) : Json(body));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new RanobeHubGetter(config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://ranobehub.org/ranobe/947-test")));

        Assert.Contains("RanobeHub", error.Message);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task RanobeHubRepeatedPageFailsWithoutDownloadingPartialBook()
    {
        var handler = new AfAuditSupport.Handler(request => request.RequestUri.AbsolutePath == "/ranobe/947-test"
            ? Html(ModernBook) : Toc([1], 2, request.RequestUri.Query.Contains("offset=0") ? 1 : null));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new RanobeHubGetter(config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://ranobehub.org/ranobe/947-test")));

        Assert.Contains("повторил главу", error.Message);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task RanobeHubTruncatedTocFailsWithoutDownloadingPartialBook()
    {
        var handler = new AfAuditSupport.Handler(request => request.RequestUri.AbsolutePath == "/ranobe/947-test"
            ? Html(ModernBook) : Toc([1], 2, null));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new RanobeHubGetter(config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://ranobehub.org/ranobe/947-test")));

        Assert.Contains("неполное оглавление", error.Message);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    public async Task RanobeHubHttpDenialIsNotReadAsJsonOrRetriedElsewhere(int status)
    {
        var handler = new AfAuditSupport.Handler(request => request.RequestUri.AbsolutePath == "/ranobe/947-test"
            ? Html(ModernBook) : Html("Недоступно", (HttpStatusCode)status));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new RanobeHubGetter(config);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(new Uri("https://ranobehub.org/ranobe/947-test")));

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("2001-01-01T12:30:00Z")]
    [InlineData("2001-01-01T15:30:00+03:00")]
    public async Task RanobeHubPublishedChapterMayRetainItsPastScheduledDate(string scheduledFor)
    {
        var handler = new AfAuditSupport.Handler(request => request.RequestUri.AbsolutePath == "/ranobe/947-test"
            ? Html(ModernBook) : request.RequestUri.AbsolutePath.StartsWith("/api/") ? Toc([1], 1, null, scheduledFor)
            : Html("<div id='reader-content-1'><p>Опубликованный текст.</p></div>"));
        using var config = AfAuditSupport.Config(handler, new Options { NoImage = true });
        using var getter = new RanobeHubGetter(config);

        using var book = await getter.Get(new Uri("https://ranobehub.org/ranobe/947-test"));

        Assert.Contains("Опубликованный текст", Assert.Single(book.Chapters).Content);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData("9999-01-01T00:00:00Z", false, "будущей датой")]
    [InlineData("не дата", false, "некорректную дату")]
    [InlineData("", false, "некорректную дату")]
    [InlineData("2001-01-01T12:30:00Z", true, "недоступную")]
    public async Task RanobeHubFutureInvalidOrDraftChapterDoesNotReachDownload(string scheduledFor, bool isDraft, string reason)
    {
        var handler = new AfAuditSupport.Handler(request => request.RequestUri.AbsolutePath == "/ranobe/947-test"
            ? Html(ModernBook) : Toc([1], 1, null, scheduledFor, isDraft));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new RanobeHubGetter(config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://ranobehub.org/ranobe/947-test")));

        Assert.Contains(reason, error.Message);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("<div class='reader-content' id='reader-content-2'><p>Чужая глава</p></div>")]
    [InlineData("<div class='reader-content' id='reader-content-1'></div>")]
    [InlineData("<div data-callback='correctCaptcha'></div>")]
    public async Task RanobeHubRequiresRequestedChapterInsteadOfAcceptingMissingOrWrongContent(string html)
    {
        var handler = new AfAuditSupport.Handler(request => request.RequestUri.AbsolutePath == "/ranobe/947-test"
            ? Html(ModernBook) : request.RequestUri.AbsolutePath.StartsWith("/api/") ? Toc([1], 1, null) : Html(html));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new RanobeHubGetter(config);

        await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://ranobehub.org/ranobe/947-test")));

        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LibSocialEmptyTocExplainsAvailabilityBeforeReturningAnEmptyBook(bool licensed)
    {
        var handler = new AfAuditSupport.Handler(request => request.RequestUri.AbsolutePath.EndsWith("/chapters")
            ? Json("{\"data\":[]}") : LibBook(licensed));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new MangaLibGetter(config);

        var error = await Record.ExceptionAsync(() => getter.Get(new Uri("https://mangalib.me/ru/1--book")));

        if (licensed)
            Assert.Equal(SiteAccessReason.AccessDenied, Assert.IsType<SiteAccessException>(error).Reason);
        else
            Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("пустое оглавление (0 глав)", error.Message);
        Assert.Equal(licensed, error.Message.Contains("лицензия"));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(404, false)]
    [InlineData(403, false)]
    [InlineData(429, false)]
    [InlineData(404, true)]
    public async Task LibSocialMetadataAndTocHttpStatusSurviveToDiagnostics(int status, bool toc)
    {
        var handler = new AfAuditSupport.Handler(request => toc && !request.RequestUri.AbsolutePath.EndsWith("/chapters")
            ? LibBook(false) : Html("Недоступно", (HttpStatusCode)status));
        using var config = AfAuditSupport.Config(handler);
        using GetterBase getter = new SlashLibGetter(config);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(new Uri("https://v2.slashlib.me/ru/1--book")));

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(toc ? 2 : 1, handler.Calls);
    }

    [Fact]
    public async Task LibSocialMissingCardDataFailsBeforeNullReferenceOrCoverDownload()
    {
        var handler = new AfAuditSupport.Handler(_ => Json("{\"data\":null}"));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new HentaiLibGetter(config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://hentailib.me/ru/1--book")));

        Assert.Contains("данные карточки", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    private static HttpResponseMessage Toc(IEnumerable<int> ids, int total, int? next, string scheduledFor = null, bool isDraft = false)
        => AfAuditSupport.Json(new
        {
            items = ids.Select(id => new { id, bookId = 947, volume = 1, number = id, title = "Глава " + id, scheduledFor, isDraft }).ToArray(),
            total, nextOffset = next
        });

    private static HttpResponseMessage LibBook(bool licensed) => AfAuditSupport.Json(new
    {
        data = new { id = 1, slug_url = "1--book", name = "Книга", is_licensed = licensed, authors = Array.Empty<object>(),
            cover = new { @default = (string)null }, summary = "Описание" }
    });

    private static HttpResponseMessage Html(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
