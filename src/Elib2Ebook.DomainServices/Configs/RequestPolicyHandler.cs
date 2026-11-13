using System.Net;
using Elib2Ebook.DomainServices.Extensions;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Configs;

/// <summary>Пауза перед каждым сетевым запросом, в том числе при перенаправлении.</summary>
public sealed class RequestPolicyHandler(RequestPacer pacer, ILogger logger, HttpMessageHandler innerHandler)
    : DelegatingHandler(innerHandler)
{
    private static readonly HttpRequestOptionsKey<Uri> ExternalImageOrigin = new("Elib2Ebook.ExternalImageOrigin");

    // Список разрешений намеренно не зависит от названий токенов конкретного сайта.
    private static readonly HashSet<string> CrossOriginHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Encoding", "Accept-Language", "User-Agent", "Range",
        "If-Range", "If-Match", "If-None-Match", "If-Modified-Since", "If-Unmodified-Since",
        "Cache-Control", "Pragma"
    };

    internal static void ScopeImageCredentials(HttpRequestMessage request, Uri site, Uri image)
    {
        if (!SameOrigin(site, image)) request.Options.Set(ExternalImageOrigin, image);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // HttpClient уже добавил DefaultRequestHeaders: очистка только в GetterBase недостаточна.
        if (request.Options.TryGetValue(ExternalImageOrigin, out var image))
            RemoveCrossOriginHeaders(request, image);
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

            var method = request.Method;
            var preserveContent = true;
            if ((response.StatusCode == HttpStatusCode.SeeOther && method != HttpMethod.Get && method != HttpMethod.Head) ||
                (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect && method == HttpMethod.Post))
            {
                method = HttpMethod.Get;
                preserveContent = false;
            }

            var sameOrigin = SameOrigin(request.RequestUri, location);
            // Тело входа может содержать пароль/токен. Не пересылаем его другому origin.
            if (!sameOrigin && preserveContent && request.Content != null)
                return response;

            using (response)
            {
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

                foreach (var header in request.Headers)
                {
                    if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                        (!sameOrigin && (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                                         header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))))
                        continue;
                    next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (!sameOrigin) RemoveCrossOriginHeaders(next);
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

    private static bool SameOrigin(Uri first, Uri second) =>
        first.Scheme.Equals(second.Scheme, StringComparison.OrdinalIgnoreCase) &&
        first.IdnHost.Equals(second.IdnHost, StringComparison.OrdinalIgnoreCase) && first.Port == second.Port;

    private static void RemoveCrossOriginHeaders(HttpRequestMessage request, Uri image = null)
    {
        // AuthorToday/Litnet иногда отправляют запрос на IP, сохраняя публичный Host изображения.
        // Оставлять можно только такой Host, но не Host исходного сайта из заголовков клиента.
        var routedHost = request.Headers.Host;
        var keepRoutedHost = image != null && !SameOrigin(request.RequestUri, image) && routedHost != null &&
            Uri.TryCreate($"{image.Scheme}://{routedHost}", UriKind.Absolute, out var hostUri) &&
            SameOrigin(image, hostUri);
        Uri referer = null;
        if (request.Headers.TryGetValues("Referer", out var values) && values.Count() == 1 &&
            Uri.TryCreate(values.First(), UriKind.Absolute, out var original) &&
            original.Scheme is "http" or "https")
            referer = new UriBuilder(original.Scheme, original.Host, original.Port, "/").Uri;

        foreach (var name in request.Headers.Select(header => header.Key).ToArray())
            if (!CrossOriginHeaders.Contains(name)) request.Headers.Remove(name);

        // CDN с защитой от прямых ссылок получает только origin: без пути, query, userinfo и fragment.
        if (referer != null) request.Headers.Referrer = referer;
        if (keepRoutedHost) request.Headers.Host = routedHost;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
