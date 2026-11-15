using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Litres;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitresAuthCacheTests
{
    private const string Login = "reader/private@example.test";
    private const string Password = "private-password";
    private const string OldSid = "old-private-session";
    private const string NewSid = "new-private-session";
    private const string Profile = "{\"payload\":{\"data\":{}}}";
    private const string SuccessfulLogin = "{\"payload\":{\"data\":{\"sid\":\"" + NewSid + "\"}}}";

    [Fact]
    public async Task FreshLoginPersistsHashedAccountAndNewGetterReusesVerifiedSession()
    {
        using var cache = new CacheFolder();
        var postCount = 0;
        var checkCount = 0;
        var contents = new List<TrackedContent>();
        HttpResponseMessage Send(HttpRequestMessage request)
        {
            Assert.Equal("https", request.RequestUri.Scheme);
            if (request.Method == HttpMethod.Post)
            {
                postCount++;
                Assert.Equal("/foundation/api/auth/login", request.RequestUri.AbsolutePath);
                Assert.False(request.Headers.Contains("Session-Id"));
                return TrackedResponse(SuccessfulLogin, contents);
            }
            checkCount++;
            Assert.Equal("/foundation/api/users/me/detailed", request.RequestUri.AbsolutePath);
            Assert.Equal(NewSid, Assert.Single(request.Headers.GetValues("Session-Id")));
            return TrackedResponse(Profile, contents);
        }

        using (var config = AfAuditSupport.Config(new AfAuditSupport.Handler(Send), Options(cache.Path)))
        using (var getter = new LitresGetter(config))
            await getter.Authorize();

        var saved = Assert.Single(Directory.GetFiles(cache.Path));
        Assert.Equal(CacheFile(cache.Path), saved);
        Assert.DoesNotContain("reader", Path.GetFileName(saved));
        Assert.DoesNotContain(Password, await File.ReadAllTextAsync(saved));

        using (var config = AfAuditSupport.Config(new AfAuditSupport.Handler(Send), Options(cache.Path)))
        using (var getter = new LitresGetter(config))
            await getter.Authorize();

        Assert.Equal(1, postCount);
        Assert.Equal(2, checkCount);
        Assert.All(contents, content => Assert.True(content.Disposed));
        Assert.Single(Directory.GetFiles(cache.Path));
    }

    [Theory]
    [InlineData("broken JSON private-password")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"sid\":\"\"}")]
    [InlineData("{\"sid\":\" \"}")]
    [InlineData("{\"sid\":\"bad\\r\\nheader\"}")]
    public async Task CorruptOrEmptyCachedSessionTriggersFreshLogin(string cached)
    {
        using var cache = new CacheFolder();
        await cache.Seed(cached);
        var methods = new List<HttpMethod>();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request =>
        {
            methods.Add(request.Method);
            if (request.Method == HttpMethod.Post) Assert.False(request.Headers.Contains("Session-Id"));
            return Response(request.Method == HttpMethod.Post ? SuccessfulLogin : Profile);
        }), Options(cache.Path));
        using var getter = new LitresGetter(config);

        await getter.Authorize();

        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Get }, methods);
        Assert.Equal(NewSid, Assert.Single(config.Client.DefaultRequestHeaders.GetValues("Session-Id")));
        Assert.Contains(NewSid, await File.ReadAllTextAsync(CacheFile(cache.Path)));
    }

    [Theory]
    [InlineData(401, "secret body")]
    [InlineData(403, "secret body")]
    [InlineData(200, "null")]
    [InlineData(200, "{}")]
    [InlineData(200, "{\"payload\":null}")]
    [InlineData(200, "{\"payload\":{\"data\":null}}")]
    [InlineData(200, "{\"errors\":\"expired private-session\"}")]
    public async Task ExpiredSessionIsRemovedBeforeLoginAndReplacedOnlyAfterProfileCheck(int status, string body)
    {
        using var cache = new CacheFolder();
        await cache.Seed(JsonSerializer.Serialize(new { sid = OldSid }));
        var step = 0;
        var contents = new List<TrackedContent>();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request =>
        {
            switch (++step)
            {
                case 1:
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.Equal(OldSid, Assert.Single(request.Headers.GetValues("Session-Id")));
                    return TrackedResponse(body, contents, (HttpStatusCode)status);
                case 2:
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.False(request.Headers.Contains("Session-Id"));
                    return TrackedResponse(SuccessfulLogin, contents);
                default:
                    Assert.Equal(3, step);
                    Assert.Equal(NewSid, Assert.Single(request.Headers.GetValues("Session-Id")));
                    return TrackedResponse(Profile, contents);
            }
        }), Options(cache.Path));
        using var getter = new LitresGetter(config);

        await getter.Authorize();

        Assert.Equal(3, step);
        Assert.All(contents, content => Assert.True(content.Disposed));
        Assert.Contains(NewSid, await File.ReadAllTextAsync(CacheFile(cache.Path)));
        Assert.DoesNotContain(OldSid, await File.ReadAllTextAsync(CacheFile(cache.Path)));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task TemporaryProfileFailureNeverRetriesPasswordLogin(int status)
    {
        using var cache = new CacheFolder();
        var cached = JsonSerializer.Serialize(new { sid = OldSid });
        await cache.Seed(cached);
        var handler = new AfAuditSupport.Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Response(Password + OldSid, (HttpStatusCode)status);
        });
        using var config = AfAuditSupport.Config(handler, Options(cache.Path));
        using var getter = new LitresGetter(config);

        var error = await Assert.ThrowsAsync<HttpRequestException>(getter.Authorize);

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(cached, await File.ReadAllTextAsync(CacheFile(cache.Path)));
        Assert.False(config.Client.DefaultRequestHeaders.Contains("Session-Id"));
        Assert.DoesNotContain(Password, error.ToString());
        Assert.DoesNotContain(OldSid, error.ToString());
    }

    [Fact]
    public async Task ProfileNetworkFailureDoesNotRetryPasswordLogin()
    {
        using var cache = new CacheFolder();
        await cache.Seed(JsonSerializer.Serialize(new { sid = OldSid }));
        var handler = new AfAuditSupport.Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            throw new HttpRequestException("Сеть недоступна");
        });
        using var config = AfAuditSupport.Config(handler, Options(cache.Path));
        using var getter = new LitresGetter(config);

        await Assert.ThrowsAsync<HttpRequestException>(getter.Authorize);

        Assert.Equal(1, handler.Calls);
        Assert.Contains(OldSid, await File.ReadAllTextAsync(CacheFile(cache.Path)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"payload\":{\"data\":{\"sid\":\"\"}}}")]
    [InlineData("{\"payload\":{\"data\":{\"sid\":\"bad\\r\\nheader\"}}}")]
    [InlineData("{\"errors\":\"private-password new-private-session\"}")]
    [InlineData("<html>private-password new-private-session</html>")]
    public async Task InvalidLoginResponseNeverSavesSessionOrDisclosesBody(string body)
    {
        using var cache = new CacheFolder();
        var logger = new CapturingLogger();
        var handler = new AfAuditSupport.Handler(_ => Response(body));
        using var config = AfAuditSupport.Config(handler, Options(cache.Path), logger);
        using var getter = new LitresGetter(config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(getter.Authorize);

        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.GetFiles(cache.Path));
        Assert.False(config.Client.DefaultRequestHeaders.Contains("Session-Id"));
        AssertPrivate(error, logger);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task FailedLoginPreservesHttpStatusWithoutSavingSession(int status)
    {
        using var cache = new CacheFolder();
        var handler = new AfAuditSupport.Handler(_ => Response(Password + NewSid, (HttpStatusCode)status));
        using var config = AfAuditSupport.Config(handler, Options(cache.Path));
        using var getter = new LitresGetter(config);

        var error = await Assert.ThrowsAsync<HttpRequestException>(getter.Authorize);

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.GetFiles(cache.Path));
        Assert.False(config.Client.DefaultRequestHeaders.Contains("Session-Id"));
        Assert.DoesNotContain(Password, error.ToString());
        Assert.DoesNotContain(NewSid, error.ToString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"payload\":{}}")]
    [InlineData("{\"errors\":\"private-password new-private-session\"}")]
    [InlineData("<html>private-password new-private-session</html>")]
    public async Task FreshSessionRequiresValidProfileBeforeCaching(string body)
    {
        using var cache = new CacheFolder();
        var logger = new CapturingLogger();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request =>
            Response(request.Method == HttpMethod.Post ? SuccessfulLogin : body)), Options(cache.Path), logger);
        using var getter = new LitresGetter(config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(getter.Authorize);

        Assert.Empty(Directory.GetFiles(cache.Path));
        Assert.False(config.Client.DefaultRequestHeaders.Contains("Session-Id"));
        AssertPrivate(error, logger);
    }

    [Fact]
    public async Task InaccessibleCacheCannotAbortSuccessfulLogin()
    {
        using var cache = new CacheFolder();
        var occupied = Path.Combine(cache.Path, "occupied");
        await File.WriteAllTextAsync(occupied, "Папку с таким именем создать нельзя");
        var logger = new CapturingLogger();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(request =>
            Response(request.Method == HttpMethod.Post ? SuccessfulLogin : Profile)), Options(occupied), logger);
        using var getter = new LitresGetter(config);

        await getter.Authorize();

        Assert.Equal(NewSid, Assert.Single(config.Client.DefaultRequestHeaders.GetValues("Session-Id")));
        Assert.Contains(logger.Messages, message => message.Contains("кэш сессии недоступен"));
        Assert.All(logger.Messages, message => Assert.DoesNotContain("private", message));
        Assert.Single(Directory.GetFiles(cache.Path));
    }

    [Fact]
    public async Task DisabledCacheNeedsNoDirectoryAndRepeatedAuthorizationHasOneHeader()
    {
        var handler = new AfAuditSupport.Handler(request =>
        {
            if (request.Method == HttpMethod.Post) Assert.False(request.Headers.Contains("Session-Id"));
            return Response(request.Method == HttpMethod.Post ? SuccessfulLogin : Profile);
        });
        using var config = AfAuditSupport.Config(handler, Options(""));
        using var getter = new LitresGetter(config);

        await getter.Authorize();
        await getter.Authorize();

        Assert.Equal(4, handler.Calls);
        Assert.Equal(NewSid, Assert.Single(config.Client.DefaultRequestHeaders.GetValues("Session-Id")));
    }

    [Fact]
    public void DefaultCacheIsInAbsoluteLocalUserDataDirectory()
    {
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => throw new InvalidOperationException()), Options(null));
        using var getter = new LitresGetter(config);
        var path = (string)typeof(LitresGetter).GetMethod("GetAuthCacheFile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(getter, null);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.True(Path.IsPathFullyQualified(path));
        Assert.Equal(CacheFile(Path.Combine(local, "Elib2Ebook", "auth-cache")), path);
    }

    [Fact]
    public async Task CancellationStopsProfileCheckWithoutFreshLoginOrCacheRewrite()
    {
        using var cache = new CacheFolder();
        var original = JsonSerializer.Serialize(new { sid = OldSid });
        await cache.Seed(original);
        using var cancellation = new CancellationTokenSource();
        var handler = new WaitingHandler();
        using var config = AfAuditSupport.Config(handler, Options(cache.Path));
        config.CancellationToken = cancellation.Token;
        using var getter = new LitresGetter(config);

        var authorize = getter.Authorize();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorize);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(original, await File.ReadAllTextAsync(CacheFile(cache.Path)));
        Assert.False(config.Client.DefaultRequestHeaders.Contains("Session-Id"));
    }

    private static Options Options(string path) => new() { Login = Login, Password = Password, AuthCachePath = path };

    private static string CacheFile(string directory)
        => Path.Combine(directory, "litres-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Login))).ToLowerInvariant() + ".json");

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage TrackedResponse(string body, List<TrackedContent> contents, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new TrackedContent(body);
        contents.Add(content);
        return new HttpResponseMessage(status) { Content = content };
    }

    private static void AssertPrivate(Exception error, CapturingLogger logger)
    {
        foreach (var text in logger.Messages.Append(error.ToString()))
        {
            Assert.DoesNotContain(Login, text);
            Assert.DoesNotContain(Password, text);
            Assert.DoesNotContain(NewSid, text);
            Assert.DoesNotContain(OldSid, text);
        }
    }

    private sealed class CacheFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "litres-auth-" + Guid.NewGuid().ToString("N"));
        public CacheFolder() => Directory.CreateDirectory(Path);
        public Task Seed(string value) => File.WriteAllTextAsync(CacheFile(Path), value);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class TrackedContent(string body) : StringContent(body, Encoding.UTF8, "application/json")
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Запрос должен быть отменён");
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState, Exception, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
