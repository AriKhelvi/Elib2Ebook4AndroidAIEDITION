using System.Net;
using System.Text;
using System.Text.Json;
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
                await Task.Delay(GetTimeout(errorTimeout));
            }
        }

        if (lastEx != null)
        {
            throw lastEx;
        }

        return null;
    }

    public static async Task<HttpResponseMessage> SendWithTriesAsync(this HttpClient client, Func<HttpRequestMessage> message, TimeSpan errorTimeout = default)
    {
        Exception lastEx = null;

        for (var i = 0; i < MAX_TRY_COUNT; i++)
        {
            try
            {
                var response = await client.SendAsync(message());

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
                await Task.Delay(GetTimeout(errorTimeout));
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
            throw new Exception($"Не удалось получить ответ от {url}");
        }

        response.EnsureDownloadSuccess();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);

        var payload = Encoding.UTF8.GetString(memoryStream.ToArray());
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new Exception($"Ответ {url} пустой");
        }

        if (LooksLikeHtml(payload))
        {
            throw new Exception(
                $"Сайт вернул HTML вместо JSON по адресу {url}. Возможно сработала защита (DDoS-Guard). Попробуйте увеличить параметры --delay/--timeout или настроить --flare.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new Exception($"Сайт вернул пустой JSON по адресу {url}");
        }
        catch (JsonException ex)
        {
            throw new Exception($"Не удалось разобрать JSON ответа {url}. {ex.Message}. {FormatSnippet(payload)}", ex);
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

    private static string FormatSnippet(string value, int maxLength = 300)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (LooksLikeHtml(trimmed))
        {
            return string.Empty;
        }

        if (trimmed.Length > maxLength)
        {
            trimmed = trimmed[..maxLength] + "…";
        }

        return $" Ответ сервера: {trimmed}";
    }
}
