using System.Reflection;
using E2E.Droid.Services;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;

namespace ServiceTests;

/// <summary>В диагностике работает настоящий конвейер CBZ, а загрузчик страниц целиком локальный.</summary>
internal sealed class DiagnosticComicResolverScope : IDisposable
{
    private static readonly FieldInfo Types = typeof(GetterResolver).GetField("_getterTypes", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Probes = typeof(GetterResolver).GetField("_probes", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object _previousTypes = Types.GetValue(null);
    private readonly object _previousProbes = Probes.GetValue(null);

    public DiagnosticComicResolverScope()
    {
        Types.SetValue(null, new[] { typeof(DiagnosticComicGetter) });
        Probes.SetValue(null, null);
    }

    public void Dispose()
    {
        Types.SetValue(null, _previousTypes);
        Probes.SetValue(null, _previousProbes);
    }
}

public sealed class DiagnosticComicGetter(BookGetterConfig config) : GetterBase(config)
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a52kAAAAASUVORK5CYII=");
    protected override Uri SystemUrl => new("https://mangalib.me/");

    public override Task Init()
    {
        Assert.Equal(new[] { "cbz" }, Config.Options.Format);
        Assert.False(Config.Options.NoImage);
        return Task.CompletedTask;
    }

    public override async Task<Book> Get(Uri url)
    {
        var page = await TempFile.Create(new Uri(SystemUrl, "page.png"), Config.TempFolder.Path, "page.png",
            url.AbsolutePath.EndsWith("/invalid", StringComparison.Ordinal) ? new byte[] { 1, 2, 3 } : Png);
        return new Book(url)
        {
            Title = "Комикс", Author = new Author("Автор"),
            // Изображение без текста: проверка числа текстовых глав здесь дала бы ложную ошибку.
            Chapters = [new Chapter { Title = "Глава", Images = [page] }]
        };
    }
}
