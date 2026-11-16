using System.IO.Compression;
using System.Reflection;
using E2E.Droid.Services;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Microsoft.Extensions.Logging.Abstractions;

namespace ServiceTests;

public sealed class ComicFormatPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "e2e-comic-format-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DirectPipelineReplacesLegacyTextFormatsAndKeepsPagesEvenWithImagesDisabled()
    {
        var types = typeof(GetterResolver).GetField("_getterTypes", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = types.GetValue(null);
        types.SetValue(null, new[] { typeof(ComicFormatFixtureGetter) });
        try
        {
            var options = new Options
            {
                Url = [ComicFormatFixtureGetter.BookUrl], Format = ["fb2", "epub", "txt"], NoImage = true,
                Timeout = 30, BookNamePattern = "comic", SavePath = Path.Combine(_root, "out"), TempPath = Path.Combine(_root, "tmp")
            };

            var result = await BookPipeline.RunAsync(options, NullLogger.Instance, null, CancellationToken.None);

            Assert.Equal(new[] { "cbz" }, options.Format);
            Assert.False(options.NoImage);
            Assert.Equal(1, result.Info.Chapters);
            var file = Assert.Single(result.Files);
            Assert.Equal(".cbz", Path.GetExtension(file));
            using var zip = ZipFile.OpenRead(file);
            var page = Assert.Single(zip.Entries);
            Assert.Equal("1.png", page.FullName);
            using var stream = page.Open();
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes);
            Assert.Equal(ComicFormatFixtureGetter.Page, bytes.ToArray());
        }
        finally
        {
            types.SetValue(null, previous);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

public sealed class ComicFormatFixtureGetter(BookGetterConfig config) : GetterBase(config)
{
    internal const string BookUrl = "https://mangalib.me/ru/manga/1--fixture";
    internal static readonly byte[] Page = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
    protected override Uri SystemUrl => new("https://mangalib.me/");

    public override Task Init()
    {
        Assert.Equal(new[] { "cbz" }, Config.Options.Format);
        Assert.False(Config.Options.NoImage);
        return Task.CompletedTask;
    }

    public override async Task<Book> Get(Uri url)
    {
        var page = await TempFile.Create(new Uri(SystemUrl, "page.png"), Config.TempFolder.Path, "page.png", Page);
        return new Book(url)
        {
            Title = "Комикс", Author = new Author("Автор"),
            Chapters = [new Chapter { Title = "Глава", Content = "<p><img src='page.png'/></p>", Images = [page] }]
        };
    }
}
