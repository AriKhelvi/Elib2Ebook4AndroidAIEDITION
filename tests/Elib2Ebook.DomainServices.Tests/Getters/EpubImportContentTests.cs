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
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Microsoft.Extensions.Logging.Abstractions;
using E2E.Droid.Services;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class EpubImportContentTests
{
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

    private static void Add(ZipArchive zip, string path, string text)
    {
        using var stream = zip.CreateEntry(path).Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes);
    }

    private sealed class ImportGetter(BookGetterConfig config) : GetterBase(config)
    {
        protected override Uri SystemUrl => new("https://example.com");
        public Task<IEnumerable<Chapter>> Import(TempFile file) => FillChaptersFromEpub(file);
        public override Task<Book> Get(Uri url) => throw new NotSupportedException();
    }
}
