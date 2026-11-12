using System.IO.Compression;
using System.Xml.Linq;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Configs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Logic.Builders;

public sealed class UnicodeBookSerializationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "unicode-books-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("fb2", false)]
    [InlineData("epub", false)]
    [InlineData("fb2", true)]
    [InlineData("epub", true)]
    public async Task EmojiSurviveMetadataAndChapterWhileLongFileNameKeepsWholeCharacters(string format, bool longTitle)
    {
        var prefix = new string('A', 99);
        var title = longTitle ? prefix + "🐠 хвост" : "Книга 🐠 👩🏽‍💻";
        const string chapterTitle = "Глава 👩🏽‍💻 🇷🇺";
        const string chapterText = "Текст 😀 ☀️ 🏳️‍🌈";
        using var book = new Book(new Uri("https://example.com/unicode-book"))
        {
            Title = title,
            Author = new Author("Автор"),
            Chapters = [new Chapter { Title = chapterTitle, Content = "<p>" + chapterText + "</p>" }],
        };
        var options = new Options([book.Url.AbsoluteUri])
        {
            Format = [format], SavePath = _directory, BookNamePattern = "{Book.Title}",
        };
        BuilderBase builder = format == "fb2"
            ? new Fb2Builder(options, NullLogger.Instance)
            : new EpubBuilder(options, NullLogger.Instance);
        await builder.Build(book);

        var path = Assert.Single(Directory.GetFiles(_directory));
        Assert.Equal((longTitle ? prefix : title) + "." + format, Path.GetFileName(path));
        if (format == "fb2")
        {
            XNamespace ns = "http://www.gribuser.ru/xml/fictionbook/2.0";
            var doc = XDocument.Load(path);
            Assert.Equal(title, doc.Descendants(ns + "book-title").Single().Value);
            var section = Assert.Single(doc.Descendants(ns + "section"));
            Assert.Equal(chapterTitle, section.Element(ns + "title").Value);
            Assert.Equal(chapterText, section.Element(ns + "p").Value);
        }
        else
        {
            using var archive = ZipFile.OpenRead(path);
            var package = archive.Entries.Single(entry => entry.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
            using var packageStream = package.Open();
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            Assert.Equal(title, XDocument.Load(packageStream).Descendants(dc + "title").Single().Value);

            XNamespace xhtml = "http://www.w3.org/1999/xhtml";
            var chapters = new List<XDocument>();
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = entry.Open();
                var document = XDocument.Load(stream);
                if (document.Descendants(xhtml + "h1").Any(node => node.Value == chapterTitle)) chapters.Add(document);
            }
            var chapter = Assert.Single(chapters);
            Assert.Contains(chapter.Descendants(xhtml + "p"), node => node.Value == chapterText);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
