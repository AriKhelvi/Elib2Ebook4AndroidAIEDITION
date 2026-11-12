using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using FlareSolverrSharp;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Configs;

public class BookGetterConfig : IDisposable
{
    public readonly ILogger Logger;

    private readonly RequestPacer _pacer;
    private readonly List<HttpClient> _additionalClients = [];

    /// <summary>
    /// Проверка сертификата сервера для всех запросов ядра. По умолчанию принимает любой сертификат (как раньше);
    /// приложение может подставить свою политику (так делает Android-версия, см. android/E2E.Droid).
    /// </summary>
    public static Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> ServerCertificateValidator { get; set; } = (_, _, _, _) => true;

    public HttpClient Client { get; private set; }

    public Options Options { get; }

    public CookieContainer CookieContainer { get; }

    public bool HasCredentials => !string.IsNullOrWhiteSpace(Options.Login) && !string.IsNullOrWhiteSpace(Options.Password);

    public TempFolder TempFolder { get; }

    public BookGetterConfig(Options options, HttpClient client, CookieContainer cookieContainer, TempFolder tempFolder, ILogger logger)
    {
        Logger = logger;
        _pacer = new RequestPacer(options);
        Client = client;
        CookieContainer = cookieContainer;
        Options = options;
        TempFolder = tempFolder;
    }

    public void Dispose()
    {
        foreach (var client in _additionalClients) client.Dispose();
        Client?.Dispose();
        TempFolder?.Dispose();
    }

    public static BookGetterConfig GetDefault(Options options, ILogger logger)
    {
        var config = new BookGetterConfig(options, null, new CookieContainer(), TempFolderFactory.Create(options.TempPath), logger);
        config.Client = config.CreateClientCore();
        return config;
    }

    /// <summary>Клиент с отдельными заголовками, но общей паузой, cookie, прокси и проверкой сертификатов.</summary>
    public HttpClient CreateAdditionalClient()
    {
        var client = CreateClientCore();
        _additionalClients.Add(client);
        return client;
    }

    private HttpClient CreateClientCore()
    {
        var options = Options;
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) => ServerCertificateValidator(request, certificate, chain, errors),
            CookieContainer = CookieContainer,
            Proxy = null,
            UseProxy = false
        };

        if (!string.IsNullOrEmpty(options.Proxy))
        {
            handler.Proxy = new WebProxy(options.Proxy.AsUri());
            handler.UseProxy = true;
        }

        var policyHandler = new RequestPolicyHandler(_pacer, Logger, handler);

        if (!string.IsNullOrWhiteSpace(options.Flare))
        {
            var chandler = new ClearanceHandler(options.Flare)
            {
                MaxTimeout = options.Timeout * 1000, InnerHandler = policyHandler
            };

            if (!string.IsNullOrEmpty(options.Proxy))
            {
                chandler.ProxyUrl = options.Proxy;
            }

            var cclient = new HttpClient(chandler);
            cclient.Timeout = TimeSpan.FromSeconds(options.Timeout);

            return cclient;
        }

        var client = new HttpClient(policyHandler);

        client.Timeout = TimeSpan.FromSeconds(options.Timeout);

        return client;
    }
}
