using System.IO.Compression;
using System.Net;
using E2E.Droid.Services;
using Elib2Ebook.DomainServices.Getters;

namespace ServiceTests;

public sealed class SiteDiagnosticBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "e2e-site-probes-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OneFailureDoesNotStopOtherSitesAndEveryCaseGetsExactlyOneResult()
    {
        var cases = new[] { Case("first"), Case("missing") with { BookUrl = null, SkipReason = "Нет примера" }, Case("last") };
        var called = new List<string>();
        var published = new List<SiteDiagnosticResult>();
        var results = await SiteDiagnosticBatch.RunAsync(cases, (item, _) =>
        {
            called.Add(item.Id);
            if (item.Id == "first") throw new HttpRequestException("private-value", null, HttpStatusCode.Forbidden);
            return Task.FromResult(new SiteProbeOutcome(SiteDiagnosticStatus.Ok, "Файлы проверены"));
        }, published.Add, CancellationToken.None);

        Assert.Equal(new[] { "first", "last" }, called);
        Assert.Equal(new[] { SiteDiagnosticStatus.AccessRestricted, SiteDiagnosticStatus.Skipped, SiteDiagnosticStatus.Ok }, results.Select(result => result.Status));
        Assert.Equal(cases.Select(item => item.Id), published.Select(result => result.Case.Id));
        Assert.DoesNotContain(results, result => result.Detail.Contains("private-value"));
    }

    [Fact]
    public async Task CancellationIncludesCurrentAndAllUnstartedCasesWithoutCallingThem()
    {
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var results = await SiteDiagnosticBatch.RunAsync(new[] { Case("1"), Case("2"), Case("3") }, (_, ct) =>
        {
            calls++;
            stop.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new SiteProbeOutcome(SiteDiagnosticStatus.Ok, "Не должно стать успехом"));
        }, null, stop.Token);

        Assert.Equal(1, calls);
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.Equal(SiteDiagnosticStatus.Cancelled, result.Status));
    }

    [Fact]
    public async Task SiteTimeoutDoesNotCancelTheNextSite()
    {
        var results = await SiteDiagnosticBatch.RunAsync(new[] { Case("slow"), Case("next") }, async (item, ct) =>
        {
            if (item.Id == "slow") await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new SiteProbeOutcome(SiteDiagnosticStatus.Ok, "Файлы проверены");
        }, null, CancellationToken.None, TimeSpan.FromMilliseconds(20));

        Assert.Equal(new[] { SiteDiagnosticStatus.Timeout, SiteDiagnosticStatus.Ok }, results.Select(result => result.Status));
        Assert.Contains("общий лимит примера", results[0].Detail);
    }

    [Fact]
    public async Task TimeoutCannotBecomeOkWhenProbeIgnoresCancellationUntilReturning()
    {
        var results = await SiteDiagnosticBatch.RunAsync(new[] { Case("slow") }, async (_, _) =>
        {
            await Task.Delay(40);
            return new SiteProbeOutcome(SiteDiagnosticStatus.Ok, "Запоздалый ответ");
        }, null, CancellationToken.None, TimeSpan.FromMilliseconds(5));

        Assert.Equal(SiteDiagnosticStatus.Timeout, Assert.Single(results).Status);
        Assert.Contains("общий лимит примера", results[0].Detail);
    }

    [Fact]
    public async Task RequestTimeoutBeforeSiteBudgetIsNotReportedAsExhaustedSiteBudget()
    {
        var results = await SiteDiagnosticBatch.RunAsync(new[] { Case("request") }, (_, _) =>
            Task.FromException<SiteProbeOutcome>(new TaskCanceledException("private-value", new TimeoutException("private-value"))),
            null, CancellationToken.None, TimeSpan.FromMinutes(1));

        var result = Assert.Single(results);
        Assert.Equal(SiteDiagnosticStatus.Timeout, result.Status);
        Assert.Contains("отдельного запроса", result.Detail);
        Assert.DoesNotContain("общий лимит", result.Detail);
        Assert.DoesNotContain("private-value", result.Detail);
    }

    [Fact]
    public void Http200HtmlOrEmptyOutputsCannotBeSuccessfulDownloads()
    {
        Directory.CreateDirectory(_root);
        var fakeFb2 = Path.Combine(_root, "book.fb2");
        var fakeEpub = Path.Combine(_root, "book.epub");
        File.WriteAllText(fakeFb2, "<html><body>HTTP 200 private-book-text</body></html>");
        File.WriteAllText(fakeEpub, "<html>HTTP 200</html>");

        var checks = SiteDiagnosticBatch.CheckOutput(Case("fake"), 1, [fakeFb2, fakeEpub]);

        Assert.Equal(SiteDiagnosticStatus.Error, SiteDiagnosticBatch.Evaluate(checks, 1, false).Status);
        Assert.DoesNotContain(checks, check => check.Message.Contains("private-book-text"));
        Assert.Equal(SiteDiagnosticStatus.Error, SiteDiagnosticBatch.Evaluate([], 0, false).Status);
    }

    [Fact]
    public void ImageOnlyComicDoesNotNeedTextChapterCountButMustContainActualImageHeaders()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "images.cbz");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var stream = archive.CreateEntry("1.png").Open())
            stream.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

        var checks = SiteDiagnosticBatch.CheckOutput(Case("comic") with { Images = true }, 0, [path]);

        Assert.All(checks, check => Assert.True(check.Passed, check.Message));
        Assert.Equal(SiteDiagnosticStatus.Ok, SiteDiagnosticBatch.Evaluate(checks, 0, false).Status);
    }

    [Fact]
    public void ComicWithHtmlDisguisedAsImageCannotPass()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "images.cbz");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("1.jpg").Open()))
            writer.Write("<html>Access denied</html>");

        var checks = SiteDiagnosticBatch.CheckOutput(Case("comic") with { Images = true }, 0, [path]);

        Assert.Equal(SiteDiagnosticStatus.Error, SiteDiagnosticBatch.Evaluate(checks, 0, false).Status);
    }

    [Fact]
    public async Task RealBuilderOutputsAreCheckedAndWholeBookResponseIsAWarning()
    {
        await DiagnosticBookChecks.RunSelfTestAsync(_root, CancellationToken.None);
        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Where(file => Path.GetExtension(file) is ".fb2" or ".epub").ToArray();

        var checks = SiteDiagnosticBatch.CheckOutput(Case("text"), 2, files);

        Assert.DoesNotContain(checks, check => !check.Passed && !check.Warning);
        Assert.Equal(SiteDiagnosticStatus.Warning, SiteDiagnosticBatch.Evaluate(checks, 2, false).Status);
        Assert.Equal(SiteDiagnosticStatus.Warning, SiteDiagnosticBatch.Evaluate([new(true, "проверено")], 1, true).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public void NotFoundDescribesAnUnavailableExampleWithoutDeclaringBrokenLoader(HttpStatusCode code)
    {
        var result = SiteDiagnosticBatch.ClassifyFailure(new HttpRequestException("private-session", null, code));
        Assert.Equal(SiteDiagnosticStatus.ExampleUnavailable, result.Status);
        Assert.Contains(((int)code).ToString(), result.Detail);
        Assert.Contains("не доказывает", result.Detail);
        Assert.DoesNotContain("private-session", result.Detail);
    }

    [Fact]
    public void TypedAgeRestrictionAndNestedHttpFailuresKeepTheirMeaningWithoutExposingMessages()
    {
        var age = SiteDiagnosticBatch.ClassifyFailure(new InvalidOperationException("wrapper-secret",
            new SiteAccessException(SiteAccessReason.AgeConfirmationRequired, "session-secret")));
        Assert.Equal(SiteDiagnosticStatus.AccessRestricted, age.Status);
        Assert.Contains("18+", age.Detail);
        Assert.DoesNotContain("secret", age.Detail);

        var timeout = SiteDiagnosticBatch.ClassifyFailure(new HttpRequestException("request-secret", new TimeoutException()));
        Assert.Equal(SiteDiagnosticStatus.Timeout, timeout.Status);
        var network = SiteDiagnosticBatch.ClassifyFailure(new HttpRequestException("ssl-secret", new IOException("EOF")));
        Assert.Equal(SiteDiagnosticStatus.NetworkError, network.Status);
        Assert.Contains("не подтверждает ошибку сертификата", network.Detail);
        Assert.DoesNotContain("secret", network.Detail);
        Assert.Equal(SiteDiagnosticStatus.Error, SiteDiagnosticBatch.ClassifyFailure(new NullReferenceException()).Status);
    }

    [Fact]
    public void SummaryCountsEachCaseSeparatelyFromDistinctSites()
    {
        var results = Enum.GetValues<SiteDiagnosticStatus>().Select((status, index) =>
            new SiteDiagnosticResult(Case(index.ToString()), status, "", TimeSpan.Zero)).ToArray();
        var summary = SiteDiagnosticBatch.DescribeSummary(results);
        Assert.Contains("Сайтов 1, примеров 9", summary);
        Assert.Contains("завершено проб 7", summary);
        Assert.Contains("OK 1", summary);
        Assert.Contains("ошибок 1", summary);
        Assert.Contains("пропущено 1, отменено 1", summary);
    }

    private static SiteDiagnosticCase Case(string id) => new(id, "example.test", "https://example.test/book/1", null, null);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
