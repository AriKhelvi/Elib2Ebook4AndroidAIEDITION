using System.Net;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.ExternalServices.Litsovet;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class LitsovetLayoutTests
{
    private const string Url = "https://litsovet.ru/books/1002083-vsyakoe-sluchaetsya";
    private const string Card = """
        <h1>Проверочная книга</h1>
        <div class="card-book big"><div class="item-author"><a href="/user/133746"><span itemprop="author">Автор</span></a></div></div>
        <div itemprop="description"><p>Аннотация</p></div>
        <div id="book-sections">
          <div class="card-glava"><div class="item-name"><a href="/books/1002083-vsyakoe-sluchaetsya/read/page-1"><h3>Первая</h3></a></div></div>
          <div class="card-glava"><div class="item-name"><a href="/books/1002083-vsyakoe-sluchaetsya/read/page-2"><h3>Вторая</h3></a></div></div>
        </div>
        """;

    [Fact]
    public async Task CurrentCardSuppliesAuthorAndTocAndOnlySelectedChapterIsFetched()
    {
        var requests = new List<string>();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            return Html(path.EndsWith("/page-2") ? "<div class='reader-text'><p>Текст &lt;сравнение&gt; &amp; данные.</p></div>" : Card);
        }), new Options { Start = 2, End = 2 });
        using var getter = new LitsovetGetter(config);

        var book = await getter.Get(new Uri(Url));

        Assert.Equal("Автор", book.Author.Name);
        Assert.Contains("Аннотация", book.Annotation);
        var chapter = Assert.Single(book.Chapters);
        Assert.Equal("Вторая", chapter.Title);
        Assert.Contains("&lt;сравнение&gt;", chapter.Content);
        Assert.Equal(new[] { "/books/1002083-vsyakoe-sluchaetsya", "/books/1002083-vsyakoe-sluchaetsya/read/page-2" }, requests);
    }

    [Fact]
    public async Task LockedSelectedChapterDoesNotBecomeEmptySuccessfulChapter()
    {
        var locked = Card.Replace("<a href=\"/books/1002083-vsyakoe-sluchaetsya/read/page-1\"><h3>Первая</h3></a>", "<h3>Первая</h3>");
        var handler = new AfAuditSupport.Handler(_ => Html(locked));
        using var config = AfAuditSupport.Config(handler, new Options { End = 1 });
        using var getter = new LitsovetGetter(config);

        var error = await Assert.ThrowsAsync<SiteAccessException>(() => getter.Get(new Uri(Url)));

        Assert.Contains("не предоставил доступ", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task LegacyTocAndReaderStillWork()
    {
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request => Html(request.RequestUri!.AbsolutePath switch
        {
            "/books/1002083-vsyakoe-sluchaetsya" => "<h1>Книга</h1><div class='p-book-author'><a href='/user/1'><span>Старый автор</span></a></div>",
            "/books/1002083-vsyakoe-sluchaetsya/read" or "/books/1002083-vsyakoe-sluchaetsya/read/" => "<div class='book-navi-list'><li><a onclick=\"open '/books/chapter-1'\">Глава</a></li></div>",
            _ => "<div class='reader'><p>Старый текст</p></div>"
        })));
        using var getter = new LitsovetGetter(config);

        var book = await getter.Get(new Uri(Url));

        Assert.Equal("Старый автор", book.Author.Name);
        Assert.Contains("Старый текст", Assert.Single(book.Chapters).Content);
    }

    [Fact]
    public async Task MissingReaderIsReportedWithoutNullReference()
    {
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request => Html(request.RequestUri!.AbsolutePath.EndsWith("/page-1") ? "<h1>Вход</h1>" : Card)), new Options { End = 1 });
        using var getter = new LitsovetGetter(config);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(new Uri(Url)));
        Assert.Contains("не вернул текст", error.Message);
    }

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };

    [Theory]
    [InlineData("")]
    [InlineData("<p>Начало главы доступно бесплатно.</p>")]
    public async Task PurchaseNoticeInsideReaderDoesNotBecomeSuccessfulChapter(string preview)
    {
        var reader = "<div class='reader-text'>" + preview + "<div class='card-alert'><div class='item-descr'><span>Для продолжения чтения книги, вам необходимо</span></div><a>Купить</a></div></div>";
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request => Html(request.RequestUri!.AbsolutePath.EndsWith("/page-1") ? reader : Card)), new Options { End = 1 });
        using var getter = new LitsovetGetter(config);
        var error = await Assert.ThrowsAsync<SiteAccessException>(() => getter.Get(new Uri(Url)));
        Assert.Equal(SiteAccessReason.AccessDenied, error.Reason);
    }

    [Fact]
    public async Task PaymentModalOutsideReaderDoesNotBlockFreeChapter()
    {
        const string reader = "<div class='reader-text'><p>Полный текст главы.</p></div><div class='modal-pay'><div class='card-alert'><div class='item-descr'>Для продолжения чтения книги, вам необходимо</div></div></div>";
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request => Html(request.RequestUri!.AbsolutePath.EndsWith("/page-1") ? reader : Card)), new Options { End = 1 });
        using var getter = new LitsovetGetter(config);
        var book = await getter.Get(new Uri(Url));
        Assert.Contains("Полный текст главы", Assert.Single(book.Chapters).Content);
    }
}
