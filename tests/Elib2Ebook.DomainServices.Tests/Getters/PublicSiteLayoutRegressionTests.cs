using System.Net;
using System.Text;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.ExternalServices.Acomics;
using Elib2Ebook.ExternalServices.HogwartsNet;
using Elib2Ebook.ExternalServices.Libking;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class PublicSiteLayoutRegressionTests
{
    private static readonly Uri ComicUrl = new("https://acomics.ru/~test/1");
    private static readonly Uri LibkingUrl = new("https://libking.ru/book.html");
    private static readonly Uri HogwartsUrl = new("https://hogwartsnet.ru/mfanf/ffshowfic.php?fid=123");

    static PublicSiteLayoutRegressionTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    public async Task AcomicsReportsRegionalBlockWithoutReturningResponsePersonalData()
    {
        using var getter = new AcomicsGetter(Config(_ => Html("По требованию Роскомнадзора сайт недоступен в вашем регионе. " +
            "<br>Ваш IP адрес: 192.0.2.123<br>Ваш регион: RU")));
        var error = await Assert.ThrowsAsync<SiteAccessException>(() => getter.Get(ComicUrl));
        Assert.Equal(SiteAccessReason.AccessDenied, error.Reason);
        Assert.Contains("недоступен в вашем регионе", error.Message);
        Assert.DoesNotContain("192.0.2.123", error.Message);
    }

    [Theory]
    [InlineData("<html><body>Страница проверки</body></html>", "карточку")]
    [InlineData("<meta property='og:title' content='Комикс'>", "количество страниц")]
    [InlineData("<meta property='og:title' content='Комикс'><span class='issueNumber'>1/1</span>", "изображение страницы")]
    public async Task AcomicsRejectsMissingLayoutWithUsefulError(string html, string expected)
    {
        using var getter = new AcomicsGetter(Config(_ => Html(html)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(ComicUrl));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task AcomicsHonorsPageRangeAndDoesNotRequireAuthorMetadata()
    {
        var requests = new List<string>();
        using var getter = new AcomicsGetter(Config(request =>
        {
            requests.Add(request.RequestUri.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith(".png"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII=")) };
            return Html("<meta property='og:title' content='Комикс'><span class='issueNumber'>1/400</span><img id='mainImage' src='/1.png'>");
        }, new Options { Start = 1, End = 1 }));

        var book = await getter.Get(ComicUrl);
        Assert.Single(Assert.Single(book.Chapters).Images);
        Assert.Equal("Комикс", book.Title);
        Assert.Equal("Acomics", book.Author.Name);
        Assert.Equal(new[] { "/~test/1", "/1.png" }, requests);
    }

    [Fact]
    public async Task AcomicsStopsOnRegionalBlockOnLaterPage()
    {
        using var getter = new AcomicsGetter(Config(request => Html(request.RequestUri.AbsolutePath.EndsWith("/1")
            ? "<meta property='og:title' content='Комикс'><span class='issueNumber'>1/2</span><img id='mainImage' src='/1.png'>"
            : "Сайт недоступен в вашем регионе")));
        var error = await Assert.ThrowsAsync<SiteAccessException>(() => getter.Get(ComicUrl));
        Assert.Equal(SiteAccessReason.AccessDenied, error.Reason);
        Assert.Contains("недоступен в вашем регионе", error.Message);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("book")]
    public async Task LibkingReadsCurrentAndPreviousTextContainers(string container)
    {
        using var getter = new LibkingGetter(Config(_ => Html($"<div itemprop='name'>Книга</div><div id='{container}'>" +
            "<div class='title'>Начало</div><p>Первый текст &lt;пример&gt;.</p>" +
            "<div class='title'>Продолжение</div><p>Второй текст.</p><div class='navigation'>Служебное</div></div>")));

        var book = await getter.Get(LibkingUrl);
        var chapters = book.Chapters.ToArray();
        Assert.Equal(2, chapters.Length);
        Assert.Contains("Первый текст &lt;пример&gt;", chapters[0].Content);
        Assert.Contains("Второй текст", chapters[1].Content);
        Assert.DoesNotContain("Служебное", chapters[1].Content);
    }

    [Theory]
    [InlineData("<div itemprop='name'>Книга</div>")]
    [InlineData("<div id='read'><div class='navigation'>Назад</div></div>")]
    [InlineData("<div id='read'><div class='title'>Только название</div></div>")]
    public async Task LibkingRejectsMissingOrEmptyText(string html)
    {
        using var getter = new LibkingGetter(Config(_ => Html(html)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(LibkingUrl));
        Assert.Contains("LibKing", error.Message);
    }

    [Fact]
    public async Task LibkingTrailingHeadingDoesNotCrashOrCreateEmptyChapter()
    {
        using var getter = new LibkingGetter(Config(_ => Html("<div id='read'><div class='title'>Глава</div><p>Текст.</p>" +
            "<div class='title'>Конец</div></div>")));
        var book = await getter.Get(LibkingUrl);
        Assert.Single(book.Chapters);
        Assert.Contains("Текст.", book.Chapters.Single().Content);
    }

    [Fact]
    public async Task HogwartsReadsSinglePartWithoutTocAndPreservesLineBreaksAndLiteralMarkup()
    {
        var handler = new AfAuditSupport.Handler(_ => HogwartsHtml(HogwartsHeader +
            "<div id='chap_text'><center>Заголовок</center>Первая строка.<br>Вторая &lt;строка&gt;.</div>"));
        using var getter = new HogwartsNetGetter(AfAuditSupport.Config(handler, new Options { NoImage = true }));

        var book = await getter.Get(HogwartsUrl);
        var chapter = Assert.Single(book.Chapters);
        Assert.Equal("Фанфик", chapter.Title);
        Assert.Equal("<p>Первая строка.</p><p>Вторая &lt;строка&gt;.</p>", chapter.Content);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task HogwartsStillReadsSelectedTocChapter()
    {
        var requests = new List<string>();
        using var getter = new HogwartsNetGetter(Config(request =>
        {
            requests.Add(request.RequestUri.Query);
            return HogwartsHtml(request.RequestUri.Query.Contains("chapter=2")
                ? "<div id='chap_text'>Текст второй главы.</div>"
                : HogwartsHeader + "<table><tr><td class='fichead_chapters'><select name='chapter'>" +
                  "<option value='1'>Первая</option><option value='2'>Вторая</option></select></td></tr></table>");
        }, new Options { NoImage = true, Start = 2, End = 2 }));

        var book = await getter.Get(HogwartsUrl);
        Assert.Equal("Вторая", Assert.Single(book.Chapters).Title);
        Assert.Equal(new[] { "?fid=123", "?fid=123&chapter=2" }, requests);
    }

    [Fact]
    public async Task HogwartsReportsLoginRequirementBeforeEmptyToc()
    {
        using var getter = new HogwartsNetGetter(Config(_ => HogwartsHtml(HogwartsHeader +
            "<div id='chap_text'>Текст фанфика доступен только зарегистрированным пользователям старше 18 лет</div>")));
        var error = await Assert.ThrowsAsync<SiteAccessException>(() => getter.Get(HogwartsUrl));
        Assert.Equal(SiteAccessReason.AgeConfirmationRequired, error.Reason);
        Assert.Contains("авторизацию по ID и паролю", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<div id='chap_text'> <center>Заголовок</center><br> </div>")]
    public async Task HogwartsRejectsAbsentOrEmptySinglePart(string chapter)
    {
        using var getter = new HogwartsNetGetter(Config(_ => HogwartsHtml(HogwartsHeader + chapter)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(HogwartsUrl));
        Assert.Contains("HogwartsNet", error.Message);
    }

    private const string HogwartsHeader = "<table><tr><td class='fichead'><a href='ffshowfic.php?fid=123'>Фанфик</a></td></tr></table>";
    private static BookGetterConfig Config(Func<HttpRequestMessage, HttpResponseMessage> response, Options options = null)
        => AfAuditSupport.Config(new AfAuditSupport.Handler(response), options ?? new Options { NoImage = true });
    private static HttpResponseMessage Html(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpResponseMessage HogwartsHtml(string text)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.GetEncoding(1251).GetBytes(text)) };
}
