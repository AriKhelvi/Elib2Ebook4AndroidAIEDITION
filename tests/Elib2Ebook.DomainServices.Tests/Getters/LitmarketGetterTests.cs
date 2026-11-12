using System.Net;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Litmarket;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitmarketGetterTests
{
    private static readonly Uri BookUri = new("https://litmarket.ru/books/test");

    [Theory]
    [InlineData("Глава 1. ", "Глава 1.")]
    [InlineData("Глава 2. \n ", "Глава 2.")]
    [InlineData("Глава\u00a03.\r\nНачало ", "Глава 3. Начало")]
    public async Task RemovesHeaderBlockWithTrailingSpacesAndLineBreaks(string sourceTitle, string title)
    {
        var heading = Block(0, "chapter", Text(sourceTitle));
        using var getter = CreateGetter([heading], [heading, Block(1, "unstyled", Text("Текст главы"))]);

        using var book = await getter.Get(BookUri);

        var chapter = Assert.Single(book.Chapters);
        Assert.Equal(title, chapter.Title);
        Assert.Equal("<p>Текст главы</p>", chapter.Content);
    }

    [Fact]
    public async Task CombinesStyledTitleFragmentsAndDecodesEntitiesBeforeComparingHeader()
    {
        var tocHeading = Block(0, "chapter", Text("Глава\u00a01. "), Text("Начало", "BOLD"));
        var textHeading = Block(0, "chapter", Text("Глава&nbsp;1."), Text("\nНачало", "ITALIC"));
        using var getter = CreateGetter([tocHeading], [textHeading, Block(1, "unstyled", Text("Текст главы"))]);

        using var book = await getter.Get(BookUri);

        var chapter = Assert.Single(book.Chapters);
        Assert.Equal("Глава 1. Начало", chapter.Title);
        Assert.Equal("<p>Текст главы</p>", chapter.Content);
    }

    [Fact]
    public async Task KeepsOrdinaryParagraphEvenWhenItMatchesTitle()
    {
        var heading = Block(0, "chapter", Text("Повтор"));
        using var getter = CreateGetter([heading], [heading, Block(1, "unstyled", Text("Повтор")), Block(2, "unstyled", Text("Дальше"))]);

        using var book = await getter.Get(BookUri);

        Assert.Equal("<p>Повтор</p><p>Дальше</p>", Assert.Single(book.Chapters).Content);
    }

    [Fact]
    public async Task DoesNotMistakeFirstOrdinaryParagraphForAHeader()
    {
        var heading = Block(0, "chapter", Text("Повтор"));
        using var getter = CreateGetter([heading], [Block(0, "unstyled", Text("Повтор")), Block(1, "unstyled", Text("Дальше"))]);

        using var book = await getter.Get(BookUri);

        Assert.Equal("<p>Повтор</p><p>Дальше</p>", Assert.Single(book.Chapters).Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastSelectedChapterStopsAtNextFullTocBoundary(bool byName)
    {
        var headings = Enumerable.Range(1, 3).Select(i => Block((i - 1) * 10, "chapter", Text($"Глава {i}. \n "))).ToArray();
        var blocks = headings.SelectMany((heading, i) => new[] { heading, Block(i * 10 + 1, "unstyled", Text($"Текст {i + 1}")) }).ToArray();
        var options = byName
            ? new Options { StartName = "Глава 2.", EndName = "Глава 2." }
            : new Options { Start = 2, End = 2 };
        using var getter = CreateGetter(headings, blocks, options);

        using var book = await getter.Get(BookUri);

        var chapter = Assert.Single(book.Chapters);
        Assert.Equal("Глава 2.", chapter.Title);
        Assert.Equal("<p>Текст 2</p>", chapter.Content);
        Assert.DoesNotContain("Текст 3", chapter.Content);
    }

    [Fact]
    public async Task EmptyTocKeepsFirstParagraphMatchingBookTitle()
    {
        using var getter = CreateGetter([], [Block(0, "unstyled", Text("Книга")), Block(1, "unstyled", Text("Текст"))]);

        using var book = await getter.Get(BookUri);

        var chapter = Assert.Single(book.Chapters);
        Assert.Equal("Книга", chapter.Title);
        Assert.Equal("<p>Книга</p><p>Текст</p>", chapter.Content);
    }

    private static object Text(string text, params string[] styles)
        => new { type = "INLINE", text, styles };

    private static object Block(int index, string type, params object[] mods)
        => new { index, chunk = new { type, mods } };

    private static LitmarketGetter CreateGetter(object[] toc, object[] blocks, Options options = null)
        => new(new BookGetterConfig(options ?? new Options(), new HttpClient(new FakeHandler(toc, blocks)), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance));

    private sealed class FakeHandler(object[] toc, object[] blocks) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/books/test" => "<h1 class='card-title'>Книга</h1><div class='card-author'>Автор</div>",
                "/reader/data/test" => JsonSerializer.Serialize(new { book = new { ebookId = 123 }, tableOfContent = JsonSerializer.Serialize(toc) }),
                "/reader/blocks/123" => JsonSerializer.Serialize(blocks),
                _ => throw new InvalidOperationException($"Неожиданный запрос {request.RequestUri}")
            };
            return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request, new StringContent(body)));
        }
    }
}
