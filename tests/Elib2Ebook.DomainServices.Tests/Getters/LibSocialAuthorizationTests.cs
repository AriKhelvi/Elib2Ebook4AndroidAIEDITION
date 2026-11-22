using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.LibSocial.Getters;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LibSocialAuthorizationTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task OAuthConfirmationHttpFailureKeepsStatusWithoutExchangingToken(int status)
    {
        using var fixture = new AuthFixture((HttpStatusCode)status);

        var error = await Assert.ThrowsAsync<HttpRequestException>(fixture.Getter.Authorize);

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains($"{status}", error.Message);
        Assert.DoesNotContain("код подтверждения не получен", error.Message);
        Assert.DoesNotContain("private-", error.ToString());
        fixture.AssertNoTokenExchange();
    }

    [Fact]
    public async Task ExpiredCsrfKeepsSpecificExplanationWithoutExchangingToken()
    {
        using var fixture = new AuthFixture((HttpStatusCode)419);

        var error = await Assert.ThrowsAsync<Exception>(fixture.Getter.Authorize);

        Assert.Contains("419", error.Message);
        Assert.Contains("CSRF", error.Message);
        Assert.DoesNotContain("private-", error.ToString());
        fixture.AssertNoTokenExchange();
    }

    [Fact]
    public async Task SuccessfulHttpWithoutCodeDoesNotBlamePasswordOrExposeResponse()
    {
        using var fixture = new AuthFixture(HttpStatusCode.OK);

        var error = await Assert.ThrowsAsync<Exception>(fixture.Getter.Authorize);

        Assert.Contains("код подтверждения не получен", error.Message);
        Assert.DoesNotContain("парол", error.Message);
        Assert.DoesNotContain("логин", error.Message);
        Assert.DoesNotContain("private-", error.ToString());
        fixture.AssertNoTokenExchange();
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task OAuthRedirectStillExchangesCodeRegardlessOfCallbackPageStatus(int callbackStatus)
    {
        using var fixture = new AuthFixture(HttpStatusCode.Redirect, (HttpStatusCode)callbackStatus);

        await fixture.Getter.Authorize();

        Assert.Equal(6, fixture.Transport.Calls);
        Assert.Equal(1, fixture.TokenExchanges);
        Assert.Equal("private-authorization-code", fixture.ExchangedCode);
        Assert.Equal("Bearer", fixture.Config.Client.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("private-access-token", fixture.Config.Client.DefaultRequestHeaders.Authorization?.Parameter);
        Assert.Contains(fixture.Logger.Messages, message => message.Contains("Успешно авторизовались"));
        Assert.DoesNotContain(fixture.Logger.Messages, message => message.Contains("private-"));
    }

    [Fact]
    public async Task AnonymousDownloadDoesNotStartAuthorizationOrChallenge()
    {
        using var fixture = new AuthFixture(HttpStatusCode.Redirect, hasCredentials: false);

        await fixture.Getter.Authorize();

        Assert.Equal(0, fixture.Transport.Calls);
        Assert.Empty(fixture.LoginPayloads);
        Assert.Equal(0, fixture.AltchaRequests);
    }

    [Fact]
    public async Task LoginWithoutAltchaDoesNotRequestChallenge()
    {
        using var fixture = new AuthFixture(HttpStatusCode.Redirect);

        await fixture.Getter.Authorize();

        var payload = Assert.Single(fixture.LoginPayloads);
        Assert.Equal(3, payload.Count);
        Assert.Equal("private-csrf", payload["_token"]);
        Assert.Equal("private-login", payload["login"]);
        Assert.Equal("private-password", payload["password"]);
        Assert.Equal(0, fixture.AltchaRequests);
        Assert.Equal(6, fixture.Transport.Calls);
    }

    [Theory]
    [InlineData(null, "/altcha/challenge")]
    [InlineData("verification", "https://auth.lib.social/altcha/challenge")]
    public async Task AltchaIsSolvedFromTheLoginFormBeforeCredentialsAreSubmitted(string fieldName, string challengeUrl)
    {
        var field = fieldName == null ? string.Empty : $" name='{fieldName}'";
        var loginPage = "<form method='post' action='/other'><input name='_token' value='wrong-token'><altcha-widget challengeurl='https://other.invalid/challenge'></altcha-widget></form>" +
                        AuthFixture.LoginForm($"<altcha-widget challengeurl='{challengeUrl}'{field}></altcha-widget>");
        using var fixture = new AuthFixture(HttpStatusCode.Redirect, loginPage: loginPage);

        await fixture.Getter.Authorize();

        var payload = Assert.Single(fixture.LoginPayloads);
        Assert.Equal(4, payload.Count);
        Assert.Equal("private-csrf", payload["_token"]);
        Assert.Equal("private-login", payload["login"]);
        Assert.Equal("private-password", payload["password"]);
        using var solution = JsonDocument.Parse(Convert.FromBase64String(payload[fieldName ?? "altcha"]));
        Assert.Equal("SHA-256", solution.RootElement.GetProperty("algorithm").GetString());
        Assert.Equal("test-salt", solution.RootElement.GetProperty("salt").GetString());
        Assert.Equal("test-signature", solution.RootElement.GetProperty("signature").GetString());
        Assert.Equal(0, solution.RootElement.GetProperty("number").GetInt32());
        Assert.Equal(1, fixture.AltchaRequests);
        Assert.True(fixture.Requests.IndexOf("GET auth.lib.social/altcha/challenge") < fixture.Requests.IndexOf("POST auth.lib.social/auth/login"));
        Assert.Equal(1, fixture.TokenExchanges);
        Assert.DoesNotContain(fixture.Logger.Messages, message => message.Contains("private-") || message.Contains("test-signature"));
    }

    [Fact]
    public async Task FullRedirectChainReturningToLoginDoesNotSubmitCredentialsOrHiddenTokenAgain()
    {
        using var fixture = new AuthFixture(HttpStatusCode.Redirect, redirectLogin: true, returnToLogin: true);

        var error = await Assert.ThrowsAsync<Exception>(fixture.Getter.Authorize);

        Assert.Contains("снова показал форму входа", error.Message);
        Assert.DoesNotContain("парол", error.Message);
        Assert.DoesNotContain("private-", error.ToString());
        Assert.Equal(new[]
        {
            "GET auth.lib.social/auth/oauth/authorize", "GET auth.lib.social/auth/login",
            "POST auth.lib.social/auth/login", "GET ranobelib.me/",
            "GET auth.lib.social/auth/oauth/authorize", "GET auth.lib.social/auth/login"
        }, fixture.Requests);
        Assert.Single(fixture.LoginPayloads);
        Assert.Equal(0, fixture.ConfirmationPosts);
        Assert.Equal(0, fixture.TokenExchanges);
        Assert.Null(fixture.Config.Client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("<form method='POST' action='https://auth.lib.social/auth/../auth/login/'><input type='hidden' name='_token' value='private-csrf'></form>")]
    [InlineData("<form method='post' action='/auth/oauth/confirm'><input type='hidden' name='_token' value='private-csrf'><input type='PASSWORD' name='pass'></form>")]
    public async Task LoginFormIsNotMistakenForConsent(string returnedForm)
    {
        using var fixture = new AuthFixture(HttpStatusCode.Redirect, confirmationPage: returnedForm);

        var error = await Assert.ThrowsAsync<Exception>(fixture.Getter.Authorize);

        Assert.Contains("снова показал форму входа", error.Message);
        Assert.Equal(3, fixture.Transport.Calls);
        Assert.Single(fixture.LoginPayloads);
        Assert.Equal(0, fixture.ConfirmationPosts);
        Assert.Equal(0, fixture.TokenExchanges);
        Assert.DoesNotContain("private-", error.ToString());
    }

    [Fact]
    public async Task ConsentIsSelectedAfterUnrelatedPostAndLoginForms()
    {
        using var fixture = new AuthFixture(HttpStatusCode.Redirect,
            confirmationPage: "<form method='post' action='/auth/logout'><input type='hidden' name='_token' value='private-logout'></form>" +
                              "<form method='post' action='/auth/verify'><input type='hidden' name='_token' value='private-verification'><input name='code'></form>" +
                              AuthFixture.LoginForm() + AuthFixture.ConfirmationForm());

        await fixture.Getter.Authorize();

        Assert.Single(fixture.LoginPayloads);
        Assert.Equal(1, fixture.ConfirmationPosts);
        Assert.Equal(1, fixture.TokenExchanges);
    }

    [Fact]
    public async Task AdditionalVerificationIsNotSubmittedAsOAuthConsent()
    {
        using var fixture = new AuthFixture(HttpStatusCode.Redirect,
            confirmationPage: "<form method='post' action='/auth/verify'><input type='hidden' name='_token' value='private-verification'><input name='code'></form>");

        var error = await Assert.ThrowsAsync<Exception>(fixture.Getter.Authorize);

        Assert.Contains("авторизация не завершена", error.Message);
        Assert.Equal(3, fixture.Transport.Calls);
        Assert.Equal(0, fixture.ConfirmationPosts);
        Assert.Equal(0, fixture.TokenExchanges);
        Assert.DoesNotContain("private-", error.ToString());
    }

    [Fact]
    public async Task FormErrorDoesNotExposeServerEchoOfCredentialsOrProof()
    {
        using var fixture = new AuthFixture(HttpStatusCode.Redirect,
            confirmationPage: "<div class='form-field__error'>private-password private-signature private-csrf</div>" + AuthFixture.LoginForm());

        var error = await Assert.ThrowsAsync<Exception>(fixture.Getter.Authorize);

        Assert.Contains("Сайт отклонил форму входа", error.Message);
        Assert.Equal(3, fixture.Transport.Calls);
        Assert.Equal(0, fixture.TokenExchanges);
        Assert.DoesNotContain("private-", error.ToString());
        Assert.DoesNotContain(fixture.Logger.Messages, message => message.Contains("private-"));
    }

    [Fact]
    public async Task TokenExchangeFailurePreservesStatusWithoutEchoingSecrets()
    {
        using var fixture = new AuthFixture(HttpStatusCode.Redirect, tokenStatus: HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<HttpRequestException>(fixture.Getter.Authorize);

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal(1, fixture.TokenExchanges);
        Assert.Null(fixture.Config.Client.DefaultRequestHeaders.Authorization);
        Assert.DoesNotContain("private-", error.ToString());
        Assert.DoesNotContain(fixture.Logger.Messages, message => message.Contains("private-"));
    }

    [Theory]
    [InlineData("http://auth.lib.social/altcha/challenge")]
    [InlineData("https://auth.lib.social:444/altcha/challenge")]
    [InlineData("https://private-user@auth.lib.social/altcha/challenge")]
    [InlineData("https://other.invalid/altcha/challenge")]
    [InlineData("//other.invalid/altcha/challenge")]
    [InlineData("")]
    public async Task UnsafeAltchaEndpointDoesNotReceiveCredentialsOrChallengeRequest(string challengeUrl)
    {
        using var fixture = new AuthFixture(HttpStatusCode.Redirect,
            loginPage: AuthFixture.LoginForm($"<altcha-widget challengeurl='{challengeUrl}'></altcha-widget>"));

        var error = await Assert.ThrowsAsync<Exception>(fixture.Getter.Authorize);

        Assert.Equal(1, fixture.Transport.Calls);
        Assert.Empty(fixture.LoginPayloads);
        Assert.Equal(0, fixture.AltchaRequests);
        Assert.Equal(0, fixture.TokenExchanges);
        Assert.DoesNotContain("private-", error.ToString());
        Assert.DoesNotContain("other.invalid", error.ToString());
    }

    [Theory]
    [InlineData("login")]
    [InlineData("PASSWORD")]
    [InlineData("_token")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("login[altcha]")]
    [InlineData("altcha password")]
    public async Task AltchaCannotReplaceCredentialsOrCsrf(string fieldName)
    {
        using var fixture = new AuthFixture(HttpStatusCode.Redirect,
            loginPage: AuthFixture.LoginForm($"<altcha-widget name='{fieldName}' challengeurl='/altcha/challenge'></altcha-widget>"));

        var error = await Assert.ThrowsAsync<Exception>(fixture.Getter.Authorize);

        Assert.Contains("некорректное поле", error.Message);
        Assert.Equal(1, fixture.Transport.Calls);
        Assert.Empty(fixture.LoginPayloads);
        Assert.Equal(0, fixture.AltchaRequests);
        Assert.DoesNotContain("private-", error.ToString());
    }

    [Theory]
    [InlineData("http://auth.lib.social/auth/oauth/confirm")]
    [InlineData("https://auth.lib.social:444/auth/oauth/confirm")]
    [InlineData("https://private-user@auth.lib.social/auth/oauth/confirm")]
    [InlineData("https://other.invalid/auth/oauth/confirm")]
    [InlineData("//other.invalid/auth/oauth/confirm")]
    [InlineData("")]
    public async Task UnsafeConsentActionDoesNotReceiveHiddenFields(string action)
    {
        using var fixture = new AuthFixture(HttpStatusCode.Redirect, confirmationPage: AuthFixture.ConfirmationForm(action));

        var error = await Assert.ThrowsAsync<Exception>(fixture.Getter.Authorize);

        Assert.Equal(3, fixture.Transport.Calls);
        Assert.Single(fixture.LoginPayloads);
        Assert.Equal(0, fixture.ConfirmationPosts);
        Assert.Equal(0, fixture.TokenExchanges);
        Assert.DoesNotContain("private-", error.ToString());
        Assert.DoesNotContain("other.invalid", error.ToString());
    }

    [Fact]
    public async Task UnsafeLoginFormActionIsRejectedBeforeSubmittingCredentials()
    {
        using var fixture = new AuthFixture(HttpStatusCode.Redirect,
            loginPage: AuthFixture.LoginForm().Replace("action='/auth/login'", "action='https://other.invalid/auth/login'"));

        var error = await Assert.ThrowsAsync<Exception>(fixture.Getter.Authorize);

        Assert.Equal(1, fixture.Transport.Calls);
        Assert.Empty(fixture.LoginPayloads);
        Assert.DoesNotContain("private-", error.ToString());
        Assert.DoesNotContain("other.invalid", error.ToString());
    }

    private sealed class AuthFixture : IDisposable
    {
        public AfAuditSupport.Handler Transport { get; }
        public BookGetterConfig Config { get; }
        public RanobeLibGetter Getter { get; }
        public CapturingLogger Logger { get; } = new();
        public int TokenExchanges { get; private set; }
        public string ExchangedCode { get; private set; }
        public int ConfirmationPosts { get; private set; }
        public int AltchaRequests { get; private set; }
        public List<Dictionary<string, string>> LoginPayloads { get; } = [];
        public List<string> Requests { get; } = [];

        public AuthFixture(HttpStatusCode confirmationStatus, HttpStatusCode callbackStatus = HttpStatusCode.OK,
            string loginPage = null, string confirmationPage = null, bool redirectLogin = false, bool returnToLogin = false,
            bool hasCredentials = true, HttpStatusCode tokenStatus = HttpStatusCode.OK)
        {
            var options = new Options { Login = hasCredentials ? "private-login" : null, Password = hasCredentials ? "private-password" : null, DelayMode = 1 };
            var challengeRequests = 0;
            Transport = new AfAuditSupport.Handler(request =>
            {
                var path = request.RequestUri.AbsolutePath;
                Requests.Add($"{request.Method} {request.RequestUri.Host}{path}");
                if (request.Method == HttpMethod.Get && path == "/auth/oauth/authorize")
                {
                    var firstRequest = ++challengeRequests == 1;
                    if ((firstRequest && redirectLogin) || (!firstRequest && returnToLogin))
                        return Redirect("https://auth.lib.social/auth/login");
                    return Html(firstRequest ? loginPage ?? LoginForm() : confirmationPage ?? ConfirmationForm());
                }
                if (request.Method == HttpMethod.Get && path == "/auth/login")
                    return Html(loginPage ?? LoginForm());
                if (request.Method == HttpMethod.Post && path == "/auth/login")
                {
                    LoginPayloads.Add(ReadForm(request));
                    return redirectLogin ? Redirect("https://ranobelib.me/") : Html("<p>Вход принят</p>");
                }
                if (request.Method == HttpMethod.Get && request.RequestUri.Host == "ranobelib.me" && path == "/")
                    return Html("<p>Главная страница</p>");
                if (request.Method == HttpMethod.Get && path == "/altcha/challenge")
                {
                    AltchaRequests++;
                    return AfAuditSupport.Json(new
                    {
                        algorithm = "SHA-256", salt = "test-salt", signature = "test-signature", maxnumber = 0,
                        challenge = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("test-salt0"))).ToLowerInvariant()
                    });
                }
                if (request.Method == HttpMethod.Post && path == "/auth/oauth/confirm")
                {
                    ConfirmationPosts++;
                    var response = Html("private-response-body", confirmationStatus);
                    if (confirmationStatus == HttpStatusCode.Redirect)
                        response.Headers.Location = new Uri("https://ranobelib.me/ru/front/auth/oauth/callback?code=private-authorization-code&state=private-state");
                    return response;
                }
                if (request.Method == HttpMethod.Get && path == "/ru/front/auth/oauth/callback")
                    return Html("<p>Страница callback</p>", callbackStatus);
                if (request.Method == HttpMethod.Post && path == "/api/auth/oauth/token")
                {
                    TokenExchanges++;
                    using var payload = JsonDocument.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    ExchangedCode = payload.RootElement.GetProperty("code").GetString();
                    return AfAuditSupport.Json(new { access_token = "private-access-token" }, tokenStatus);
                }
                throw new InvalidOperationException("Непредусмотренный запрос в проверке авторизации");
            });
            // Настоящая политика выполняет переход OAuth, весь транспорт остаётся подставным.
            var policy = new RequestPolicyHandler(new RequestPacer(options), Logger, Transport);
            Config = AfAuditSupport.Config(policy, options, Logger);
            Getter = new RanobeLibGetter(Config);
        }

        public void AssertNoTokenExchange()
        {
            Assert.Equal(4, Transport.Calls);
            Assert.Equal(0, TokenExchanges);
            Assert.Null(Config.Client.DefaultRequestHeaders.Authorization);
            Assert.DoesNotContain(Logger.Messages, message => message.Contains("Успешно авторизовались") || message.Contains("private-"));
        }

        public void Dispose()
        {
            Getter.Dispose();
            Config.Dispose();
        }

        private static HttpResponseMessage Html(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(body) };

        private static HttpResponseMessage Redirect(string location)
        {
            var response = Html(string.Empty, HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(location);
            return response;
        }

        private static Dictionary<string, string> ReadForm(HttpRequestMessage request)
            => request.Content.ReadAsStringAsync().GetAwaiter().GetResult().Split('&')
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair.Length > 1 ? pair[1] : string.Empty));

        public static string LoginForm(string extra = "")
            => $"<form method='post' action='/auth/login'><input type='hidden' name='_token' value='private-csrf'><input name='login'><input type='password' name='password'>{extra}</form>";

        public static string ConfirmationForm(string action = "/auth/oauth/confirm")
            => $"<form method='post' action='{action}'><input type='hidden' name='_token' value='private-csrf'></form>";
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
