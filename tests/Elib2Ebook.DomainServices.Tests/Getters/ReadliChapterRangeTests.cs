using System.Net;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.ExternalServices.Readli;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class ReadliChapterRangeTests
{
    private static readonly Uri Url = new("https://readli.net/test-book/");

    [Fact]
    public async Task EndStopsAtNextHeadingAndKeepsContinuationAcrossPages()
    {
        var visited = new List<int>();
        using var getter = Create(new Options { End = 1, NoImage = true }, 96, visited, page => page switch
        {
            1 => Html("<h3>Первая</h3><p>Начало &lt;пример&gt;.</p>"),
            2 => Html("<p>Продолжение первой.</p><h3>Вторая</h3><p>Чужой текст.</p>"),
            _ => throw new InvalidOperationException("Не нужно загружать следующие страницы")
        });

        var book = await getter.Get(Url);

        var chapter = Assert.Single(book.Chapters);
        Assert.Equal("Первая", chapter.Title);
        Assert.Contains("Начало &lt;пример&gt;.", chapter.Content);
        Assert.Contains("Продолжение первой.", chapter.Content);
        Assert.DoesNotContain("Чужой", chapter.Content);
        Assert.Equal(new[] { 1, 2 }, visited);
    }

    [Fact]
    public async Task StartAndEndSelectOnlyRequestedChapterAndItsImages()
    {
        var visited = new List<int>();
        var images = new List<string>();
        using var getter = Create(new Options { Start = 2, End = 2 }, 96, visited, page => page switch
        {
            1 => Html("<h3>Первая</h3><p>Текст.</p><img src='/skip.jpg'><h3>Вторая</h3><p>Начало второй.</p>"),
            2 => Html("<p>Конец второй.</p><img src='/keep.jpg'><h3>Третья</h3><img src='/also-skip.jpg'>"),
            _ => throw new InvalidOperationException("Не нужно загружать третью страницу")
        }, image =>
        {
            images.Add(image);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([255, 216, 255, 224]) };
        });

        var book = await getter.Get(Url);

        var chapter = Assert.Single(book.Chapters);
        Assert.Equal("Вторая", chapter.Title);
        Assert.Contains("Начало второй.", chapter.Content);
        Assert.Contains("Конец второй.", chapter.Content);
        Assert.Single(chapter.Images);
        Assert.Equal(new[] { "/keep.jpg" }, images);
        Assert.Equal(new[] { 1, 2 }, visited);
    }

    [Fact]
    public async Task BookWithoutHeadingsRemainsOneWholeChapter()
    {
        var visited = new List<int>();
        using var getter = Create(new Options { End = 1, NoImage = true }, 3, visited, page => Html($"<p>Страница {page}.</p>"));

        var book = await getter.Get(Url);

        var chapter = Assert.Single(book.Chapters);
        Assert.Equal("Книга", chapter.Title);
        foreach (var page in new[] { 1, 2, 3 }) Assert.Contains($"Страница {page}.", chapter.Content);
        Assert.Equal(new[] { 1, 2, 3 }, visited);
    }

    [Fact]
    public async Task LastSelectedChapterEndsAtBookEndWithoutAnotherHeading()
    {
        var visited = new List<int>();
        using var getter = Create(new Options { Start = 2, End = 2, NoImage = true }, 3, visited, page => page switch
        {
            1 => Html("<h3>Первая</h3><p>Первая часть.</p><h3>Вторая</h3><p>Начало второй.</p>"),
            2 => Html("<p>Продолжение второй.</p>"),
            _ => Html("<p>Конец второй.</p>")
        });

        var chapter = Assert.Single((await getter.Get(Url)).Chapters);
        Assert.Equal("Вторая", chapter.Title);
        Assert.Contains("Конец второй.", chapter.Content);
        Assert.Equal(new[] { 1, 2, 3 }, visited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegativeIndicesAndNamedRangesUseCompleteChapterList(bool byName)
    {
        var visited = new List<int>();
        var options = byName ? new Options { StartName = "Глава 2", EndName = "Глава 2", NoImage = true }
            : new Options { Start = -1, NoImage = true };
        using var getter = Create(options, 3, visited, page => Html($"<h3>Глава {page}</h3><p>Текст {page}.</p>"));

        var chapter = Assert.Single((await getter.Get(Url)).Chapters);
        Assert.Equal(byName ? "Глава 2" : "Глава 3", chapter.Title);
        Assert.Equal(new[] { 1, 2, 3 }, visited);
    }

    [Fact]
    public async Task PreviewDoesNotRequestChapterPages()
    {
        var visited = new List<int>();
        using var getter = Create(new Options { NoChapters = true }, 96, visited, _ => throw new InvalidOperationException("Предпросмотр не должен читать главы"));

        var book = await getter.Get(Url);

        Assert.Equal("Книга", book.Title);
        Assert.Empty(book.Chapters);
        Assert.Empty(visited);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task UnfinishedChapterDoesNotHideHttpFailure(HttpStatusCode status)
    {
        var visited = new List<int>();
        using var getter = Create(new Options { End = 1, NoImage = true }, 3, visited, page => page == 1
            ? Html("<h3>Первая</h3><p>Начало.</p>")
            : new HttpResponseMessage(status) { Content = new StringContent("Страница недоступна") });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(Url));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(new[] { 1, 2 }, visited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptyPageDoesNotBecomeTruncatedSuccess(bool empty)
    {
        var visited = new List<int>();
        using var getter = Create(new Options { End = 1, NoImage = true }, 3, visited, page => page == 1
            ? Html("<h3>Первая</h3><p>Начало.</p>")
            : empty ? Html("<script>служебное</script>") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<h1>Проверка доступа</h1>") });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(Url));

        Assert.Contains("Readli", error.Message);
        Assert.Equal(new[] { 1, 2 }, visited);
    }

    private static ReadliGetter Create(Options options, int pageCount, List<int> visited,
        Func<int, HttpResponseMessage> pageResponse, Func<string, HttpResponseMessage> imageResponse = null)
    {
        return new ReadliGetter(AfAuditSupport.Config(new AfAuditSupport.Handler(request =>
        {
            if (request.RequestUri.AbsolutePath.EndsWith(".jpg"))
                return imageResponse?.Invoke(request.RequestUri.AbsolutePath) ?? throw new InvalidOperationException("Лишнее изображение");
            var number = request.RequestUri.GetQueryParameter("pg");
            if (number != null)
            {
                var page = int.Parse(number);
                visited.Add(page);
                return pageResponse(page);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"<span class='button-pages__right'>{pageCount} страниц</span>" +
                    "<div class='book-image'><a href='/chitat-online/?b=123'>Читать</a></div>" +
                    "<h1 class='main-info__title'>Книга</h1><a class='main-info__link' href='/author'>Автор</a>")
            };
        }), options));
    }

    private static HttpResponseMessage Html(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent($"<div class='reading__text'>{body}</div>") };
}
