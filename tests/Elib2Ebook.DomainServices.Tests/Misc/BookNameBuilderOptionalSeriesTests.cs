using System.IO.Compression;
using System.Xml.Linq;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc;
using Microsoft.Extensions.Logging.Abstractions;
using StringTokenFormatter;

namespace Elib2Ebook.DomainServices.Tests.Misc;

public sealed class BookNameBuilderOptionalSeriesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "optional-series-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("{Author.Name} - {Book.Title} {Seria.Number}", "Автор - Книга")]
    [InlineData("{Book.Title}{Seria.Name}{Seria.Number}{Seria.Url}", "Книга")]
    [InlineData("{Seria.Number}", "Книга")]
    [InlineData("{Seria.Name}", "Книга")]
    [InlineData("{Seria.Url}", "Книга")]
    [InlineData("{Book.Title}{:if,Seria.HasSeria} - {Seria.Name} #{Seria.Number}{:ifend}", "Книга")]
    public void MissingSeriesDoesNotPreventNaming(string pattern, string expected)
    {
        using var book = CreateBook();

        Assert.Equal(expected, BookNameBuilder.Build(pattern, book));
        Assert.Null(book.Seria);
    }

    [Theory]
    [InlineData(null, null, "Книга")]
    [InlineData("Цикл", null, "Книга Цикл")]
    [InlineData(null, "2", "Книга  2")]
    [InlineData("Цикл", "2", "Книга Цикл 2")]
    public void SeriesFieldsMayBeMissingIndividually(string name, string number, string expected)
    {
        using var book = CreateBook();
        var series = new Seria { Name = name, Number = number };
        book.Seria = series;

        Assert.Equal(expected, BookNameBuilder.Build("{Book.Title} {Seria.Name} {Seria.Number}{Seria.Url}", book));
        Assert.Same(series, book.Seria);
        Assert.Equal(name, series.Name);
        Assert.Equal(number, series.Number);
    }

    [Fact]
    public void ExistingSeriesStillEnablesConditionalTemplate()
    {
        using var book = CreateBook();
        book.Seria = new Seria { Name = "Цикл", Number = "2" };

        Assert.Equal("Книга - Цикл #2", BookNameBuilder.Build(
            "{Book.Title}{:if,Seria.HasSeria} - {Seria.Name} #{Seria.Number}{:ifend}", book));
    }

    [Theory]
    [InlineData("{Seria.Nubmer}")]
    [InlineData("{Book.Unknown}")]
    public void UnknownTokenStillReportsTemplateMistake(string pattern)
    {
        using var book = CreateBook();

        Assert.Throws<UnresolvedTokenException>(() => BookNameBuilder.Build(pattern, book));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("...")]
    public void EmptyOptionalTemplateAndUnusableTitleStillProduceAFileName(string title)
    {
        using var book = CreateBook();
        book.Title = title;

        Assert.Equal("Книга", BookNameBuilder.Build("{Seria.Number}", book));
    }

    [Theory]
    [InlineData("fb2")]
    [InlineData("epub")]
    public async Task DownloadedChaptersAreSavedWithoutSeriesUsingReportedTemplate(string format)
    {
        using var book = CreateBook();
        book.Chapters = [new Chapter { Title = "Последняя глава", Content = "<p>Скачанный текст</p>" }];
        var options = new Options([book.Url.AbsoluteUri])
        {
            SavePath = _directory,
            BookNamePattern = "{Author.Name} - {Book.Title} {Seria.Number}"
        };

        await BuilderProvider.Get(format, options, NullLogger.Instance).Build(book);

        var path = Assert.Single(Directory.GetFiles(_directory));
        Assert.Equal("Автор - Книга." + format, Path.GetFileName(path));
        Assert.Null(book.Seria);
        if (format == "fb2")
        {
            XNamespace ns = "http://www.gribuser.ru/xml/fictionbook/2.0";
            var document = XDocument.Load(path);
            Assert.Empty(document.Descendants(ns + "sequence"));
            Assert.Equal("Скачанный текст", Assert.Single(document.Descendants(ns + "section")).Element(ns + "p").Value);
        }
        else
        {
            using var archive = ZipFile.OpenRead(path);
            var texts = new List<string>();
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".html") || entry.FullName.EndsWith(".xhtml")))
            {
                using var stream = entry.Open();
                texts.Add(XDocument.Load(stream).Root.Value);
            }
            Assert.Contains(texts, text => text.Contains("Скачанный текст"));
        }
    }

    private static Book CreateBook() => new(new Uri("https://ranobelib.me/ru/265115--example"))
    {
        Title = "Книга", Author = new Author("Автор")
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
