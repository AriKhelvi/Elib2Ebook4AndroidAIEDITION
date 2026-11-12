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
        HttpRequestMessage redirected = null;
        try
        {
            if (request.Content != null)
                await request.Content.LoadIntoBufferAsync(cancellationToken);

            for (var redirects = 0; ; redirects++)
            {
                await pacer.WaitAsync(cancellationToken);
                var response = await base.SendAsync(request, cancellationToken);
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
                var content = request.Content;
                if ((response.StatusCode == HttpStatusCode.SeeOther && method != HttpMethod.Head) ||
                    (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect && method == HttpMethod.Post))
                {
                    method = HttpMethod.Get;
                    content = null;
                }

                var next = new HttpRequestMessage(method, location) { Content = content };
                foreach (var header in request.Headers)
                {
                    if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                        (request.RequestUri.Authority != location.Authority && header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                response.Dispose();
                // Content принадлежит исходному запросу и может понадобиться для следующего 307/308.
                if (redirected != null)
                {
                    redirected.Content = null;
                    redirected.Dispose();
                }
                redirected = next;
                request = next;
            }
        }
        finally
        {
            if (redirected != null)
            {
                redirected.Content = null;
                redirected.Dispose();
            }
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
