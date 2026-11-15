using System.Net;
using System.Web;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Prodaman;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class ProdamanRangeTests
{
    private static readonly Uri BookUrl = new("https://prodaman.ru/TestAuthor/books/Book");
    private static readonly string[] Titles = ["Глава один: начало пути", "Глава два: долгая дорога", "Глава три: возвращение домой", "Глава четыре: новая встреча", "Глава пять: конец"];
    private static readonly string[] Pages =
    [
        $" \n<!-- оформление --> <h3>{Titles[0]}</h3>Начало первой.<br/>Конец первой.<img src='/unselected.jpg'/><h3>{Titles[1]}</h3>Начало второй.<br/>",
        $"Продолжение второй.<br/>Конец второй.<h3>{Titles[2]}</h3>Начало третьей.<br/>",
        $"Продолжение третьей.<h3>***</h3>Конец третьей.<h3>{Titles[3]}</h3>Начало четвёртой.<br/>",
        $"Конец четвёртой.<h3>{Titles[4]}</h3>Текст пятой."
    ];

    [Fact]
    public async Task RangeKeepsWholeChaptersAcrossPageBoundariesAndStopsBeforeUnneededPage()
    {
        using var handler = new FakeHandler();
        using var config = AfAuditSupport.Config(handler, new Options { Start = 2, End = 3 });
        using var getter = new ProdamanGetter(config);

        using var book = await getter.Get(BookUrl);

        var chapters = book.Chapters.ToArray();
        Assert.Equal(Titles[1..3], chapters.Select(chapter => chapter.Title));
        Assert.Equal(new[] { 1, 2, 3 }, handler.Pages);
        Assert.Contains("Начало второй", chapters[0].Content);
        Assert.Contains("Конец второй", chapters[0].Content);
        Assert.Contains("Начало третьей", chapters[1].Content);
        Assert.Contains("Конец третьей", chapters[1].Content);
        Assert.DoesNotContain("первой", chapters[0].Content);
        Assert.DoesNotContain("четвёртой", chapters[1].Content);
        Assert.All(chapters, chapter => Assert.Empty(chapter.Images));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task EndOnlyDoesNotReadPagesAfterTheSelectedChapterEnds(int end, int pageCount)
    {
        using var handler = new FakeHandler();
        using var config = AfAuditSupport.Config(handler, new Options { End = end, NoImage = true });
        using var getter = new ProdamanGetter(config);
        using var book = await getter.Get(BookUrl);

        Assert.Equal(Titles[..end], book.Chapters.Select(chapter => chapter.Title));
        Assert.Equal(Enumerable.Range(1, pageCount), handler.Pages);
        Assert.DoesNotContain(book.Chapters, chapter => chapter.Title == "Книга"); // Пробелы перед h3 не образуют лишнюю главу.
    }

    [Theory]
    [InlineData("all")]
    [InlineData("names")]
    [InlineData("tail")]
    public async Task WholeBookNamesAndNegativeIndexesPreserveOrderingAndPageContinuations(string mode)
    {
        var options = mode switch
        {
            "names" => new Options { StartName = Titles[1], EndName = Titles[2], NoImage = true },
            "tail" => new Options { Start = -2, NoImage = true },
            _ => new Options { NoImage = true }
        };
        using var handler = new FakeHandler();
        using var config = AfAuditSupport.Config(handler, options);
        using var getter = new ProdamanGetter(config);
        using var book = await getter.Get(BookUrl);

        var chapters = book.Chapters.ToArray();
        var expected = mode == "names" ? Titles[1..3] : mode == "tail" ? Titles[3..] : Titles;
        Assert.Equal(expected, chapters.Select(chapter => chapter.Title));
        Assert.Equal(new[] { 1, 2, 3, 4 }, handler.Pages);
        if (mode != "names")
        {
            var fourth = chapters.Single(chapter => chapter.Title == Titles[3]);
            Assert.Contains("Начало четвёртой", fourth.Content);
            Assert.Contains("Конец четвёртой", fourth.Content);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<int> Pages { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            string html;
            if (query["nav"] == "ok") html = "<div class='pageList'><a>1</a><a>4</a></div>";
            else if (int.TryParse(query["page"], out var number))
            {
                Pages.Add(number);
                html = "<div class='blog-text'>" + ProdamanRangeTests.Pages[number - 1] + "</div>";
            }
            else if (request.RequestUri.AbsolutePath == BookUrl.AbsolutePath) html = "<h1>Книга</h1>";
            else throw new InvalidOperationException("Неожиданный запрос: " + request.RequestUri.AbsolutePath);
            return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request, new StringContent(html)));
        }
    }
}
