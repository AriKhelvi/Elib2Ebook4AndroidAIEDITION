using System.IO.Compression;
using System.Net;
using System.Text;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Microsoft.Extensions.Logging.Abstractions;

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
        Assert.NotNull(doc.GetElementbyId("start"));
        Assert.NotNull(doc.GetElementbyId("sub"));
        Assert.Contains("Текст главы", chapter.Content);
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
