using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using E2E.Droid.Services;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Litnet;
using Microsoft.Maui.Storage;

namespace ServiceTests;

public sealed class LitnetAuthServiceTests
{
    private const string SessionKey = "litnet:oauth-session:v1";
    private const string DeviceKey = "litnet:oauth-device-id:v1";
    private const string Secret = "fake-sensitive-token-do-not-display";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task BrowserSignInPersistsOnlyTokensInSecureStorageAndReloadsWithoutNetwork()
    {
        var fixture = new Fixture();
        var service = fixture.Service();

        await service.SignInAsync();
        var session = await service.GetAsync(null, CancellationToken.None);
        Assert.True(service.HasSession);
        Assert.False(service.IsBusy);
        Assert.Equal("access-new", session.AccessToken);
        Assert.Equal(fixture.Browser.Request.DeviceId, session.DeviceId);
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal(LitnetOAuth.TokenEndpoint, fixture.Handler.Requests[0].Url);
        Assert.Contains("code=browser-code", fixture.Handler.Requests[0].Body);
        Assert.Contains("code_verifier=", fixture.Handler.Requests[0].Body);
        Assert.Contains("access-new", fixture.Storage.Values[SessionKey]);
        Assert.Contains("refresh-new", fixture.Storage.Values[SessionKey]);
        Assert.DoesNotContain(fixture.Preferences.Values.Values, value => value.Contains("access-new") || value.Contains("refresh-new"));
        Assert.Equal(session.DeviceId, fixture.Preferences.Values[DeviceKey]);

        var reloaded = fixture.Service();
        await reloaded.InitializeAsync();
        Assert.Equal(session, await reloaded.GetAsync(null, CancellationToken.None));
        Assert.True(reloaded.HasSession);
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal(1, fixture.Browser.Calls);
    }

    [Fact]
    public async Task MissingSessionDoesNotLaunchBrowserOrNetworkDuringDownload()
    {
        var fixture = new Fixture();
        var service = fixture.Service();

        Assert.Null(await service.GetAsync(null, CancellationToken.None));
        Assert.False(service.HasSession);
        Assert.Empty(fixture.Handler.Requests);
        Assert.Equal(0, fixture.Browser.Calls);
    }

    [Fact]
    public async Task ConcurrentRejectedTokensRefreshOnceAndPersistRotatedPair()
    {
        var fixture = new Fixture();
        fixture.Seed();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Respond = async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return TokenResponse();
        };
        var service = fixture.Service();

