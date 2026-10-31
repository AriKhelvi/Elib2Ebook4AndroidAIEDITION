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

    private class RedirectHandler : HttpClientHandler
    {
        private readonly ILogger _logger;
        public int Delay { get; set; }

        public RedirectHandler(ILogger logger)
        {
            _logger = logger;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                var responseMessage = await base.SendAsync(request, cancellationToken);

                if (responseMessage is
                    {
                        StatusCode: HttpStatusCode.Redirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.MovedPermanently or HttpStatusCode.Moved, Headers.Location: not null
                    })
                {
                    request = new HttpRequestMessage(request.Method, responseMessage.Headers.Location);
                    responseMessage = await base.SendAsync(request, cancellationToken);
                }

                if (responseMessage.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    responseMessage.StatusCode = HttpStatusCode.Forbidden;
                }

                return responseMessage;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogInformation("Сервер не успевает ответить. Попробуйте увеличить Timeout с помощью параметра -t");
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogInformation(ex, "Сервер не успевает ответить. Попробуйте увеличить Timeout с помощью параметра -t");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex.Message);
                throw;
            }
            finally
            {
                if (Delay > 0)
                {
                    _logger.LogInformation($"Жду {Delay} секунд(-ы)");
                    await Task.Delay(TimeSpan.FromSeconds(Delay), cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Проверка сертификата сервера для всех запросов ядра. По умолчанию принимает любой сертификат (как раньше);
    /// приложение может подставить свою политику (так делает Android-версия, см. android/E2E.Droid).
    /// </summary>
    public static Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> ServerCertificateValidator { get; set; } = (_, _, _, _) => true;

    public HttpClient Client { get; }

    public Options Options { get; }

    public CookieContainer CookieContainer { get; }

    public bool HasCredentials => !string.IsNullOrWhiteSpace(Options.Login) && !string.IsNullOrWhiteSpace(Options.Password);

    public TempFolder TempFolder { get; }

    public BookGetterConfig(Options options, HttpClient client, CookieContainer cookieContainer, TempFolder tempFolder, ILogger logger)
    {
        Logger = logger;
        Client = client;
        CookieContainer = cookieContainer;
        Options = options;
        TempFolder = tempFolder;
    }

    public void Dispose()
    {
        Client?.Dispose();
        TempFolder?.Dispose();
    }

    public static BookGetterConfig GetDefault(Options options, ILogger logger)
    {
        var cookieContainer = new CookieContainer();
        var client = GetClient(options, cookieContainer, logger);

        return new BookGetterConfig(options, client, cookieContainer, TempFolderFactory.Create(options.TempPath), logger);
    }

    private static HttpClient GetClient(Options options, CookieContainer container, ILogger logger)
    {
        var handler = new RedirectHandler(logger)
        {
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli,
            ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) => ServerCertificateValidator(request, certificate, chain, errors),
            CookieContainer = container,
            Proxy = null,
            UseProxy = false,
            Delay = options.Delay
        };

        if (!string.IsNullOrEmpty(options.Proxy))
        {
            handler.Proxy = new WebProxy(options.Proxy.AsUri());
            handler.UseProxy = true;
        }

        if (!string.IsNullOrWhiteSpace(options.Flare))
        {
            var chandler = new ClearanceHandler(options.Flare)
            {
                MaxTimeout = options.Timeout * 1000, InnerHandler = handler
            };

            if (!string.IsNullOrEmpty(options.Proxy))
            {
                chandler.ProxyUrl = options.Proxy;
            }

            var cclient = new HttpClient(chandler);
            cclient.Timeout = TimeSpan.FromSeconds(options.Timeout);

            return cclient;
        }

        var client = new HttpClient(handler);

        client.Timeout = TimeSpan.FromSeconds(options.Timeout);

        return client;
    }
}
