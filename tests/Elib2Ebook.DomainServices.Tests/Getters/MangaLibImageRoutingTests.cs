using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.LibSocial.Getters;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class MangaLibImageRoutingTests
{
    [Fact]
    public async Task DisposedPreviewDoesNotRemovePagesOrCoverBeforeCbzExport()
    {
        const string slug = "206--one-piece";
        var root = Path.Combine(Path.GetTempPath(), "mangalib-cbz-" + Guid.NewGuid().ToString("N"));
        var images = new Dictionary<string, byte[]>
        {
            ["/cover.jpg"] = [255, 216, 255, 1, 255, 217],
            ["/first.jpg"] = [255, 216, 255, 2, 255, 217],
            ["/last.png"] = [137, 80, 78, 71, 13, 10, 26, 10, 3]
        };
        using var handler = new AfAuditSupport.Handler(request =>
        {
            if (request.RequestUri.Host == "img3.cdnlibs.org")
            {
                var path = request.RequestUri.AbsolutePath;
                var content = new ByteArrayContent(images[path]);
                content.Headers.ContentType = new MediaTypeHeaderValue(path.EndsWith(".jpg") ? "image/jpeg" : "image/png");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            return request.RequestUri.AbsolutePath switch
            {
                "/api/manga/" + slug => AfAuditSupport.Json(new
                {
                    data = new { id = 206, slug_url = slug, name = "Книга", is_licensed = false,
                        authors = Array.Empty<object>(), cover = new { @default = "https://img3.cdnlibs.org/cover.jpg" }, summary = "Описание" }
                }),
                "/api/manga/" + slug + "/chapters" => AfAuditSupport.Json(new
                {
                    data = new[] { new { volume = "1", number = "1", name = "Глава", branches = Array.Empty<object>() } }
                }),
                "/api/manga/" + slug + "/chapter" => AfAuditSupport.Json(new
                {
                    data = new { pages = new[] { new { url = "/first.jpg" }, new { url = "/last.png" } } }
                }),
                _ => throw new InvalidOperationException("Неожиданный запрос: " + request.RequestUri.AbsolutePath)
            };
        });
        try
        {
            var options = new Options { NoChapters = true, Cover = true, SavePath = Path.Combine(root, "out") };
            var logger = new AfAuditSupport.AuditLogger();
            using var config = new BookGetterConfig(options, new HttpClient(handler), new CookieContainer(),
                TempFolderFactory.Create(Path.Combine(root, "tmp")), logger);
            using var getter = new MangaLibGetter(config);
            var url = new Uri("https://mangalib.me/ru/manga/" + slug);
            await getter.Init();
            string previewCover;
            using (var preview = await getter.Get(url))
            {
                Assert.Empty(preview.Chapters);
                previewCover = preview.Cover.FilePath;
            }
            Assert.False(File.Exists(previewCover));
            options.NoChapters = false;
            using var book = await getter.Get(url);
            Assert.NotEqual(previewCover, book.Cover.FilePath);

            await new CbzBuilder(options, logger).Build(book);

            using var zip = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(options.SavePath, "*.cbz")));
            Assert.Equal(new[] { "1.jpg", "2.png" }, zip.Entries.Select(entry => entry.FullName));
            for (var index = 0; index < zip.Entries.Count; index++)
            {
                await using var stream = zip.Entries[index].Open();
                using var data = new MemoryStream();
                await stream.CopyToAsync(data);
                Assert.Equal(images[index == 0 ? "/first.jpg" : "/last.png"], data.ToArray());
            }
            Assert.Equal(images["/cover.jpg"], await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(options.SavePath, "*_cover.jpg"))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("mangalib.me")]
    [InlineData("mangalib.org")]
    public async Task BothDomainsDownloadChapterPagesFromWorkingSharedCdn(string siteHost)
    {
        const string slug = "18406--nan-hao-shang-feng";
        const string imagePath = "/manga/nan-hao-shang-feng/chapters/408522/page.png";
        var pixels = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII=");
        var requests = new List<Uri>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            requests.Add(request.RequestUri);
            if (request.RequestUri.Host == "img3.cdnlibs.org")
            {
                Assert.Equal(imagePath, request.RequestUri.AbsolutePath);
                var content = new ByteArrayContent(pixels);
                content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            // Старый CDN намеренно недоступен: тест должен обнаружить потерю единственной страницы главы.
            if (request.RequestUri.Host != "api.cdnlibs.org")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            Assert.Equal("1", Assert.Single(request.Headers.GetValues("Site-Id")));
            Assert.Equal(siteHost, request.Headers.Referrer.Host);
            return request.RequestUri.AbsolutePath switch
            {
                "/api/manga/" + slug => AfAuditSupport.Json(new
                {
                    data = new { id = 18406, slug_url = slug, name = "Книга", is_licensed = false,
                        authors = Array.Empty<object>(), cover = new { @default = (string)null }, summary = "Описание" }
                }),
                "/api/manga/" + slug + "/chapters" => AfAuditSupport.Json(new
                {
                    data = new[] { new { volume = "1", number = "1", name = "Глава", branches = Array.Empty<object>() } }
                }),
                "/api/manga/" + slug + "/chapter" => AfAuditSupport.Json(new
                {
                    data = new { pages = new[] { new { url = "/" + imagePath } } }
                }),
                _ => throw new InvalidOperationException("Неожиданный API-запрос: " + request.RequestUri.AbsolutePath)
            };
        });
        using var config = AfAuditSupport.Config(handler, new Options { Start = 1, End = 1 });
        using GetterBase getter = siteHost == "mangalib.org" ? new MangaLibOrgGetter(config) : new MangaLibGetter(config);
        var url = new Uri($"https://{siteHost}/ru/{slug}");
        Assert.True(getter.IsSameUrl(url));
        Assert.False(getter.IsSameUrl(new Uri(siteHost == "mangalib.org" ? "https://mangalib.me/" : "https://mangalib.org/")));
        await getter.Init();

        using var book = await getter.Get(url);

        var chapter = Assert.Single(book.Chapters);
        var image = Assert.Single(chapter.Images);
        Assert.Equal(pixels, image.Content);
        Assert.Contains(image.FullName, chapter.Content);
        Assert.Equal(4, requests.Count);
        Assert.DoesNotContain(requests, request => request.Host == "img33.imgslib.link");
        Assert.Single(requests, request => request.Host == "img3.cdnlibs.org");
    }
}