        var first = service.GetAsync("access-old", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.GetAsync("access-old", CancellationToken.None);
        release.SetResult();
        var sessions = await Task.WhenAll(first, second);

        Assert.All(sessions, session => Assert.Equal("access-new", session.AccessToken));
        var request = Assert.Single(fixture.Handler.Requests);
        Assert.Equal(LitnetOAuth.RefreshEndpoint, request.Url);
        Assert.Contains("refresh_token=refresh-old", request.Body);
        var stored = JsonSerializer.Deserialize<LitnetOAuthSession>(fixture.Storage.Values[SessionKey]);
        Assert.Equal("access-new", stored.AccessToken);
        Assert.Equal("refresh-new", stored.RefreshToken);
        Assert.Equal("0123456789abcdef", stored.DeviceId);
        Assert.Equal("access-new", (await fixture.Service().GetAsync(null, CancellationToken.None)).AccessToken);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task RejectionOfPreviousAccountDoesNotRefreshCurrentAccount()
    {
        var fixture = new Fixture();
        fixture.Seed();

        var session = await fixture.Service().GetAsync("different-account-token", CancellationToken.None);

        Assert.Equal("access-old", session.AccessToken);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task SuccessfulAccountSwitchReplacesTokenPairWithoutChangingDeviceIdentity()
    {
        var fixture = new Fixture();
        fixture.Seed();
        var service = fixture.Service();

        await service.SignInAsync();
        var session = await service.GetAsync("access-old", CancellationToken.None);

        Assert.Equal("access-new", session.AccessToken);
        Assert.Equal("0123456789abcdef", session.DeviceId);
        Assert.Equal("0123456789abcdef", fixture.Browser.Request.DeviceId);
        Assert.DoesNotContain("refresh-old", fixture.Storage.Values[SessionKey]);
        Assert.Equal(LitnetOAuth.TokenEndpoint, Assert.Single(fixture.Handler.Requests).Url);
    }

    [Fact]
    public async Task ExpiringSessionRefreshesBeforeReturningToDownloader()
    {
        var fixture = new Fixture();
        fixture.Seed(expires: Now.AddSeconds(20));

        var session = await fixture.Service().GetAsync(null, CancellationToken.None);

        Assert.Equal("access-new", session.AccessToken);
        Assert.Equal(LitnetOAuth.RefreshEndpoint, Assert.Single(fixture.Handler.Requests).Url);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task TemporaryRefreshFailureKeepsSavedAccountAndDoesNotRetry(HttpStatusCode status)
    {
        var fixture = new Fixture();
        fixture.Seed();
        var original = fixture.Storage.Values[SessionKey];
        fixture.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(Secret)
        });
        var service = fixture.Service();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.GetAsync("access-old", CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Equal(original, fixture.Storage.Values[SessionKey]);
        Assert.True(service.HasSession);
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal(0, fixture.Storage.RemoveCalls);

        var repeated = await Assert.ThrowsAsync<HttpRequestException>(() => service.GetAsync("access-old", CancellationToken.None));
        Assert.Equal(status, repeated.StatusCode);
        Assert.Single(fixture.Handler.Requests);
        fixture.UtcNow = Now.AddSeconds(61);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetAsync("access-old", CancellationToken.None));
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task InvalidRefreshTokenClearsAccountInsteadOfRetrying(HttpStatusCode status)
    {
        var fixture = new Fixture();
        fixture.Seed();
        fixture.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status));
        var service = fixture.Service();

        await Assert.ThrowsAnyAsync<Exception>(() => service.GetAsync("access-old", CancellationToken.None));

        Assert.False(service.HasSession);
        Assert.False(fixture.Storage.Values.ContainsKey(SessionKey));
        Assert.Single(fixture.Handler.Requests);
        Assert.Null(await service.GetAsync(null, CancellationToken.None));
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task UnavailableSecureStorageDoesNotSilentlySwitchDownloadToGuestOrDeleteData()
    {
        var fixture = new Fixture();
        fixture.Seed();
        fixture.Storage.ReadError = new CryptographicException(Secret);
        var service = fixture.Service();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => service.GetAsync(null, CancellationToken.None));

        Assert.DoesNotContain(Secret, error.ToString());
        Assert.True(fixture.Storage.Values.ContainsKey(SessionKey));
        Assert.Equal(0, fixture.Storage.RemoveCalls);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task FailedAccountReplacementLeavesPreviousSecureSessionAndSanitizesError()
    {
        var fixture = new Fixture();
        fixture.Seed();
        var original = fixture.Storage.Values[SessionKey];
        var service = fixture.Service();
        await service.InitializeAsync();
        fixture.Storage.WriteError = new CryptographicException(Secret);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => service.SignInAsync());

        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Equal(original, fixture.Storage.Values[SessionKey]);
        Assert.Equal("access-old", (await service.GetAsync(null, CancellationToken.None)).AccessToken);
        Assert.False(service.IsBusy);
    }

    [Fact]
    public async Task RotatedTokensArePersistedAfterStorageRecoveryWithoutRepeatingRefresh()
    {
        var fixture = new Fixture();
        fixture.Seed();
        var service = fixture.Service();
        await service.InitializeAsync();
        fixture.Storage.WriteError = new CryptographicException(Secret);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => service.GetAsync("access-old", CancellationToken.None));
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Single(fixture.Handler.Requests);
        fixture.Storage.WriteError = null;

        var session = await service.GetAsync(null, CancellationToken.None);

        Assert.Equal("access-new", session.AccessToken);
        var stored = JsonSerializer.Deserialize<LitnetOAuthSession>(fixture.Storage.Values[SessionKey]);
        Assert.Equal("access-new", stored.AccessToken);
        Assert.Equal("refresh-new", stored.RefreshToken);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task CorruptedSecureEntryIsReportedWithoutDeletingOrLeakingItsContent()
    {
        var fixture = new Fixture();
        fixture.Storage.Values[SessionKey] = "{broken-json-" + Secret;
        var service = fixture.Service();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => service.GetAsync(null, CancellationToken.None));

