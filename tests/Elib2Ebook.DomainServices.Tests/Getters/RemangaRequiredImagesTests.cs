using System.Net;
using System.Net.Http.Headers;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Renovels.Getters;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class RemangaRequiredImagesTests
{
    private static readonly Uri ComicUrl = new("https://remanga.org/manga/test/main");

    [Fact]
    public async Task ForbiddenRequiredPageStopsBeforeThirdRequestAndDeletesDownloadedPage()
    {
        var requested = new List<string>();
        BookGetterConfig config = null;
        using (config = Config(3, request =>
        {
            var path = request.RequestUri.AbsolutePath;
            requested.Add(path);
            if (path == "/1.webp") return Image(1);
            Assert.Equal("/2.webp", path);
            Assert.Single(Directory.EnumerateFiles(config.TempFolder.Path));
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Доступ запрещён") };
        }))
        using (var getter = new RemangaGetter(config))
        {
            await getter.Init();
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(ComicUrl));

            Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
            Assert.Equal(new[] { "/1.webp", "/2.webp" }, requested);
            Assert.Empty(Directory.EnumerateFiles(config.TempFolder.Path));
        }
    }

    [Fact]
    public async Task SuccessfulPagesKeepOrderAndCdnDoesNotReceiveSiteCredentials()
    {
        var requested = new List<string>();
        using var config = Config(2, request =>
        {
            requested.Add(request.RequestUri.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Session"));
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("https://remanga.org/", request.Headers.Referrer?.ToString());
            return Image(request.RequestUri.AbsolutePath == "/1.webp" ? (byte)1 : (byte)2);
        });
        config.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-session");
        config.Client.DefaultRequestHeaders.Add("X-Session", "synthetic-session");
        config.Client.DefaultRequestHeaders.Add("Cookie", "session=synthetic");
        using var getter = new RemangaGetter(config);
        await getter.Init();

        using var book = await getter.Get(ComicUrl);

        var chapter = Assert.Single(book.Chapters);
        var images = chapter.Images.ToArray();
        Assert.Equal(new[] { "/1.webp", "/2.webp" }, requested);
        Assert.Equal(new byte[] { 1, 2 }, images.Select(image => image.Content[0]));
        Assert.True(chapter.Content.IndexOf(images[0].FullName, StringComparison.Ordinal) <
                    chapter.Content.IndexOf(images[1].FullName, StringComparison.Ordinal));
        Assert.NotNull(config.Client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task TextGetterStillTreatsIllustrationFailureAsOptional()
    {
        var requested = new List<string>();
        using var config = Config(1, request =>
        {
            requested.Add(request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Доступ запрещён") };
        }, textChapter: true);
        using var getter = new RenovelsGetter(config);
        await getter.Init();

        using var book = await getter.Get(new Uri("https://renovels.org/novel/test/main"));

        var chapter = Assert.Single(book.Chapters);
        Assert.Contains("Текст главы", chapter.Content);
        Assert.Empty(chapter.Images);
        Assert.DoesNotContain("<img", chapter.Content);
        Assert.Equal(new[] { "/1.webp" }, requested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationStopsAndDeletesAlreadyDownloadedPages(bool duringSecondImage)
    {
        using var cancellation = new CancellationTokenSource();
        var requested = new List<string>();
        using var config = Config(3, request =>
        {
            requested.Add(request.RequestUri.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/1.webp")
            {
                if (!duringSecondImage) cancellation.Cancel();
                return Image(1);
            }
            Assert.Equal("/2.webp", request.RequestUri.AbsolutePath);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        config.CancellationToken = cancellation.Token;
        using var getter = new RemangaGetter(config);
        await getter.Init();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => getter.Get(ComicUrl));

        Assert.Equal(duringSecondImage ? new[] { "/1.webp", "/2.webp" } : new[] { "/1.webp" }, requested);
        Assert.Empty(Directory.EnumerateFiles(config.TempFolder.Path));
    }

    [Fact]
    public async Task NoImageStillDoesNotRequestComicPages()
    {
        using var config = Config(2, _ => throw new InvalidOperationException("Картинки выключены"), noImage: true);
        using var getter = new RemangaGetter(config);
        await getter.Init();

        using var book = await getter.Get(ComicUrl);

        Assert.Empty(Assert.Single(book.Chapters).Images);
        Assert.Empty(Directory.EnumerateFiles(config.TempFolder.Path));
    }

    private static BookGetterConfig Config(int pages, Func<HttpRequestMessage, HttpResponseMessage> image,
        bool textChapter = false, bool noImage = false)
    {
        var options = new Options { End = 1, DelayMode = 1, NoImage = noImage };
        var handler = new AfAuditSupport.Handler(request =>
        {
            if (request.RequestUri.Host == "cdn.example.test") return image(request);
            if (request.RequestUri.AbsolutePath.EndsWith("/chapters/1/"))
                return textChapter
                    ? AfAuditSupport.Json(new { content = "<p>Текст главы.</p><img src='https://cdn.example.test/1.webp'>" })
                    : AfAuditSupport.Json(new { pages = Enumerable.Range(1, pages).Select(i => new { link = $"https://cdn.example.test/{i}.webp" }) });
            if (request.RequestUri.AbsolutePath.EndsWith("/chapters/"))
                return AfAuditSupport.Json(new { results = new[] { new { id = 1, name = "Начало", chapter = "1", tome = 1 } } });
            return AfAuditSupport.Json(new { main_name = "Комикс", cover = new Dictionary<string, string>(),
                branches = new[] { new { id = 1, publishers = Array.Empty<object>() } } });
        });
        var client = new HttpClient(new RequestPolicyHandler(new RequestPacer(options), NullLogger.Instance, handler));
        return new BookGetterConfig(options, client, new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
    }

    private static HttpResponseMessage Image(byte marker)
    {
        var content = new ByteArrayContent([marker, 2, 3, 4]);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}
