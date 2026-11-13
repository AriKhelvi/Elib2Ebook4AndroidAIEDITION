using System.Net;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Bookuruk;
using Elib2Ebook.ExternalServices.FictionBook;
using Elib2Ebook.ExternalServices.Freedom.Getters;
using Elib2Ebook.ExternalServices.Jaomix;
using Elib2Ebook.ExternalServices.RanobeHub;
using Elib2Ebook.ExternalServices.TwilightRussia;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class ChangedLayoutGetterTests
{
    [Fact]
    public async Task BookurukReadsAuthorFromItsOwnLink()
    {
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => Html(
            "<h1>Книга</h1><a class='bookcard_author' href='/author/test'>Автор</a>")), new Options { NoChapters = true });
        using var getter = new BookurukGetter(config);
        using var book = await getter.Get(new Uri("https://bookuruk.com/book/test"));
        Assert.Equal("Автор", book.Author.Name);
        Assert.Equal("https://bookuruk.com/author/test", book.Author.Url.ToString());
    }

    [Fact]
    public async Task BookurukMissingAuthorIsAnExplicitMetadataWarning()
    {
        var logger = new AfAuditSupport.AuditLogger();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => Html("<h1>Книга</h1>")),
            new Options { NoChapters = true }, logger);
        using var getter = new BookurukGetter(config);
        using var book = await getter.Get(new Uri("https://bookuruk.com/book/test"));
        Assert.Equal("Bookuruk", book.Author.Name);
        Assert.Single(logger.Warnings);
    }

    [Fact]
    public async Task FreedomRejectsMissingTitleBeforeDownloadingOrSaving()
    {
        var handler = new AfAuditSupport.Handler(_ => Html("<html><title>Служебная страница</title></html>"));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new FreedomGetter(config);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://ifreedom.su/ranobe/test/")));
        Assert.Contains("название", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FreedomModernTocKeepsPaidPositionsBeforeSlicing()
    {
        var paths = new List<string>();
        var logger = new AfAuditSupport.AuditLogger();
        var handler = new AfAuditSupport.Handler(request =>
        {
            var path = request.RequestUri.AbsolutePath;
            paths.Add(path);
            return Html(path.StartsWith("/ranobe/")
                ? "<div class='book-info'><h1>Книга</h1></div><div class='chapterlinks'><div class='chapterinfo'><a href='/chapter/3'>Третья</a></div><div class='chapterinfo notload'><a href='/podpiska/'>Вторая</a></div><div class='chapterinfo'><a href='/chapter/1'>Первая</a></div></div>"
                : "<div class='chapter-content'><p>Текст третьей.</p><div class='pc-adv'>Реклама</div><h2>Внутренний подзаголовок</h2></div>");
        });
        using var config = AfAuditSupport.Config(handler, new Options { Start = 2, End = 3, NoImage = true }, logger);
        using var getter = new FreedomGetter(config);
        using var book = await getter.Get(new Uri("https://ifreedom.su/ranobe/test/"));
        var chapter = Assert.Single(book.Chapters);
        Assert.Equal("Третья", chapter.Title);
        Assert.Contains("Внутренний подзаголовок", chapter.Content);
        Assert.DoesNotContain("Реклама", chapter.Content);
        Assert.Equal(["/ranobe/test/", "/chapter/3"], paths);
        Assert.Contains("подписки", Assert.Single(logger.Warnings));
    }

    [Fact]
    public async Task BookHamsterLegacyMarkupRemainsSupported()
    {
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request => Html(request.RequestUri.AbsolutePath.StartsWith("/ranobe/")
            ? "<h1 class='entry-title'>Книга</h1><div class='li-col1-ranobe'><a href='/chapter/1'>Первая</a></div>"
            : "<div class='entry-content'><p>Старый шаблон.</p></div>")), new Options { NoImage = true });
        using var getter = new BookHamsterGetter(config);
        using var book = await getter.Get(new Uri("https://bookhamster.ru/ranobe/test/"));
        Assert.Equal("Книга", book.Title);
        Assert.Contains("Старый шаблон", Assert.Single(book.Chapters).Content);
    }

    [Fact]
    public async Task FreedomEntirelyPaidRangeDoesNotProduceEmptyBook()
    {
        var handler = new AfAuditSupport.Handler(_ => Html("<div class='book-info'><h1>Книга</h1></div><div class='chapterlinks'><div class='chapterinfo notload'><a href='/podpiska/'>Первая</a></div></div>"));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new FreedomGetter(config);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://ifreedom.su/ranobe/test/")));
        Assert.Contains("нет доступного текста", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RanobeHubModernLayoutDoesNotFallThroughToOldApi()
    {
        // В живой карточке больше нет старых html[data-id]/h1.header.
        var handler = new AfAuditSupport.Handler(_ => Html("<html data-site-version='modern'><h1>Книга</h1><script>self.__next_f.push([])</script></html>"));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new RanobeHubGetter(config);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://ranobehub.org/ranobe/947-test")));
        Assert.Contains("неподдерживаемой разметке", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task JaomixCollectsEveryTocPageBeforeApplyingChapterRange()
    {
        var pages = new List<int>();
        var chapters = new List<int>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("https://jaomix.ru/test/", request.Headers.Referrer.ToString());
                var form = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("action=loadpagenavchapstt", form);
                var page = int.Parse(form.Split("page=")[1]);
                pages.Add(page);
                return Html(Toc(page == 2 ? Enumerable.Range(2, 50).Reverse() : [1]));
            }
            var segment = request.RequestUri.Segments.Last().Trim('/');
            if (segment == "test")
                return Html("<h1>Книга</h1><select class='sel-toc'><option value='1'></option><option value='2'></option><option value='3'></option></select>" + Toc(Enumerable.Range(52, 50).Reverse()));
            var chapter = int.Parse(segment);
            chapters.Add(chapter);
            return Html($"<div class='themeform'><h1>Глава {chapter}</h1><p class='text'>Текст {chapter}</p><h2>Подзаголовок</h2><p>Конец.</p></div>");
        });
        using var config = AfAuditSupport.Config(handler, new Options { Start = 100, End = 101, NoImage = true });
        using var getter = new JaomixGetter(config);
        using var book = await getter.Get(new Uri("https://jaomix.ru/test/"));
        Assert.Equal([2, 3], pages);
        Assert.Equal([100, 101], chapters);
        Assert.Equal(["Глава 100", "Глава 101"], book.Chapters.Select(c => c.Title));
        Assert.All(book.Chapters, c => { Assert.DoesNotContain("<h1>", c.Content); Assert.Contains("<p class='text'>Текст", c.Content); Assert.Contains("<h2>Подзаголовок</h2>", c.Content); });
    }

    [Fact]
    public async Task JaomixLegacyHiddenTocDoesNotRequestAlreadyEmbeddedPages()
    {
        var handler = new AfAuditSupport.Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Html(request.RequestUri.AbsolutePath == "/test/"
                ? "<h1>Книга</h1><select class='sel-toc'><option value='1'></option><option value='2'></option></select><form class='download-chapter'><div class='hiddenstab'>" + Toc([2, 1]) + "</div></form>"
                : "<div class='themeform'><p>Текст.</p></div>");
        });
        using var config = AfAuditSupport.Config(handler, new Options { NoImage = true });
        using var getter = new JaomixGetter(config);
        using var book = await getter.Get(new Uri("https://jaomix.ru/test/"));
        Assert.Equal(["Глава 1", "Глава 2"], book.Chapters.Select(c => c.Title));
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task JaomixRejectsRepeatedTocPageInsteadOfSavingPartialBook()
    {
        var toc = Toc([3, 2]);
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request => Html(request.Method == HttpMethod.Post
            ? toc : "<h1>Книга</h1><select class='sel-toc'><option value='1'></option><option value='2'></option></select>" + toc)));
        using var getter = new JaomixGetter(config);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://jaomix.ru/test/")));
        Assert.Contains("оглавление неполное", error.Message);
    }

    [Theory]
    [InlineData("<h1>Книга</h1>")]
    [InlineData("<h1>Книга</h1><div id='chapters-book'></div>")]
    public async Task JaomixRejectsEmptyToc(string html)
    {
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => Html(html)));
        using var getter = new JaomixGetter(config);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://jaomix.ru/test/")));
        Assert.Contains("оглавления", error.Message);
    }

    [Fact]
    public async Task JaomixCaptchaStopsWithoutConsoleOrRetryLoop()
    {
        var handler = new AfAuditSupport.Handler(request => Html(request.RequestUri.AbsolutePath == "/test/"
            ? "<h1>Книга</h1>" + Toc([1])
            : "<div class='themeform'><div class='but-captcha'>Проверка</div></div>"));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new JaomixGetter(config);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://jaomix.ru/test/")));
        Assert.Contains("CAPTCHA", error.Message);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("https://https//twilightrussia.ru/publ/first", 1, "Первая", "first")]
    [InlineData("https://https://twilightrussia.ru/publ/first", 1, "Первая", "first")]
    [InlineData("https://https://twilightrussia.ru/publ/first", 2, "Вторая", "second")]
    public async Task TwilightRepairsKnownDuplicateSchemeAndHonorsRange(string link, int position, string title, string slug)
    {
        var requests = new List<string>();
        var logger = new AfAuditSupport.AuditLogger();
        var handler = new AfAuditSupport.Handler(request =>
        {
            requests.Add(request.RequestUri.AbsoluteUri);
            return Html(request.RequestUri.AbsolutePath.StartsWith("/forum/")
                ? $"<a class='forumBarA'>Книга</a><span class='forum_nik'>Автор</span><table><td class='content-block-forum'><a href='{link}'>Первая</a><a href='/publ/second'>Вторая</a></td></table>"
                : "<div id='msgd'><p>Текст главы.</p></div>");
        });
        using var config = AfAuditSupport.Config(handler, new Options { Start = position, End = position, NoImage = true }, logger);
        using var getter = new TwilightRussiaGetter(config);
        using var book = await getter.Get(new Uri("https://twilightrussia.ru/forum/37-123-1"));
        Assert.Equal(title, Assert.Single(book.Chapters).Title);
        Assert.Equal(["https://twilightrussia.ru/forum/37-123-1", "https://twilightrussia.ru/publ/" + slug], requests);
        Assert.Single(logger.Warnings);
    }

    [Theory]
    [InlineData("https://https//other.test/publ/chapter")]
    [InlineData("https://https/publ/chapter")]
    [InlineData("javascript:/publ/chapter")]
    public async Task TwilightRejectsCorruptLinkBeforeNetworkRequest(string link)
    {
        var handler = new AfAuditSupport.Handler(_ => Html($"<table><td class='content-block-forum'><a href='{link}'>Глава</a></td></table>"));
        using var config = AfAuditSupport.Config(handler);
        using var getter = new TwilightRussiaGetter(config);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://twilightrussia.ru/forum/37-123-1")));
        Assert.Contains("повреждённая ссылка", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FictionBookJoinsPageFragmentsBeforeSplittingSections()
    {
        var requests = new List<string>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            var path = request.RequestUri.AbsolutePath;
            requests.Add(path);
            return Html(path.EndsWith("page-2/")
                ? NextRead(2, 2, "<p>Продолжение первой.</p></section><section><h3 id='second'>Вторая</h3><p>Текст второй.</p></section>")
                : path.EndsWith("read_online/")
                    ? NextRead(1, 2, "<section><h3 id='first'>Первая</h3><p>Начало первой.</p>")
                    : NextBook());
        });
        using var config = AfAuditSupport.Config(handler, new Options { NoImage = true, Start = 1, End = 2 });
        using var getter = new FictionBookGetter(config);
        using var book = await getter.Get(new Uri("https://fictionbook.ru/author/test/book/read_online/page-2/"));
        Assert.Equal("Книга", book.Title);
        Assert.Equal("Имя Фамилия", book.Author.Name);
        var chapters = book.Chapters.ToList();
        Assert.Equal(2, chapters.Count);
        Assert.Equal("Первая", chapters[0].Title);
        Assert.Contains("Начало первой.", chapters[0].Content);
        Assert.Contains("Продолжение первой.", chapters[0].Content);
        Assert.DoesNotContain("<h3", chapters[0].Content);
        Assert.Contains("id=\"first\"", chapters[0].Content);
        Assert.Equal("Вторая", chapters[1].Title);
        Assert.Equal(["/author/test/book/", "/author/test/book/read_online/", "/author/test/book/read_online/page-2/"], requests);
    }

    [Theory]
    [InlineData(1, "<section><h3>Повтор</h3><p>Не та страница.</p></section>")]
    [InlineData(2, "")]
    public async Task FictionBookRejectsWrongOrEmptyReaderPage(int page, string text)
    {
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request => Html(
            request.RequestUri.AbsolutePath.EndsWith("page-2/") ? NextRead(page, 2, text) :
            request.RequestUri.AbsolutePath.EndsWith("read_online/") ? NextRead(1, 2, "<section><h3>Первая</h3><p>Текст.</p>") : NextBook())));
        using var getter = new FictionBookGetter(config);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri("https://fictionbook.ru/author/test/book/")));
        Assert.Contains("неполной", error.Message);
    }

    private static string Toc(IEnumerable<int> chapters) => "<div class='columns-toc'>" + string.Concat(chapters.Select(n =>
        $"<div class='flex-dow-txt'><a href='https://jaomix.ru/test/{n}/'><h2>Глава {n}</h2></a></div>")) + "</div>";

    private static string NextBook() => Next(new { book = new { book = new { title = "Книга", description = "Описание" }, author = new { first_name = "Имя", last_name = "Фамилия", alias = "test" } } });
    private static string NextRead(int page, int pages, string content) => Next(new { read = new { page, pages, content } });
    private static string Next(object pageProps) => "<script id='__NEXT_DATA__' type='application/json'>" + JsonSerializer.Serialize(new { props = new { pageProps } }) + "</script>";
    private static HttpResponseMessage Html(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
}
