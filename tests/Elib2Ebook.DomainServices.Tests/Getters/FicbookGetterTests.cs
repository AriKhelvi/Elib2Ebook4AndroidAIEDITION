using System.Net;
using System.Text;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Ficbook;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class FicbookGetterTests
{
    private const string Slug = "019e5fb6-20ea-7b15-8e5b-5706e6ee13a8";
    private static readonly Uri BookUri = new($"https://ficbook.net/readfic/{Slug}?from_promo=1");
    private const string BookHtml = "<html><head><title>Книга</title></head><body><h1>Книга</h1>" +
        "<a itemprop='author' href='/authors/123'>Автор</a><div itemprop='description'>Описание</div></body></html>";

    [Fact]
    public async Task UsesMobileApiAndCanonicalPublicLinks()
    {
        var handler = new Handler([Part(42038773, 1, "Начало")]);
        using var getter = await CreateGetter(handler);

        using var book = await getter.Get(BookUri);

        Assert.Equal(new Uri($"https://ficbook.net/readfic/{Slug}"), book.Url);
        Assert.Equal(new Uri("https://ficbook.net/authors/123"), book.Author.Url);
        Assert.Equal("Автор", book.Author.Name);
        Assert.Equal("Описание", book.Annotation);
        Assert.Equal("Начало", Assert.Single(book.Chapters).Title);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("fanficlets.xyz", request.Url.Host);
            Assert.Contains("ficbook (1.7.0)", request.UserAgent);
        });
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal($"/readfic/{Slug}", handler.Requests[0].Url.PathAndQuery);
        var toc = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, toc.Method);
        Assert.Equal("/api_mobile/fanfic_parts", toc.Url.AbsolutePath);
        Assert.Equal("application/json", toc.ContentType);
        using var tocPayload = JsonDocument.Parse(toc.Body);
        Assert.Equal(Slug, tocPayload.RootElement.GetProperty("fanfic_slug").GetString());
        var text = handler.Requests[2];
        Assert.Equal(HttpMethod.Post, text.Method);
        Assert.Equal("/api_mobile/fanfic_part_text", text.Url.AbsolutePath);
        Assert.Equal("application/json", text.ContentType);
        Assert.Equal(42038773, ReadPartId(text));
    }

    [Fact]
    public async Task LegacyNumericLinkKeepsItsSlugForTheApi()
    {
        var handler = new Handler([Part(42038773, 1)]);
        using var getter = await CreateGetter(handler);

        using var book = await getter.Get(new Uri("https://ficbook.net/readfic/10003242?from=search"));

        Assert.Equal("/readfic/10003242", handler.Requests[0].Url.AbsolutePath);
        using var payload = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("10003242", payload.RootElement.GetProperty("fanfic_slug").GetString());
        Assert.Equal(new Uri("https://ficbook.net/readfic/10003242"), book.Url);
        Assert.Single(book.Chapters);
    }

    [Fact]
    public async Task SupportsMirrorLinksAndKeepsCanonicalBookUrlWithoutAcceptingLookalikeHosts()
    {
        var handler = new Handler([Part(42038773, 1)]);
        using var getter = await CreateGetter(handler);
        var mirror = new Uri($"https://fanficlets.xyz/readfic/{Slug}");

        Assert.True(getter.IsSameUrl(BookUri));
        Assert.True(getter.IsSameUrl(mirror));
        Assert.False(getter.IsSameUrl(new Uri($"https://fanficlets.xyz.evil.example/readfic/{Slug}")));
        Assert.False(getter.IsSameUrl(new Uri($"https://evilficbook.net/readfic/{Slug}")));
        Assert.False(getter.IsSameUrl(new Uri($"https://evil.example/readfic/{Slug}")));

        using var book = await getter.Get(mirror);

        Assert.Equal(new Uri($"https://ficbook.net/readfic/{Slug}"), book.Url);
        Assert.Single(book.Chapters);
        Assert.All(handler.Requests, request => Assert.Equal("fanficlets.xyz", request.Url.Host));
    }

    [Fact]
    public async Task NormalBookPageWithCloudflareScriptsDoesNotTriggerFallback()
    {
        // Этот скрипт присутствует и в успешной карточке HTTP 200, без страницы проверки.
        const string scripts = "<script src='/cdn-cgi/challenge-platform/scripts/precursor/main.js'></script>" +
            "<script>window['cf-chl-test'] = true;</script>";
        var handler = new Handler([Part(1, 1)])
        {
            Override = request => request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(BookHtml.Replace("</body>", scripts + "</body>"), Encoding.UTF8, "text/html")
                } : null
        };
        using var getter = await CreateGetter(handler);

        using var book = await getter.Get(BookUri);

        Assert.Equal("Книга", book.Title);
        Assert.Single(book.Chapters);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("fanficlets.xyz", request.Url.Host));
    }

    [Fact]
    public async Task SortsPublicChaptersByServerOrderAndDeduplicatesIdsWithoutFillingGaps()
    {
        var handler = new Handler([
            Part(403, 4, "Третья"), Part(401, 1, "Первая"), Part(999, 2, "Черновик", false),
            Part(402, 3, "Вторая"), Part(402, 3, "Вторая")
        ]);
        using var getter = await CreateGetter(handler);

        using var book = await getter.Get(BookUri);

        Assert.Equal(["Первая", "Вторая", "Третья"], book.Chapters.Select(chapter => chapter.Title));
        Assert.Equal([401L, 402L, 403L], handler.TextRequests.Select(ReadPartId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppliesRangeAfterOrderingAndOnlyRequestsSelectedParts(bool byName)
    {
        var handler = new Handler([Part(303, 5, "Конец"), Part(301, 1, "Начало"), Part(302, 3, "Середина")]);
        var options = byName ? new Options { StartName = "Середина", EndName = "Середина" }
            : new Options { Start = 2, End = 2 };
        using var getter = await CreateGetter(handler, options);

        using var book = await getter.Get(BookUri);

        Assert.Equal("Середина", Assert.Single(book.Chapters).Title);
        Assert.Equal(302, ReadPartId(Assert.Single(handler.TextRequests)));
    }

    [Fact]
    public async Task DownloadsMoreThanOneHundredParts()
    {
        var handler = new Handler(Enumerable.Range(1, 105).Select(i => Part(1000 + i, i, $"Глава {i}")).ToArray());
        using var getter = await CreateGetter(handler);

        using var book = await getter.Get(BookUri);

        Assert.Equal(105, book.Chapters.Count());
        Assert.Equal(105, handler.TextRequests.Count());
        Assert.Equal(1105, ReadPartId(handler.TextRequests.Last()));
    }

    [Fact]
    public async Task SingleUntitledPartUsesBookTitleAndItsActualId()
    {
        var handler = new Handler([Part(765, 1, "")]);
        using var getter = await CreateGetter(handler);

        using var book = await getter.Get(BookUri);

        Assert.Equal("Книга", Assert.Single(book.Chapters).Title);
        Assert.Equal(765, ReadPartId(Assert.Single(handler.TextRequests)));
    }

    [Fact]
    public async Task NoChaptersOnlyLoadsMetadata()
    {
        var handler = new Handler([]);
        using var getter = await CreateGetter(handler, new Options { NoChapters = true });

        using var book = await getter.Get(BookUri);

        Assert.Equal("Книга", book.Title);
        Assert.Empty(book.Chapters);
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
    }

    [Fact]
    public async Task PlainTextPreservesLiteralMarkupAndDecodesEntitiesOnlyOnce()
    {
        const string source = "2 < 3 & 5 > 4\r\n\r\n<герой> &lt;замок&gt;\n\t\nПоследний абзац";
        var handler = new Handler([Part(1, 1)]) { Text = source };
        using var getter = await CreateGetter(handler);

        using var book = await getter.Get(BookUri);

        var content = Assert.Single(book.Chapters).Content;
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(content);
        Assert.Equal(3, doc.DocumentNode.SelectNodes("//p").Count);
        Assert.Equal("2 < 3 & 5 > 4<герой> <замок>Последний абзац",
            WebUtility.HtmlDecode(doc.DocumentNode.InnerText));
        Assert.Null(doc.DocumentNode.SelectSingleNode("//герой"));
    }

    [Fact]
    public async Task ChapterHtmlKeepsFormattingWithoutTurningItIntoLiteralTags()
    {
        var handler = new Handler([Part(1, 1)])
        {
            Text = "<p>Первый <b>жирный</b> &amp; <i>курсивный</i>.</p><p>Второй абзац</p>"
        };
        using var getter = await CreateGetter(handler);

        using var book = await getter.Get(BookUri);

        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(Assert.Single(book.Chapters).Content);
        Assert.Equal(2, doc.DocumentNode.SelectNodes("//p").Count);
        Assert.Equal("жирный", doc.DocumentNode.SelectSingleNode("//b|//strong")?.InnerText);
        Assert.Equal("курсивный", doc.DocumentNode.SelectSingleNode("//i|//em")?.InnerText);
        Assert.Equal("Первый жирный & курсивный.Второй абзац", WebUtility.HtmlDecode(doc.DocumentNode.InnerText));
    }

    [Fact]
    public async Task RichTextKeepsFootnotesAndFormattingWithoutActiveContentOrAttributes()
    {
        var handler = new Handler([Part(1, 1)])
        {
            Text = "<p onclick='steal()' style='color:red'>Текст <b data-secret='x'>выделен</b>" +
                "<footnote id='note'>Примечание &lt;1&gt;</footnote>" +
                "<a href='javascript:steal()'>ссылка</a></p><script>alert(1)</script>"
        };
        using var getter = await CreateGetter(handler);

        using var book = await getter.Get(BookUri);

        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(Assert.Single(book.Chapters).Content);
        Assert.Equal("выделен", doc.DocumentNode.SelectSingleNode("//b")?.InnerText);
        Assert.Contains("[Примечание <1>]", WebUtility.HtmlDecode(doc.DocumentNode.InnerText));
        Assert.Contains("ссылка", doc.DocumentNode.InnerText);
        Assert.Null(doc.DocumentNode.SelectSingleNode("//script|//footnote|//a"));
        Assert.All(doc.DocumentNode.Descendants().Where(node => node.NodeType == HtmlAgilityPack.HtmlNodeType.Element),
            node => Assert.Empty(node.Attributes));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"result\":false,\"error\":\"Недоступно\"}")]
    [InlineData("{\"result\":true,\"data\":{}}")]
    [InlineData("{\"result\":true,\"data\":{\"parts\":[]}}")]
    [InlineData("{\"result\":true,\"data\":{\"parts\":[{\"part_title\":\"Нет id\",\"order_num\":1,\"is_public\":true}]}}")]
    public async Task InvalidTocFailsInsteadOfReturningAnEmptyBook(string body)
    {
        var handler = new Handler([])
        {
            Override = request => request.Url.AbsolutePath == "/api_mobile/fanfic_parts" ? Raw(body) : null
        };
        using var getter = await CreateGetter(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => getter.Get(BookUri));

        Assert.Empty(handler.TextRequests);
        Assert.All(handler.Requests, request => Assert.Equal("fanficlets.xyz", request.Url.Host));
    }

    [Theory]
    [InlineData("{\"result\":false,\"error\":\"Нужен вход\"}")]
    [InlineData("{\"result\":true,\"data\":\"\"}")]
    [InlineData("{\"result\":true,\"data\":\" \\n \\t\"}")]
    [InlineData("{\"result\":true,\"data\":\"<p><br /></p>\"}")]
    [InlineData("{\"result\":true,\"data\":null}")]
    [InlineData("{\"result\":true,\"data\":{\"text\":\"Другой контракт\"}}")]
    [InlineData("<html><h1>Войдите на сайт</h1></html>")]
    public async Task InvalidChapterFailsInsteadOfReturningPartialOrEmptyText(string body)
    {
        var handler = new Handler([Part(1, 1), Part(2, 2)])
        {
            Override = request => request.Url.AbsolutePath == "/api_mobile/fanfic_part_text" && ReadPartId(request) == 2
                ? Raw(body) : null
        };
        using var getter = await CreateGetter(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => getter.Get(BookUri));

        Assert.Equal(2, handler.TextRequests.Count());
        Assert.All(handler.Requests, request => Assert.Equal("fanficlets.xyz", request.Url.Host));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    public async Task AccessAndRateLimitFailuresDoNotRetryOnAnotherHost(int status)
    {
        var handler = new Handler([Part(1, 1)])
        {
            Override = request => request.Url.AbsolutePath == "/api_mobile/fanfic_part_text"
                ? Raw("Недоступно", (HttpStatusCode)status) : null
        };
        using var getter = await CreateGetter(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUri));

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Single(handler.TextRequests);
        Assert.All(handler.Requests, request => Assert.Equal("fanficlets.xyz", request.Url.Host));
    }

    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("server")]
    [InlineData("challenge")]
    public async Task MirrorFailureFallsBackToMainAndKeepsUsingIt(string failure)
    {
        var handler = new Handler([Part(1, 1)])
        {
            Override = request => request.Url.Host != "fanficlets.xyz" ? null : failure switch
            {
                "network" => throw new HttpRequestException("Соединение разорвано"),
                "timeout" => throw new TaskCanceledException("Истекло время ожидания", new TimeoutException()),
                "server" => Raw("Недоступно", HttpStatusCode.ServiceUnavailable),
                _ => Challenge()
            }
        };
        using var getter = await CreateGetter(handler);

        using var book = await getter.Get(BookUri);

        Assert.Single(book.Chapters);
        Assert.Equal("fanficlets.xyz", handler.Requests.First().Url.Host);
        Assert.All(handler.Requests.Skip(1), request => Assert.Equal("ficbook.net", request.Url.Host));
    }

    [Fact]
    public async Task MidDownloadMirrorFailureRetriesOnlyTheCurrentPartAndKeepsCompletedChapters()
    {
        var handler = new Handler([Part(10, 1), Part(20, 2), Part(30, 3)])
        {
            Override = request => request.Url.Host == "fanficlets.xyz" &&
                request.Url.AbsolutePath == "/api_mobile/fanfic_part_text" && ReadPartId(request) == 20
                    ? Challenge() : null
        };
        using var getter = await CreateGetter(handler);

        using var book = await getter.Get(BookUri);

        Assert.Equal(3, book.Chapters.Count());
        Assert.Equal([10L, 20L, 20L, 30L], handler.TextRequests.Select(ReadPartId));
        Assert.Equal(["fanficlets.xyz", "fanficlets.xyz", "ficbook.net", "ficbook.net"],
            handler.TextRequests.Select(request => request.Url.Host));
    }

    [Fact]
    public async Task ChallengesOnBothHostsGiveExplicitDiagnosis()
    {
        var handler = new Handler([]) { Override = _ => Challenge() };
        using var getter = await CreateGetter(handler);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => getter.Get(BookUri));

        Assert.Contains("Cloudflare", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["fanficlets.xyz", "ficbook.net"], handler.Requests.Select(request => request.Url.Host));
    }

    [Fact]
    public async Task CancellationDuringRequestDoesNotSwitchMirrors()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler([])
        {
            Override = _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
        };
        using var getter = await CreateGetter(handler, cancellationToken: cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => getter.Get(BookUri));

        Assert.Equal("fanficlets.xyz", Assert.Single(handler.Requests).Url.Host);
    }

    [Fact]
    public async Task CancellationBeforeDownloadDoesNotSendAnyRequest()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new Handler([Part(1, 1)]);
        using var getter = await CreateGetter(handler, cancellationToken: cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => getter.Get(BookUri));

        Assert.Empty(handler.Requests);
    }

    private static object Part(long id, int order, string title = "Глава", bool isPublic = true)
        => new { id, order_num = order, part_title = title, is_public = isPublic };

    private static async Task<FicbookGetter> CreateGetter(Handler handler, Options options = null,
        CancellationToken cancellationToken = default)
    {
        var config = AfAuditSupport.Config(handler, options);
        config.CancellationToken = cancellationToken;
        var getter = new FicbookGetter(config);
        await getter.Init();
        return getter;
    }

    private static HttpResponseMessage Raw(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Json(object body) => Raw(JsonSerializer.Serialize(body));

    private static HttpResponseMessage Challenge()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<html><title>Проверка безопасности</title></html>", Encoding.UTF8, "text/html")
        };
        response.Headers.TryAddWithoutValidation("Cf-Mitigated", "challenge");
        return response;
    }

    private static long ReadPartId(Request request)
    {
        using var json = JsonDocument.Parse(request.Body);
        return json.RootElement.GetProperty("part_id").GetInt64();
    }

    private sealed record Request(Uri Url, HttpMethod Method, string Body, string ContentType, string UserAgent);

    private sealed class Handler(object[] parts) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public IEnumerable<Request> TextRequests => Requests.Where(request => request.Url.AbsolutePath == "/api_mobile/fanfic_part_text");
        public string Text { get; init; } = "Первый абзац\nВторой абзац";
        public Func<Request, HttpResponseMessage> Override { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recorded = new Request(request.RequestUri!, request.Method,
                request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Content?.Headers.ContentType?.MediaType, request.Headers.UserAgent.ToString());
            Requests.Add(recorded);
            var response = Override?.Invoke(recorded) ?? request.RequestUri!.AbsolutePath switch
            {
                var path when path.StartsWith("/readfic/", StringComparison.Ordinal) =>
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BookHtml, Encoding.UTF8, "text/html") },
                "/api_mobile/fanfic_parts" => Json(new { result = true, data = new { fanfic_title = "Книга", parts } }),
                "/api_mobile/fanfic_part_text" => Json(new { result = true, data = Text }),
                _ => throw new InvalidOperationException($"Неожиданный запрос {request.Method} {request.RequestUri}")
            };
            response.RequestMessage = request;
            return response;
        }
    }
}
