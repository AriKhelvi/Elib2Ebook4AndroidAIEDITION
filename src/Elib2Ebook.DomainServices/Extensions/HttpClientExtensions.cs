using System.Net;
using System.Text;
using System.Text.Json;
using Elib2Ebook.DomainServices.Misc;
using HtmlAgilityPack;

namespace Elib2Ebook.DomainServices.Extensions;

public static class HttpClientExtensions
{
    private const int MAX_TRY_COUNT = 5;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    private static TimeSpan GetTimeout(TimeSpan errorTimeout)
    {
        return errorTimeout == TimeSpan.Zero ? DefaultTimeout : errorTimeout;
    }

    public static async Task<HttpResponseMessage> GetWithTriesAsync(this HttpClient client, Uri url, TimeSpan errorTimeout = default)
    {
        Exception lastEx = null;

        for (var i = 0; i < MAX_TRY_COUNT; i++)
        {
            try
            {
                var response = await client.GetAsync(url);

                // Повтор не исправляет отсутствие доступа и усугубляет ограничение частоты.
                if ((int)response.StatusCode is >= 400 and < 500)
                    return response;

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    if (i == MAX_TRY_COUNT - 1)
                    {
                        return response;
                    }

                    response.Dispose();
                    DownloadDiagnostics.Retry(client, HttpMethod.Get, url, i + 2, MAX_TRY_COUNT, GetTimeout(errorTimeout), response.StatusCode);
                    await Task.Delay(GetTimeout(errorTimeout));
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException) { throw; }
            catch (ObjectDisposedException) { throw; }
            catch (HttpRequestException ex) when (ex.StatusCode.HasValue && (int)ex.StatusCode.Value is >= 400 and < 500) { throw; }
            catch (Exception ex)
            {
                lastEx = ex;
                if (i < MAX_TRY_COUNT - 1)
                    DownloadDiagnostics.Retry(client, HttpMethod.Get, url, i + 2, MAX_TRY_COUNT, GetTimeout(errorTimeout), exception: ex);
                await Task.Delay(GetTimeout(errorTimeout));
            }
        }

        if (lastEx != null)
        {
            throw lastEx;
        }

        return null;
    }

    public static async Task<HttpResponseMessage> SendWithTriesAsync(this HttpClient client, Func<HttpRequestMessage> message,
        TimeSpan errorTimeout = default, CancellationToken cancellationToken = default,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        Exception lastEx = null;

        for (var i = 0; i < MAX_TRY_COUNT; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpRequestMessage attempted = null;
            try
            {
                attempted = message();
                var response = await client.SendAsync(attempted, completionOption, cancellationToken);

                // Повтор не исправляет отсутствие доступа и усугубляет ограничение частоты.
                if ((int)response.StatusCode is >= 400 and < 500)
                    return response;

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    if (i == MAX_TRY_COUNT - 1)
                    {
                        return response;
                    }

                    response.Dispose();
                    DownloadDiagnostics.Retry(client, attempted.Method, attempted.RequestUri, i + 2, MAX_TRY_COUNT, GetTimeout(errorTimeout), response.StatusCode);
                    await Task.Delay(GetTimeout(errorTimeout), cancellationToken);
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException) { throw; }
            catch (ObjectDisposedException) { throw; }
            catch (HttpRequestException ex) when (ex.StatusCode.HasValue && (int)ex.StatusCode.Value is >= 400 and < 500) { throw; }
            catch (Exception ex)
            {
                lastEx = ex;
                if (i < MAX_TRY_COUNT - 1)
                    DownloadDiagnostics.Retry(client, attempted?.Method, attempted?.RequestUri, i + 2, MAX_TRY_COUNT, GetTimeout(errorTimeout), exception: ex);
                if (i < MAX_TRY_COUNT - 1)
                    await Task.Delay(GetTimeout(errorTimeout), cancellationToken);
            }
        }

        if (lastEx != null)
        {
            throw lastEx;
        }

        return null;
    }

    public static async Task<HttpResponseMessage> PostWithTriesAsync(this HttpClient client, Uri url, HttpContent content, TimeSpan errorTimeout = default)
    {
        Exception lastEx = null;

        for (var i = 0; i < MAX_TRY_COUNT; i++)
        {
            try
            {
                var response = await client.PostAsync(url, content);

                // Повтор не исправляет отсутствие доступа и усугубляет ограничение частоты.
                if ((int)response.StatusCode is >= 400 and < 500)
                    return response;

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    if (i == MAX_TRY_COUNT - 1)
                    {
                        return response;
                    }

                    response.Dispose();
                    DownloadDiagnostics.Retry(client, HttpMethod.Post, url, i + 2, MAX_TRY_COUNT, GetTimeout(errorTimeout), response.StatusCode);
                    await Task.Delay(GetTimeout(errorTimeout));
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException) { throw; }
            catch (ObjectDisposedException) { throw; }
            catch (HttpRequestException ex) when (ex.StatusCode.HasValue && (int)ex.StatusCode.Value is >= 400 and < 500) { throw; }
            catch (Exception ex)
            {
                lastEx = ex;
                if (i < MAX_TRY_COUNT - 1)
                    DownloadDiagnostics.Retry(client, HttpMethod.Post, url, i + 2, MAX_TRY_COUNT, GetTimeout(errorTimeout), exception: ex);
                await Task.Delay(GetTimeout(errorTimeout));
            }
        }

        if (lastEx != null)
        {
            throw lastEx;
        }

        return null;
    }

