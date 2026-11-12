using System.Xml;
using System.Xml.Linq;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Configs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Logic.Builders;

public sealed class Fb2SerializationTests : IDisposable
{
    private static readonly XNamespace Ns = "http://www.gribuser.ru/xml/fictionbook/2.0";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fb2-tests-" + Guid.NewGuid());

    public Fb2SerializationTests() => Directory.CreateDirectory(_directory);

    private Fb2Builder CreateBuilder() => new(new Options(["https://example.com/book"])
    {
        Format = ["fb2"], SavePath = _directory, BookNamePattern = "{Book.Title}",
    }, NullLogger.Instance);

    private static Book CreateBook(string html) => new(new Uri("https://example.com/book?id=1&lang=ru"))
    {
        Title = "Книга",
        Author = new Author("Псевдоним"),
        Chapters = [new Chapter { Title = "Глава", Content = html }],
    };

    private async Task<XDocument> Build(Book book, Fb2Builder builder = null)
    {
        await (builder ?? CreateBuilder()).Build(book);
        return XDocument.Load(Path.Combine(_directory, book.Title + ".fb2"));
    }

    [Fact]
    public async Task MetadataContainsRequiredFieldsInSchemaOrder()
    {
        using var book = CreateBook("Текст");
        book.CoAuthors = [new Author("Анна Михайлова")];
        book.Annotation = "Начало<br><u>описания</u>";
        book.Lang = null;
        var doc = await Build(book);

        var titleInfo = doc.Root.Element(Ns + "description").Element(Ns + "title-info");
        Assert.Equal(["genre", "author", "author", "book-title", "annotation", "lang"],
            titleInfo.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("unrecognised", titleInfo.Element(Ns + "genre").Value);
        Assert.Equal("und", titleInfo.Element(Ns + "lang").Value);
        Assert.Equal("Псевдоним", titleInfo.Elements(Ns + "author").First().Element(Ns + "nickname").Value);
        Assert.Equal("Михайлова", titleInfo.Elements(Ns + "author").Last().Element(Ns + "last-name").Value);
        Assert.Equal(["Начало", "описания"], titleInfo.Element(Ns + "annotation").Elements(Ns + "p").Select(e => e.Value));

        var info = doc.Root.Element(Ns + "description").Element(Ns + "document-info");
        Assert.Equal(["author", "author", "program-used", "date", "src-url", "id", "version"],
            info.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(book.Url.ToString(), info.Element(Ns + "src-url").Value);
        Assert.False(string.IsNullOrWhiteSpace(info.Element(Ns + "id").Value));
        Assert.Equal(1f, XmlConvert.ToSingle(info.Element(Ns + "version").Value));
    }

    [Fact]
    public async Task BreaksSplitParagraphsAndPreserveNestedFormattingAndText()
    {
        using var book = CreateBook("<p>До <strong>жирный<br><em>курсив</em><br><br>конец</strong>"
            + "<u> &amp; &lt;текст&gt; 😀</u> после</p>");
        var doc = await Build(book);
        var blocks = doc.Descendants(Ns + "section").Single().Elements().Skip(1).ToArray();

        Assert.Equal(["p", "p", "empty-line", "p"], blocks.Select(e => e.Name.LocalName));
        Assert.Equal(["До жирный", "курсив", "", "конец & <текст> 😀 после"], blocks.Select(e => e.Value));
        Assert.Equal("жирный", blocks[0].Element(Ns + "strong").Value);
        Assert.Equal("курсив", blocks[1].Element(Ns + "strong").Element(Ns + "emphasis").Value);
        Assert.Equal("конец", blocks[3].Element(Ns + "strong").Value);
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName is "br" or "u");
        Assert.DoesNotContain(doc.Descendants(Ns + "p"), p => p.Descendants(Ns + "p").Any());
    }

    [Fact]
    public async Task BareTextNestedBlocksAndLinksRemainReadableWithoutNestedParagraphs()
    {
        using var book = CreateBook("Вступление <span>рядом</span><div><p>Один</p>"
            + "<div>Два <a href='https://example.com/?a=1&amp;b=2'><b>ссылка</b></a></div></div>"
            + "<blockquote><p>Цитата</p><p>Продолжение</p></blockquote><h2>Подзаголовок</h2>");
        var doc = await Build(book);
        var section = doc.Descendants(Ns + "section").Single();

        Assert.Equal(["Вступление рядом", "Один", "Два ссылка"], section.Elements(Ns + "p").Select(p => p.Value));
        Assert.Equal(["Цитата", "Продолжение"], section.Element(Ns + "cite").Elements(Ns + "p").Select(p => p.Value));
        Assert.Equal("Подзаголовок", section.Element(Ns + "subtitle").Value);
        var link = section.Descendants(Ns + "a").Single();
        Assert.Equal("https://example.com/?a=1&b=2", link.Attribute(Xlink + "href").Value);
        Assert.Equal("ссылка", link.Element(Ns + "strong").Value);
        Assert.DoesNotContain(section.Nodes(), node => node is XText text && !string.IsNullOrWhiteSpace(text.Value));
        Assert.DoesNotContain(section.Descendants(Ns + "p"), p => p.Descendants(Ns + "p").Any());
    }

    [Fact]
    public async Task ImagesUseValidUniqueIdsAndMatchingLinks()
    {
        using var image = await TempFile.Create(new Uri("https://example.com/image.jpg"), _directory,
            "1 image.jpg", new byte[] { 0xff, 0xd8, 0xff });
        using var book = CreateBook("<p>Текст<img src='1 image.jpg'></p>");
        book.Cover = image;
        book.Chapters.Single().Images = [image, image];
        var doc = await Build(book);

        var binary = Assert.Single(doc.Root.Elements(Ns + "binary"));
        var id = binary.Attribute("id").Value;
        Assert.Equal(id, XmlConvert.VerifyNCName(id));
        Assert.Equal("image/jpeg", binary.Attribute("content-type").Value);
        Assert.Equal(image.Content, Convert.FromBase64String(binary.Value));
        Assert.Equal(2, doc.Descendants(Ns + "image").Count());
        Assert.All(doc.Descendants(Ns + "image"), img => Assert.Equal("#" + id, img.Attribute(Xlink + "href").Value));
    }

    [Fact]
    public async Task ReusingBuilderDoesNotCarryPreviousDocumentContent()
    {
        var builder = CreateBuilder();
        using var first = CreateBook("Первая версия");
        var original = await Build(first, builder);
        using var second = CreateBook("Вторая версия");
        var current = await Build(second, builder);

        Assert.Single(current.Root.Elements(Ns + "description"));
        Assert.Single(current.Descendants(Ns + "section"));
        Assert.DoesNotContain("Первая версия", current.ToString());
        Assert.Contains("Вторая версия", current.ToString());
        Assert.Equal(original.Descendants(Ns + "id").Single().Value, current.Descendants(Ns + "id").Single().Value);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
