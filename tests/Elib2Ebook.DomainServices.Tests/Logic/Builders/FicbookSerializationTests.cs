using System.IO.Compression;
using System.Xml.Linq;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Ficbook;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Logic.Builders;

public sealed class FicbookSerializationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ficbook-export-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("fb2")]
    [InlineData("epub")]
    [InlineData("txt")]
    public async Task ApiTextPreservesLiteralTagsEntitiesAndFormattingInFinishedFile(string format)
    {
        const string content = "Обычный <герой> &lt;замок&gt; &amp;lt;буквы&amp;gt; &amp;\n"
            + "<script>alert(\"текст\")</script>\n"
            + "<p><strong>Жирный</strong> и <em>курсив</em></p>";
        using var book = new Book(new Uri("https://ficbook.net/readfic/test"))
        {
            Title = "Книга", Author = new Author("Автор"),
            Chapters = [new Chapter { Title = "Глава <герой> & сюжет", Content = FicbookContent.ToHtml(content) }]
        };
        var options = new Options([book.Url.AbsoluteUri])
        {
            Format = [format], SavePath = _directory, BookNamePattern = "{Book.Title}"
        };
        BuilderBase builder = format switch
        {
            "fb2" => new Fb2Builder(options, NullLogger.Instance),
            "epub" => new EpubBuilder(options, NullLogger.Instance),
            _ => new TxtBuilder(options, NullLogger.Instance)
        };
        await builder.Build(book);

        var path = Path.Combine(_directory, "Книга." + format);
        XElement body = null;
        if (format == "fb2")
        {
            XNamespace ns = "http://www.gribuser.ru/xml/fictionbook/2.0";
            body = XDocument.Load(path).Descendants(ns + "section").Single();
            Assert.Contains(body.Descendants(ns + "strong"), node => node.Value == "Жирный");
            Assert.Contains(body.Descendants(ns + "emphasis"), node => node.Value == "курсив");
        }
        else if (format == "epub")
        {
            XNamespace ns = "http://www.w3.org/1999/xhtml";
            using var archive = ZipFile.OpenRead(path);
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = entry.Open();
                var document = XDocument.Load(stream);
                if (document.Descendants(ns + "h1").Any())
                {
                    Assert.Equal("Глава <герой> & сюжет", document.Descendants(ns + "h1").Single().Value);
                    body = document.Descendants(ns + "body").Single();
                }
            }
            Assert.NotNull(body);
            Assert.Contains(body.Descendants(ns + "strong"), node => node.Value == "Жирный");
            Assert.Contains(body.Descendants(ns + "em"), node => node.Value == "курсив");
        }

        var readable = body?.Value ?? await File.ReadAllTextAsync(path);
        Assert.Contains("<герой> <замок> &lt;буквы&gt; &", readable);
        Assert.Contains("<script>alert(\"текст\")</script>", readable);
        Assert.Contains("Жирный", readable);
        Assert.Contains("курсив", readable);
        if (body != null)
            Assert.DoesNotContain(body.Descendants(), node => node.Name.LocalName is "script" or "герой" or "замок" or "буквы");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
