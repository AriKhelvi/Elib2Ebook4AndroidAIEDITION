using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Litres;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class LitresAnonymousDownloadTests
{
    private static readonly Uri BookUrl = new("https://www.litres.ru/123");
    private const string MetadataPath = "/foundation/api/arts/123";
    private const string ExcerptPath = "/pub/t/123.fb3";
    private const string SyntheticSession = "test-user-session";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnonymousDownloadUsesPublicExcerptWithoutSessionOrFullBookRequests(bool authorize, bool additional)
    {
        var requests = new List<Uri>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            requests.Add(request.RequestUri);
            Assert.False(request.Headers.Contains("Session-Id"));
            Assert.Equal(HttpMethod.Get, request.Method);
            return request.RequestUri.AbsolutePath switch
            {
                MetadataPath => Metadata(),
                ExcerptPath => Excerpt(),
                _ => throw new InvalidOperationException("Анонимная загрузка запросила непубличный маршрут: " + request.RequestUri.AbsolutePath)
            };
        });
        var logger = new AfAuditSupport.AuditLogger();
        using var config = AfAuditSupport.Config(handler, new Options { Additional = additional, AuthCachePath = "" }, logger);
        using var getter = new LitresGetter(config);
        if (authorize) await getter.Authorize();

        using var book = await getter.Get(BookUrl);

        Assert.Contains("Публичный отрывок", Assert.Single(book.Chapters).Content);
        Assert.Equal(new[] { MetadataPath, ExcerptPath }, requests.Select(uri => uri.AbsolutePath));
        Assert.Equal("catalit.litres.ru", requests[1].Host);
        Assert.Empty(requests[1].Query);
        Assert.False(config.Client.DefaultRequestHeaders.Contains("Session-Id"));
        Assert.Contains(logger.Warnings, warning => warning.Contains("ознакомительный отрывок") && warning.Contains("Полнота книги не подтверждена"));
    }

    [Fact]
    public async Task RemovingCredentialsClearsPreviouslyAuthenticatedSessionAndSelectsExcerpt()
    {
        var loginRequests = 0;
        var downloadRequests = new List<string>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            if (request.RequestUri.AbsolutePath == "/foundation/api/auth/login")
            {
                loginRequests++;
                Assert.False(request.Headers.Contains("Session-Id"));
                return Login();
            }
            if (request.RequestUri.AbsolutePath == "/foundation/api/users/me/detailed")
            {
                Assert.Equal(SyntheticSession, Assert.Single(request.Headers.GetValues("Session-Id")));
                return Profile();
            }
            Assert.False(request.Headers.Contains("Session-Id"));
            downloadRequests.Add(request.RequestUri.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                MetadataPath => Metadata(),
                ExcerptPath => Excerpt(),
                _ => throw new InvalidOperationException("Старый сеанс не должен использоваться после удаления логина")
            };
        });
        var options = Credentials();
        using var config = AfAuditSupport.Config(handler, options);
        using var getter = new LitresGetter(config);
        await getter.Authorize();
        options.Login = null;
        options.Password = null;

        await getter.Authorize();
        using var book = await getter.Get(BookUrl);

        Assert.Equal(1, loginRequests);
        Assert.Equal(new[] { MetadataPath, ExcerptPath }, downloadRequests);
        Assert.Single(book.Chapters);
        Assert.False(config.Client.DefaultRequestHeaders.Contains("Session-Id"));
    }

    [Fact]
    public async Task AuthenticatedDownloadStillUsesConfirmedUserSessionForFullBook()
    {
        var paths = new List<string>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            var path = request.RequestUri.AbsolutePath;
            paths.Add(path);
            if (path == "/foundation/api/auth/login") return Login();
            Assert.Equal(SyntheticSession, Assert.Single(request.Headers.GetValues("Session-Id")));
            if (path == "/foundation/api/users/me/detailed") return Profile();
            if (path == MetadataPath) return Metadata();
            Assert.Equal("/pages/download_book_j", path);
            var response = Excerpt();
            response.Headers.AcceptRanges.Add("bytes");
            return response;
        });
        var logger = new AfAuditSupport.AuditLogger();
        using var config = AfAuditSupport.Config(handler, Credentials(), logger);
        using var getter = new LitresGetter(config);

        await getter.Authorize();
        using var book = await getter.Get(BookUrl);

        Assert.Single(book.Chapters);
        Assert.Contains("/pages/download_book_j", paths);
        Assert.DoesNotContain(ExcerptPath, paths);
        Assert.DoesNotContain(logger.Warnings, warning => warning.Contains("ознакомительный отрывок"));
    }

    [Fact]
    public async Task AuthenticatedFallbackDisposesRejectedResponsesAndWarnsThatOnlyExcerptWasDownloaded()
    {
        var rejected = new List<TrackedContent>();
        var logger = new AfAuditSupport.AuditLogger();
        var handler = new AfAuditSupport.Handler(request =>
        {
            var path = request.RequestUri.AbsolutePath;
            if (path == "/foundation/api/auth/login") return Login();
            if (path == "/foundation/api/users/me/detailed") return Profile();
            if (path == MetadataPath) return Metadata();
            if (path == ExcerptPath) return Excerpt();
            Assert.StartsWith("/pages/download_", path);
            var content = new TrackedContent("Полная книга недоступна");
            rejected.Add(content);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = content };
        });
        using var config = AfAuditSupport.Config(handler, Credentials(), logger);
        using var getter = new LitresGetter(config);

        await getter.Authorize();
        using var book = await getter.Get(BookUrl);

        Assert.Contains("Публичный отрывок", Assert.Single(book.Chapters).Content);
        Assert.Equal(6, rejected.Count);
        Assert.All(rejected, response => Assert.True(response.Disposed));
        Assert.Contains(logger.Warnings, warning => warning.Contains("ознакомительный отрывок") && warning.Contains("Полнота книги не подтверждена"));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    public async Task ExcerptHttpFailureIsPreservedAndNeverParsedAsAnEmptyBook(int status)
    {
        var rejectedBody = new TrackedContent("Отказ в выдаче отрывка");
        var handler = new AfAuditSupport.Handler(request =>
        {
            Assert.False(request.Headers.Contains("Session-Id"));
            if (request.RequestUri.AbsolutePath == MetadataPath) return Metadata();
            Assert.Equal(ExcerptPath, request.RequestUri.AbsolutePath);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = rejectedBody };
        });
        using var config = AfAuditSupport.Config(handler);
        using var getter = new LitresGetter(config);
        await getter.Authorize();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Get(BookUrl));

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.True(rejectedBody.Disposed);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CancellingPublicExcerptRequestStopsDownloadInsteadOfReturningAnEmptyBook()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new WaitingExcerptHandler();
        using var config = AfAuditSupport.Config(handler);
        config.CancellationToken = cancellation.Token;
        using var getter = new LitresGetter(config);
        await getter.Authorize();

        var download = getter.Get(BookUrl);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task DisabledAdditionalFilesNeverRequestLinkedTextOrAudio()
    {
        var paths = new List<string>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            var path = request.RequestUri.AbsolutePath;
            paths.Add(path);
            if (path == MetadataPath) return Metadata([RelatedArt(456, 0), RelatedArt(789, 1)]);
            if (path == ExcerptPath) return Excerpt();
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var config = AfAuditSupport.Config(handler, new Options { Additional = false });
        using var getter = new LitresGetter(config);

        using var book = await getter.Get(BookUrl);

        Assert.Contains("Публичный отрывок", Assert.Single(book.Chapters).Content);
        Assert.Equal("Тестовая книга", book.Title);
        Assert.Equal(new[] { MetadataPath, ExcerptPath }, paths);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    public async Task UnavailableLinkedExcerptWarnsAndPreservesAvailablePrimaryText(int status)
    {
        var logger = new AfAuditSupport.AuditLogger();
        var rejected = new TrackedContent("Связанное издание недоступно");
        var handler = new AfAuditSupport.Handler(request =>
        {
            var path = request.RequestUri.AbsolutePath;
            if (path == MetadataPath) return Metadata([RelatedArt(456, 0)]);
            if (path == ExcerptPath) return Excerpt();
            Assert.Equal("/pub/t/456.fb3", path);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = rejected };
        });
        using var config = AfAuditSupport.Config(handler, new Options { Additional = true }, logger);
        using var getter = new LitresGetter(config);

        using var book = await getter.Get(BookUrl);

        Assert.Contains("Публичный отрывок", Assert.Single(book.Chapters).Content);
        Assert.Equal("Тестовая книга", book.Title);
        Assert.Equal(3, handler.Calls);
        Assert.True(rejected.Disposed);
        Assert.Contains(logger.Warnings, warning => warning.Contains("связанное дополнение 456") && warning.Contains("HTTP " + status));
    }

    [Fact]
    public async Task LinkedFileWithSameServerFilenameCannotOverwritePrimaryFileOrText()
    {
        var handler = new AfAuditSupport.Handler(request => request.RequestUri.AbsolutePath switch
        {
            MetadataPath => Metadata([RelatedArt(456, 0)]),
            ExcerptPath => Excerpt("Основной текст", "../nested\\sample.fb3"),
            "/pub/t/456.fb3" => Excerpt("Текст связанного издания", "../nested\\sample.fb3"),
            _ => throw new InvalidOperationException("Неожиданный запрос")
        });
        using var config = AfAuditSupport.Config(handler, new Options { Additional = true });
        using var getter = new LitresGetter(config);

        using var book = await getter.Get(BookUrl);

        Assert.Contains("Основной текст", Assert.Single(book.Chapters).Content);
        Assert.DoesNotContain("Текст связанного", Assert.Single(book.Chapters).Content);
        var files = book.AdditionalFiles.Get(AdditionalTypeEnum.Books);
        Assert.Equal(2, files.Count);
        Assert.Equal(new[] { "123-sample.fb3", "456-sample.fb3" }, files.Select(file => file.FullName));
        Assert.NotEqual(files[0].FilePath, files[1].FilePath);
        Assert.All(files, file => Assert.NotEmpty(file.Content));
        Assert.Equal("Тестовая книга", book.Title);

        var output = Path.Combine(config.TempFolder.Path, "export");
        var builderOptions = new Options { Additional = true, AdditionalTypes = [AdditionalTypeEnum.Books], SavePath = output, BookNamePattern = "linked-test" };
        await new AdditionaFileBuilder(builderOptions, config.Logger).Build(book);

        var exported = Directory.GetFiles(output, "*.fb3", SearchOption.AllDirectories);
        Assert.Equal(2, exported.Length);
        foreach (var file in files)
        {
            var path = Path.Combine(output, "linked-test", "Books", file.FullName);
            Assert.Contains(path, exported);
            Assert.Equal(file.Content, await File.ReadAllBytesAsync(path));
        }
        Assert.Contains("Основной текст", Assert.Single(book.Chapters).Content);
        Assert.DoesNotContain("Текст связанного", Assert.Single(book.Chapters).Content);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LinkedAudioIsNeverRequestedAsFb3AndRespectsSelectedAdditionalTypes(bool onlyBooks, bool expectWarning)
    {
        var logger = new AfAuditSupport.AuditLogger();
        var handler = new AfAuditSupport.Handler(request =>
        {
            if (request.RequestUri.AbsolutePath == MetadataPath) return Metadata([RelatedArt(789, 1)]);
            Assert.Equal(ExcerptPath, request.RequestUri.AbsolutePath);
            return Excerpt();
        });
        var options = new Options { Additional = true, AdditionalTypes = onlyBooks ? [AdditionalTypeEnum.Books] : null };
        using var config = AfAuditSupport.Config(handler, options, logger);
        using var getter = new LitresGetter(config);

        using var book = await getter.Get(BookUrl);

        Assert.Single(book.Chapters);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(expectWarning, logger.Warnings.Any(warning => warning.Contains("аудиокнига 789 пропущена")));
    }

    [Fact]
    public async Task AuthenticatedLinkedAudioMetadataFailureKeepsPrimaryFullBook()
    {
        var logger = new AfAuditSupport.AuditLogger();
        var rejected = new TrackedContent("Аудио недоступно");
        var paths = new List<string>();
        var handler = new AfAuditSupport.Handler(request =>
        {
            var path = request.RequestUri.AbsolutePath;
            paths.Add(path);
            if (path == "/foundation/api/auth/login") return Login();
            Assert.Equal(SyntheticSession, Assert.Single(request.Headers.GetValues("Session-Id")));
            if (path == "/foundation/api/users/me/detailed") return Profile();
            if (path == MetadataPath) return Metadata([RelatedArt(789, 1)]);
            if (path == "/pages/download_book_j")
            {
                var response = Excerpt("Полный основной текст");
                response.Headers.AcceptRanges.Add("bytes");
                return response;
            }
            Assert.Equal("/foundation/api/arts/789/files/grouped", path);
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = rejected };
        });
        var options = Credentials();
        options.Additional = true;
        options.AdditionalTypes = [AdditionalTypeEnum.Audio];
        using var config = AfAuditSupport.Config(handler, options, logger);
        using var getter = new LitresGetter(config);
        await getter.Authorize();

        using var book = await getter.Get(BookUrl);

        Assert.Contains("Полный основной текст", Assert.Single(book.Chapters).Content);
        Assert.True(rejected.Disposed);
        Assert.DoesNotContain(paths, path => path.StartsWith("/pub/t/"));
        Assert.Contains(logger.Warnings, warning => warning.Contains("связанное дополнение 789") && warning.Contains("HTTP 403"));
        Assert.DoesNotContain(logger.Warnings, warning => warning.Contains("ознакомительный отрывок"));
    }

    [Fact]
    public async Task LinkedIoFailureWarnsWithoutDiscardingPrimaryText()
    {
        var logger = new AfAuditSupport.AuditLogger();
        var handler = new AfAuditSupport.Handler(request => request.RequestUri.AbsolutePath switch
        {
            MetadataPath => Metadata([RelatedArt(456, 0)]),
            ExcerptPath => Excerpt(),
            _ => throw new IOException("Синтетическая ошибка чтения дополнения")
        });
        using var config = AfAuditSupport.Config(handler, new Options { Additional = true }, logger);
        using var getter = new LitresGetter(config);

        using var book = await getter.Get(BookUrl);

        Assert.Contains("Публичный отрывок", Assert.Single(book.Chapters).Content);
        Assert.Contains(logger.Warnings, warning => warning.Contains("не удалось прочитать или сохранить связанное дополнение 456"));
    }

    [Fact]
    public async Task CancellingLinkedDownloadStillPropagatesAfterPrimaryTextWasDownloaded()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new WaitingExcerptHandler(linked: true);
        using var config = AfAuditSupport.Config(handler, new Options { Additional = true });
        config.CancellationToken = cancellation.Token;
        using var getter = new LitresGetter(config);

        var download = getter.Get(BookUrl);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(3, handler.Calls);
    }

    private static Options Credentials() => new() { Login = "reader@example.test", Password = "test-password", AuthCachePath = "" };

    private static HttpResponseMessage Login() => AfAuditSupport.Json(new { payload = new { data = new { sid = SyntheticSession } } });
    private static HttpResponseMessage Profile() => AfAuditSupport.Json(new { payload = new { data = new { } } });
    private static object RelatedArt(long id, int artType) => new { id, title = "Связанное издание", art_type = artType };
    private static HttpResponseMessage Metadata(object[] linked = null) => AfAuditSupport.Json(new { payload = new { data = new { id = 123, title = "Тестовая книга", art_type = 0, linked_arts = linked } } });

    private static HttpResponseMessage Excerpt(string text = "Публичный отрывок.", string fileName = "sample.fb3")
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var body = zip.CreateEntry("fb3/body.xml").Open())
            body.Write(Encoding.UTF8.GetBytes("<fb3-body><section><title>Глава</title><p>" + WebUtility.HtmlEncode(text) + "</p></section></fb3-body>"));
        var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class TrackedContent(string body) : StringContent(body)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class WaitingExcerptHandler(bool linked = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.False(request.Headers.Contains("Session-Id"));
            if (request.RequestUri.AbsolutePath == MetadataPath)
                return SetRequest(Metadata(linked ? [RelatedArt(456, 0)] : null), request);
            if (linked && request.RequestUri.AbsolutePath == ExcerptPath)
                return SetRequest(Excerpt(), request);
            Assert.Equal(linked ? "/pub/t/456.fb3" : ExcerptPath, request.RequestUri.AbsolutePath);
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Запрос должен быть отменён");
        }

        private static HttpResponseMessage SetRequest(HttpResponseMessage response, HttpRequestMessage request)
        {
            response.RequestMessage = request;
            return response;
        }
    }
}
