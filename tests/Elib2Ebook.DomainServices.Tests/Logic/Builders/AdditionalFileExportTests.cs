using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Configs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Logic.Builders;

public sealed class AdditionalFileExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "additional-export-" + Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> ExportSelections()
    {
        yield return [true, new[] { AdditionalTypeEnum.Audio }, new[] { "Audio/audio.mp3" }];
        yield return [true, new[] { AdditionalTypeEnum.Images }, new[] { "Images/attachment.png" }];
        yield return [true, new[] { AdditionalTypeEnum.Books, AdditionalTypeEnum.Audio }, new[] { "Audio/audio.mp3", "Books/source.fb3" }];
        yield return [true, null, new[] { "Audio/audio.mp3", "Books/source.fb3", "Images/attachment.png" }];
        yield return [false, new[] { AdditionalTypeEnum.Books, AdditionalTypeEnum.Audio, AdditionalTypeEnum.Images }, Array.Empty<string>()];
    }

    [Theory]
    [MemberData(nameof(ExportSelections))]
    public async Task ExportsOnlySelectedTypesEvenWhenOtherFilesWereRetainedForParsing(
        bool additional, AdditionalTypeEnum[] selected, string[] expectedFiles)
    {
        var input = Path.Combine(_root, "input");
        var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(input);
        using var book = new Book(new Uri("https://example.test/book/1")) { Title = "Book", Author = new Author("Author") };
        var attachments = new[]
        {
            (AdditionalTypeEnum.Books, "source.fb3", new byte[] { 1, 2, 3 }),
            (AdditionalTypeEnum.Audio, "audio.mp3", new byte[] { 4, 5, 6 }),
            (AdditionalTypeEnum.Images, "attachment.png", new byte[] { 7, 8, 9 })
        };
        foreach (var (type, name, bytes) in attachments)
            book.AdditionalFiles.Add(type, await TempFile.Create(new Uri("https://example.test/" + name), input, name, bytes));
        var options = new Options
        {
            Additional = additional, AdditionalTypes = selected,
            SavePath = output, BookNamePattern = "{Book.Title}"
        };

        await new AdditionaFileBuilder(options, NullLogger.Instance).Build(book);

        var exportedRoot = Path.Combine(output, "Book");
        var actual = Directory.GetFiles(exportedRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(exportedRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedFiles, actual);
        foreach (var (type, name, bytes) in attachments)
        {
            // Экспорт не должен удалять исходники, которые ещё использует сборщик книги.
            Assert.Equal(bytes, book.AdditionalFiles.Get(type).Single().Content);
            if (expectedFiles.Contains(type + "/" + name))
                Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(exportedRoot, type.ToString(), name)));
            else
                Assert.False(Directory.Exists(Path.Combine(exportedRoot, type.ToString())));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
