using System.Net;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Neobook;
using Elib2Ebook.ExternalServices.Rulate.Getters;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LoaderParserDiagnosticsTests
{
    [Fact]
    public async Task RulateDiagnosticsDistinguishPaginationSelectionAndDeniedAccessWithoutChapterText()
    {
        var log = new DebugLogger();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request =>
        {
            if (request.RequestUri.AbsolutePath.EndsWith("/readyajax"))
                return Response(JsonSerializer.Serialize(new { can_read = true, content = "<div class='content-text'><p>private-book-text</p></div>" }));
            var rows = request.RequestUri.Query.Length == 0 ? new[] { 1, 2, 3 } : new[] { 4, 5 };
            var html = "<h1>Книга</h1><table id='Chapters'>" + string.Concat(rows.Select(id =>
                $"<tr data-id='{id}'><td class='t'>Глава {id}</td><td>" +
                (id == 2 ? "<span class='disabled' title='Вы не можете скачивать перевод этой главы.'></span>" : "<a title='Получить готовый перевод.'>читать</a>") + "</td></tr>")) + "</table>";
            return Response(html + (request.RequestUri.Query.Length == 0 ? "<a href='?Chapter_page=2'>2</a>" : ""));
        }), new Options { Start = 2, End = 4, NoImage = true }, log);
        using var getter = new BllateGetter(config);

        using var book = await getter.Get(new Uri("https://bllate.org/book/1"));

        Assert.Equal(2, book.Chapters.Count());
        Assert.Contains(log.DebugMessages, line => line.Contains("page=1, rows=3, newIds=3, denied=1"));
        Assert.Contains(log.DebugMessages, line => line.Contains("page=2, rows=2, newIds=2"));
        Assert.Contains(log.DebugMessages, line => line.Contains("tocTotal=5, selected=3"));
        Assert.Contains(log.DebugMessages, line => line.Contains("selected=3, returned=2"));
        Assert.DoesNotContain(log.DebugMessages, line => line.Contains("private-book-text"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RulateAuthStructureIsDebugOnlyAndNeverContainsTokenOrInputValues(bool debug)
    {
        const string main = "<meta name='csrf-token' content='private-csrf'><form><input name='login[login]' value='private-login'><input name='login[pass]' type='password' value='private-password'></form><script>var key='private-key';</script>";
        var log = new DebugLogger(debug);
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => Response(main)),
            new Options { Login = "private-login", Password = "private-password" }, log);
        using var getter = new RulateGetter(config);

        await Assert.ThrowsAsync<InvalidOperationException>(getter.Authorize);

        Assert.DoesNotContain(log.DebugMessages, line => line.Contains("private-"));
        if (debug)
            Assert.Contains(log.DebugMessages, line => line.Contains("Rulate DOM") && line.Contains("csrfMeta=True") && line.Contains("passwordField=True"));
        else
            Assert.Empty(log.DebugMessages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingNeobookMetadataHasSafeDebugStructureWithoutScriptsOrFormValues(bool debug)
    {
        const string html = "<form><input type='password' value='private-password'><input name='private-session' value='private-token'></form><script>var session='private-session';</script><div class='captcha'>private-book-text</div>";
        var log = new DebugLogger(debug);
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => Response(html)), logger: log);
        using var getter = new NeobookGetter(config);

        await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://neobook.org/book/test/")));

        Assert.DoesNotContain(log.DebugMessages, line => line.Contains("private-"));
        if (debug)
        {
            Assert.Contains(log.DebugMessages, line => line.Contains("assignmentFound=False"));
            Assert.Contains(log.DebugMessages, line => line.Contains("Neobook DOM") && line.Contains("passwordField=True") && line.Contains("challengeMarker=True"));
        }
        else Assert.Empty(log.DebugMessages);
    }

    private static HttpResponseMessage Response(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class DebugLogger(bool debug = true) : ILogger
    {
        public List<string> DebugMessages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information || debug;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (level == LogLevel.Debug && IsEnabled(level)) DebugMessages.Add(formatter(state, exception));
        }
    }
}