    public static async Task<HtmlDocument> GetHtmlDocWithTriesAsync(this HttpClient client, Uri url, Encoding encoding = null)
    {
        using var response = await client.GetWithTriesAsync(url);
        response.EnsureDownloadSuccess();
        return (await response.Content.ReadAsStreamAsync()).AsHtmlDoc(encoding);
    }

    public static async Task<T> GetFromJsonWithTriesAsync<T>(this HttpClient client, Uri url, TimeSpan errorTimeout = default)
    {
        using var response = await client.GetWithTriesAsync(url, errorTimeout);
        if (response == null)
        {
            throw new Exception($"Не удалось получить ответ от {DownloadDiagnostics.SafeUri(url)}");
        }

        response.EnsureDownloadSuccess();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);

        var payload = Encoding.UTF8.GetString(memoryStream.ToArray());
        if (string.IsNullOrWhiteSpace(payload))
        {
            DownloadDiagnostics.JsonFailure(response, payload);
            throw new Exception($"Ответ {DownloadDiagnostics.SafeUri(url)} пустой");
        }

        if (LooksLikeHtml(payload))
        {
            DownloadDiagnostics.JsonFailure(response, payload);
            throw new Exception(
                $"Сайт вернул HTML вместо JSON по адресу {DownloadDiagnostics.SafeUri(url)}. Возможно, требуется вход или проверка доступа на сайте.");
        }

        try
        {
            var data = JsonSerializer.Deserialize<T>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (data != null) return data;
            DownloadDiagnostics.JsonFailure(response, payload);
            throw new Exception($"Сайт вернул пустой JSON по адресу {DownloadDiagnostics.SafeUri(url)}");
        }
        catch (JsonException ex)
        {
            DownloadDiagnostics.JsonFailure(response, payload, ex);
            // Message/Path и исходный JSON могут содержать реквизиты или текст книги.
            throw new Exception($"Не удалось разобрать JSON ответа {DownloadDiagnostics.SafeUri(url)}. Строка {ex.LineNumber}, байт {ex.BytePositionInLine}. {DownloadDiagnostics.DescribePayload(payload)}");
        }
    }

    public static async Task<HtmlDocument> PostHtmlDocWithTriesAsync(this HttpClient client, Uri url, HttpContent content, Encoding encoding = null)
    {
        using var response = await client.PostWithTriesAsync(url, content);
        response.EnsureDownloadSuccess();
        return (await response.Content.ReadAsStreamAsync()).AsHtmlDoc(encoding);
    }

    /// <summary>Сохраняет HTTP-код в исключении и объясняет ошибку до попытки разобрать HTML или JSON.</summary>
    public static void EnsureDownloadSuccess(this HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.DownloadFailureMessage(), null, response.StatusCode);
    }

    public static string DownloadFailureMessage(this HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter;
            double? seconds = retry?.Delta is { } delay ? delay.TotalSeconds
                : retry?.Date is { } date ? (date - DateTimeOffset.UtcNow).TotalSeconds : null;
            var when = seconds.HasValue ? $" Повторите через {Math.Max(1, Math.Ceiling(seconds.Value)):0} сек." : " Повторите позже.";
            return $"Сайт ограничил частоту запросов (HTTP 429).{when} Увеличьте задержку в настройках.";
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Сайт требует входа (HTTP 401). Проверьте сохранённые логин и пароль.",
            HttpStatusCode.Forbidden => "Сайт запретил доступ (HTTP 403). Возможны защита сайта или отсутствие доступа у аккаунта. Проверьте книгу в браузере.",
            HttpStatusCode.NotFound => "Страница или файл не найдены на сайте (HTTP 404).",
            _ => $"Сайт вернул ошибку HTTP {status} ({response.ReasonPhrase})."
        };
    }

    private static async Task<string> SafeReadAsStringAsync(HttpContent content)
    {
        try
        {
            return await content.ReadAsStringAsync();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool LooksLikeHtml(string value)
    {
        return value.TrimStart().StartsWith("<", StringComparison.Ordinal);
    }

}
