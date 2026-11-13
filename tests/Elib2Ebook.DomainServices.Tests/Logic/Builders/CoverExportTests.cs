using System.IO.Compression;
using System.Xml.Linq;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Logic.Builders;

public sealed class CoverExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cover-export-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a52kAAAAASUVORK5CYII=");

    [Theory]
    [InlineData("epub", false)]
    [InlineData("epub", true)]
    [InlineData("fb2", false)]
    [InlineData("fb2", true)]
    public async Task CoverSwitchControlsBothSeparatePathsButKeepsEmbeddedCoverAndOtherAttachments(string format, bool separateCover)
    {
        var input = Path.Combine(_root, "input");
        var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(input);
        using var book = new Book(new Uri("https://example.test/book/1")) { Title = "Book", Author = new Author("Author") };
        book.Cover = await TempFile.Create(new Uri("https://example.test/cover.png"), input, "cover.png", Png);
        using var illustration = await TempFile.Create(new Uri("https://example.test/illustration.png"), input, "illustration.png", Png);
        book.Chapters = [new Chapter { Title = "Chapter", Content = "<p>Text</p><img src='illustration.png' />", Images = [illustration] }];
        book.AdditionalFiles.Add(AdditionalTypeEnum.Audio, await TempFile.Create(new Uri("https://example.test/audio.mp3"), input, "audio.mp3", new byte[] { 1, 2, 3 }));
        var options = new Options
        {
            Format = [format], SavePath = output, BookNamePattern = "{Book.Title}", Cover = separateCover,
            Additional = true, AdditionalTypes = [AdditionalTypeEnum.Images, AdditionalTypeEnum.Audio]
        };

        await BuilderProvider.Get(format, options, NullLogger.Instance).Build(book);
        await new AdditionaFileBuilder(options, NullLogger.Instance).Build(book);

        Assert.Equal(separateCover, File.Exists(Path.Combine(output, "Book_cover.png")));
        Assert.Equal(separateCover, File.Exists(Path.Combine(output, "Book", "Images", "Cover.png")));
        Assert.Equal(Png, await File.ReadAllBytesAsync(Path.Combine(output, "Book", "Images", "Chapter", "1.png")));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(output, "Book", "Audio", "audio.mp3")));
        if (format == "fb2")
        {
            var xml = XDocument.Load(Path.Combine(output, "Book.fb2"));
            XNamespace ns = "http://www.gribuser.ru/xml/fictionbook/2.0";
            Assert.Single(xml.Descendants(ns + "coverpage"));
            Assert.Equal(2, xml.Descendants(ns + "binary").Count());
            Assert.All(xml.Descendants(ns + "binary"), item => Assert.Equal(Png, Convert.FromBase64String(item.Value)));
        }
        else
        {
            using var zip = ZipFile.OpenRead(Path.Combine(output, "Book.epub"));
            var package = zip.Entries.Single(entry => entry.FullName.EndsWith(".opf"));
            await using var stream = package.Open();
            var xml = XDocument.Load(stream);
            // EpubSharp пишет EPUB 3.2: обложка помечена в manifest, без старого EPUB 2 meta name="cover".
            var coverItem = Assert.Single(xml.Descendants(), node => node.Name.LocalName == "item" &&
                ((string)node.Attribute("properties") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("cover-image"));
            var name = (string)coverItem.Attribute("href");
            var image = zip.Entries.Single(entry => entry.FullName.EndsWith(name, StringComparison.Ordinal));
            await using var imageStream = image.Open();
            using var content = new MemoryStream();
            await imageStream.CopyToAsync(content);
            Assert.Equal(Png, content.ToArray());
            Assert.Contains(zip.Entries, entry => entry.FullName.EndsWith("illustration.png", StringComparison.Ordinal));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
