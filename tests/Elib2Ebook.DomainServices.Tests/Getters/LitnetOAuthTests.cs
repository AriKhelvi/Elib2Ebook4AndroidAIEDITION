using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Elib2Ebook.ExternalServices.Litnet;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitnetOAuthTests
{
    [Fact]
    public void AuthorizationUsesPkceAndUniqueStateWithoutDisclosingVerifier()
    {
        var request = LitnetOAuth.CreateAuthorization("0123456789abcdef");
        var second = LitnetOAuth.CreateAuthorization("0123456789abcdef");
        var query = HttpUtility.ParseQueryString(request.AuthorizationUri.Query);
        Assert.Equal(LitnetOAuth.AuthorizationEndpoint, request.AuthorizationUri.GetLeftPart(UriPartial.Path));
        Assert.Equal(LitnetOAuth.ClientId, query["client_id"]);
        Assert.Equal(LitnetOAuth.RedirectUri, query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("choose", query["prompt"]);
        Assert.Equal("name email", query["scope"]);
        Assert.Equal("name email", query["requirements"]);
        Assert.Equal(64, request.CodeVerifier.Length);
        Assert.Equal(Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier))), query["code_challenge"]);
        Assert.Equal(request.State, query["state"]);
        Assert.NotEqual(request.State, second.State);
        Assert.NotEqual(request.CodeVerifier, second.CodeVerifier);
        Assert.DoesNotContain(request.CodeVerifier, request.AuthorizationUri.AbsoluteUri);
        Assert.DoesNotContain(request.CodeVerifier, request.ToString());
        Assert.DoesNotContain(request.State, request.ToString());

        var padded = request.State.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        using var state = JsonDocument.Parse(Convert.FromBase64String(padded));
        Assert.Equal(LitnetOAuth.RedirectUri, state.RootElement.GetProperty("redirect_url").GetString());
        Assert.False(string.IsNullOrWhiteSpace(state.RootElement.GetProperty("params").GetProperty("nonce").GetString()));
    }

    [Fact]
    public void CallbackAcceptsOnlyMatchingAttemptAndDecodesCode()
    {
        var request = LitnetOAuth.CreateAuthorization("device");
        Assert.Equal("code+/=", LitnetOAuth.ValidateCallback(request, Callback(request, "code%2B%2F%3D")));
        var another = LitnetOAuth.CreateAuthorization("device");
        Assert.Throws<InvalidOperationException>(() => LitnetOAuth.ValidateCallback(another, Callback(request)));
    }

    [Theory]
    [InlineData("http://id.litnet.com/auth/l-audio/callback")]
    [InlineData("https://id.litnet.com.evil.test/auth/l-audio/callback")]
    [InlineData("https://evil.test/auth/l-audio/callback")]
    [InlineData("https://id.litnet.com:8443/auth/l-audio/callback")]
    [InlineData("https://user@id.litnet.com/auth/l-audio/callback")]
    [InlineData("https://id.litnet.com/auth/l-audio/callback/other")]
    [InlineData("https://id.litnet.com/auth/l-audio/callback/")]
    public void CallbackRejectsDifferentOriginOrPath(string redirect)
    {
        var request = LitnetOAuth.CreateAuthorization("device");
        Assert.Throws<InvalidOperationException>(() => LitnetOAuth.ValidateCallback(request,
            new Uri(redirect + "?code=secret-code&state=" + Uri.EscapeDataString(request.State))));
    }

    [Theory]
    [InlineData("&state=other")]
    [InlineData("&code=other")]
    [InlineData("#access_token=do-not-log")]
    public void CallbackRejectsAmbiguousParametersAndFragments(string suffix)
    {
        var request = LitnetOAuth.CreateAuthorization("device");
        Assert.Throws<InvalidOperationException>(() => LitnetOAuth.ValidateCallback(request,
            new Uri(Callback(request).AbsoluteUri + suffix)));
    }

    [Fact]
    public void CallbackErrorIsSanitized()
    {
        var request = LitnetOAuth.CreateAuthorization("device");
        var error = Assert.Throws<InvalidOperationException>(() => LitnetOAuth.ValidateCallback(request,
            new Uri(Callback(request).AbsoluteUri + "&error=secret-token&error_description=secret-password")));
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain(request.State, error.ToString());
    }

    [Fact]
    public async Task ExchangeSendsFormAndPrefersInternalTokensAndExpiry()
    {
        var authorization = LitnetOAuth.CreateAuthorization("0123456789abcdef");
        using var client = Client(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(LitnetOAuth.TokenEndpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Host);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("Dart/3.10 (dart:io)", request.Headers.UserAgent.ToString());
            var form = HttpUtility.ParseQueryString(await request.Content.ReadAsStringAsync());
            Assert.Equal(6, form.Count);
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal(LitnetOAuth.ClientId, form["client_id"]);
            Assert.Equal(LitnetOAuth.RedirectUri, form["redirect_uri"]);
            Assert.Equal(authorization.CodeVerifier, form["code_verifier"]);
            Assert.Equal("code+/=", form["code"]);
            Assert.Equal(authorization.DeviceId, form["device_id"]);
            return Json("""{"access_token":"external-access","refresh_token":"external-refresh","expires_in":86400,"internal":{"access_token":"internal-access","refresh_token":"internal-refresh","expires_in":3600}}""");
        });
        var before = DateTimeOffset.UtcNow;
        var session = await LitnetOAuth.ExchangeAsync(client, authorization, "code+/=");
        Assert.Equal("internal-access", session.AccessToken);
        Assert.Equal("internal-refresh", session.RefreshToken);
        Assert.Equal(authorization.DeviceId, session.DeviceId);
        Assert.InRange(session.ExpiresAtUtc!.Value, before.AddHours(1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.DoesNotContain(session.AccessToken, session.ToString());
        Assert.DoesNotContain(session.RefreshToken, session.ToString());
    }

    [Fact]
    public async Task ExchangeUsesIndependentRootFallbacksWithoutBorrowingExternalExpiry()
    {
        using var client = Client(_ => Task.FromResult(Json("""{"access_token":"external","refresh_token":"root-refresh","expires_in":86400,"internal":{"access_token":"internal"}}""")));
        var session = await LitnetOAuth.ExchangeAsync(client, LitnetOAuth.CreateAuthorization("device"), "code");
        Assert.Equal("internal", session.AccessToken);
        Assert.Equal("root-refresh", session.RefreshToken);
        Assert.Null(session.ExpiresAtUtc);
    }

    [Fact]
    public async Task RefreshUsesInternalEndpointAndRotatedTokens()
    {
        var old = new LitnetOAuthSession("old-access", "old-refresh+/=", "device", DateTimeOffset.UtcNow);
        using var client = Client(async request =>
        {
            Assert.Equal(LitnetOAuth.RefreshEndpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync());
            Assert.Equal(3, form.Count);
            Assert.Equal(LitnetOAuth.ClientId, form["client_id"]);
            Assert.Equal(old.DeviceId, form["device_id"]);
            Assert.Equal(old.RefreshToken, form["refresh_token"]);
            Assert.Null(form["grant_type"]);
            return Json("""{"access_token":"new-access","refresh_token":"new-refresh","expires_in":"1800"}""");
        });
        var before = DateTimeOffset.UtcNow;
        var updated = await LitnetOAuth.RefreshAsync(client, old);
        Assert.Equal("new-access", updated.AccessToken);
        Assert.Equal("new-refresh", updated.RefreshToken);
        Assert.Equal(old.DeviceId, updated.DeviceId);
        Assert.InRange(updated.ExpiresAtUtc!.Value, before.AddMinutes(30), DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.Equal("old-refresh+/=", old.RefreshToken);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"access_token\":\"secret\"}")]
    [InlineData("{\"refresh_token\":\"secret\"}")]
    [InlineData("{\"access_token\":\"secret\",\"refresh_token\":\" \"}")]
    [InlineData("{\"access_token\":\"secret\",\"refresh_token\":\"secret\",\"internal\":{\"access_token\":\"\"}}")]
    [InlineData("{\"access_token\":42,\"refresh_token\":\"secret\"}")]
    [InlineData("{\"access_token\":\"secret\",\"refresh_token\":\"secret\",\"expires_in\":-1}")]
    [InlineData("{\"access_token\":\"secret\",\"refresh_token\":\"secret\",\"expires_in\":9223372036854775807}")]
    [InlineData("{\"secret-token-and-password\":")]
    [InlineData("[\"secret\"]")]
    public async Task MissingTokensOrInvalidResponseAreRejectedWithoutPayload(string json)
    {
        using var client = Client(_ => Task.FromResult(Json(json)));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            LitnetOAuth.ExchangeAsync(client, LitnetOAuth.CreateAuthorization("device"), "secret-code"));
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task HttpFailureKeepsStatusButDoesNotExposeBodyOrReason(HttpStatusCode status)
    {
        var requests = 0;
        using var client = Client(_ =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                ReasonPhrase = "secret-code", Content = new StringContent("secret-token-and-password")
            });
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            LitnetOAuth.ExchangeAsync(client, LitnetOAuth.CreateAuthorization("device"), "secret-code"));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task TransportExceptionDoesNotLeakSecrets()
    {
        using var client = Client(_ => throw new HttpRequestException("secret-code-and-token",
            new Exception("secret-inner"), HttpStatusCode.Forbidden));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            LitnetOAuth.ExchangeAsync(client, LitnetOAuth.CreateAuthorization("device"), "secret-code"));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task ClientWithSiteHeadersIsRejectedBeforeSending()
    {
        var requests = 0;
        using var client = Client(_ => { requests++; return Task.FromResult(Json("{}")); });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "other-site-secret");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LitnetOAuth.ExchangeAsync(client, LitnetOAuth.CreateAuthorization("device"), "secret-code"));
        Assert.Equal(0, requests);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal("other-site-secret", client.DefaultRequestHeaders.Authorization.Parameter);
    }

    [Fact]
    public async Task CancellationReachesTransportAndDoesNotRetry()
    {
        using var cancel = new CancellationTokenSource();
        var requests = 0;
        using var client = new HttpClient(new Handler(async (_, ct) =>
        {
            requests++;
            cancel.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json("{}");
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LitnetOAuth.ExchangeAsync(client, LitnetOAuth.CreateAuthorization("device"), "code", cancel.Token));
        Assert.Equal(1, requests);
    }

    private static Uri Callback(LitnetAuthorizationRequest request, string encodedCode = "code")
        => new(LitnetOAuth.RedirectUri + "?code=" + encodedCode + "&state=" + Uri.EscapeDataString(request.State));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        => new(new Handler((request, _) => send(request)));

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => send(request, ct);
    }
}
