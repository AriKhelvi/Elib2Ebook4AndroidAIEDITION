using System.Net;
using System.Net.Http.Headers;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Litnet.Getters;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class LitnetImageReferrerTests
{
    private static readonly byte[] Pixels = [137, 80, 78, 71, 13, 10, 26, 10, 1];
    private static readonly Uri PrivateReferrer = new("https://litnet.com/ru/book/private-b123?user_token=private-token#chapter");
    private static readonly string[] SensitiveHeaders = ["Authorization", "Cookie", "X-Session", "X-Future-Session"];

    [Theory]
    [InlineData("https://i.ibb.co/example/illustration.png")]
    [InlineData("https://publiccdn.litnet.com/uploads/covers/book.png")]
    public async Task ImageReceivesOnlyLitnetOriginReferrerAndNoSiteCredentials(string url)
    {
        var imageUri = new Uri(url);
        var handler = new AfAuditSupport.Handler(request =>
        {
            Assert.Equal(imageUri, request.RequestUri);
            AssertExternalImageHeaders(request);
            return ImageResponse();
        });
        using var config = Config(handler);
        using var getter = new ImageGetter(config);
        await getter.Init();
        AddSharedCredentials(config.Client);

        using var image = await getter.Image(imageUri);

        Assert.NotNull(image);
        Assert.Equal(Pixels, image.Content);
        Assert.Equal(".png", image.Extension);
        Assert.Equal(1, handler.Calls);
        AssertSharedCredentialsUnchanged(config.Client);
    }

    [Fact]
    public async Task RedirectFromLitnetCdnToExternalImageKeepsOriginReferrerWithoutRestoringCredentials()
    {
        var cdn = new Uri("https://publiccdn.litnet.com/uploads/book.png");
        var external = new Uri("https://i.ibb.co/example/illustration.png");
        var visited = new List<Uri>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            visited.Add(request.RequestUri);
            AssertExternalImageHeaders(request);
            if (request.RequestUri == cdn)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = external;
                return redirect;
            }
            Assert.Equal(external, request.RequestUri);
            return ImageResponse();
        });
        using var config = Config(handler);
        using var getter = new ImageGetter(config);
        await getter.Init();
        AddSharedCredentials(config.Client);

        using var image = await getter.Image(cdn);

        Assert.NotNull(image);
        Assert.Equal(Pixels, image.Content);
        Assert.Equal(new[] { cdn, external }, visited);
        Assert.Equal(2, handler.Calls);
        AssertSharedCredentialsUnchanged(config.Client);
    }

    private static void AssertExternalImageHeaders(HttpRequestMessage request)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://litnet.com/", request.Headers.Referrer?.AbsoluteUri);
        Assert.Equal(request.RequestUri.IdnHost, request.Headers.Host ?? request.RequestUri.IdnHost);
        Assert.NotEqual("sapi.litnet.com", request.Headers.Host);
        Assert.All(SensitiveHeaders, name => Assert.False(request.Headers.Contains(name), name));
        Assert.DoesNotContain("private-token", request.RequestUri.AbsoluteUri);
        Assert.DoesNotContain("private-token", request.Headers.ToString());
        Assert.DoesNotContain("private-b123", request.Headers.ToString());
    }

    private static void AddSharedCredentials(HttpClient client)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "private-token");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "session=private-token");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Session", "private-token");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Future-Session", "private-token");
        client.DefaultRequestHeaders.Host = "sapi.litnet.com";
        client.DefaultRequestHeaders.Referrer = PrivateReferrer;
    }

    private static void AssertSharedCredentialsUnchanged(HttpClient client)
    {
        Assert.All(SensitiveHeaders, name => Assert.True(client.DefaultRequestHeaders.Contains(name), name));
        Assert.Equal("sapi.litnet.com", client.DefaultRequestHeaders.Host);
        Assert.Equal(PrivateReferrer, client.DefaultRequestHeaders.Referrer);
    }

    private static BookGetterConfig Config(HttpMessageHandler handler)
    {
        var options = new Options { DelayMode = 1 };
        var logger = new AfAuditSupport.AuditLogger();
        return AfAuditSupport.Config(new RequestPolicyHandler(new RequestPacer(options), logger, handler), options, logger);
    }

    private static HttpResponseMessage ImageResponse()
    {
        var content = new ByteArrayContent(Pixels);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class ImageGetter(BookGetterConfig config) : LitnetGetter(config)
    {
        public Task<TempFile> Image(Uri uri) => SaveImage(uri);
    }
}
