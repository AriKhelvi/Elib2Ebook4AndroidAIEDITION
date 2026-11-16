using System.Collections.Concurrent;
using System.Net;
using System.Web;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Litnet.Getters;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitnetAuthSessionTests
{
    private static readonly Uri BookUrl = new("https://litnet.com/ru/book/test-b123");

    [Fact]
    public async Task SecondGetterUsesSuccessfulSessionWithoutAnotherLoginOrCacheFile()
    {
        var backend = new Backend();
        var scope = NewScope();

        await ReadBook(backend, Options(scope));
        await ReadBook(backend, Options(scope));

        Assert.Equal(1, backend.AuthRequests);
        Assert.Equal(["session-1", "session-1"], backend.BookTokens.ToArray());
        Assert.False(Directory.Exists(scope));
    }

    [Theory]
    [InlineData("login")]
    [InlineData("login-case")]
    [InlineData("password")]
    [InlineData("password-space")]
    [InlineData("guest")]
    [InlineData("scope")]
    public async Task DifferentIdentityCannotBorrowAnotherAccountsSession(string difference)
    {
        var backend = new Backend();
        var scope = NewScope();
        var changed = Options(scope);
        switch (difference)
        {
            case "login": changed.Login = "other@example.test"; break;
            case "login-case": changed.Login = "READER@example.test"; break;
            case "password": changed.Password = "different-password"; break;
            case "password-space": changed.Password += " "; break;
            case "guest": changed.Login = changed.Password = null; break;
            case "scope": changed.AuthCachePath = NewScope(); break;
        }

        await ReadBook(backend, Options(scope));
        await ReadBook(backend, changed);
        await ReadBook(backend, Options(scope));

        Assert.Equal(2, backend.AuthRequests);
        Assert.Equal(["session-1", "session-2", "session-1"], backend.BookTokens.ToArray());
        if (difference == "guest")
            Assert.Equal(["/v1/user/find-by-login", "/v1/registration/registration-by-device"], backend.AuthPaths.ToArray());
    }

    [Fact]
    public async Task GuestSessionIsReusedWithoutAnAccount()
    {
        var backend = new Backend();
        var scope = NewScope();
        var first = Options(scope);
        first.Login = first.Password = null;
        var second = Options(scope);
        second.Login = second.Password = null;

        await ReadBook(backend, first);
        await ReadBook(backend, second);

        Assert.Equal(1, backend.AuthRequests);
        Assert.Equal("/v1/registration/registration-by-device", Assert.Single(backend.AuthPaths));
        Assert.Equal(["session-1", "session-1"], backend.BookTokens.ToArray());
    }

    [Fact]
    public async Task EquivalentPhoneLoginUsesTheSameSessionAsTheActualRequest()
    {
        var backend = new Backend();
        var scope = NewScope();
        var first = Options(scope);
        first.Login = "+70000000000";
        var second = Options(scope);
        second.Login = "70000000000";

        await ReadBook(backend, first);
        await ReadBook(backend, second);

        Assert.Equal(1, backend.AuthRequests);
        Assert.Equal(["session-1", "session-1"], backend.BookTokens.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingCacheScopeKeepsIndependentSessions(string scope)
    {
        var backend = new Backend();

        await ReadBook(backend, Options(scope));
        await ReadBook(backend, Options(scope));

        Assert.Equal(2, backend.AuthRequests);
        Assert.Equal(["session-1", "session-2"], backend.BookTokens.ToArray());
    }

    [Fact]
    public async Task ConcurrentGettersWaitForOneAuthenticationAndReceiveItsSession()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend
        {
            Authenticate = async count =>
            {
                entered.TrySetResult();
                await release.Task;
                return AfAuditSupport.Json(new { token = "session-" + count });
            }
        };
        var scope = NewScope();
        using var first = Create(backend, Options(scope));
        using var second = Create(backend, Options(scope));
        await first.Init();
        await second.Init();

        var firstLogin = first.Authorize();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondLogin = second.Authorize();
        release.TrySetResult();
        await Task.WhenAll(firstLogin, secondLogin).WaitAsync(TimeSpan.FromSeconds(5));
        using var firstBook = await first.Get(BookUrl);
        using var secondBook = await second.Get(BookUrl);

        Assert.Equal(1, backend.AuthRequests);
        Assert.Equal(["session-1", "session-1"], backend.BookTokens.ToArray());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FailedLoginIsNotCachedAndIsNotRetriedInTheSameAttempt(HttpStatusCode status)
    {
        var backend = new Backend
        {
            Authenticate = count => Task.FromResult(count == 1
                ? new HttpResponseMessage(status) { Content = new StringContent("<html>Вход отклонён</html>") }
                : AfAuditSupport.Json(new { token = "session-" + count }))
        };
        var scope = NewScope();
        using (var rejected = Create(backend, Options(scope)))
        {
            await rejected.Init();
            var error = await Assert.ThrowsAsync<HttpRequestException>(rejected.Authorize);
            Assert.Equal(status, error.StatusCode);
            Assert.Equal(1, backend.AuthRequests);
        }

        await ReadBook(backend, Options(scope));
        await ReadBook(backend, Options(scope));

        Assert.Equal(2, backend.AuthRequests);
        Assert.Equal(["session-2", "session-2"], backend.BookTokens.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task EmptyTokenDoesNotPoisonTheSessionCache(string token)
    {
        var backend = new Backend
        {
            Authenticate = count => Task.FromResult(AfAuditSupport.Json(new { token = count == 1 ? token : "session-2" }))
        };
        var scope = NewScope();
        using (var rejected = Create(backend, Options(scope)))
        {
            await rejected.Init();
            await Assert.ThrowsAnyAsync<Exception>(rejected.Authorize);
            Assert.Equal(1, backend.AuthRequests);
        }

        await ReadBook(backend, Options(scope));
        await ReadBook(backend, Options(scope));

        Assert.Equal(2, backend.AuthRequests);
        Assert.Equal(["session-2", "session-2"], backend.BookTokens.ToArray());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 2, "session-2")]
    [InlineData(HttpStatusCode.Forbidden, 1, "session-1")]
    [InlineData(HttpStatusCode.TooManyRequests, 1, "session-1")]
    public async Task ApiRejectionDoesNotRetryLoginAndOnlyUnauthorizedExpiresTheSession(
        HttpStatusCode status, int expectedLogins, string nextToken)
    {
        var backend = new Backend();
        var scope = NewScope();
        await ReadBook(backend, Options(scope));
        backend.BookStatus = status;
        using (var rejected = Create(backend, Options(scope)))
        {
            await rejected.Init();
            await rejected.Authorize();
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => rejected.Get(BookUrl));
            Assert.Equal(status, error.StatusCode);
            Assert.Equal(1, backend.AuthRequests);
            if (status == HttpStatusCode.Unauthorized)
            {
                var requestsBeforeRepeat = backend.TotalRequests;
                var repeated = await Assert.ThrowsAsync<HttpRequestException>(() => rejected.Get(BookUrl));
                Assert.Equal(HttpStatusCode.Unauthorized, repeated.StatusCode);
                Assert.Equal(requestsBeforeRepeat, backend.TotalRequests);
            }
        }
        backend.BookStatus = HttpStatusCode.OK;

        await ReadBook(backend, Options(scope));

        Assert.Equal(expectedLogins, backend.AuthRequests);
        Assert.Equal(["session-1", "session-1", nextToken], backend.BookTokens.ToArray());
    }

    private static Options Options(string scope) => new()
    {
        AuthCachePath = scope, Login = "reader@example.test", Password = "fake-password",
        NoChapters = true, NoImage = true
    };

    private static string NewScope() => Path.Combine(Path.GetTempPath(), "litnet-auth-tests-" + Guid.NewGuid().ToString("N"));

    private static LitnetGetter Create(Backend backend, Options options)
        => new(AfAuditSupport.Config(new Handler(backend), options));

    private static async Task ReadBook(Backend backend, Options options)
    {
        using var getter = Create(backend, options);
        await getter.Init();
        await getter.Authorize();
        using var book = await getter.Get(BookUrl);
        Assert.Equal("Книга", book.Title);
    }

    private sealed class Backend
    {
        private int _authRequests;
        private int _totalRequests;
        public int AuthRequests => Volatile.Read(ref _authRequests);
        public int TotalRequests => Volatile.Read(ref _totalRequests);
        public Func<int, Task<HttpResponseMessage>> Authenticate { get; init; }
        public HttpStatusCode BookStatus { get; set; } = HttpStatusCode.OK;
        public ConcurrentQueue<string> BookTokens { get; } = new();
        public ConcurrentQueue<string> AuthPaths { get; } = new();

        public async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            Interlocked.Increment(ref _totalRequests);
            var uri = request.RequestUri!;
            if (uri.AbsolutePath is "/v1/user/find-by-login" or "/v1/registration/registration-by-device")
            {
                Assert.Equal("api.litnet.com", request.Headers.Host);
                AuthPaths.Enqueue(uri.AbsolutePath);
                var count = Interlocked.Increment(ref _authRequests);
                return Authenticate != null ? await Authenticate(count) : AfAuditSupport.Json(new { token = "session-" + count });
            }
            if (uri.AbsolutePath == "/v1/book/get/123")
            {
                BookTokens.Enqueue(HttpUtility.ParseQueryString(uri.Query)["user_token"]);
                return AfAuditSupport.Json(new { title = "Книга", url = BookUrl.AbsoluteUri, author_name = "Автор" }, BookStatus);
            }
            Assert.Equal(BookUrl.AbsolutePath, uri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") };
        }
    }

    private sealed class Handler(Backend backend) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await backend.Send(request);
            response.RequestMessage = request;
            return response;
        }
    }
}
