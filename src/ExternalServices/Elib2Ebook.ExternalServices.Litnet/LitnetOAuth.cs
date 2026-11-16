using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Elib2Ebook.ExternalServices.Litnet;

/// <summary>Данные одной попытки входа. Не записывать в журнал или обычные настройки.</summary>
public sealed record LitnetAuthorizationRequest(
    Uri AuthorizationUri, string State, string CodeVerifier, string DeviceId, DateTimeOffset CreatedAtUtc)
{
    public override string ToString() => "LitnetAuthorizationRequest { данные входа скрыты }";
}

/// <summary>Токены Litnet ID для защищённого хранилища приложения.</summary>
public sealed record LitnetOAuthSession(
    string AccessToken, string RefreshToken, string DeviceId, DateTimeOffset? ExpiresAtUtc)
{
    public override string ToString() => "LitnetOAuthSession { токены скрыты }";
}

/// <summary>
/// Вход Litnet ID через браузер и PKCE. Для HTTP нужен отдельный клиент без общих заголовков,
/// автоматических перенаправлений и cookies; не передавать клиент загрузчика книг.
/// </summary>
public static class LitnetOAuth
{
    public const string ClientId = "9fd7aff9-7443-467d-a572-3d102cfd43d6";
    public const string RedirectUri = "https://id.litnet.com/auth/l-audio/callback";
    public const string AuthorizationEndpoint = "https://id.litnet.com/oauth/authorize";
    public const string TokenEndpoint = "https://id.litnet.com/oauth/token";
    public const string RefreshEndpoint = "https://id.litnet.com/oauth/refresh-internal";

    private static readonly Uri CallbackEndpoint = new(RedirectUri);
    private const int MaxTokenResponseBytes = 256 * 1024;

