using System.Net;
using System.Reflection;
using System.Web;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.RanobesCom;
using Elib2Ebook.ExternalServices.Younettranslate;
using Elib2Ebook.ExternalServices.Romfant;
using Elib2Ebook.ExternalServices.ReadManga;
using Elib2Ebook.ExternalServices.Rulate.Getters;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class PaginationAuditTests
{
    [Fact]
    public async Task RanobesFollowsArrowLinkAndDynamicallyDiscoveredPagesBeyond100()
    {
        using var getter = new RanobesComGetter(Config(uri =>
        {
            var page = uri.AbsolutePath.Contains("/page/") ? int.Parse(uri.Segments.Last().Trim('/')) : 1;
            var links = page == 1 ? "<div class='pages'><a href='/chapters/book/page/2/'>→</a></div>"
                : page == 2 ? "<div class='pages'><a href='/chapters/book/page/3/'>Последняя →</a><a href='https://other.example/chapters/book/page/9/'>9</a></div>" : "";
            return RanobesPage(211 - page * 70, 70) + links;
        }));
        var chapters = (await Toc(getter, new Uri("https://ranobes.com/chapters/book"))).ToList();
        Assert.Equal(210, chapters.Count);
        Assert.Equal(210, chapters.Select(chapter => chapter.Url).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 210).Select(id => $"Глава {id}"), chapters.Select(chapter => chapter.Title));
    }

    [Theory]
    [InlineData("<h1>Access denied</h1>")]
    [InlineData("<div id='dle-content'></div>")]
    public async Task RanobesRejectsMissingPageInsteadOfReturningPartialToc(string broken)
    {
        using var getter = new RanobesComGetter(Config(uri => uri.AbsolutePath.Contains("/page/")
            ? broken : RanobesPage(1, 100) + "<div class='pages'><a href='/chapters/book/page/2/'>→</a></div>"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Toc(getter, new Uri("https://ranobes.com/chapters/book")));
    }

    [Fact]
    public async Task YounetReadsAllPagesUntilAValidEmptyTable()
    {
        using var getter = new YounettranslateGetter(Config(uri =>
        {
            var page = int.Parse(HttpUtility.ParseQueryString(uri.Query)["page"]!);
            return YounetPage((page - 1) * 70, page <= 3 ? 70 : 0);
        }));
        Assert.Equal(210, (await Toc(getter, new Uri("https://younettranslate.com/projects/book"))).Count());
    }

    [Fact]
    public async Task RanobesRejectsRepeatedPage()
    {
        using var getter = new RanobesComGetter(Config(_ => RanobesPage(1, 100)
            + "<div class='pages'><a href='/chapters/book/page/2/'>→</a></div>"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Toc(getter, new Uri("https://ranobes.com/chapters/book")));
    }

    [Fact]
    public async Task YounetRejectsMissingFirstPageToc()
    {
        using var getter = new YounettranslateGetter(Config(_ => "<html><h1>Access denied</h1></html>"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Toc(getter, new Uri("https://younettranslate.com/projects/book")));
    }

    [Theory]
    [InlineData("<html><h1>Access denied</h1></html>")]
    [InlineData("<html><h1>Больше глав нет</h1></html>")]
    public async Task YounetKeepsCollectedChaptersButWarnsWhenNextPageHasNoTable(string lastPage)
    {
        var log = new AuditLogger();
        using var getter = new YounettranslateGetter(Config(uri => uri.Query == "?page=1"
            ? YounetPage(1, 100) : lastPage, log));
        Assert.Equal(100, (await Toc(getter, new Uri("https://younettranslate.com/projects/book"))).Count());
        Assert.Contains(log.Warnings, message => message.Contains("подтвердить полноту")
            && message.Contains("странице 2") && message.Contains("100 глав"));
    }

    [Fact]
    public async Task YounetRejectsRepeatedPage()
    {
        using var getter = new YounettranslateGetter(Config(_ => YounetPage(1, 100)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Toc(getter, new Uri("https://younettranslate.com/projects/book")));
    }

    [Fact]
    public async Task RomfantReportsPaywallAfterFreePages()
    {
        var log = new AuditLogger();
        using var getter = new RomfantGetter(Config(uri => uri.Query == "?page=2"
            ? "Чтобы продолжить чтение, пожалуйста, оплатите доступ."
            : "<h1>Книга</h1><div class='navigation'><a href='?page=2'>2</a></div><div class='book'><p>Доступный текст</p></div>", log));
        using var book = await getter.Get(new Uri("https://romfant.ru/book/test/"));
        Assert.Contains("Доступный текст", book.Chapters.Single().Content);
        Assert.Contains(log.Warnings, message => message.Contains("2 из 2") && message.Contains("оплаты"));
    }

    [Fact]
    public async Task ReadMangaReportsUnavailableChapter()
    {
        var log = new AuditLogger();
        using var getter = new ReadMangaGetter(Config(uri => uri.AbsolutePath.Contains("chapter")
            ? "<div class='buy-button'>Купить</div>"
            : "<span class='name'>Книга</span><div class='manga-description'>Аннотация</div><table><tr><td class='item-title'><a class='chapter-link' href='/chapter/1'>Глава 1</a></td></tr></table>", log));
        using var book = await getter.Get(new Uri("https://readmanga.live/book"));
        Assert.Contains(log.Warnings, message => message.Contains("Глава 1") && message.Contains("покупки"));
        Assert.False(book.Chapters.Single().IsValid);
    }

    [Fact]
    public async Task RulateReportsDeniedChapterAndRejectsMalformedReadableChapter()
    {
        var log = new AuditLogger();
        using var getter = new RulateGetter(Config(uri => uri.AbsolutePath.EndsWith("/readyajax")
            ? "{\"can_read\":false}" : "<h1>Книга</h1><div id='Chapters'><table><tr data-id='1'><td class='t'>Глава 1</td></tr></table></div>", log));
        using var book = await getter.Get(new Uri("https://tl.rulate.ru/book/1"));
        Assert.Contains(log.Warnings, message => message.Contains("Глава 1") && message.Contains("доступ"));

        using var malformed = new RulateGetter(Config(uri => uri.AbsolutePath.EndsWith("/readyajax")
            ? "{\"can_read\":true,\"content\":\"<h1>Challenge</h1>\"}" : "<h1>Книга</h1><div id='Chapters'><table><tr data-id='1'><td class='t'>Глава 1</td></tr></table></div>"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => malformed.Get(new Uri("https://tl.rulate.ru/book/1")));
    }

    private static Task<IEnumerable<UrlChapter>> Toc(GetterBase getter, Uri uri)
        => (Task<IEnumerable<UrlChapter>>)getter.GetType().GetMethod("GetToc", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(getter, [uri])!;

    private static string RanobesPage(int start, int count)
        => "<div id='dle-content'>" + string.Concat(Enumerable.Range(start, count).Reverse().Select(id => $"<div class='cat_block cat_line'><a href='/chapter/{id}' title='Глава {id}'>Глава</a></div>")) + "</div>";

    private static string YounetPage(int start, int count)
        => "<table class='catposts-table'>" + string.Concat(Enumerable.Range(start, count).Select(id => $"<tr><td class='hidden-sm'><a href='/chapter/{id}'>Глава {id}</a></td></tr>")) + "</table>";

    private static BookGetterConfig Config(Func<Uri, string> response, AuditLogger logger = null)
        => new(new Options { NoImage = true }, new HttpClient(new Handler(response)), new CookieContainer(), TempFolderFactory.Create(null), logger ?? new AuditLogger());

    private sealed class Handler(Func<Uri, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request.RequestUri!)), RequestMessage = request });
    }

    private sealed class AuditLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception ex, Func<TState, Exception, string> format)
        {
            if (level >= LogLevel.Warning) Warnings.Add(format(state, ex));
        }
    }
}
