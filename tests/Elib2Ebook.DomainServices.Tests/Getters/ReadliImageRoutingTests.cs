using System.Net;
using System.Net.Http.Headers;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Readli;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class ReadliImageRoutingTests
{
    [Theory]
    [InlineData("/wp-content/cover.jpg", "213.227.140.81", "readli.net")]
    [InlineData("https://readli.net/wp-content/cover.jpg", "213.227.140.81", "readli.net")]
    [InlineData("https://cdn.example.test/cover.jpg", "cdn.example.test", null)]
    public async Task CoverKeepsLogicalTlsHostOnlyForReadli(string source, string connectionHost, string expectedHost)
    {
        var options = new Options { DelayMode = 1 };
        var calls = 0;
        using var handler = new AfAuditSupport.Handler(request =>
        {
            calls++;
            Assert.Equal(connectionHost, request.RequestUri!.Host);
            Assert.Equal(expectedHost, request.Headers.Host);
            Assert.Equal(expectedHost != null, request.Headers.Contains("X-Session"));
            var content = new ByteArrayContent([255, 216, 255, 224]);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(new RequestPolicyHandler(new RequestPacer(options), NullLogger.Instance, handler));
        client.DefaultRequestHeaders.Add("X-Session", "synthetic-session");
        using var config = new BookGetterConfig(options, client, new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance);
        using var getter = new ReadliGetter(config);
        await getter.Init();
        var doc = new HtmlDocument();
        doc.LoadHtml($"<div><img src='{source}'></div>");

        using var cover = await AfAuditSupport.Invoke<TempFile>(getter, "GetCover", doc.DocumentNode, new Uri("https://213.227.140.81/sample/"));

        Assert.NotNull(cover);
        Assert.Equal(1, calls);
        Assert.Equal("readli.net", client.DefaultRequestHeaders.Host);
        Assert.True(client.DefaultRequestHeaders.Contains("X-Session"));
    }
}
