using System.Reflection;
using System.Text;
using System.Xml.Linq;
using E2E.Droid.Services;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Microsoft.Extensions.Logging.Abstractions;

namespace ServiceTests;

public sealed class AdditionalFilePipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "e2e-additional-pipeline-" + Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> Selections()
    {
        yield return [true, null, true, new[] { "primary.fb2", "primary/Audio/audio.mp3", "primary/Images/attachment.png" }];
        yield return [true, Array.Empty<AdditionalTypeEnum>(), true, new[] { "primary.fb2", "primary/Audio/audio.mp3", "primary/Images/attachment.png" }];
        yield return [true, new[] { AdditionalTypeEnum.Books }, false, new[] { "primary.fb2" }];
        yield return [true, new[] { AdditionalTypeEnum.Books, AdditionalTypeEnum.Audio }, true, new[] { "primary.fb2", "primary/Audio/audio.mp3" }];
        yield return [true, new[] { AdditionalTypeEnum.Images }, true, new[] { "primary.fb2", "primary/Images/attachment.png" }];
        yield return [false, new[] { AdditionalTypeEnum.Books, AdditionalTypeEnum.Audio }, false, new[] { "primary.fb2" }];
    }

    [Theory]
    [MemberData(nameof(Selections))]
    public async Task BuildsPrimaryBookFromInternalSourceAndExportsOnlyAllowedExtras(
        bool additional, AdditionalTypeEnum[] requested, bool expectedAdditional, string[] expectedFiles)
    {
        using var resolver = new ResolverScope();
        var options = new Options
        {
            Url = [AdditionalFilePipelineGetter.Site], Format = ["fb2"], Timeout = 30,
            Additional = additional, AdditionalTypes = requested, BookNamePattern = "primary",
            SavePath = Path.Combine(_root, "out"), TempPath = Path.Combine(_root, "tmp")
        };

        var result = await BookPipeline.RunAsync(options, NullLogger.Instance, null, CancellationToken.None);

        Assert.Equal(expectedAdditional, options.Additional);
        Assert.False(options.HasAdditionalType(AdditionalTypeEnum.Books));
        Assert.Equal(1, result.Info.Chapters);
        Assert.Equal(expectedFiles, result.Files.Select(path => Path.GetRelativePath(options.SavePath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray());
        var primary = XDocument.Load(Path.Combine(options.SavePath, "primary.fb2"));
        Assert.Contains(AdditionalFilePipelineGetter.SourceText,
            primary.Descendants().Single(node => node.Name.LocalName == "body").Value);
        Assert.DoesNotContain(result.Files, path => path.EndsWith(".fb3", StringComparison.OrdinalIgnoreCase));

        // BuildOptions и сам конвейер вызывают нормализацию: второй вызов не должен возвращать отключённые типы.
        var normalized = options.AdditionalTypes.ToArray();
        BookPipeline.NormalizeAdditionalTypes(options);
        Assert.Equal(expectedAdditional, options.Additional);
        Assert.Equal(normalized, options.AdditionalTypes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class ResolverScope : IDisposable
    {
        private static readonly FieldInfo Types = typeof(GetterResolver).GetField("_getterTypes", BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly FieldInfo Probes = typeof(GetterResolver).GetField("_probes", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object _previousTypes = Types.GetValue(null);
        private readonly object _previousProbes = Probes.GetValue(null);

        public ResolverScope()
        {
            Types.SetValue(null, new[] { typeof(AdditionalFilePipelineGetter) });
            Probes.SetValue(null, null);
        }

        public void Dispose()
        {
            Types.SetValue(null, _previousTypes);
            Probes.SetValue(null, _previousProbes);
        }
    }
}

/// <summary>Исходник нужен для получения текста, хотя его экспорт в Android запрещён.</summary>
public sealed class AdditionalFilePipelineGetter(BookGetterConfig config) : GetterBase(config)
{
    internal const string Site = "https://additional-pipeline.example.test/book/1";
    internal const string SourceText = "Текст основной книги из внутреннего исходника.";
    protected override Uri SystemUrl => new("https://additional-pipeline.example.test");

    public override Task Init()
    {
        Assert.False(Config.Options.HasAdditionalType(AdditionalTypeEnum.Books));
        return Task.CompletedTask;
    }

    public override async Task<Book> Get(Uri url)
    {
        var book = new Book(url) { Title = "Основная книга", Author = new Author("Автор") };
        var source = await TempFile.Create(new Uri(SystemUrl, "source.fb3"), Config.TempFolder.Path,
            "source.fb3", Encoding.UTF8.GetBytes("<p>" + SourceText + "</p>"));
        book.AdditionalFiles.Add(AdditionalTypeEnum.Books, source);
        await using (var stream = book.AdditionalFiles.Get(AdditionalTypeEnum.Books).Single().GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            book.Chapters = [new Chapter { Title = "Глава", Content = await reader.ReadToEndAsync() }];

        book.AdditionalFiles.Add(AdditionalTypeEnum.Audio, await TempFile.Create(new Uri(SystemUrl, "audio.mp3"),
            Config.TempFolder.Path, "audio.mp3", new byte[] { 1, 2, 3 }));
        book.AdditionalFiles.Add(AdditionalTypeEnum.Images, await TempFile.Create(new Uri(SystemUrl, "attachment.png"),
            Config.TempFolder.Path, "attachment.png", new byte[] { 4, 5, 6 }));
        return book;
    }
}