    public static LitnetAuthorizationRequest CreateAuthorization(string deviceId)
    {
        ValidateRequired(deviceId, nameof(deviceId));
        var verifier = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        // Сохраняем форму state официального приложения, добавляя случайную привязку к попытке.
        var state = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            redirect_url = RedirectUri,
            @params = new { nonce = Base64Url(RandomNumberGenerator.GetBytes(32)) }
        }));
        var query = EncodeForm(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["scope"] = "name email",
            ["requirements"] = "name email",
            ["prompt"] = "choose"
        });
        return new LitnetAuthorizationRequest(new Uri(AuthorizationEndpoint + "?" + query),
            state, verifier, deviceId, DateTimeOffset.UtcNow);
    }

    /// <summary>Возвращает код только для точного адреса возврата и текущей попытки входа.</summary>
    public static string ValidateCallback(LitnetAuthorizationRequest request, Uri callbackUri)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (callbackUri == null || !callbackUri.IsAbsoluteUri
            || callbackUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(callbackUri.IdnHost, CallbackEndpoint.IdnHost, StringComparison.OrdinalIgnoreCase)
            || callbackUri.Port != CallbackEndpoint.Port
            || callbackUri.UserInfo.Length != 0 || callbackUri.Fragment.Length != 0
            || !string.Equals(callbackUri.AbsolutePath, CallbackEndpoint.AbsolutePath, StringComparison.Ordinal))
            throw new InvalidOperationException("Litnet вернул неподходящий адрес завершения входа.");

        var query = ParseCallbackQuery(callbackUri.Query);
        if (string.IsNullOrEmpty(request.State)
            || !query.TryGetValue("state", out var state)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.State), Encoding.UTF8.GetBytes(state)))
            throw new InvalidOperationException("Ответ Litnet относится к другой попытке входа. Начните вход заново.");
        if (query.ContainsKey("error"))
            throw new InvalidOperationException("Вход в Litnet отменён или отклонён.");
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code) || code.Any(char.IsControl))
            throw new InvalidOperationException("Litnet не вернул код авторизации. Начните вход заново.");
        return code;
    }

    public static Task<LitnetOAuthSession> ExchangeAsync(HttpClient client, LitnetAuthorizationRequest request,
        string code, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequired(code, nameof(code));
        ValidateRequired(request.CodeVerifier, nameof(request.CodeVerifier));
        ValidateRequired(request.DeviceId, nameof(request.DeviceId));
        return SendTokenRequestAsync(client, TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = request.CodeVerifier,
            ["code"] = code,
            ["device_id"] = request.DeviceId
        }, request.DeviceId, ct);
    }

    public static Task<LitnetOAuthSession> RefreshAsync(HttpClient client, LitnetOAuthSession session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateRequired(session.RefreshToken, nameof(session.RefreshToken));
        ValidateRequired(session.DeviceId, nameof(session.DeviceId));
        return SendTokenRequestAsync(client, RefreshEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["device_id"] = session.DeviceId,
            ["refresh_token"] = session.RefreshToken
        }, session.DeviceId, ct);
    }

    private static async Task<LitnetOAuthSession> SendTokenRequestAsync(HttpClient client, string endpoint,
        Dictionary<string, string> body, string deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        // Общий клиент может содержать Host, Cookie, Authorization или заголовки другого сайта.
        // Не изменяем его во время параллельной загрузки и не отправляем эти данные в Litnet ID.
        if (client.DefaultRequestHeaders.Any())
            throw new InvalidOperationException("Для входа Litnet требуется отдельный HTTP-клиент без общих заголовков.");
        ct.ThrowIfCancellationRequested();
        // HttpClient.Timeout при ResponseHeadersRead не распространяется на тело ответа.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (client.Timeout != Timeout.InfiniteTimeSpan) budget.CancelAfter(client.Timeout);
        byte[] json = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new FormUrlEncodedContent(body);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("Dart/3.10 (dart:io)");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw HttpFailure(response.StatusCode);
            json = await ReadTokenResponseAsync(response.Content, budget.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidResponse();
            var internalTokens = root.TryGetProperty("internal", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested : default;
            var internalAccess = ReadToken(internalTokens, "access_token");
            var access = internalAccess ?? ReadToken(root, "access_token");
            var refresh = ReadToken(internalTokens, "refresh_token") ?? ReadToken(root, "refresh_token");
            if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
                throw InvalidResponse();
            // Внешний и внутренний access_token могут иметь разный срок действия.
            var expires = ReadExpiry(internalAccess != null ? internalTokens : root);
            return new LitnetOAuthSession(access, refresh, deviceId, expires);
        }
        catch (HttpRequestException error)
        {
            // Тело ответа, URL из исключения и вложенные исключения могут содержать код или токен.
            throw HttpFailure(error.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Вход в Litnet отменён.", ct);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Litnet не ответил вовремя. Повторите вход.");
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
        catch (IOException)
        {
            throw InvalidResponse();
        }
        finally
        {
            if (json != null) CryptographicOperations.ZeroMemory(json);
        }
    }

    private static async Task<byte[]> ReadTokenResponseAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxTokenResponseBytes) throw InvalidResponse();
        await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var chunk = new byte[16384];
        int size;
        while ((size = await source.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (output.Length + size > MaxTokenResponseBytes) throw InvalidResponse();
            output.Write(chunk, 0, size);
        }
        ct.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static string ReadToken(JsonElement container, string key)
    {
        if (container.ValueKind != JsonValueKind.Object || !container.TryGetProperty(key, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidResponse();
        return value.GetString();
    }

    private static DateTimeOffset? ReadExpiry(JsonElement container)
    {
        if (!container.TryGetProperty("expires_in", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        long seconds;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric))
            seconds = numeric;
        else if (value.ValueKind == JsonValueKind.String
                 && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var text))
            seconds = text;
        else
            throw InvalidResponse();
        if (seconds < 0) throw InvalidResponse();
        try { return DateTimeOffset.UtcNow.AddSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { throw InvalidResponse(); }
    }

    private static Dictionary<string, string> ParseCallbackQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            try
            {
                var key = Uri.UnescapeDataString((separator < 0 ? pair : pair[..separator]).Replace('+', ' '));
                var value = Uri.UnescapeDataString((separator < 0 ? "" : pair[(separator + 1)..]).Replace('+', ' '));
                if (!result.TryAdd(key, value))
                    throw new InvalidOperationException("Litnet вернул неоднозначный ответ авторизации.");
            }
            catch (UriFormatException)
            {
                throw new InvalidOperationException("Litnet вернул некорректный адрес завершения входа.");
            }
        }
        return result;
    }

    private static string EncodeForm(Dictionary<string, string> values)
        => string.Join("&", values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void ValidateRequired(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new ArgumentException("Не заполнены данные для входа Litnet.", parameter);
    }

    private static InvalidDataException InvalidResponse()
        => new("Litnet вернул некорректный ответ авторизации. Повторите вход.");

    private static HttpRequestException HttpFailure(HttpStatusCode? status)
        => new(status.HasValue
            ? $"Не удалось выполнить вход Litnet (HTTP {(int)status.Value}). Повторите вход позже."
            : "Не удалось связаться с Litnet для входа. Проверьте подключение и повторите попытку.", null, status);
}
