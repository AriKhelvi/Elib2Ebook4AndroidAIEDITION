using E2E.Droid.Services;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Microsoft.Extensions.Logging.Abstractions;

namespace ServiceTests;

public sealed class MangaDownloadLimitTests
{
    private const string MangaUrl = "https://mangalib.me/ru/manga/206--one-piece";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1250)]
    public void AtMost1250SelectedChaptersAreAllowed(int count)
    {
        using var getter = CreateGetter(new Options());

        var downloaded = getter.Download(Chapters(count));

        Assert.Equal(count, downloaded.Count);
        Assert.Equal(count, getter.ChapterRequests);
    }

    [Theory]
    [InlineData(1251)]
    [InlineData(4000)]
    public void OversizedSelectionFailsBeforeAnyChapterIsRequested(int count)
    {
        using var getter = CreateGetter(new Options());

        var error = Assert.Throws<MangaChapterLimitException>(() => getter.Download(Chapters(count)));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(0, getter.ChapterRequests);
    }

    [Theory]
    [InlineData(2001, 3250, 2001, 3250)]
    [InlineData(2751, null, 2751, 4000)]
    [InlineData(null, 1250, 1, 1250)]
    [InlineData(-1250, null, 2751, 4000)]
    [InlineData(null, -2750, 1, 1250)]
    [InlineData(-1251, -1, 2750, 3999)]
    public void LimitCountsSelectedChaptersInsteadOfTheirNumbers(int? start, int? end, int first, int last)
    {
        using var getter = CreateGetter(new Options { Start = start, End = end });

        var downloaded = getter.Download(Chapters(4000));

        Assert.Equal(1250, downloaded.Count);
        Assert.Equal($"Глава {first}", downloaded[0]);
        Assert.Equal($"Глава {last}", downloaded[^1]);
        Assert.Equal(1250, getter.ChapterRequests);
    }

    [Theory]
    [InlineData(2001, 3251)]
    [InlineData(2750, null)]
    [InlineData(null, 1251)]
    [InlineData(-1251, null)]
    [InlineData(null, -2749)]
    public void OversizedRangeIsRejectedWithoutSilentlyTakingFirst1250(int? start, int? end)
    {
        using var getter = CreateGetter(new Options { Start = start, End = end });

        Assert.Throws<MangaChapterLimitException>(() => getter.Download(Chapters(4000)));

        Assert.Equal(0, getter.ChapterRequests);
    }

    [Theory]
    [InlineData("Глава 2001", "Глава 3250", false)]
    [InlineData("Глава 2001", "Глава 3251", true)]
    [InlineData("Глава 2751", null, false)]
    [InlineData("Глава 2750", null, true)]
    [InlineData(null, "Глава 1250", false)]
    [InlineData(null, "Глава 1251", true)]
    public void ChapterNamesAreResolvedBeforeLimit(string startName, string endName, bool exceedsLimit)
    {
        using var getter = CreateGetter(new Options { StartName = startName, EndName = endName });

        if (exceedsLimit)
        {
            Assert.Throws<MangaChapterLimitException>(() => getter.Download(Chapters(4000)));
            Assert.Equal(0, getter.ChapterRequests);
        }
        else
        {
            Assert.Equal(1250, getter.Download(Chapters(4000)).Count);
            Assert.Equal(1250, getter.ChapterRequests);
        }
    }

    [Theory]
    [InlineData("https://mangalib.me/ru/manga/example")]
    [InlineData("https://mangalib.org/ru/manga/example")]
    [InlineData("https://www.MANGALIB.me/ru/manga/example")]
    [InlineData("https://hentailib.me/ru/manga/example")]
    [InlineData("https://slashlib.me/ru/manga/example")]
    [InlineData("https://v2.slashlib.me/ru/manga/example")]
    [InlineData("https://mangamammy.ru/manga/example")]
    [InlineData("https://manga.ovh/content/example")]
    [InlineData("https://remanga.org/manga/example")]
    [InlineData("https://readmanga.live/example")]
    public void AllChapterBasedComicSitesEnforceLimit(string url)
    {
        using var getter = CreateGetter(new Options(), url);

        Assert.Throws<MangaChapterLimitException>(() => getter.Download(Chapters(1251)));
        Assert.Equal(0, getter.ChapterRequests);
    }

    [Theory]
    [InlineData("https://ranobelib.me/ru/book/example")]
    [InlineData("https://renovels.org/novel/example")]
    [InlineData("https://ranobe.ovh/content/example")]
    [InlineData("https://tl.rulate.ru/book/123")]
    [InlineData("https://bllate.org/book/123")]
    [InlineData("https://books.yandex.ru/books/example")]
    [InlineData("https://mangalib.me.example.com/book/123")]
    public void TextBooksAndUnrelatedHostsKeepAllSelectedChapters(string url)
    {
        using var getter = CreateGetter(new Options(), url);

        Assert.Equal(4000, getter.Download(Chapters(4000)).Count);
        Assert.Equal(4000, getter.ChapterRequests);
    }

    [Fact]
    public void AcomicsPageSelectionIsNotMistakenForChapterCount()
    {
        using var getter = CreateGetter(new Options(), "https://acomics.ru/~example/1");

        Assert.Equal(1251, getter.Download(Chapters(1251)).Count);
    }

    [Fact]
    public void CoreWithoutAndroidPolicyKeepsExistingUnlimitedBehavior()
    {
        using var config = new BookGetterConfig(new Options(), null, null, null, NullLogger.Instance);
        using var getter = new SelectionGetter(config);

        Assert.Null(config.ValidateSelectedChapterCount);
        Assert.Equal(4000, getter.Download(Chapters(4000)).Count);
    }

    private static SelectionGetter CreateGetter(Options options, string url = MangaUrl)
    {
        var config = new BookGetterConfig(options, null, null, null, NullLogger.Instance);
        MangaDownloadLimit.Apply(config, new Uri(url));
        return new SelectionGetter(config);
    }

    private static string[] Chapters(int count) => Enumerable.Range(1, count).Select(index => $"Глава {index}").ToArray();

    private sealed class SelectionGetter(BookGetterConfig config) : GetterBase(config)
    {
        protected override Uri SystemUrl => new(MangaUrl);

        public int ChapterRequests { get; private set; }

        public List<string> Download(ICollection<string> toc)
        {
            var downloaded = new List<string>();
            foreach (var chapter in SliceToc(toc, title => title))
            {
                ChapterRequests++;
                downloaded.Add(chapter);
            }
            return downloaded;
        }

        public override Task<Book> Get(Uri url) => throw new NotSupportedException();
    }
}