        Assert.DoesNotContain(Secret, error.ToString());
        Assert.True(fixture.Storage.Values.ContainsKey(SessionKey));
        Assert.Equal(0, fixture.Storage.RemoveCalls);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task LogoutKeepsDeviceIdentityAndOtherSitesCredentials()
    {
        var fixture = new Fixture();
        fixture.Seed();
        fixture.Storage.Values["cred:other.example"] = "other-site-session";
        var service = fixture.Service();
        await service.InitializeAsync();

        await service.SignOutAsync();

        Assert.False(service.HasSession);
        Assert.Null(await service.GetAsync(null, CancellationToken.None));
        Assert.False(fixture.Storage.Values.ContainsKey(SessionKey));
        Assert.Equal("other-site-session", fixture.Storage.Values["cred:other.example"]);
        Assert.Equal("0123456789abcdef", fixture.Preferences.Values[DeviceKey]);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task FailedSecureRemovalIsReportedWithoutSecretDetails()
    {
        var fixture = new Fixture();
        fixture.Seed();
        fixture.Storage.RemoveError = new CryptographicException(Secret);
        var service = fixture.Service();
        await service.InitializeAsync();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => service.SignOutAsync());

        Assert.DoesNotContain(Secret, error.ToString());
        Assert.True(fixture.Storage.Values.ContainsKey(SessionKey));
        Assert.False(service.HasSession);
        Assert.Null(await service.GetAsync(null, CancellationToken.None));
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task LogoutDuringBrowserWaitRejectsLateSuccessfulCallback()
    {
        var fixture = new Fixture();
        fixture.Seed();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Browser.Authorize = (request, _) =>
        {
            entered.TrySetResult();
            return callback.Task;
        };
        var service = fixture.Service();
        var login = service.SignInAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.SignOutAsync().WaitAsync(TimeSpan.FromSeconds(5));
        callback.SetResult(SuccessfulCallback(fixture.Browser.Request));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);

        Assert.False(service.HasSession);
        Assert.False(service.IsBusy);
        Assert.False(fixture.Storage.Values.ContainsKey(SessionKey));
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task BrowserCancellationWhileSwitchingAccountPreservesPreviousAccount()
    {
        var fixture = new Fixture();
        fixture.Seed();
        fixture.Browser.Authorize = (_, _) => Task.FromException<Uri>(new OperationCanceledException());
        var service = fixture.Service();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SignInAsync());

        Assert.Equal("access-old", (await service.GetAsync(null, CancellationToken.None)).AccessToken);
        Assert.False(service.IsBusy);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    public async Task BrowserFailureDoesNotExposeCallbackOrTokenInException(string kind)
    {
        var fixture = new Fixture();
        Exception source = kind switch
        {
            "http" => new HttpRequestException(Secret),
            "timeout" => new TimeoutException(Secret),
            "cancel" => new OperationCanceledException(Secret),
            _ => new IOException(Secret)
        };
        fixture.Browser.Authorize = (_, _) => Task.FromException<Uri>(source);
        var service = fixture.Service();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => service.SignInAsync());

        Assert.DoesNotContain(Secret, error.ToString());
        Assert.False(service.IsBusy);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task SuccessfulBrowserLoginRemovesOnlyLitnetLegacyCredentials()
    {
        var fixture = new Fixture();
        fixture.SeedLegacy();
        var service = fixture.Service(cleanLegacy: true);

        await service.SignInAsync();

        Assert.True(service.HasSession);
        Assert.Null(service.LegacyCleanupWarning);
        Assert.False(fixture.Storage.Values.ContainsKey("cred:litnet.com"));
        Assert.True(fixture.Storage.Values.ContainsKey(SessionKey));
        Assert.Equal("other-credentials", fixture.Storage.Values["cred:other.example"]);
        Assert.Equal("other.example", fixture.Preferences.Values["cred-hosts"]);
    }

    [Fact]
    public async Task FailedTokenPersistenceDoesNotEraseLegacyCredentials()
    {
        var fixture = new Fixture();
        fixture.SeedLegacy();
        fixture.Storage.WriteError = new CryptographicException(Secret);
        var service = fixture.Service(cleanLegacy: true);

        await Assert.ThrowsAnyAsync<Exception>(() => service.SignInAsync());

        Assert.False(service.HasSession);
        Assert.True(fixture.Storage.Values.ContainsKey("cred:litnet.com"));
        Assert.Equal(0, fixture.Storage.RemoveCalls);
    }

    [Fact]
    public async Task FailedLegacyCleanupRetainsOAuthAndOffersSafeRetryWithoutAnotherLogin()
    {
        var fixture = new Fixture();
        fixture.SeedLegacy();
        fixture.Storage.RemoveError = new CryptographicException(Secret);
        var service = fixture.Service(cleanLegacy: true);

        await service.SignInAsync();

        Assert.True(service.HasSession);
        Assert.False(string.IsNullOrWhiteSpace(service.LegacyCleanupWarning));
        Assert.DoesNotContain(Secret, service.LegacyCleanupWarning);
        Assert.True(fixture.Storage.Values.ContainsKey("cred:litnet.com"));
        Assert.True(fixture.Storage.Values.ContainsKey(SessionKey));
        fixture.Storage.RemoveError = null;
        await service.RetryLegacyCleanupAsync();
        Assert.Null(service.LegacyCleanupWarning);
        Assert.False(fixture.Storage.Values.ContainsKey("cred:litnet.com"));
        Assert.Equal("access-new", (await service.GetAsync(null, CancellationToken.None)).AccessToken);
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal(1, fixture.Browser.Calls);
    }

    [Theory]
    [InlineData("https://litnet.com/ru/book/example-b1", true)]
    [InlineData("https://www.litnet.com/ru/book/example-b1", true)]
    [InlineData("https://booknet.com/en/book/example-b1", false)]
    [InlineData("https://booknet.ua/book/example-b1", false)]
    [InlineData("https://litnet.com.evil.example/book", false)]
    [InlineData("https://other.example/book", false)]
    public void DownloadOptionsExposeBrowserSessionOnlyToLitnet(string url, bool litnet)
    {
        Preferences.Default = new RateLimitFixture.Preferences();
        FileSystem.AppDataDirectory = Path.GetTempPath();
        var provider = new Provider();
        var manager = new DownloadManager(new AppSettings(), null, null, null, null, provider);
        var request = new DownloadRequest { Urls = [url], Formats = ["fb2"], Login = "legacy-user", Password = "legacy-password" };
        var method = typeof(DownloadManager).GetMethod("BuildOptions", BindingFlags.Instance | BindingFlags.NonPublic);

        var options = (Options)method.Invoke(manager, [request, url, Path.GetTempPath()]);

        Assert.Equal(litnet, options.LitnetSessionProvider != null);
        Assert.Equal(litnet ? null : "legacy-user", options.Login);
        Assert.Equal(litnet ? null : "legacy-password", options.Password);
        if (litnet) Assert.Same(provider, options.LitnetSessionProvider.Target);
    }

    private static Uri SuccessfulCallback(LitnetAuthorizationRequest request) => new(
        LitnetOAuth.RedirectUri + "?code=browser-code&state=" + Uri.EscapeDataString(request.State));

    private static HttpResponseMessage TokenResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"internal\":{\"access_token\":\"access-new\",\"refresh_token\":\"refresh-new\",\"expires_in\":3600}}", Encoding.UTF8, "application/json")
    };

    private sealed class Fixture
    {
        public readonly FakeSecureStorage Storage = new();
        public readonly FakePreferences Preferences = new();
        public readonly FakeBrowser Browser = new();
        public readonly FakeHandler Handler = new();
        public DateTimeOffset UtcNow = Now;
        public LitnetAuthService Service(bool cleanLegacy = false) => new(Storage, Preferences, Browser,
            () => new HttpClient(Handler, disposeHandler: false), () => UtcNow,
            cleanLegacy ? new CredentialStore(Storage, Preferences, new ToastService()) : null);
        public void Seed(DateTimeOffset? expires = null)
        {
            Storage.Values[SessionKey] = JsonSerializer.Serialize(new LitnetOAuthSession(
                "access-old", "refresh-old", "0123456789abcdef", expires ?? Now.AddHours(4)));
            Preferences.Values[DeviceKey] = "0123456789abcdef";
        }
        public void SeedLegacy()
        {
            Storage.Values["cred:litnet.com"] = JsonSerializer.Serialize(new SiteCredential("legacy-user", "legacy-password"));
            Storage.Values["cred:other.example"] = "other-credentials";
            Preferences.Values["cred-hosts"] = "litnet.com\nother.example";
        }
    }

    private sealed class FakeSecureStorage : ISecureStorage
    {
        public readonly Dictionary<string, string> Values = [];
        public Exception ReadError, WriteError, RemoveError;
        public int RemoveCalls;
        public Task<string> GetAsync(string key) => ReadError != null
            ? Task.FromException<string>(ReadError) : Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value)
        {
            if (WriteError != null) return Task.FromException(WriteError);
            Values[key] = value;
            return Task.CompletedTask;
        }
        public bool Remove(string key)
        {
            RemoveCalls++;
            if (RemoveError != null) throw RemoveError;
            return Values.Remove(key);
        }
    }

    private sealed class FakePreferences : IPreferences
    {
        public readonly Dictionary<string, string> Values = [];
        public T Get<T>(string key, T defaultValue, string sharedName = null) =>
            Values.TryGetValue(key, out var value) ? (T)(object)value : defaultValue;
        public void Set<T>(string key, T value, string sharedName = null) => Values[key] = (string)(object)value;
    }

    private sealed class FakeBrowser : ILitnetAuthorizationBrowser
    {
        public LitnetAuthorizationRequest Request;
        public int Calls;
        public Func<LitnetAuthorizationRequest, CancellationToken, Task<Uri>> Authorize =
            (request, _) => Task.FromResult(SuccessfulCallback(request));
        public Task<Uri> AuthorizeAsync(LitnetAuthorizationRequest request, CancellationToken ct)
        {
            Request = request;
            Calls++;
            return Authorize(request, ct);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<(string Url, string Body)> Requests = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond =
            (_, _) => Task.FromResult(TokenResponse());
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri.AbsoluteUri, await request.Content.ReadAsStringAsync(ct)));
            return await Respond(request, ct);
        }
    }

    private sealed class Provider : ILitnetSessionProvider
    {
        public Task<BrowserAuthSession> GetAsync(string rejectedToken, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Изоляция параметров не должна запускать вход.");
    }
}
