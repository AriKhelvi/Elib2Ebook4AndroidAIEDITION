using System.Net;
using Elib2Ebook.DomainServices.Extensions;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Configs;

/// <summary>Пауза перед каждым сетевым запросом, в том числе при перенаправлении.</summary>
public sealed class RequestPolicyHandler(RequestPacer pacer, ILogger logger, HttpMessageHandler innerHandler)
    : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content != null)
            await request.Content.LoadIntoBufferAsync(cancellationToken);
        return await SendWithRedirectsAsync(request, 0, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(HttpRequestMessage request, int redirects, CancellationToken cancellationToken)
    {
        var response = await pacer.SendAsync(() => base.SendAsync(request, cancellationToken), cancellationToken);
        try
        {
            if (!IsRedirect(response.StatusCode) || response.Headers.Location == null)
            {
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    logger?.LogWarning("{Message}", response.DownloadFailureMessage());
                return response;
            }

            var location = new Uri(request.RequestUri, response.Headers.Location);
            // Как HttpClient: не следуем на небезопасный протокол и не уходим в бесконечный цикл.
            if (redirects >= 49 || location.Scheme is not ("http" or "https") ||
                (request.RequestUri.Scheme == "https" && location.Scheme == "http"))
                return response;

            using (response)
            {
                var method = request.Method;
                var preserveContent = true;
                if ((response.StatusCode == HttpStatusCode.SeeOther && method != HttpMethod.Get && method != HttpMethod.Head) ||
                    (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect && method == HttpMethod.Post))
                {
                    method = HttpMethod.Get;
                    preserveContent = false;
                }

                using var next = new HttpRequestMessage(method, location)
                {
                    Version = request.Version,
                    VersionPolicy = request.VersionPolicy
                };
                if (preserveContent && request.Content != null)
                {
                    // Собственная копия: Dispose перенаправленного запроса не закрывает тело caller.
                    next.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                    foreach (var header in request.Content.Headers)
                        next.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                var sameAuthority = request.RequestUri.Authority.Equals(location.Authority, StringComparison.OrdinalIgnoreCase);
                foreach (var header in request.Headers)
                {
                    if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                        (!sameAuthority && (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                                            header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))))
                        continue;
                    next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (method == HttpMethod.Get && method != request.Method)
                    next.Headers.TransferEncodingChunked = false;

                // Освобождаем ответ до следующего запроса, в том числе до ожидания общей паузы.
                response.Dispose();
                return await SendWithRedirectsAsync(next, redirects + 1, cancellationToken);
            }
        }
        catch
        {
            // Включает ошибку разбора Location и логгера до входа в using.
            response.Dispose();
            throw;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
