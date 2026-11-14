using E2E.Droid.Services;

namespace ServiceTests;

public class SiteDiagnosticCatalogTests
{
    [Fact]
    public void EveryReadmeSiteHasCasesOrAnExplicitReasonToSkip()
    {
        var urls = SupportedSites.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md")));
        Assert.True(urls.Count > 70);

        var cases = SiteDiagnosticCatalog.ForSites(urls.Select(url => new SiteEntry(url, url, true, true)));

        Assert.Equal(urls.Select(Host).Distinct().Order(), cases.Select(test => test.SiteHost).Distinct().Order());
        Assert.All(cases, test => Assert.True(test.CanRun || !string.IsNullOrWhiteSpace(test.SkipReason)));
        Assert.Equal(cases.Count, cases.Select(test => test.Id).Distinct().Count());
    }

    [Fact]
    public void VersionedSamplesContainRealIndividualUrlsAndNoSilentMissingCases()
    {
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}\.\d+$", SiteDiagnosticCatalog.Version);
        var samples = SiteDiagnosticCatalog.Samples;
        Assert.True(samples.Count >= 83);
        Assert.Equal(samples.Count, samples.Select(test => test.Id).Distinct().Count());
        Assert.All(samples, test =>
        {
            Assert.False(string.IsNullOrWhiteSpace(test.SiteHost));
            Assert.True(test.CanRun || !string.IsNullOrWhiteSpace(test.SkipReason));
            if (test.BookUrl == null) return;
            var uri = new Uri(test.BookUrl);
            Assert.True(uri.Scheme is "http" or "https");
            Assert.NotEqual("/", uri.AbsolutePath);
            Assert.Empty(uri.UserInfo);
            Assert.DoesNotContain("/test", uri.AbsolutePath);
            Assert.NotEqual("/projects/book", uri.AbsolutePath);
        });
    }

    [Fact]
    public void ExamplesAreTraceableToAuditOrConcreteUserReports()
    {
        var audit = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "CONTENT-AUDIT-2026-11-12.md"));
        var followup = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "DIAGNOSTICS-2026-11-14.md"));
        string[] userReports =
        [
            "https://bllate.org/book/20455",
            "https://tl.rulate.ru/book/209502",
            "https://litnet.com/ru/book/v-obyatiyah-krasnoi-planety-b640815"
        ];

        foreach (var example in SiteDiagnosticCatalog.Samples.Where(test => test.BookUrl != null))
            Assert.True(audit.Contains(example.BookUrl, StringComparison.Ordinal) || followup.Contains(example.BookUrl, StringComparison.Ordinal) || userReports.Contains(example.BookUrl),
                $"Для примера {example.Id} необходимо зафиксировать источник реальной ссылки.");
    }

    [Fact]
    public void ReaderSampleSkipsIllustrationOnlyOpeningButKeepsPositiveRange()
    {
        Assert.All(SiteDiagnosticCatalog.Samples, sample => Assert.True(sample.StartChapter > 0));
        Assert.Equal(2, SiteDiagnosticCatalog.Samples.Single(sample => sample.SiteHost == "ranobehub.org").StartChapter);
    }

    [Fact]
    public void DisabledAndUnavailableLoadersNeverBecomeRunnableFromKnownExample()
    {
        var cases = SiteDiagnosticCatalog.ForSites([
            new("https://litres.ru/", "Литрес", true, true, "временно отключён"),
            new("https://litmarket.ru/", "Литмаркет", true, false)
        ]);

        Assert.All(cases, test => { Assert.NotNull(test.BookUrl); Assert.False(test.CanRun); });
        Assert.Contains("временно отключён", cases[0].SkipReason);
        Assert.Contains("не включён", cases[1].SkipReason);
    }

    [Fact]
    public void UnknownSiteRemainsVisibleAndDoesNotGetAnInventedBookUrl()
    {
        var test = Assert.Single(SiteDiagnosticCatalog.ForSites([
            new("https://new-books.example/", "Новый сайт", false, true)
        ]));

        Assert.Equal("new-books.example", test.Id);
        Assert.Equal("new-books.example", test.SiteHost);
        Assert.Null(test.BookUrl);
        Assert.False(test.CanRun);
        Assert.Contains("адрес отдельной книги", test.SkipReason);
    }

    [Fact]
    public void WwwAndUnicodeHostsHaveStableDeduplicatedIdentifiers()
    {
        var cases = SiteDiagnosticCatalog.ForSites([
            new("https://www.litres.ru/", "Литрес", true, true),
            new("https://litres.ru/", "Литрес", false, true),
            new("https://ранобэ.рф/", "Ранобэ", true, true)
        ]);

        Assert.Equal(2, cases.Count);
        Assert.Equal("litres.ru", cases[0].Id);
        Assert.Equal(new Uri("https://ранобэ.рф").IdnHost, cases[1].Id);
    }

    [Fact]
    public void YandexBooksAndComicsUseDifferentLoadersAndRequireExplicitToken()
    {
        var cases = SiteDiagnosticCatalog.ForSites([new("https://books.yandex.ru/", "Яндекс Книги", true, true)]);

        Assert.Equal(2, cases.Count);
        Assert.Contains(cases, test => test.Id == "books.yandex.ru/books" && !test.Images);
        Assert.Contains(cases, test => test.Id == "books.yandex.ru/comicbooks" && test.Images);
        Assert.All(cases, test => { Assert.False(test.CanRun); Assert.Contains("token", test.SkipReason); });
    }

    [Theory]
    [InlineData("acomics.ru")]
    [InlineData("hentailib.me")]
    [InlineData("mangalib.me")]
    [InlineData("mangalib.org")]
    [InlineData("remanga.org")]
    [InlineData("v2.slashlib.me")]
    public void ComicExamplesRequestImages(string host)
    {
        var test = Assert.Single(SiteDiagnosticCatalog.ForSites([new("https://" + host, host, true, true)]));

        Assert.True(test.Images);
        Assert.Contains("CBZ", test.Limitation);
    }

    [Theory]
    [InlineData("litres.ru")]
    [InlineData("mybook.ru")]
    [InlineData("fictionbook.ru")]
    [InlineData("litmarket.ru")]
    public void WholeBookLoadersDiscloseThatChapterLimitDoesNotLimitDownloadSize(string host)
    {
        var test = Assert.Single(SiteDiagnosticCatalog.ForSites([new("https://" + host, host, true, true)]));

        Assert.Contains("всей книги", test.Limitation);
        Assert.Contains("таймаут", test.Limitation);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "docs", "CONTENT-AUDIT-2026-11-12.md"))) return directory.FullName;
        throw new InvalidOperationException("Не найден корень репозитория с источниками диагностических примеров.");
    }

    private static string Host(string url)
    {
        var host = new Uri(url).IdnHost.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}
