using System.Net;
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

    private sealed class AuthFixture : IDisposable
    {
        public AfAuditSupport.Handler Transport { get; }
        public BookGetterConfig Config { get; }
        public RanobeLibGetter Getter { get; }
        public CapturingLogger Logger { get; } = new();
        public int TokenExchanges { get; private set; }
        public string ExchangedCode { get; private set; }

        public AuthFixture(HttpStatusCode confirmationStatus, HttpStatusCode callbackStatus = HttpStatusCode.OK)
        {
            var options = new Options { Login = "private-login", Password = "private-password", DelayMode = 1 };
            var challengeRequests = 0;
            Transport = new AfAuditSupport.Handler(request =>
            {
                var path = request.RequestUri.AbsolutePath;
                if (request.Method == HttpMethod.Get && path == "/auth/oauth/authorize")
                    return Html(++challengeRequests == 1
                        ? "<input name='_token' value='private-csrf'>"
                        : "<form method='post' action='/auth/oauth/confirm'><input type='hidden' name='_token' value='private-csrf'></form>");
                if (request.Method == HttpMethod.Post && path == "/auth/login")
                    return Html("<p>Вход принят</p>");
                if (request.Method == HttpMethod.Post && path == "/auth/oauth/confirm")
                {
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
                    return AfAuditSupport.Json(new { access_token = "private-access-token" });
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
