using System.Net;
using System.Xml.Linq;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Bookuruk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class BookurukContentTests
{
    private static readonly Uri BookUrl = new("https://bookuruk.com/book/test");
    private static readonly XNamespace Fb = "http://www.gribuser.ru/xml/fictionbook/2.0";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";

    // Синтетические варианты служебного контейнера, а не копии ответа сайта из журнала.
    [Theory]
    [InlineData("<script>document.write('Служебный код');</script>")]
    [InlineData("<style>p { color: black; }</style>")]
    [InlineData("<template><p>Шаблон будущего текста.</p></template>")]
    [InlineData("<script>var state = 1;</script><style>p { display: block; }</style><p>&nbsp;</p>")]
    [InlineData("<!-- Только комментарий --><p> </p>")]
    public async Task ServiceOnlyReaderIsRejectedInsteadOfBecomingAnEmptyBook(string content)
    {
        using var config = Create(content);
        using var getter = new BookurukGetter(config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(BookUrl));

        Assert.Contains("не вернул читаемый текст", error.Message);
    }

    [Fact]
    public async Task RealTextAndAnnotationSurviveCleanupAndFb2HasNoDanglingImageLinks()
    {
        var logger = new DebugLogger();
        const string secret = "PRIVATE-DIAGNOSTIC-MARKER";
        var content = "<p>Начало <em>настоящей главы</em>.</p>" +
                      $"<script>var token='{secret}';</script><style>.reader {{ color: black; }}</style>" +
                      "<template><p>Не опубликованный шаблон.</p></template><p>Продолжение.</p>";
        var annotation = "<b>Аннотация:</b><p>Описание <em>книги</em>.</p><img src='/unsupported.jpg'>" +
                         "<script>service()</script><style>.x{color:red}</style><template>Шаблон</template>";
        using var config = Create(content, annotation: annotation, cover: true, logger: logger);
        using var getter = new BookurukGetter(config);

        using var book = await getter.Get(BookUrl);
        var chapter = Assert.Single(book.Chapters);
        Assert.Contains("<em>настоящей главы</em>", chapter.Content);
        Assert.Contains("Продолжение.", chapter.Content);
        Assert.DoesNotContain(secret, chapter.Content);
        Assert.DoesNotContain("<script", chapter.Content);
        Assert.DoesNotContain("<style", chapter.Content);
        Assert.DoesNotContain("<template", chapter.Content);
        Assert.Contains("Описание <em>книги</em>.", book.Annotation);
        Assert.DoesNotContain("<img", book.Annotation);
        Assert.DoesNotContain("<script", book.Annotation);
        Assert.DoesNotContain("<style", book.Annotation);
        Assert.DoesNotContain("<template", book.Annotation);

        await new Fb2Builder(new Options { SavePath = config.TempFolder.Path, BookNamePattern = "{Book.Title}" }, NullLogger.Instance).Build(book);
        var document = XDocument.Load(Path.Combine(config.TempFolder.Path, "Книга.fb2"));
        var binary = Assert.Single(document.Root.Elements(Fb + "binary"));
        var image = Assert.Single(document.Descendants(Fb + "image"));
        Assert.Equal("#" + binary.Attribute("id").Value, image.Attribute(Xlink + "href").Value);
        var body = document.Descendants(Fb + "section").Single().Elements().Where(element => element.Name != Fb + "title");
        Assert.Contains("настоящей главы", string.Concat(body.Select(element => element.Value)));

        Assert.Contains(logger.Messages, message => message.Contains("stage=chapter-before") && message.Contains("scripts=1") && message.Contains("styles=1") && message.Contains("templates=1"));
        Assert.Contains(logger.Messages, message => message.Contains("stage=chapter-after") && message.Contains("scripts=0") && message.Contains("styles=0") && message.Contains("templates=0"));
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(secret, message);
            Assert.DoesNotContain("настоящей главы", message);
            Assert.DoesNotContain("unsupported.jpg", message);
        });
    }

    [Fact]
    public async Task ReaderWithDownloadedIllustrationIsNotRejectedAsEmpty()
    {
        using var config = Create("<img src='/chapter.jpg'><script>service()</script>");
        using var getter = new BookurukGetter(config);

        using var book = await getter.Get(BookUrl);

        var chapter = Assert.Single(book.Chapters);
        var image = Assert.Single(chapter.Images);
        Assert.Contains(image.FullName, chapter.Content);
        Assert.DoesNotContain("<script", chapter.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderWithoutTextOrSavedIllustrationsIsRejectedAfterImageProcessing(bool noImage)
    {
        using var config = Create("<img src='/chapter.jpg'><script>service()</script>",
            options: new Options { NoImage = noImage }, imageStatus: HttpStatusCode.Forbidden);
        using var getter = new BookurukGetter(config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(BookUrl));

        Assert.Contains("не вернул читаемый текст", error.Message);
    }

    [Fact]
    public async Task ChapterHttpFailureIsNotReclassifiedAsEmptyContent()
    {
        using var config = Create("Недоступно", chapterStatus: HttpStatusCode.Forbidden);
        using var getter = new BookurukGetter(config);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUrl));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    private static BookGetterConfig Create(string content, string annotation = null, bool cover = false,
        Options options = null, ILogger logger = null, HttpStatusCode imageStatus = HttpStatusCode.OK,
        HttpStatusCode chapterStatus = HttpStatusCode.OK)
        => AfAuditSupport.Config(new AfAuditSupport.Handler(request => request.RequestUri.AbsolutePath switch
        {
            "/book/test" => Html("<h1>Книга</h1><a class='bookcard_author' href='/author/test'>Автор</a>" +
                "<div class='content aside-right'><li><a href='/book/test'>Карточка</a></li><li><a href='/reader/123'>Читать</a></li></div>" +
                (cover ? "<div class='single-prew__img'><img src='/cover.png'></div>" : "") +
                (annotation == null ? "" : "<div class='single-prew__desc_anot'>" + annotation + "</div>")),
            "/book/chapters/123" => Html("<a class='read-chapters-item' href='/reader/123'>Глава 1</a>"),
            "/reader/123" => Html("<div class='read-area'>" + content + "</div>", chapterStatus),
            "/chapter.jpg" or "/cover.png" => new HttpResponseMessage(imageStatus) { Content = new ByteArrayContent([255, 216, 255, 224]) },
            _ => throw new InvalidOperationException("Неожиданный запрос")
        }), options, logger);

    private static HttpResponseMessage Html(string text, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(text) };

    private sealed class DebugLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (level == LogLevel.Debug) Messages.Add(formatter(state, exception));
        }
    }
}
