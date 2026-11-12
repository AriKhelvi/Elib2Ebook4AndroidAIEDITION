using E2E.Droid.Services;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;

namespace ServiceTests;

public sealed class BookContentTests
{
    [Fact]
    public void EmptyBookIsNotReportedAsSuccessful()
    {
        using var book = new Book(new Uri("https://example.com/book"));
        Assert.Throws<InvalidOperationException>(() => BookPipeline.CheckDownloadedContent(book, new CallbackLogger(_ => { })));
    }

    [Fact]
    public void OnlyMissingChapterIsNotReportedAsSuccessful()
    {
        using var book = new Book(new Uri("https://example.com/book")) { Chapters = [new Chapter { Title = "Закрытая глава" }] };
        Assert.Throws<InvalidOperationException>(() => BookPipeline.CheckDownloadedContent(book, new CallbackLogger(_ => { })));
    }

    [Fact]
    public void PartialBookIdentifiesMissingChapter()
    {
        using var book = new Book(new Uri("https://example.com/book"))
        {
            Chapters = [new Chapter { Title = "Начало", Content = "Текст" }, new Chapter { Title = "Продолжение" }]
        };
        var warnings = new List<string>();
        BookPipeline.CheckDownloadedContent(book, new CallbackLogger(_ => { }, warnings.Add));
        Assert.Single(warnings);
        Assert.Contains("Продолжение", warnings[0]);
    }

    [Fact]
    public async Task ComicImagesAreUsableWithoutText()
    {
        var directory = Directory.CreateTempSubdirectory("e2e-content-");
        try
        {
            using var image = await TempFile.Create(new Uri("https://example.com/image"), directory.FullName, "page.png", new byte[] { 1 });
            using var book = new Book(new Uri("https://example.com/book")) { Chapters = [new Chapter { Images = [image] }] };
            var warnings = new List<string>();
            BookPipeline.CheckDownloadedContent(book, new CallbackLogger(_ => { }, warnings.Add));
            Assert.Empty(warnings);
        }
        finally { directory.Delete(true); }
    }
}
