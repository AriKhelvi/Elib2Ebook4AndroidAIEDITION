using System.Net;
using System.Web;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Rulate.Getters;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class RulateAuthorizationTests
{
    private const string Meta = "<meta name='csrf-token' content='test-token'>";
    private const string Fields = "<input name='login[login]'><input type='password' name='login[pass]'>";
    private const string Guest = Meta + "<form>" + Fields + "</form>";
    private const string SignedIn = Meta + "<header><a href='/user/profile'>Мой профиль</a></header>";

    [Theory]
    [InlineData("tl.rulate.ru", "<meta name='csrf-token' content='test-token'><form>" + Fields + "</form>", "test-token")]
    [InlineData("bllate.org", "<form><input type='hidden' name='csrf_token' value='test&amp;token'>" + Fields + "</form>", "test&token")]
    [InlineData("tl.rulate.ru", "<meta name='csrf-token'><form><input name='csrf_token' value='form-token'>" + Fields + "</form>", "form-token")]
    public async Task AcceptsBothCsrfLocationsWithoutNullReference(string host, string main, string expectedToken)
    {
        var handler = new FakeHandler(main, SignedIn);
        var log = new RecordingLogger();
        using var getter = CreateGetter(host, handler, log);

        await getter.Authorize();

        var body = HttpUtility.ParseQueryString(Assert.Single(handler.PostedBodies));
        Assert.Equal(expectedToken, body["csrf_token"]);
        Assert.Equal("test-login", body["login[login]"]);
        Assert.Equal("test-password", body["login[pass]"]);
        Assert.Contains(log.Messages, text => text.Contains("Успешно"));
        Assert.All(log.Messages, text => Assert.DoesNotContain("test-password", text));
    }

    [Theory]
    [InlineData("<title>Проверка браузера</title>")]
    [InlineData("<form>" + Fields + "</form>")]
    [InlineData("<meta name='csrf-token'><form>" + Fields + "</form>")]
    [InlineData("<meta name='csrf-token' content=' '><form>" + Fields + "</form>")]
    [InlineData(Meta)]
    public async Task MissingFormOrTokenStopsBeforeSendingCredentials(string main)
    {
        var handler = new FakeHandler(main, SignedIn);
        var log = new RecordingLogger();
        using var getter = CreateGetter("tl.rulate.ru", handler, log);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Authorize());

        Assert.Contains("форму входа", error.Message);
        Assert.Empty(handler.PostedBodies);
        Assert.DoesNotContain(log.Messages, text => text.Contains("Успешно"));
    }

    [Theory]
    [InlineData(Guest)]
    [InlineData("<title>Проверка браузера</title>")]
    [InlineData("{}")]
    [InlineData("{not-json}")]
    [InlineData("{\"error\":\"Неверный пароль\"}")]
    [InlineData("<div class='alert in alert-block'>Неверный пароль</div>")]
    [InlineData("<div class='alert alert-danger'>Неверный пароль</div>")]
    public async Task RejectedOrUnknownResponseDoesNotReportSuccessfulLogin(string response)
    {
        var handler = new FakeHandler(Guest, response);
        var log = new RecordingLogger();
        using var getter = CreateGetter("bllate.org", handler, log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Authorize());

        Assert.Single(handler.PostedBodies);
        Assert.DoesNotContain(log.Messages, text => text.Contains("Успешно"));
    }

    [Fact]
    public async Task AjaxResponseNeedsConfirmedSessionOnNextPage()
    {
        var handler = new FakeHandler(Guest, "{}", SignedIn);
        var log = new RecordingLogger();
        using var getter = CreateGetter("tl.rulate.ru", handler, log);

        await getter.Authorize();

        Assert.Equal(2, handler.GetRequests);
        Assert.Single(handler.PostedBodies);
        Assert.Contains(log.Messages, text => text.Contains("Успешно"));
    }

    [Theory]
    [InlineData("<a href='/logout'>Выйти</a>")]
    [InlineData("<form action='/user/logout' method='post'><button>Выйти</button></form>")]
    public async Task SignedInPageCanConfirmSessionWithoutMetaToken(string response)
    {
        var log = new RecordingLogger();
        using var getter = CreateGetter("tl.rulate.ru", new FakeHandler(Guest, response), log);

        await getter.Authorize();

        Assert.Contains(log.Messages, text => text.Contains("Успешно"));
    }

    [Theory]
    [InlineData("<a href='https://foreign.invalid/logout'>Выйти</a>")]
    [InlineData("<a href='https://tl.rulate.ru.evil.invalid/logout'>Выйти</a>")]
    [InlineData("<a href='http://tl.rulate.ru/logout'>Выйти</a>")]
    public async Task ForeignSignOutLinkDoesNotConfirmSession(string response)
    {
        using var getter = CreateGetter("tl.rulate.ru", new FakeHandler(Guest, response), new RecordingLogger());

        await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Authorize());
    }

    [Theory]
    [InlineData("{\"error\":\"test-login test-password session=secret-value\"}")]
    [InlineData("<div class='alert in alert-block'>test-login test-password session=secret-value</div>")]
    public async Task ServerErrorDoesNotLeakCredentialsOrSessionIntoException(string response)
    {
        var log = new RecordingLogger();
        using var getter = CreateGetter("tl.rulate.ru", new FakeHandler(Guest, response), log);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Authorize());

        Assert.DoesNotContain("test-login", error.Message);
        Assert.DoesNotContain("test-password", error.Message);
        Assert.DoesNotContain("secret-value", error.Message);
        Assert.All(log.Messages, text => Assert.DoesNotContain("secret-value", text));
    }

    [Fact]
    public async Task HttpForbiddenKeepsStatusAndDoesNotSubmitCredentials()
    {
        var handler = new FakeHandler("<title>Проверка браузера</title>", SignedIn, status: HttpStatusCode.Forbidden);
        using var getter = CreateGetter("tl.rulate.ru", handler, new RecordingLogger());

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => getter.Authorize());

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Empty(handler.PostedBodies);
    }

    private static RulateGetterBase CreateGetter(string host, HttpMessageHandler handler, ILogger logger)
    {
        var options = new Options { Login = "test-login", Password = "test-password" };
        var config = new BookGetterConfig(options, new HttpClient(handler), new CookieContainer(), TempFolderFactory.Create(null), logger);
        return host == "bllate.org" ? new BllateGetter(config) : new RulateGetter(config);
    }

    private sealed class FakeHandler(string main, string posted, string verified = null, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> PostedBodies { get; } = [];
        public int GetRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body;
            if (request.Method == HttpMethod.Post)
            {
                PostedBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                body = posted;
            }
            else
            {
                GetRequests++;
                body = GetRequests > 1 ? verified ?? main : main;
            }
            return TestHttpResponse.Create(status, request, new StringContent(body));
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
