using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.ExternalServices.Bookinist;
using Elib2Ebook.ExternalServices.Boosty;
using Elib2Ebook.ExternalServices.DarkNovels;
using Elib2Ebook.ExternalServices.Fb2Top.Getters;
using HtmlAgilityPack;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class HeadingPreservationGetterTests
{
    [Theory]
    [InlineData("Boosty", "h1")]
    [InlineData("DarkNovels", "h1")]
    [InlineData("Bookinist", "h2")]
    [InlineData("Fb2Top", "h2")]
    [InlineData("LadyLib", "h3")]
    public async Task RemovesOnlyLeadingTitleAndKeepsInternalHeadings(string kind, string tag)
    {
        var body = $"<p>Начало.</p><h1>Часть первая</h1><p>Текст.</p><h2>Встреча</h2><h3>На следующий день</h3><{tag}>Глава 1</{tag}><p>Конец.</p>";

        var result = await ReadChapter(kind, $"<{tag}>Глава 1</{tag}>" + body);

        Assert.Equal(body, result);
    }

    [Theory]
    [InlineData("Boosty", "h1")]
    [InlineData("DarkNovels", "h1")]
    [InlineData("Bookinist", "h2")]
    [InlineData("Fb2Top", "h2")]
    [InlineData("LadyLib", "h3")]
    public async Task KeepsNonmatchingOpeningHeadingAndOrdinaryTitleParagraph(string kind, string tag)
    {
        var body = $"<{tag}>Авторское посвящение</{tag}><p>Глава 1</p><p>Текст.</p>";

        Assert.Equal(body, await ReadChapter(kind, body));
    }

    [Theory]
    [InlineData("Boosty", "h1")]
    [InlineData("DarkNovels", "h1")]
    [InlineData("Bookinist", "h2")]
    [InlineData("Fb2Top", "h2")]
    [InlineData("LadyLib", "h3")]
    public async Task KeepsImageInsideMatchingHeading(string kind, string tag)
    {
        const string image = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var body = $"<{tag}>Глава 1<img src='{image}'></{tag}><p>Текст.</p>";

        var result = await ReadChapter(kind, body);

        Assert.Contains($"<{tag}>Глава 1<img", result);
        Assert.Contains("<p>Текст.</p>", result);
    }

    private static async Task<string> ReadChapter(string kind, string body)
    {
        var handler = new AfAuditSupport.Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = kind == "DarkNovels"
                ? new ByteArrayContent(Zip(body))
                : new StringContent(kind switch
                {
                    "Boosty" => "<article class='Post_content_ETkyT'>" + body + "</article>",
                    "Fb2Top" or "LadyLib" => "<section>" + body + "</section>",
                    _ => body
                })
        });
        using var config = AfAuditSupport.Config(handler);
        using GetterBase getter = kind switch
        {
            "Boosty" => new BoostyGetter(config),
            "DarkNovels" => new DarkNovelsGetter(config),
            "Bookinist" => new BookinistGetter(config),
            "Fb2Top" => new Fb2TopGetter(config),
            "LadyLib" => new LadyLibGetter(config),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        if (kind == "Boosty")
        {
            var postType = typeof(BoostyGetter).Assembly.GetType("Elib2Ebook.ExternalServices.Boosty.Types.BoostyPost")!;
            var post = Activator.CreateInstance(postType)!;
            postType.GetProperty("Title")!.SetValue(post, "Глава 1");
            postType.GetProperty("HasAccess")!.SetValue(post, true);
            return (await AfAuditSupport.Invoke<HtmlDocument>(getter, "GetChapter", post, "test")).DocumentNode.InnerHtml;
        }

        if (kind == "DarkNovels")
        {
            var chapterType = typeof(DarkNovelsGetter).Assembly.GetType("Elib2Ebook.ExternalServices.DarkNovels.Types.DarkNovelsChapter")!;
            var source = Activator.CreateInstance(chapterType)!;
            chapterType.GetProperty("Id")!.SetValue(source, 1);
            var chapter = new Chapter { Title = "Глава 1" };
            await (Task)typeof(DarkNovelsGetter).GetMethod("FillChapter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(getter, ["123", source, chapter])!;
            return chapter.Content;
        }

        var urlChapter = new UrlChapter(new Uri("https://example.test/chapter/1"), "Глава 1");
        var declaringType = kind == "Bookinist" ? typeof(BookinistGetter) : typeof(Fb2TopGetterBase);
        var method = declaringType.GetMethod("GetChapter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var doc = await (Task<HtmlDocument>)method.Invoke(getter, [urlChapter])!;
        return doc.DocumentNode.InnerHtml;
    }

    private static byte[] Zip(string html)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("chapter.html").Open(), new UTF8Encoding(false)))
        {
            writer.Write(html);
        }
        return stream.ToArray();
    }
}
