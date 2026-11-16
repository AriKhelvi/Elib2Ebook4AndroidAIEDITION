using System.Diagnostics;
using System.Net;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Misc;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Configs;

/// <summary>Пауза перед каждым сетевым запросом, в том числе при перенаправлении.</summary>
public sealed class RequestPolicyHandler(RequestPacer pacer, ILogger logger, HttpMessageHandler innerHandler)
    : DelegatingHandler(innerHandler)
{
    private long _diagnosticRequestId;
    private static readonly HttpRequestOptionsKey<Uri> ExternalImageOrigin = new("Elib2Ebook.ExternalImageOrigin");
    private static readonly HttpRequestOptionsKey<ImageRequestBudget> OptionalImageTimeout = new("Elib2Ebook.OptionalImageTimeout");

    internal static void SetOptionalImageTimeout(HttpRequestMessage request, ImageRequestBudget budget)
        => request.Options.Set(OptionalImageTimeout, budget);

    // Один бюджет на изображение, включая повторы и перенаправления. Очередь между
    // запросами не учитывается, поэтому длинная пользовательская задержка не мешает загрузке.
    internal sealed class ImageRequestBudget(TimeSpan timeout)
    {
        internal TimeSpan Remaining { get; private set; } = timeout;
        internal void Spend(TimeSpan elapsed) => Remaining -= elapsed;
    }

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
        var trace = logger?.IsEnabled(LogLevel.Debug) == true;
        var requestId = trace ? Interlocked.Increment(ref _diagnosticRequestId) : 0;
        var started = trace ? Stopwatch.GetTimestamp() : 0;
        var networkStarted = 0L;
        if (trace)
            logger.LogDebug("HTTP #{RequestId} начало: method={Method}; url={Url}; redirect={Redirect}; request_type={Type}; request_bytes={Bytes}",
                requestId, DownloadDiagnostics.Method(request.Method), DownloadDiagnostics.SafeUri(request.RequestUri), redirects,
                DownloadDiagnostics.MediaType(request.Content), request.Content?.Headers.ContentLength);
        HttpResponseMessage response;
        try
        {
            response = await pacer.SendAsync(() =>
            {
                if (trace)
                {
                    networkStarted = Stopwatch.GetTimestamp();
                    logger.LogDebug("HTTP #{RequestId} отправка: queue_ms={Milliseconds}", requestId,
                        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                return SendNetworkAsync(request, cancellationToken);
            }, cancellationToken);
        }
        catch (Exception error)
        {
            if (trace)
                logger.LogDebug("HTTP #{RequestId} ошибка: elapsed_ms={Elapsed}; network_ms={Network}; error={Error}; hresult={HResult}; cancelled={Cancelled}; status={Status}",
                    requestId, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    networkStarted == 0 ? 0 : (long)Stopwatch.GetElapsedTime(networkStarted).TotalMilliseconds,
                    error.GetType().Name, error.HResult, cancellationToken.IsCancellationRequested,
                    error is HttpRequestException http && http.StatusCode.HasValue ? (int)http.StatusCode.Value : 0);
            throw;
        }
        try
        {
            if (trace)
            {
                DownloadDiagnostics.Register(response, logger);
                // Не читаем тело: важны потоковое чтение, отмена и отсутствие дополнительного сетевого ожидания.
                logger.LogDebug("HTTP #{RequestId} заголовки: status={Status}; elapsed_ms={Elapsed}; network_ms={Network}; version={Version}; content_type={Type}; content_bytes={Bytes}; retry_after={RetryAfter}",
                    requestId, (int)response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    (long)Stopwatch.GetElapsedTime(networkStarted).TotalMilliseconds, response.Version,
                    DownloadDiagnostics.MediaType(response.Content), response.Content?.Headers.ContentLength, DownloadDiagnostics.RetryAfter(response));
            }
            if (!IsRedirect(response.StatusCode) || response.Headers.Location == null)
            {
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    // HTTP-отказ может быть штатным промежуточным ответом: обновление сессии,
                    // другой разрешённый маршрут файла. Только загрузчик знает итог операции.
                    logger?.LogDebug("{Message}", response.DownloadFailureMessage());
                return response;
            }

            var location = new Uri(request.RequestUri, response.Headers.Location);
            if (trace)
                logger.LogDebug("HTTP #{RequestId} перенаправление: status={Status}; next={Url}; count={Count}",
                    requestId, (int)response.StatusCode, DownloadDiagnostics.SafeUri(location), redirects + 1);
            // Как HttpClient: не следуем на небезопасный протокол и не уходим в бесконечный цикл.
            if (redirects >= 49 || location.Scheme is not ("http" or "https") ||
                (request.RequestUri.Scheme == "https" && location.Scheme == "http"))
            {
                if (trace) logger.LogDebug("HTTP #{RequestId} перенаправление остановлено: недопустимый протокол или предел переходов", requestId);
                return response;
            }

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
            {
                if (trace) logger.LogDebug("HTTP #{RequestId} перенаправление остановлено: тело запроса не пересылается другому origin", requestId);
                return response;
            }

            using (response)
            {
                using var next = new HttpRequestMessage(method, location)
                {
                    Version = request.Version,
                    VersionPolicy = request.VersionPolicy
                };
                if (request.Options.TryGetValue(OptionalImageTimeout, out var imageTimeout))
                    SetOptionalImageTimeout(next, imageTimeout);
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

    private async Task<HttpResponseMessage> SendNetworkAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(OptionalImageTimeout, out var budget))
            return await base.SendAsync(request, cancellationToken);

        // Пауза очереди уже прошла. Ограничиваем и ожидание заголовков, и чтение
        // картинки: один зависший внешний хост не должен останавливать текстовую книгу.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.Remaining <= TimeSpan.Zero)
            throw new TaskCanceledException("Истекло время загрузки изображения.", new TimeoutException());
        deadline.CancelAfter(budget.Remaining);
        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage response = null;
        try
        {
            response = await base.SendAsync(request, deadline.Token);
            logger?.LogDebug("Картинка: получены заголовки; url={Url}; status={Status}; content_type={Type}; content_bytes={Bytes}; remaining_ms={Remaining}",
                DownloadDiagnostics.SafeUri(request.RequestUri), (int)response.StatusCode,
                DownloadDiagnostics.MediaType(response.Content), response.Content.Headers.ContentLength,
                Math.Max(0, (long)(budget.Remaining - Stopwatch.GetElapsedTime(started)).TotalMilliseconds));
            // Ошибки (особенно 429) и перенаправления обрабатываются сразу, без чтения тела.
            // HttpClient обычно буферизует тело сам; здесь нужен тот же бюджет на его чтение.
            if (response.IsSuccessStatusCode)
                await response.Content.LoadIntoBufferAsync(deadline.Token);
            return response;
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw new TaskCanceledException("Истекло время загрузки изображения.", new TimeoutException(null, error), deadline.Token);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
        finally { budget.Spend(Stopwatch.GetElapsedTime(started)); }
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
