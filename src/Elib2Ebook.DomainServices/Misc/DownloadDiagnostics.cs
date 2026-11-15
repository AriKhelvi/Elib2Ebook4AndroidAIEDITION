using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Misc;

/// <summary>Структурная диагностика без реквизитов доступа и текста произведений.</summary>
public static class DownloadDiagnostics
{
    private static readonly ConditionalWeakTable<HttpClient, ILogger> ClientLoggers = new();
    private static readonly ConditionalWeakTable<HttpResponseMessage, ILogger> ResponseLoggers = new();
    private static readonly HashSet<string> SafeRouteSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "v1", "v2", "v3", "api", "ru", "en", "uk", "book", "books", "reader", "readfic", "part",
        "chapter", "chapters", "content", "contents", "text", "texts", "get", "get-chapters-texts",
        "user", "users", "find-by-login", "registration", "registration-by-device", "authorization",
        "auth", "login", "logout", "download", "ajax", "ajax.php", "index.php", "list", "page"
    };
    private static readonly HashSet<string> SafeQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "book", "bookId", "chapter", "chapter_id", "chapter_ids[]", "page", "per_page", "start", "end",
        "app", "device_id", "sign", "signature", "token", "user_token", "login", "password", "sid", "uid",
        "utoken", "action", "resource", "version", "format", "type", "sort", "offset", "limit", "key", "code"
    };
    private static readonly HashSet<string> SafeMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json", "application/problem+json", "text/json", "text/html", "application/xhtml+xml",
        "text/plain", "application/xml", "text/xml", "application/octet-stream", "application/zip",
        "application/epub+zip", "image/jpeg", "image/png", "image/webp", "image/gif", "image/svg+xml"
    };

    internal static void Register(HttpClient client, ILogger logger)
    {
        if (client != null && logger != null) ClientLoggers.GetValue(client, _ => logger);
    }

    internal static void Register(HttpResponseMessage response, ILogger logger)
    {
        if (logger?.IsEnabled(LogLevel.Debug) == true) ResponseLoggers.GetValue(response, _ => logger);
    }

    /// <summary>Значения query, userinfo, fragment и произвольные сегменты пути не попадают в журнал.</summary>
    public static string SafeUri(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri) return "[адрес скрыт]";
        if (uri.Scheme is not ("http" or "https")) return "[не HTTP(S)]";
        var origin = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri.GetLeftPart(UriPartial.Authority);
        var path = uri.AbsolutePath;
        var pathCut = path.Length > 1024;
        if (pathCut) path = path[..1024];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safePath = "/" + string.Join("/", segments.Take(12).Select(segment =>
        {
            var decoded = Uri.UnescapeDataString(segment);
            return SafeRouteSegments.Contains(decoded) ? decoded.ToLowerInvariant() : "[значение]";
        }));
        if (pathCut || segments.Length > 12) safePath += "/…";
        var query = uri.Query;
        if (query.Length == 0) return origin + safePath;
        var queryCut = query.Length > 2048;
        if (queryCut) query = query[..2048];
        var fields = query.TrimStart('?').Split('&');
        var names = fields.Take(16).Select(field =>
        {
            var key = Uri.UnescapeDataString(field.Split('=', 2)[0]);
            return (SafeQueryNames.Contains(key) ? key.ToLowerInvariant() : "[параметр]") + "=[скрыто]";
        });
        return origin + safePath + "?" + string.Join("&", names) + (queryCut || fields.Length > 16 ? "&…" : "");
    }

    internal static string Method(HttpMethod method) => method?.Method switch
    {
        "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" => method.Method,
        _ => "OTHER"
    };

    internal static string MediaType(HttpContent content)
    {
        var type = content?.Headers.ContentType?.MediaType;
        return type == null ? "absent" : SafeMediaTypes.Contains(type) ? type.ToLowerInvariant() : "other";
    }

    internal static string RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        return retry?.Delta is { } delta ? $"{Math.Max(0, delta.TotalSeconds):0}s"
            : retry?.Date is { } date ? date.ToUniversalTime().ToString("O") : "absent";
    }

    internal static void Retry(HttpClient client, HttpMethod method, Uri uri, int nextAttempt, int maxAttempts,
        TimeSpan delay, HttpStatusCode? status = null, Exception exception = null)
    {
        if (!ClientLoggers.TryGetValue(client, out var logger) || !logger.IsEnabled(LogLevel.Debug)) return;
        logger.LogDebug("HTTP повтор: method={Method}; url={Url}; attempt={Attempt}/{Maximum}; wait_ms={Delay}; status={Status}; error={Error}",
            Method(method), SafeUri(uri), nextAttempt, maxAttempts, (long)delay.TotalMilliseconds,
            status.HasValue ? (int)status.Value : 0, exception?.GetType().Name ?? "none");
    }

    internal static void JsonFailure(HttpResponseMessage response, string payload, JsonException error = null)
    {
        if (!ResponseLoggers.TryGetValue(response, out var logger) || !logger.IsEnabled(LogLevel.Debug)) return;
        logger.LogDebug("HTTP разбор JSON: status={Status}; content_type={Type}; {Structure}; line={Line}; byte={Byte}",
            (int)response.StatusCode, MediaType(response.Content), DescribePayload(payload), error?.LineNumber, error?.BytePositionInLine);
    }

    /// <summary>Ограниченный анализ уже прочитанного ответа: никаких ключей, строковых значений или фрагментов HTML.</summary>
    public static string DescribePayload(string payload)
    {
        const int sampleLimit = 8192;
        if (string.IsNullOrEmpty(payload)) return "shape=empty; chars=0";
        var sample = payload.AsSpan(0, Math.Min(payload.Length, sampleLimit)).TrimStart();
        var shape = sample.Length == 0 ? "whitespace" : sample[0] switch
        {
            '<' => "html/xml", '{' => "object", '[' => "array", '"' => "string", _ => "other"
        };
        var properties = 0;
        var containers = 0;
        var complete = false;
        if (shape is "object" or "array" or "string")
        {
            try
            {
                var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(sample.ToString()));
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName) properties++;
                    if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject) containers++;
                }
                complete = payload.Length <= sampleLimit;
            }
            catch (JsonException) { /* Частичная структура тоже полезна, значения не сохраняем. */ }
        }
        var cloudflare = sample.Contains("cf-chl".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            sample.Contains("challenge-platform".AsSpan(), StringComparison.OrdinalIgnoreCase);
        var ddosGuard = sample.Contains("ddos-guard".AsSpan(), StringComparison.OrdinalIgnoreCase);
        var captcha = sample.Contains("captcha".AsSpan(), StringComparison.OrdinalIgnoreCase);
        return $"shape={shape}; chars={payload.Length}; sample_chars={sample.Length}; properties={properties}; containers={containers}; json_complete={complete}; cloudflare={cloudflare}; ddos_guard={ddosGuard}; captcha={captcha}";
    }
}
