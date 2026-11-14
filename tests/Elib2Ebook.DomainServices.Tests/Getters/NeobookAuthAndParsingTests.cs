using System.Net;
using System.Text;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Neobook;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class NeobookAuthAndParsingTests
{
    private const string Metadata = "{\"title\":\"Книга\",\"user\":{\"username\":\"author\",\"firstname\":\"Автор\",\"lastname\":\"Книги\"},\"chapters\":[{\"token\":\"1\",\"title\":\"Глава\"}]}";

    [Fact]
    public async Task AuthenticatedSessionAndBookRequestsNeverUseHttp()
    {
        var requests = new List<Uri>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            requests.Add(request.RequestUri);
            return Raw(request.Method == HttpMethod.Post
                ? "{\"login\":{\"utoken\":\"session-token\",\"uid\":\"123\"}}"
                : "<script>var postData = " + Metadata + ";\n</script>");
        });
        using var config = AfAuditSupport.Config(handler, new Options { Login = "reader@example.test", Password = "password", NoChapters = true });
        using var getter = new NeobookGetter(config);

        await getter.Authorize();
        using var book = await getter.Get(new Uri("http://neobook.org/book/test/"));

        Assert.All(requests, request => Assert.Equal("https", request.Scheme));
        Assert.Equal("https", book.Url.Scheme);
        var cookies = config.CookieContainer.GetCookies(new Uri("https://neobook.org/"));
        Assert.Equal("session-token", cookies["utoken"]?.Value);
        Assert.Equal("123", cookies["uid"]?.Value);
        Assert.All(cookies.Cast<Cookie>(), cookie => { Assert.True(cookie.Secure); Assert.True(cookie.HttpOnly); });
        Assert.Empty(config.CookieContainer.GetCookies(new Uri("http://neobook.org/")));
        Assert.Empty(config.CookieContainer.GetCookies(new Uri("https://elsewhere.test/")));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task HttpAuthFailureRemainsAnHttpFailureAndNeverAddsCookies(int status)
    {
        var logger = new CapturingLogger();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => Raw("<html>Вход не выполнен</html>", (HttpStatusCode)status)),
            new Options { Login = "reader@example.test", Password = "password" }, logger);
        using var getter = new NeobookGetter(config);

        var error = await Assert.ThrowsAsync<HttpRequestException>(getter.Authorize);

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Empty(config.CookieContainer.GetCookies(new Uri("https://neobook.org/")));
        Assert.DoesNotContain(logger.Messages, line => line.Contains("Успешно"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"login\":null}")]
    [InlineData("{\"login\":{\"uid\":\"123\"}}")]
    [InlineData("{\"login\":{\"utoken\":\"private-token\"}}")]
    [InlineData("{\"login\":{\"utoken\":\"private-token\",\"uid\":\" \"}}")]
    [InlineData("{\"error\":\"private-token private-password\"}")]
    [InlineData("<html>private-token private-password</html>")]
    public async Task MissingOrInvalidLoginNeverBecomesSuccessfulAuthentication(string body)
    {
        var logger = new CapturingLogger();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => Raw(body)),
            new Options { Login = "reader@example.test", Password = "private-password" }, logger);
        using var getter = new NeobookGetter(config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(getter.Authorize);

        Assert.Contains("Neobook", error.Message);
        Assert.DoesNotContain("private-token", error.ToString());
        Assert.DoesNotContain("private-password", error.ToString());
        Assert.Empty(config.CookieContainer.GetCookies(new Uri("https://neobook.org/")));
        Assert.DoesNotContain(logger.Messages, line => line.Contains("Успешно") || line.Contains("private-"));
    }

    [Theory]
    [InlineData("var postData=", "const data = ", false)]
    [InlineData("let postData = \n", "var data =\r\n", true)]
    public async Task ScriptJsonSupportsMinifiedAndMultilinePagesWithoutRequiredTrailingNewline(string metadataPrefix, string chapterPrefix, bool pretty)
    {
        var chapter = JsonSerializer.Serialize(new { active_chapter_index = 0, chapters = new[] { new { data = new { html = "<p>Текст; дальше &lt;герой&gt;</p>" } } } },
            new JsonSerializerOptions { WriteIndented = pretty });
        var metadata = pretty ? JsonSerializer.Serialize(JsonDocument.Parse(Metadata).RootElement, new JsonSerializerOptions { WriteIndented = true }) : Metadata;
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request => Raw(request.RequestUri.AbsolutePath == "/reader/"
            ? "<script>" + chapterPrefix + chapter + ";next();</script>"
            : "<script>" + metadataPrefix + metadata + ";next();</script>")));
        using var getter = new NeobookGetter(config);

        using var book = await getter.Get(new Uri("https://neobook.org/book/test/"));

        var content = Assert.Single(book.Chapters).Content;
        Assert.Contains("Текст; дальше &lt;герой&gt;", content);
        Assert.Equal("Книга", book.Title);
    }

    [Fact]
    public async Task ScriptJsonPreservesLiteralAndDoubleEncodedEntitiesBeforeHtmlParsing()
    {
        // В живом script JSON может содержать буквальные '&', а не только JSON-экранирование \u0026.
        // Декодирование всей страницы до JSON разрушило бы кавычки и текст, похожий на HTML.
        const string html = "<p>&lt;герой&gt; &amp;lt;двойной&amp;gt; &quot;слова&quot; &amp;quot;цитата&amp;quot;</p>";
        const string chapter = "{\"active_chapter_index\":0,\"chapters\":[{\"data\":{\"html\":\"" + html + "\"}}]}";
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request => Raw(request.RequestUri.AbsolutePath == "/reader/"
            ? "<script>const data=" + chapter + ";</script>"
            : "<script>const postData=" + Metadata + ";</script>")));
        using var getter = new NeobookGetter(config);

        using var book = await getter.Get(new Uri("https://neobook.org/book/test/"));

        Assert.Equal(html, Assert.Single(book.Chapters).Content);
    }

    [Theory]
    [InlineData("<h1>Вход</h1>")]
    [InlineData("<script>var postData = null;</script>")]
    [InlineData("<script>var postData = {};</script>")]
    [InlineData("<script>var postData = {broken};</script>")]
    public async Task MissingMetadataGivesExplicitFailureInsteadOfJsonOrNullReferenceCrash(string html)
    {
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => Raw(html)));
        using var getter = new NeobookGetter(config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://neobook.org/book/test/")));

        Assert.Contains("Neobook", error.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[{\"title\":\"Нет идентификатора\"}]")]
    public async Task MissingTocNeverProducesAnEmptyBook(string chapters)
    {
        var metadata = "{\"title\":\"Книга\",\"user\":{\"username\":\"author\"},\"chapters\":" + chapters + "}";
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => Raw("<script>var postData=" + metadata + ";</script>")));
        using var getter = new NeobookGetter(config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://neobook.org/book/test/")));

        Assert.Contains("не будет сохранена", error.Message);
    }

    private static HttpResponseMessage Raw(string value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
