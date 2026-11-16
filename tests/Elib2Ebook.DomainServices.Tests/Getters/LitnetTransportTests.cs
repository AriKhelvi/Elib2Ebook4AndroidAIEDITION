using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Litnet.Getters;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitnetTransportTests
{
    [Theory]
    [InlineData(false, "publiccdn.litnet.com")]
    [InlineData(true, "publiccdn.litnet.com")]
    [InlineData(false, "covers.example.test")]
    public async Task OfficialTransportCoversEveryApiStageWithoutLeakingIntoHtmlOrImages(bool guest, string coverHost)
    {
        var backend = new ContractBackend("litnet.com", officialTransport: true, guest, coverHost);
        var options = backend.Options();
        options.NoChapters = true;
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(backend.Send), options);
        using var getter = new LitnetGetter(config);
        await getter.Init();
        backend.RememberDefaultHeaders(config.Client);
        await getter.Authorize();
        using (var preview = await getter.GetPreview(backend.BookUrl))
        {
            Assert.Equal("Книга", preview.Title);
            Assert.Empty(preview.Chapters);
            Assert.Equal(["auth", "metadata", "cover"], backend.Requests);
        }
        options.NoChapters = false;

        using var book = await getter.Get(backend.BookUrl);

        Assert.Equal(["auth", "metadata", "cover", "toc", "text", "series"], backend.Requests);
        Assert.Equal(["<p>Глава 1</p>", "<p>Глава 2</p>"], book.Chapters.Select(chapter => chapter.Content));
        Assert.Equal(ContractBackend.CoverBytes, book.Cover.Content);
        Assert.Equal("Цикл", book.Seria.Name);
        Assert.Equal(backend.DefaultUserAgent, config.Client.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Equal(backend.DefaultAccept, config.Client.DefaultRequestHeaders.Accept.ToString());
    }

    [Theory]
    [InlineData("booknet.com")]
    [InlineData("booknet.ua")]
    public async Task BooknetKeepsItsOwnApiTextRouteAndBrowserHeaders(string host)
    {
        var backend = new ContractBackend(host, officialTransport: false, guest: false, "publiccdn." + host);
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(backend.Send), backend.Options());
        using LitnetGetterBase getter = host == "booknet.com" ? new BooknetComGetter(config) : new BooknetUaGetter(config);
        await getter.Init();
        backend.RememberDefaultHeaders(config.Client);
        await getter.Authorize();

        using var book = await getter.Get(backend.BookUrl);

        Assert.Equal(["auth", "metadata", "cover", "toc", "text", "series"], backend.Requests);
        Assert.Equal(2, book.Chapters.Count());
        Assert.Equal(ContractBackend.CoverBytes, book.Cover.Content);
        Assert.Equal("Цикл", book.Seria.Name);
    }

    private sealed class ContractBackend(string siteHost, bool officialTransport, bool guest, string coverHost)
    {
        // Параметр публичного протокола из официального клиента, не пользовательский секрет.
        private const string AppSecret = "14a6579a984b3c6abecda6c2dfa83a64";
        private const string Login = "reader+test@example.test";
        private const string Password = "fake &+ пароль";
        private const string Token = "test-session-token";
        private string _deviceId;

        public static readonly byte[] CoverBytes = [137, 80, 78, 71, 13, 10, 26, 10, 1];
        public Uri BookUrl { get; } = new($"https://{siteHost}/ru/book/test-b123");
        public List<string> Requests { get; } = [];
        public string DefaultUserAgent { get; private set; }
        public string DefaultAccept { get; private set; }

        public Options Options() => new() { Login = guest ? null : Login, Password = guest ? null : Password };

        public void RememberDefaultHeaders(HttpClient client)
        {
            DefaultUserAgent = client.DefaultRequestHeaders.UserAgent.ToString();
            DefaultAccept = client.DefaultRequestHeaders.Accept.ToString();
            Assert.Contains("Safari/605.1.15", DefaultUserAgent);
            Assert.Contains("text/html", DefaultAccept);
            Assert.DoesNotContain("Dart/", DefaultUserAgent);
        }

        public HttpResponseMessage Send(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Equal("https", uri.Scheme);
            Assert.Equal(officialTransport ? HttpVersion.Version11 : HttpVersion.Version20, request.Version);
            var api = uri.AbsolutePath.StartsWith("/v1/");
            if (api) AssertApiContract(request);
            else AssertPublicRequest(request);

            switch (uri.AbsolutePath)
            {
                case "/v1/user/find-by-login":
                case "/v1/registration/registration-by-device":
                    Requests.Add("auth");
                    Assert.Equal(guest ? "/v1/registration/registration-by-device" : "/v1/user/find-by-login", uri.AbsolutePath);
                    var credentials = HttpUtility.ParseQueryString(uri.Query);
                    if (guest)
                    {
                        Assert.True(string.IsNullOrEmpty(credentials["login"]));
                        Assert.True(string.IsNullOrEmpty(credentials["password"]));
                    }
                    else
                    {
                        Assert.Equal(Login, credentials["login"]);
                        Assert.Equal(Password, credentials["password"]);
                    }
                    return AfAuditSupport.Json(new { token = Token });
                case "/v1/book/get/123":
                    Requests.Add("metadata");
                    return AfAuditSupport.Json(new
                    {
                        title = "Книга", url = BookUrl.AbsoluteUri, author_name = "Автор", lang = "ru",
                        cover = $"https://{coverHost}/cover.png", cycle_priority = 2
                    });
                case "/v1/book/contents":
                    Requests.Add("toc");
                    Assert.Equal("123", HttpUtility.ParseQueryString(uri.Query)["bookId"]);
                    return AfAuditSupport.Json(new[]
                    {
                        new { id = 1, title = "Глава 1", access = true },
                        new { id = 2, title = "Глава 2", access = true }
                    });
                case "/v1/book/text/get-chapters":
                case "/v1/book/get-chapters-texts/":
                    Requests.Add("text");
                    Assert.Equal(officialTransport ? "/v1/book/text/get-chapters" : "/v1/book/get-chapters-texts/", uri.AbsolutePath);
                    Assert.Equal(["1", "2"], HttpUtility.ParseQueryString(uri.Query).GetValues("chapter_ids[]"));
                    if (officialTransport)
                        return LitnetArchiveTestSupport.Response([(1L, "[\"<p>Глава 1</p>\"]"), (2L, "[\"<p>Глава 2</p>\"]")]);
                    return AfAuditSupport.Json(new[]
                    {
                        new { id = 1, text = Encrypt("[\"<p>Глава 1</p>\"]") },
                        new { id = 2, text = Encrypt("[\"<p>Глава 2</p>\"]") }
                    });
                case "/cover.png":
                    Requests.Add("cover");
                    Assert.Equal(coverHost, uri.Host);
                    var image = new ByteArrayContent(CoverBytes);
                    image.Headers.ContentType = new("image/png");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = image };
                case "/ru/book/test-b123":
                    Requests.Add("series");
                    Assert.Equal(siteHost, uri.Host);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<div class='book-view-info-coll'><a href='/ru/author/books?sort=cycles'>Цикл</a></div>")
                    };
                default:
                    throw new InvalidOperationException($"Неожиданный тестовый путь {uri.AbsolutePath}");
            }
        }

        private void AssertApiContract(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            var host = officialTransport ? "sapi.litnet.com" : "api." + siteHost;
            Assert.Equal(host, uri.Host);
            Assert.Equal(host, request.Headers.Host);
            var query = HttpUtility.ParseQueryString(uri.Query);
            AssertSingleValue(query, "app", "android");
            var device = query["device_id"];
            Assert.True(Guid.TryParse(device, out _));
            _deviceId ??= device;
            AssertSingleValue(query, "device_id", _deviceId);
            var authenticating = uri.AbsolutePath is "/v1/user/find-by-login" or "/v1/registration/registration-by-device";
            var token = authenticating ? string.Empty : Token;
            var expectedSign = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(device + AppSecret + token))).ToLowerInvariant();
            AssertSingleValue(query, "sign", expectedSign);
            if (authenticating) Assert.True(string.IsNullOrEmpty(query["user_token"]));
            else
            {
                AssertSingleValue(query, "user_token", Token);
                Assert.Null(query["login"]);
                Assert.Null(query["password"]);
            }
            if (officialTransport)
            {
                AssertSingleValue(query, "version", "400001699");
                AssertSingleValue(query, "lang_content", "ru");
                AssertSingleValue(query, "lang_interface", "ru");
                Assert.Equal("Dart/3.10 (dart:io)", request.Headers.UserAgent.ToString());
                Assert.Equal("application/json", request.Headers.Accept.ToString());
            }
            else
            {
                Assert.Null(query["version"]);
                Assert.Null(query["lang_content"]);
                Assert.Null(query["lang_interface"]);
                Assert.Equal(DefaultUserAgent, request.Headers.UserAgent.ToString());
                Assert.Equal(DefaultAccept, request.Headers.Accept.ToString());
            }
        }

        private void AssertPublicRequest(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            Assert.Empty(uri.Query);
            Assert.Equal(uri.Host, request.Headers.Host ?? uri.Host);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal(DefaultUserAgent, request.Headers.UserAgent.ToString());
            Assert.Equal(DefaultAccept, request.Headers.Accept.ToString());
        }

        private static void AssertSingleValue(NameValueCollection query, string key, string expected)
            => Assert.Equal(expected, Assert.Single(query.GetValues(key) ?? []));

        private static string Encrypt(string json)
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(AppSecret);
            aes.IV = new byte[16];
            using var encryptor = aes.CreateEncryptor();
            var bytes = new byte[16].Concat(Encoding.UTF8.GetBytes(json)).ToArray();
            return Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length));
        }
    }
}
