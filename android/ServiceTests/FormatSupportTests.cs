using E2E.Droid.Services;

namespace ServiceTests;

public sealed class FormatSupportTests
{
    [Fact]
    public void EveryComicInDiagnosticCatalogIsRecognizedByTheSameFormatPolicy()
    {
        Assert.All(SiteDiagnosticCatalog.Samples.Where(sample => sample.Images), sample =>
            Assert.True(FormatSupport.IsComic(sample.BookUrl ?? "https://" + sample.SiteHost + "/"), sample.Id));
    }

    [Theory]
    [InlineData("https://acomics.ru/~slightly-damned/1")]
    [InlineData("https://desu.me/manga/one-piece.12/")]
    [InlineData("https://www.desu.me/manga/one-piece.12/")]
    [InlineData("https://desu.uno/manga/one-piece.12/")]
    [InlineData("https://mangalib.me/ru/manga/206--one-piece?ui=8188222")]
    [InlineData("https://mangalib.org/ru/18406--nan-hao-shang-feng")]
    [InlineData("https://hentailib.me/ru/33052--adabana")]
    [InlineData("https://v2.slashlib.me/ru/23435--secret-alliance")]
    [InlineData("https://mangamammy.ru/book/example")]
    [InlineData("https://manga.ovh/content/v-temnote")]
    [InlineData("https://v1.manga.ovh/content/v-temnote")]
    [InlineData("https://readmanga.live/example")]
    [InlineData("https://remanga.org/manga/rma-new-student-at-the-academy-is-a-hidden-disciple-of-the-legendary-school/main")]
    [InlineData("https://books.yandex.ru/comicbooks/oF30TijM")]
    [InlineData("https://www.books.yandex.ru/COMICBOOKS/oF30TijM?from=library#page")]
    public void ComicUrlsAlwaysSelectOnlyCbz(string url)
    {
        Assert.True(FormatSupport.IsComic(url));
        Assert.Equal("cbz", FormatSupport.ForUrl(url, "fb2"));
        Assert.Equal(new[] { "cbz" }, FormatSupport.ForDownload(url, ["epub", "fb2", "txt"]));
    }

    [Theory]
    [InlineData("https://ranobelib.me/ru/29609--mikkakan-no-koufuku")]
    [InlineData("https://renovels.org/novel/novel-fffclass-trashero/main")]
    [InlineData("https://novel.ovh/content/example")]
    [InlineData("https://books.yandex.ru/books/Xk1d5Skg")]
    [InlineData("https://books.yandex.ru/audiobooks/example")]
    [InlineData("https://books.yandex.ru/books/comicbooks")]
    [InlineData("https://books.yandex.ru/comicbookshelf/example")]
    [InlineData("https://other.example/comicbooks/example")]
    [InlineData("https://tl.rulate.ru/book/129557")]
    [InlineData("https://bllate.org/book/17227")]
    [InlineData("https://litnet.com/ru/book/v-obyatiyah-krasnoi-planety-b640815")]
    [InlineData("https://www.litres.ru/book/aleksandr-pushkin/kapitanskaya-dochka-72130255/")]
    public void TextUrlsKeepTextFormatsEvenOnPlatformsWithComicLoaders(string url)
    {
        Assert.False(FormatSupport.IsComic(url));
        Assert.Equal("fb2", FormatSupport.ForUrl(url, "fb2"));
        Assert.Equal(new[] { "fb2", "epub", "txt" }, FormatSupport.ForDownload(url, ["fb2", "epub", "txt"]));
        Assert.Equal(new[] { "epub" }, FormatSupport.ForDownload(url, ["cbz"]));
    }

    [Theory]
    [InlineData("epub")]
    [InlineData("fb2")]
    [InlineData("txt")]
    public void ComicManualChoiceAndOldHistoryCannotKeepTextFormat(string format)
    {
        const string comic = "https://mangalib.me/ru/manga/206--one-piece";
        var request = new DownloadRequest { Urls = [comic], Formats = [format] };

        Assert.NotNull(FormatSupport.Problem(format, comic: true));
        Assert.Equal("cbz", FormatSupport.Fit(format, comic: true));
        Assert.Equal(new[] { "cbz" }, request.FormatsFor(comic));
        Assert.Null(FormatSupport.Problem("cbz", comic: true));
        Assert.NotNull(FormatSupport.Problem("cbz", comic: false));
    }

    [Fact]
    public void MixedQueueAppliesManualFormatToTextWithoutConvertingMangaToFb2()
    {
        const string comic = "https://mangalib.me/ru/manga/206--one-piece";
        const string novel = "https://ranobelib.me/ru/29609--mikkakan-no-koufuku";
        var request = new DownloadRequest { Urls = [novel, comic], Formats = ["fb2"] };

        Assert.Equal(new[] { "fb2" }, request.FormatsFor(novel));
        Assert.Equal(new[] { "cbz" }, request.FormatsFor(comic));
    }

    [Fact]
    public void MixedQueueAfterManualCbzChoiceDoesNotLoseNovelText()
    {
        const string comic = "https://mangalib.me/ru/manga/206--one-piece";
        const string novel = "https://tl.rulate.ru/book/129557";
        var request = new DownloadRequest { Urls = [comic, novel], Formats = ["cbz"] };

        Assert.Equal(new[] { "cbz" }, request.FormatsFor(comic));
        Assert.Equal(new[] { "epub" }, request.FormatsFor(novel));
    }

    [Fact]
    public void PerUrlOverridesCannotBypassSeparationAndValidTextFormatsSurvive()
    {
        const string comic = "https://books.yandex.ru/comicbooks/oF30TijM";
        const string novel = "https://books.yandex.ru/books/Xk1d5Skg";
        var request = new DownloadRequest
        {
            Urls = [comic, novel], Formats = ["txt"],
            FormatByUrl = new Dictionary<string, string> { [comic] = "fb2", [novel] = "cbz" }
        };

        Assert.Equal(new[] { "cbz" }, request.FormatsFor(comic));
        Assert.Equal(new[] { "epub" }, request.FormatsFor(novel));
        Assert.Equal(new[] { "fb2", "epub" }, FormatSupport.ForDownload(novel, ["cbz", "fb2", "epub", "fb2"]));
    }
}
