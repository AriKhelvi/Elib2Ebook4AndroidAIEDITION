using System.Net;
using System.Net.Http.Headers;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.ExternalServices.LibSocial.Getters;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class MangaLibImageRoutingTests
{
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
