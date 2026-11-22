using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Microsoft.Extensions.Logging.Abstractions;
using E2E.Droid.Services;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class EpubImportContentTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a52kAAAAASUVORK5CYII=");

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ImportedEpubHonorsIllustrationSwitchWithAndWithoutToc(bool noImage, bool withToc)
    {
        using var config = new BookGetterConfig(new Options { NoImage = noImage }, new HttpClient(), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        using var getter = new ImportGetter(config);
        using var file = await TempFile.Create(null, config.TempFolder.Path, "book.epub", CreateIllustratedEpub(withToc));

        var chapter = Assert.Single(await getter.Import(file));
        var doc = chapter.Content.AsHtmlDoc();
        Assert.Contains("Текст главы", chapter.Content);
        Assert.Contains("После рисунка", chapter.Content);
        var link = Assert.Single(doc.DocumentNode.Descendants("a"), node => node.Attributes["href"] != null);
        Assert.NotNull(doc.GetElementbyId(link.Attributes["href"].Value[1..]));
        if (noImage)
        {
            Assert.Empty(chapter.Images);
            Assert.Empty(doc.DocumentNode.Descendants("img"));
            Assert.DoesNotContain("illustration.png", chapter.Content);
            Assert.Single(Directory.GetFiles(config.TempFolder.Path, "*", SearchOption.AllDirectories)); // Только исходный EPUB.
        }
        else
        {
            var image = Assert.Single(chapter.Images);
            Assert.Equal(Png, image.Content);
            Assert.Equal(image.FullName, Assert.Single(doc.DocumentNode.Descendants("img")).Attributes["src"].Value);
            Assert.True(File.Exists(image.FilePath));
        }
    }

    [Theory]
    [InlineData("epub")]
    [InlineData("fb2")]
    public async Task EpubImportWithoutIllustrationsStillEmbedsCover(string format)
    {
        var options = new Options { NoImage = true, Cover = false, BookNamePattern = "rebuilt" };
        using var config = new BookGetterConfig(options, new HttpClient(), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        using var getter = new ImportGetter(config);
        using var file = await TempFile.Create(null, config.TempFolder.Path, "book.epub", CreateIllustratedEpub(true));
        options.SavePath = Path.Combine(config.TempFolder.Path, "output");
        using var book = new Book(new Uri("https://example.test/book"))
        {
            Title = "Книга", Author = new Author("Автор"), Chapters = await getter.Import(file),
            Cover = await TempFile.Create(null, config.TempFolder.Path, "cover.png", Png)
        };

        await BuilderProvider.Get(format, options, NullLogger.Instance).Build(book);

        Assert.Empty(Assert.Single(book.Chapters).Images);
        var output = Assert.Single(Directory.GetFiles(options.SavePath)); // Отдельного файла обложки нет.
        if (format == "fb2")
        {
            var xml = XDocument.Load(output);
            XNamespace ns = "http://www.gribuser.ru/xml/fictionbook/2.0";
            Assert.Single(xml.Descendants(ns + "coverpage"));
            Assert.Equal(Png, Convert.FromBase64String(Assert.Single(xml.Descendants(ns + "binary")).Value));
            Assert.Empty(xml.Descendants(ns + "body").Descendants(ns + "image"));
        }
        else
        {
            using var zip = ZipFile.OpenRead(output);
            using var packageStream = zip.Entries.Single(entry => entry.FullName.EndsWith(".opf")).Open();
            var package = XDocument.Load(packageStream);
            var image = Assert.Single(package.Descendants(), node => node.Name.LocalName == "item" &&
                ((string)node.Attribute("media-type") ?? "").StartsWith("image/", StringComparison.Ordinal));
            Assert.Contains("cover-image", (string)image.Attribute("properties") ?? "");
            using var imageStream = zip.Entries.Single(entry => entry.FullName.EndsWith((string)image.Attribute("href")!, StringComparison.Ordinal)).Open();
            using var bytes = new MemoryStream();
            await imageStream.CopyToAsync(bytes);
            Assert.Equal(Png, bytes.ToArray());
            Assert.DoesNotContain(zip.Entries, entry => entry.FullName.EndsWith("illustration.png", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ImportedEpubRemovesItsTitleButPreservesInnerHeadingsAndAnchors()
    {
        using var config = new BookGetterConfig(new Options(), new HttpClient(), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        using var getter = new ImportGetter(config);
        using var file = await TempFile.Create(null, config.TempFolder.Path, "book.epub", CreateEpub());

        var chapters = (await getter.Import(file)).ToList();

        var chapter = Assert.Single(chapters);
        var doc = chapter.Content.AsHtmlDoc();
        Assert.Equal("Глава 1", chapter.Title);
        Assert.Empty(doc.DocumentNode.Descendants("h1"));
        Assert.Equal(new[] { "Подзаголовок", "Глава 1" }, doc.DocumentNode.Descendants("h2").Select(n => n.InnerText));
        Assert.Equal("Другой раздел", doc.DocumentNode.Descendants("h3").Single().InnerText);
        var target = doc.DocumentNode.Descendants("a").Single(node => node.Attributes["href"] != null).Attributes["href"].Value;
        Assert.StartsWith("#", target);
        Assert.NotNull(doc.GetElementbyId(target[1..]));
        Assert.Contains(doc.DocumentNode.Descendants("a"), node => node.Attributes["id"] != null);
        Assert.Contains("Текст главы", chapter.Content);
    }

    [Fact]
    public async Task RebuiltEpubPreservesFootnotesFileLinksAndDuplicateIdsAcrossSourceFiles()
    {
        var documents = await RebuildAndCheck(CreateLinkedEpub(), 2);
        Assert.Contains(documents.Values, doc => doc.Root!.Value.Contains("Текст сноски"));
    }

    [Theory]
    [InlineData("epub")]
    [InlineData("fb2")]
    public async Task EpubWithoutTocPreservesSourceSectionsAndBuildsNavigation(string format)
    {
        using var config = new BookGetterConfig(new Options(), new HttpClient(), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        using var getter = new ImportGetter(config);
        using var file = await TempFile.Create(null, config.TempFolder.Path, "book.epub", CreateLinkedEpubWithoutToc());
        using var book = new Book(new Uri("https://example.test/book"))
        {
            Title = "Книга", Author = new Author("Автор"), Chapters = await getter.Import(file)
        };
        var chapters = book.Chapters.ToList();
        Assert.Equal(new[] { "Глава", "Сноски" }, chapters.Select(chapter => chapter.Title));
        Assert.Contains("Основной текст", chapters[0].Content);
        Assert.DoesNotContain("Текст сноски", chapters[0].Content);
        Assert.Contains("Текст сноски", chapters[1].Content);
        Assert.DoesNotContain("Основной текст", chapters[1].Content);

        var output = Path.Combine(config.TempFolder.Path, "output");
        await BuilderProvider.Get(format, new Options { SavePath = output, BookNamePattern = "sections" }, NullLogger.Instance).Build(book);
        var outputFile = Assert.Single(Directory.GetFiles(output));
        Assert.All(DiagnosticBookChecks.CheckFile(outputFile, 2), check => Assert.True(check.Passed, check.Message));
        if (format == "fb2")
        {
            var xml = XDocument.Load(outputFile);
            XNamespace ns = "http://www.gribuser.ru/xml/fictionbook/2.0";
            var sections = xml.Root!.Element(ns + "body")!.Elements(ns + "section").ToList();
            Assert.Equal(new[] { "Глава", "Сноски" }, sections.Select(section => section.Element(ns + "title")!.Value));
            var ids = xml.Descendants().Attributes("id").Select(attribute => attribute.Value).ToHashSet();
            foreach (var link in xml.Descendants(ns + "a"))
            {
                var href = link.Attributes().Single(attribute => attribute.Name.LocalName == "href").Value;
                Assert.Contains(href.TrimStart('#'), ids);
            }
        }
        else
        {
            using var zip = ZipFile.OpenRead(outputFile);
            using var tocStream = zip.Entries.Single(entry => entry.FullName.EndsWith(".ncx", StringComparison.Ordinal)).Open();
            var toc = XDocument.Load(tocStream);
            Assert.Equal(new[] { "Глава", "Сноски" }, toc.Descendants().Where(node => node.Name.LocalName == "navLabel").Select(node => node.Value));
        }
    }

    [Fact]
    public async Task EpubWithoutTocPreservesLinksBetweenSeparateSourceFiles()
    {
        await RebuildAndCheck(CreateLinkedEpubWithoutToc(), 2);
    }

    [Theory]
    [InlineData(true, "epub")]
    [InlineData(false, "epub")]
    [InlineData(true, "fb2")]
    [InlineData(false, "fb2")]
    public async Task MyBookExplicitHeadingsSplitLargeSectionsAndKeepContentLinksAndImages(bool withToc, string format)
    {
        using var config = new BookGetterConfig(new Options(), new HttpClient(), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        using var getter = new ImportGetter(config);
        using var file = await TempFile.Create(null, config.TempFolder.Path, "book.epub", CreateMyBookStructuredEpub(withToc));
        using var book = new Book(new Uri("https://example.test/book"))
        {
            Title = "Книга", Author = new Author("Автор"), Chapters = await getter.Import(file, splitNumberedHeadings: true)
        };
        var chapters = book.Chapters.ToList();
        Assert.Equal(new[] { "Глава", "Пролог.", "Глава 1.", "Глава 2.", "Сноски" }, chapters.Select(chapter => chapter.Title));
        Assert.Contains("Вступительный текст", chapters[0].Content);
        Assert.Contains("Текст пролога", chapters[1].Content);
        Assert.DoesNotContain("Основной текст", chapters[1].Content);
        Assert.Contains("Основной текст", chapters[2].Content);
        Assert.Contains("Обычный подзаголовок", chapters[2].Content);
        Assert.DoesNotContain("Текст второй главы", chapters[2].Content);
        Assert.Contains("Текст второй главы", chapters[3].Content);
        Assert.Contains("Глава 1.", chapters[4].Content); // Группировка примечаний по главам не разрезается.
        Assert.Contains("Глава 2.", chapters[4].Content);
        Assert.Equal(Png, Assert.Single(chapters[2].Images).Content);
        Assert.All(chapters.Where((_, index) => index != 2), chapter => Assert.Empty(chapter.Images));
        var allIds = chapters.SelectMany(chapter => chapter.Content.AsHtmlDoc().DocumentNode.Descendants()
            .Select(node => node.GetAttributeValue("id", string.Empty))).Where(id => id.Length > 0).ToList();
        Assert.Equal(allIds.Count, allIds.Distinct().Count());

        var output = Path.Combine(config.TempFolder.Path, "output");
        await BuilderProvider.Get(format, new Options { SavePath = output, BookNamePattern = "mybook" }, NullLogger.Instance).Build(book);
        var outputFile = Assert.Single(Directory.GetFiles(output));
        Assert.All(DiagnosticBookChecks.CheckFile(outputFile, 5), check => Assert.True(check.Passed, check.Message));
        if (format == "fb2")
        {
            var xml = XDocument.Load(outputFile);
            var ids = xml.Descendants().Attributes("id").Select(attribute => attribute.Value).ToHashSet();
            foreach (var link in xml.Descendants().Where(node => node.Name.LocalName == "a"))
                Assert.Contains(link.Attributes().Single(attribute => attribute.Name.LocalName == "href").Value[1..], ids);
        }
        else
        {
            using var zip = ZipFile.OpenRead(outputFile);
            var documents = zip.Entries.Where(entry => entry.FullName.EndsWith(".html") || entry.FullName.EndsWith(".xhtml"))
                .ToDictionary(entry => entry.FullName, entry => { using var stream = entry.Open(); return XDocument.Load(stream); });
            foreach (var (path, document) in documents)
                foreach (var link in document.Descendants().Where(node => node.Name.LocalName == "a" && node.Attribute("href") != null))
                {
                    var target = new Uri(new Uri("https://epub.invalid/" + path), (string)link.Attribute("href"));
                    Assert.True(documents.TryGetValue(target.AbsolutePath.TrimStart('/'), out var targetDoc));
                    if (target.Fragment.Length > 0) Assert.Contains(targetDoc!.Descendants(), node => (string)node.Attribute("id") == target.Fragment[1..]);
                }
        }
    }

    [Fact]
    public async Task MyBookDoesNotCreateAnEmptyIntroFromBookTitleAndAnchors()
    {
        using var config = new BookGetterConfig(new Options(), new HttpClient(), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        using var getter = new ImportGetter(config);
        using var file = await TempFile.Create(null, config.TempFolder.Path, "book.epub", CreateMyBookStructuredEpub(true, intro: false));
        var chapters = (await getter.Import(file, splitNumberedHeadings: true)).ToList();
        Assert.Equal(new[] { "Пролог.", "Глава 1.", "Глава 2.", "Сноски" }, chapters.Select(chapter => chapter.Title));
        var docs = chapters.Select(chapter => chapter.Content.AsHtmlDoc()).ToList();
        var ids = docs.SelectMany(doc => doc.DocumentNode.Descendants()).Select(node => node.GetAttributeValue("id", string.Empty)).ToHashSet();
        foreach (var link in docs.SelectMany(doc => doc.DocumentNode.Descendants("a")).Where(node => node.Attributes["href"] != null))
            Assert.Contains(link.Attributes["href"].Value[1..], ids);
    }

    [Fact]
    public async Task DefaultEpubImportDoesNotSplitMyBookSpecificHeadings()
    {
        using var config = new BookGetterConfig(new Options(), new HttpClient(), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        using var getter = new ImportGetter(config);
        using var file = await TempFile.Create(null, config.TempFolder.Path, "book.epub", CreateMyBookStructuredEpub(true));
        Assert.Equal(2, (await getter.Import(file)).Count());
    }

    [Fact]
    public async Task MyBookKeepsOrdinarySubheadingsAndAnAlreadySeparatedChapter()
    {
        using var config = new BookGetterConfig(new Options(), new HttpClient(), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        using var getter = new ImportGetter(config);
        using var file = await TempFile.Create(null, config.TempFolder.Path, "book.epub", CreateEpub());
        var chapter = Assert.Single(await getter.Import(file, splitNumberedHeadings: true));
        Assert.Equal("Глава 1", chapter.Title);
        Assert.Contains("Подзаголовок", chapter.Content);
        Assert.Contains("Другой раздел", chapter.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EpubWithoutTocKeepsImageOnlySectionsUnlessImagesAreDisabled(bool noImage)
    {
        using var config = new BookGetterConfig(new Options { NoImage = noImage }, new HttpClient(), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        using var getter = new ImportGetter(config);
        using var file = await TempFile.Create(null, config.TempFolder.Path, "book.epub", CreateEpubWithImageOnlySection());

        var chapters = (await getter.Import(file)).ToList();
        Assert.Equal(noImage ? new[] { "Глава 1" } : new[] { "Рисунок", "Глава 1" }, chapters.Select(chapter => chapter.Title));
        Assert.DoesNotContain(chapters, chapter => chapter.Title == "Пустой файл");
        if (!noImage)
        {
            Assert.Equal(Png, Assert.Single(chapters[0].Images).Content);
            Assert.Single(chapters[0].Content.AsHtmlDoc().DocumentNode.Descendants("img"));
        }
        Assert.Contains("Текст главы", chapters[^1].Content);
    }

    [Theory]
    [InlineData("epub")]
    [InlineData("fb2")]
    public async Task SkippedEpubFilesAndImagesKeepTheirLinkTargets(string format)
    {
        using var config = new BookGetterConfig(new Options { NoImage = true }, new HttpClient(), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        using var getter = new ImportGetter(config);
        using var file = await TempFile.Create(null, config.TempFolder.Path, "book.epub", CreateEpubWithImageOnlySection());
        using var book = new Book(new Uri("https://example.test/book"))
        {
            Title = "Книга", Author = new Author("Автор"), Chapters = await getter.Import(file)
        };
        Assert.Single(book.Chapters);
        var output = Path.Combine(config.TempFolder.Path, "output");
        await BuilderProvider.Get(format, new Options { SavePath = output, BookNamePattern = "targets" }, NullLogger.Instance).Build(book);
        var outputFile = Assert.Single(Directory.GetFiles(output));
        Assert.All(DiagnosticBookChecks.CheckFile(outputFile, 1), check => Assert.True(check.Passed, check.Message));
        var documents = new Dictionary<string, XDocument>();
        if (format == "fb2") documents.Add("book.fb2", XDocument.Load(outputFile));
        else
        {
            using var zip = ZipFile.OpenRead(outputFile);
            foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith(".html") || entry.FullName.EndsWith(".xhtml")))
            {
                using var stream = entry.Open();
                documents.Add(entry.FullName, XDocument.Load(stream));
            }
        }
        var checkedLinks = 0;
        foreach (var (path, document) in documents)
            foreach (var link in document.Descendants().Where(node => node.Name.LocalName == "a"
                && node.Attributes().Any(attribute => attribute.Name.LocalName == "href")))
            {
                var href = link.Attributes().Single(attribute => attribute.Name.LocalName == "href").Value;
                var target = new Uri(new Uri("https://epub.invalid/" + path), href);
                var targetDoc = documents[target.AbsolutePath.TrimStart('/')];
                if (target.Fragment.Length > 0)
                    Assert.Contains(targetDoc.Descendants(), node => (string)node.Attribute("id") == target.Fragment[1..]);
                checkedLinks++;
            }
        Assert.True(checkedLinks >= 3); // Картинка, пустой завершающий XHTML и исходный внутренний якорь.
    }

    [Fact]
    public async Task SplitSourceContainerRetainsOneTargetInItsFirstChapter()
    {
        var content = ReplaceEntries(CreateLinkedEpub(), new Dictionary<string, string>
        {
            ["Text/one.xhtml"] = "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Главы</title></head><body><section id='part'><h1 id='start'>Глава</h1><p id='ref'>Первая часть. <a href='../Notes/notes.xhtml#note'>Сноска</a></p><h1 id='second'>Вторая глава</h1><p>Вторая часть. <a href='#part'>К началу раздела</a></p></section></body></html>",
            ["Notes/notes.xhtml"] = "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Сноски</title></head><body><h1 id='start'>Сноски</h1><p id='note'>Текст сноски. <a href='../Text/one.xhtml#part'>Раздел</a> <a href='../Text/one.xhtml#ref'>Первая часть</a> <a href='../Text/one.xhtml#second'>Вторая часть</a></p></body></html>"
        }, toc => toc.Replace("<navPoint id='notes'", "<navPoint id='two' playOrder='2'><navLabel><text>Вторая глава</text></navLabel><content src='Text/one.xhtml#second'/></navPoint><navPoint id='notes'"));
        var documents = await RebuildAndCheck(content, 3);
        var notes = documents.Single(pair => pair.Value.Root!.Value.Contains("Текст сноски"));
        var link = notes.Value.Descendants().Single(node => node.Name.LocalName == "a" && node.Value == "Раздел");
        var target = new Uri(new Uri("https://epub.invalid/" + notes.Key), (string)link.Attribute("href")!);
        Assert.Contains("Первая часть", documents[target.AbsolutePath.TrimStart('/')].Root!.Value);
        Assert.DoesNotContain("Вторая часть", documents[target.AbsolutePath.TrimStart('/')].Root!.Value);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("html")]
    public async Task RootContainerAnchorSurvivesTocSlicingAndLinksFromAnotherFile(string rootTag)
    {
        var content = ReplaceEntries(CreateLinkedEpub(), new Dictionary<string, string>
        {
            ["Text/one.xhtml"] = $"<html xmlns='http://www.w3.org/1999/xhtml' {(rootTag == "html" ? "id='root'" : "")}><head><title>Глава</title></head><body {(rootTag == "body" ? "id='root'" : "")}><h1 id='start'>Глава</h1><p id='ref'>Основной текст. <a href='../Notes/notes.xhtml#note'>Сноска</a> <a href='#root'>Начало файла</a></p></body></html>",
            ["Notes/notes.xhtml"] = "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Сноски</title></head><body><h1 id='start'>Сноски</h1><p id='note'>Текст сноски. <a href='../Text/one.xhtml#root'>Начало</a> <a href='../Text/one.xhtml#ref'>Текст</a> <a href='../Text/one.xhtml'>Файл</a></p></body></html>"
        }, toc => toc.Replace("Text/one.xhtml#start", "Text/one.xhtml#root"));
        await RebuildAndCheck(content, 2);
    }

    private static byte[] ReplaceEntries(byte[] original, Dictionary<string, string> replacements, Func<string, string> changeToc)
    {
        using var memory = new MemoryStream();
        memory.Write(original);
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, true))
        {
            var toc = zip.GetEntry("toc.ncx")!;
            string text;
            using (var reader = new StreamReader(toc.Open())) text = reader.ReadToEnd();
            replacements["toc.ncx"] = changeToc(text);
            foreach (var (path, replacement) in replacements)
            {
                zip.GetEntry(path)!.Delete();
                Add(zip, path, replacement);
            }
        }
        return memory.ToArray();
    }

    private static async Task<Dictionary<string, XDocument>> RebuildAndCheck(byte[] content, int expectedChapters)
    {
        using var config = new BookGetterConfig(new Options(), new HttpClient(), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        using var getter = new ImportGetter(config);
        using var file = await TempFile.Create(null, config.TempFolder.Path, "book.epub", content);
        using var book = new Book(new Uri("https://example.test/book"))
        {
            Title = "Ссылки", Author = new Author("Автор"), Chapters = await getter.Import(file)
        };
        Assert.Equal(expectedChapters, book.Chapters.Count());
        var output = Path.Combine(config.TempFolder.Path, "output");
        await new EpubBuilder(new Options { SavePath = output, BookNamePattern = "links" }, NullLogger.Instance).Build(book);

        var outputFile = Directory.GetFiles(output, "*.epub").Single();
        Assert.All(DiagnosticBookChecks.CheckFile(outputFile, expectedChapters), check => Assert.True(check.Passed, check.Message));
        using var zip = ZipFile.OpenRead(outputFile);
        var documents = zip.Entries.Where(entry => entry.FullName.EndsWith(".html") || entry.FullName.EndsWith(".xhtml"))
            .ToDictionary(entry => entry.FullName, entry => { using var stream = entry.Open(); return XDocument.Load(stream); });
        var origin = new Uri("https://epub.invalid/");
        var checkedLinks = 0;
        foreach (var (path, document) in documents)
        {
            var ids = document.Descendants().Attributes("id").Select(attribute => attribute.Value).ToArray();
            Assert.Equal(ids.Length, ids.Distinct().Count());
            foreach (var link in document.Descendants().Where(node => node.Name.LocalName == "a" && node.Attribute("href") != null))
            {
                var target = new Uri(new Uri(origin, path), link.Attribute("href")!.Value);
                if (target.Host != origin.Host) continue;
                Assert.True(documents.TryGetValue(target.AbsolutePath.TrimStart('/'), out var targetDoc), target.ToString());
                if (target.Fragment.Length > 0) Assert.Contains(targetDoc!.Descendants(), node => (string)node.Attribute("id") == target.Fragment[1..]);
                checkedLinks++;
            }
        }
        Assert.True(checkedLinks >= 7); // Три перехода, два локальных якоря и оглавление.
        return documents;
    }

    private static byte[] CreateLinkedEpub()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            Add(zip, "mimetype", "application/epub+zip");
            Add(zip, "META-INF/container.xml", "<container version='1.0' xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add(zip, "book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='2.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>linked-book</dc:identifier><dc:title>Книга</dc:title><dc:language>ru</dc:language></metadata><manifest><item id='one' href='Text/one.xhtml' media-type='application/xhtml+xml'/><item id='notes' href='Notes/notes.xhtml' media-type='application/xhtml+xml'/><item id='ncx' href='toc.ncx' media-type='application/x-dtbncx+xml'/></manifest><spine toc='ncx'><itemref idref='one'/><itemref idref='notes'/></spine></package>");
            Add(zip, "toc.ncx", "<ncx xmlns='http://www.daisy.org/z3986/2005/ncx/' version='2005-1'><head><meta name='dtb:uid' content='linked-book'/></head><docTitle><text>Книга</text></docTitle><navMap><navPoint id='one' playOrder='1'><navLabel><text>Глава</text></navLabel><content src='Text/one.xhtml#start'/></navPoint><navPoint id='notes' playOrder='2'><navLabel><text>Сноски</text></navLabel><content src='Notes/notes.xhtml#start'/></navPoint></navMap></ncx>");
            Add(zip, "Text/one.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Глава</title></head><body><h1 id='start'>Глава</h1><p id='ref'>Основной текст. <a href='../Notes/notes.xhtml#note'>Сноска</a> <a href='../Notes/notes.xhtml'>Все сноски</a> <a href='#start'>В начало</a></p></body></html>");
            Add(zip, "Notes/notes.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Сноски</title></head><body><h1 id='start'>Сноски</h1><p id='note'>Текст сноски. <a href='../Text/one.xhtml#ref'>Назад</a> <a href='#start'>В начало сносок</a></p></body></html>");
        }
        return memory.ToArray();
    }

    private static byte[] CreateLinkedEpubWithoutToc()
    {
        return ReplaceEntries(CreateLinkedEpub(), new Dictionary<string, string>(), toc =>
        {
            var document = XDocument.Parse(toc);
            document.Descendants().Single(node => node.Name.LocalName == "navMap").RemoveNodes();
            return document.ToString();
        });
    }

    private static byte[] CreateMyBookStructuredEpub(bool withToc, bool intro = true)
    {
        var replacements = new Dictionary<string, string>
        {
            ["Text/one.xhtml"] = "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Глава</title></head><body><section id='whole'><h1 id='start'>Глава</h1>"
                + (intro ? "<p>Вступительный текст. <a href='#second'>Ко второй главе</a></p>" : "")
                + "<div><h2 id='prologue'>Пролог.</h2><p>Текст пролога. <a href='#whole'>Начало книги</a></p>"
                + "<div id='wrapper'><h2 id='first'>Глава 1.</h2><p id='ref'>Основной текст. <a href='../Notes/notes.xhtml#note'>Сноска</a><img src='../illustration.png'/></p>"
                + "<h3>Обычный подзаголовок</h3><p>Продолжение первой главы.</p>"
                + "<h2 id='second'>Глава 2.</h2><p>Текст второй главы. <a href='#wrapper'>К началу части</a> <a href='#first'>Первая глава</a></p></div></div></section></body></html>",
            ["Notes/notes.xhtml"] = "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Сноски</title></head><body><h1 id='start'>Сноски</h1><h2>Глава 1.</h2><p id='note'>Текст сноски. <a href='../Text/one.xhtml#ref'>Назад</a></p><h2>Глава 2.</h2><p>Другое примечание.</p></body></html>"
        };
        var content = ReplaceEntries(CreateLinkedEpub(), replacements, toc =>
        {
            if (withToc) return toc;
            var document = XDocument.Parse(toc);
            document.Descendants().Single(node => node.Name.LocalName == "navMap").RemoveNodes();
            return document.ToString();
        });
        using var memory = new MemoryStream();
        memory.Write(content);
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, true))
        {
            string package;
            using (var reader = new StreamReader(zip.GetEntry("book.opf")!.Open())) package = reader.ReadToEnd();
            package = package.Replace("</manifest>", "<item id='illustration' href='illustration.png' media-type='image/png'/></manifest>");
            zip.GetEntry("book.opf")!.Delete();
            Add(zip, "book.opf", package);
            using var image = zip.CreateEntry("illustration.png").Open();
            image.Write(Png);
        }
        return memory.ToArray();
    }

    private static byte[] CreateEpubWithImageOnlySection()
    {
        using var memory = new MemoryStream();
        memory.Write(CreateIllustratedEpub(false));
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, true))
        {
            string package;
            using (var reader = new StreamReader(zip.GetEntry("book.opf")!.Open())) package = reader.ReadToEnd();
            package = package.Replace("</manifest>", "<item id='picture' href='picture.xhtml' media-type='application/xhtml+xml'/><item id='empty' href='empty.xhtml' media-type='application/xhtml+xml'/></manifest>")
                .Replace("<itemref idref='chapter'/>", "<itemref idref='picture'/><itemref idref='chapter'/><itemref idref='empty'/>");
            zip.GetEntry("book.opf")!.Delete();
            Add(zip, "book.opf", package);
            string chapter;
            using (var reader = new StreamReader(zip.GetEntry("chapter.xhtml")!.Open())) chapter = reader.ReadToEnd();
            chapter = chapter.Replace("</body>", "<p><a href='picture.xhtml#picture'>Рисунок</a> <a href='empty.xhtml#empty'>Конец</a></p></body>");
            zip.GetEntry("chapter.xhtml")!.Delete();
            Add(zip, "chapter.xhtml", chapter);
            Add(zip, "picture.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Рисунок</title></head><body><img id='picture' src='illustration.png' alt='Рисунок'/></body></html>");
            Add(zip, "empty.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Пустой файл</title></head><body><span id='empty'></span> \n </body></html>");
        }
        return memory.ToArray();
    }

    private static byte[] CreateEpub()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            Add(zip, "mimetype", "application/epub+zip");
            Add(zip, "META-INF/container.xml", "<container version='1.0' xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add(zip, "book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='2.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>synthetic-book</dc:identifier><dc:title>Книга</dc:title><dc:language>ru</dc:language></metadata><manifest><item id='chapter' href='chapter.xhtml' media-type='application/xhtml+xml'/><item id='ncx' href='toc.ncx' media-type='application/x-dtbncx+xml'/></manifest><spine toc='ncx'><itemref idref='chapter'/></spine></package>");
            Add(zip, "toc.ncx", "<ncx xmlns='http://www.daisy.org/z3986/2005/ncx/' version='2005-1'><head><meta name='dtb:uid' content='synthetic-book'/></head><docTitle><text>Книга</text></docTitle><navMap><navPoint id='one' playOrder='1'><navLabel><text>Глава 1</text></navLabel><content src='chapter.xhtml#start'/></navPoint></navMap></ncx>");
            Add(zip, "chapter.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Глава 1</title></head><body><h1 id='start'>Глава 1</h1><p>Текст главы.</p><h2 id='sub'>Подзаголовок</h2><p>Дальше.</p><h2>Глава 1</h2><h3>Другой раздел</h3><p><a href='#sub'>Назад</a></p></body></html>");
        }
        return memory.ToArray();
    }

    private static byte[] CreateIllustratedEpub(bool withToc)
    {
        using var memory = new MemoryStream();
        memory.Write(CreateEpub());
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, true))
        {
            string Read(string path)
            {
                using var reader = new StreamReader(zip.GetEntry(path)!.Open());
                return reader.ReadToEnd();
            }
            var package = Read("book.opf").Replace("</manifest>", "<item id='illustration' href='illustration.png' media-type='image/png'/></manifest>");
            var chapter = Read("chapter.xhtml").Replace("<p>Текст главы.</p>", "<p>Текст главы.</p><p><img src='illustration.png' alt='Рисунок' /></p><p>После рисунка.</p>");
            zip.GetEntry("book.opf")!.Delete();
            zip.GetEntry("chapter.xhtml")!.Delete();
            Add(zip, "book.opf", package);
            Add(zip, "chapter.xhtml", chapter);
            if (!withToc)
            {
                var toc = XDocument.Parse(Read("toc.ncx"));
                toc.Descendants().Single(node => node.Name.LocalName == "navMap").RemoveNodes();
                zip.GetEntry("toc.ncx")!.Delete();
                Add(zip, "toc.ncx", toc.ToString());
            }
            using var image = zip.CreateEntry("illustration.png").Open();
            image.Write(Png);
        }
        return memory.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string text)
    {
        using var stream = zip.CreateEntry(path).Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes);
    }

    private sealed class ImportGetter(BookGetterConfig config) : GetterBase(config)
    {
        protected override Uri SystemUrl => new("https://example.com");
        public Task<IEnumerable<Chapter>> Import(TempFile file, bool splitNumberedHeadings = false)
            => FillChaptersFromEpub(file, splitNumberedHeadings);
        public override Task<Book> Get(Uri url) => throw new NotSupportedException();
    }
}
