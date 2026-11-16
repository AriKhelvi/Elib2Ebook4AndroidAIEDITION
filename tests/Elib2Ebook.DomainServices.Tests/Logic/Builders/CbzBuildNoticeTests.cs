using System.IO.Compression;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Configs;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Logic.Builders;

public sealed class CbzBuildNoticeTests
{
    [Theory]
    [InlineData(499, false)]
    [InlineData(500, true)]
    public async Task LargeArchiveNoticePrecedesPackingWithoutMarkingDownloadAsFailed(int pages, bool expectedNotice)
    {
        var root = Path.Combine(Path.GetTempPath(), "cbz-notice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var image = await TempFile.Create(new Uri("https://example.test/page.png"), root, "page.png", new byte[] { 1, 2, 3 });
            using var book = new Book(new Uri("https://example.test/comic"))
            {
                Title = "Comic", Author = new Author("Author"),
                Chapters = [new Chapter { Images = Enumerable.Repeat(image, pages).ToArray() }]
            };
            var output = Path.Combine(root, "out");
            var file = Path.Combine(output, "Comic.cbz");
            var logger = new NoticeLogger(file);

            await new CbzBuilder(new Options { SavePath = output, BookNamePattern = "{Book.Title}" }, logger).Build(book);

            Assert.Equal(expectedNotice, logger.NoticeSeen);
            Assert.False(logger.WarningSeen);
            using var zip = ZipFile.OpenRead(file);
            Assert.Equal(pages, zip.Entries.Count);
            Assert.All(zip.Entries, entry => Assert.Equal(3, entry.Length));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class NoticeLogger(string outputFile) : ILogger
    {
        public bool NoticeSeen { get; private set; }
        public bool WarningSeen { get; private set; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            WarningSeen |= logLevel >= LogLevel.Warning;
            var message = formatter(state, exception);
            if (!message.StartsWith("Собираю большой архив CBZ", StringComparison.Ordinal)) return;
            NoticeSeen = true;
            Assert.Equal(LogLevel.Information, logLevel);
            Assert.Contains("не запускайте скачивание заново", message);
            Assert.False(File.Exists(outputFile));
        }
    }
}
