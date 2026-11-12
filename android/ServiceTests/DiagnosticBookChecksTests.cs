using System.IO.Compression;
using System.Text;
using E2E.Droid.Services;

namespace E2E.Droid.ServiceTests;

public sealed class DiagnosticBookChecksTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "diagnostic-formats-" + Guid.NewGuid().ToString("N"));

    public DiagnosticBookChecksTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ActualBuildersPassOfflineUnicodeAndImageCheck()
    {
        var result = await DiagnosticBookChecks.RunSelfTestAsync(_directory, CancellationToken.None);
        Assert.NotEmpty(result);
        Assert.All(result, check => Assert.True(check.Passed, check.Message));
        Assert.Equal(2, result.Count(check => check.Message.Contains("emoji")));
    }

    [Theory]
    [InlineData("<FictionBook>")]
    [InlineData("<!DOCTYPE FictionBook SYSTEM 'file:///never-read-me'><FictionBook />")]
    [InlineData("<FictionBook xmlns='wrong' />")]
    public void RejectsInvalidXmlAndDtdWithoutCopyingBookText(string xml)
    {
        var result = DiagnosticBookChecks.CheckFile(Write("invalid.fb2", xml));
        Assert.Contains(result, x => !x.Passed && !x.Warning);
        Assert.DoesNotContain(result, x => x.Message.Contains("never-read-me"));
    }

    [Fact]
    public void EmptyFb2DoesNotPassBecauseItsXmlIsValid()
    {
        var result = DiagnosticBookChecks.CheckFile(Write("empty.fb2", Fb2("")));
        Assert.Contains(result, x => !x.Passed && x.Message.Contains("глав 0"));
    }

    [Fact]
    public void FindsMissingFb2ImageAndDuplicateIds()
    {
        var result = DiagnosticBookChecks.CheckFile(Write("image.fb2", Fb2("<section id='same'><title><p>Глава</p></title><p id='same'>Текст</p><image xlink:href='#missing'/></section>")));
        Assert.Contains(result, x => !x.Passed && x.Message.Contains("binary-ссылок 1"));
        Assert.Contains(result, x => !x.Passed && x.Message.Contains("повторных id 1"));
    }

    [Fact]
    public void CountsOnlyMainFb2SectionsAndReportsMismatch()
    {
        var xml = Fb2("<section><title><p>Глава</p></title><p>Текст</p><section><p>Подраздел</p></section></section>")
            .Replace("</FictionBook>", "<body name='notes'><section><p>Примечание</p></section></body></FictionBook>");
        var path = Write("count.fb2", xml);
        Assert.DoesNotContain(DiagnosticBookChecks.CheckFile(path, 1), x => !x.Passed);
        Assert.Contains(DiagnosticBookChecks.CheckFile(path, 2), x => !x.Passed && x.Message.Contains("ожидается 2"));
    }

    [Fact]
    public void DuplicateAndNakedUrlAreWarningsWithoutPublishingText()
    {
        var result = DiagnosticBookChecks.CheckFile(Write("content.fb2", Fb2("<section><title><p>Секретное название</p></title><subtitle>Секретное название</subtitle><p>https://author.example/private-path</p></section>")), 1);
        Assert.Equal(2, result.Count(x => x.Warning));
        Assert.DoesNotContain(result, x => !x.Passed && !x.Warning);
        Assert.DoesNotContain(result, x => x.Message.Contains("Секретное") || x.Message.Contains("private-path"));
    }

    [Fact]
    public void EpubUsesSpineAndMetadataToExcludeCoverAndNavigation()
    {
        var path = Epub("<h1>Глава</h1><p id='text'>Текст</p><p><a href='#text'>Назад</a><a href='https://author.example/'>Автор</a></p>", extraPages: true);
        var result = DiagnosticBookChecks.CheckFile(path, 1);
        Assert.All(result, check => Assert.True(check.Passed, check.Message));
        Assert.Contains(result, x => x.Message == "EPUB: глав 1, ожидается 1");
    }

    [Theory]
    [InlineData("<img src='missing.png'/>", "внутренних файлов 1")]
    [InlineData("<a href='#missing'>Назад</a>", "якорей 1")]
    public void EpubDetectsDanglingImagesAndAnchors(string html, string expected)
    {
        var result = DiagnosticBookChecks.CheckFile(Epub("<h1>Глава</h1><p>Текст</p>" + html), 1);
        Assert.Contains(result, x => !x.Passed && x.Message.Contains(expected));
    }

    [Fact]
    public void EpubWithOnlyTitleIsNotContent()
    {
        var result = DiagnosticBookChecks.CheckFile(Epub("<h1>Глава</h1>"), 1);
        Assert.Contains(result, x => !x.Passed && x.Message.Contains("содержимое после заголовка"));
    }

    [Fact]
    public void EpubAcceptsBareTextAfterItsTitle()
    {
        Assert.DoesNotContain(DiagnosticBookChecks.CheckFile(Epub("<h1>Глава</h1>Текст без обёртки p"), 1), x => !x.Passed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedTextWithDifferentTitlesIsWarning(bool epub)
    {
        var path = epub
            ? Epub("<h1>Глава 100</h1><p>Один и тот же текст.</p>", secondBody: "<h1>Глава 101</h1><p>Один  и тот же\nтекст.</p>")
            : Write("repeat.fb2", Fb2("<section><title><p>Глава 100</p></title><p>Один и тот же текст.</p></section><section><title><p>Глава 101</p></title><p>Один  и тот же\nтекст.</p></section>"));
        var result = DiagnosticBookChecks.CheckFile(path, 2);
        Assert.Single(result, x => x.Warning && x.Message.Contains("повтор текста"));
        Assert.DoesNotContain(result, x => !x.Passed && !x.Warning);
    }

    [Fact]
    public void ImageOnlyChaptersDoNotCountAsRepeatedText()
    {
        var xml = Fb2("<section><title><p>Первая</p></title><image xlink:href='#image'/></section><section><title><p>Вторая</p></title><image xlink:href='#image'/></section>")
            .Replace("</FictionBook>", "<binary id='image' content-type='image/png'>AA==</binary></FictionBook>");
        Assert.DoesNotContain(DiagnosticBookChecks.CheckFile(Write("images.fb2", xml), 2), x => x.Warning);
    }

    [Fact]
    public void EmptyEpubAndBrokenZipAreReportedAsFailures()
    {
        Assert.Contains(DiagnosticBookChecks.CheckFile(Epub("", emptySpine: true)), x => !x.Passed && x.Message.Contains("глав 0"));
        Assert.Contains(DiagnosticBookChecks.CheckFile(Write("broken.epub", "not a zip")), x => !x.Passed && !x.Warning);
    }

    [Fact]
    public async Task CancelledSelfTestDoesNotBuildFiles()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiagnosticBookChecks.RunSelfTestAsync(_directory, cts.Token));
        Assert.Empty(Directory.GetFiles(_directory, "*.epub", SearchOption.AllDirectories));
    }

    private static string Fb2(string sections) => "<FictionBook xmlns='http://www.gribuser.ru/xml/fictionbook/2.0' xmlns:xlink='http://www.w3.org/1999/xlink'>"
        + "<description><title-info><book-title>Книга</book-title></title-info></description><body>" + sections + "</body></FictionBook>";

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private string Epub(string body, bool extraPages = false, bool emptySpine = false, string secondBody = null)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".epub");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "mimetype", "application/epub+zip");
        Add(zip, "META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='EPUB/content.opf'/></rootfiles></container>");
        var extras = extraPages ? "<item id='cover' href='front.xhtml' media-type='application/xhtml+xml'/><item id='nav' href='navigation.xhtml' properties='nav' media-type='application/xhtml+xml'/>" : "";
        if (secondBody != null) extras += "<item id='chapter2' href='part2.xhtml' media-type='application/xhtml+xml'/>";
        var refs = emptySpine ? "" : "<itemref idref='chapter'/>";
        if (secondBody != null) refs += "<itemref idref='chapter2'/>";
        if (extraPages) refs = "<itemref idref='cover'/><itemref idref='nav'/>" + refs;
        Add(zip, "EPUB/content.opf", "<package xmlns='http://www.idpf.org/2007/opf'><manifest><item id='chapter' href='part.xhtml' media-type='application/xhtml+xml'/>" + extras + "</manifest><spine>" + refs + "</spine>"
            + (extraPages ? "<guide><reference type='cover' href='front.xhtml'/></guide>" : "") + "</package>");
        Add(zip, "EPUB/part.xhtml", Xhtml(body));
        if (secondBody != null) Add(zip, "EPUB/part2.xhtml", Xhtml(secondBody));
        if (extraPages)
        {
            Add(zip, "EPUB/front.xhtml", Xhtml("<p>Обложка</p>"));
            Add(zip, "EPUB/navigation.xhtml", Xhtml("<nav><a href='part.xhtml'>Глава</a></nav>"));
        }
        return path;
    }

    private static string Xhtml(string body) => "<!DOCTYPE html SYSTEM 'https://example.invalid/not-loaded.dtd'><html xmlns='http://www.w3.org/1999/xhtml'><head><title>Книга</title></head><body>" + body + "</body></html>";
    private static void Add(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
