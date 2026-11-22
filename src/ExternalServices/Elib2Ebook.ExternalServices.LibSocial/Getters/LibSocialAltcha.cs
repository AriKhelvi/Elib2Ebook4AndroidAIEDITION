using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;

namespace Elib2Ebook.ExternalServices.LibSocial.Getters;

internal static class LibSocialAltcha
{
    private const int MaxResponseBytes = 16 * 1024;
    private const int MaxNumber = 1_000_000;
    private static readonly TimeSpan SolveTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    public static async Task<string> CreatePayloadAsync(BookGetterConfig config, Uri challengeUrl)
    {
        var cancellationToken = config.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (challengeUrl == null || !challengeUrl.IsAbsoluteUri ||
            challengeUrl.Scheme != Uri.UriSchemeHttps || challengeUrl.Port != 443 ||
            !string.Equals(challengeUrl.Host, "auth.lib.social", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(challengeUrl.UserInfo))
            throw new InvalidOperationException("Недопустимый адрес проверки входа ALTCHA.");

        using var response = await config.Client.GetAsync(challengeUrl,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureDownloadSuccess();
        // При ResponseHeadersRead HttpClient.Timeout перестаёт действовать после заголовков.
        // У небольшого задания должен быть отдельный предел ожидания тела ответа.
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readTimeout = config.Client.Timeout != Timeout.InfiniteTimeSpan && config.Client.Timeout < ReadTimeout
            ? config.Client.Timeout
            : ReadTimeout;
        readCancellation.CancelAfter(readTimeout);
        Challenge challenge;
        try
        {
            challenge = await ReadChallengeAsync(response.Content, readCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && readCancellation.IsCancellationRequested)
        {
            throw new TimeoutException("Сайт не передал проверку входа ALTCHA вовремя. Повторите вход позже.");
        }
        var number = await Task.Run(() => Solve(challenge, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Подпись выдана сервером: возвращаем её и исходные поля без изменений.
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            algorithm = challenge.Algorithm,
            challenge = challenge.Hash,
            salt = challenge.Salt,
            signature = challenge.Signature,
            number
        });
        return Convert.ToBase64String(payload);
    }

    private static async Task<Challenge> ReadChallengeAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content == null || content.Headers.ContentLength > MaxResponseBytes)
            throw InvalidChallenge();

        // Лимит действует и для ответа без Content-Length, до разбора JSON.
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            length += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (length > MaxResponseBytes)
            throw InvalidChallenge();

        try
        {
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidChallenge();

            var algorithm = ReadString(root, "algorithm", 16);
            var hashLength = algorithm switch
            {
                "SHA-1" => 20,
                "SHA-256" => 32,
                "SHA-512" => 64,
                _ => throw new InvalidOperationException("Сайт использует неподдерживаемый алгоритм проверки входа ALTCHA.")
            };
            var hash = ReadString(root, "challenge", hashLength * 2);
            if (hash.Length != hashLength * 2 || hash.Any(character => !Uri.IsHexDigit(character)))
                throw InvalidChallenge();

            var salt = ReadString(root, "salt", 1024);
            var signature = ReadString(root, "signature", 2048);
            var maxNumber = MaxNumber;
            if (root.TryGetProperty("maxnumber", out var max) &&
                (max.ValueKind != JsonValueKind.Number || !max.TryGetInt32(out maxNumber) ||
                 maxNumber < 0 || maxNumber > MaxNumber))
                throw InvalidChallenge();

            return new Challenge(algorithm, hash, salt, signature, maxNumber);
        }
        catch (JsonException)
        {
            // Ошибка JSON может содержать фрагменты ответа; не включаем её в журнал.
            throw InvalidChallenge();
        }
    }

    private static string ReadString(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw InvalidChallenge();
        var value = property.GetString();
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
            throw InvalidChallenge();
        return value;
    }

    private static int Solve(Challenge challenge, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var saltBytes = Encoding.UTF8.GetBytes(challenge.Salt);
        var input = new byte[saltBytes.Length + 10];
        saltBytes.CopyTo(input, 0);
        var expected = Convert.FromHexString(challenge.Hash);
        Span<byte> hash = stackalloc byte[64];

        // В ALTCHA v1 верхняя граница включена. Число записывается без локализации.
        for (var number = 0; number <= challenge.MaxNumber; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (number % 64 == 0 && Stopwatch.GetElapsedTime(started) >= SolveTimeout)
                throw new TimeoutException("Проверка входа ALTCHA не завершилась за 10 секунд. Повторите вход позже.");

            Utf8Formatter.TryFormat(number, input.AsSpan(saltBytes.Length), out var written);
            var candidate = input.AsSpan(0, saltBytes.Length + written);
            var hashLength = challenge.Algorithm switch
            {
                "SHA-1" => SHA1.HashData(candidate, hash),
                "SHA-256" => SHA256.HashData(candidate, hash),
                "SHA-512" => SHA512.HashData(candidate, hash),
                _ => throw InvalidChallenge()
            };
            if (hash[..hashLength].SequenceEqual(expected))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(started) >= SolveTimeout)
                    throw new TimeoutException("Проверка входа ALTCHA не завершилась за 10 секунд. Повторите вход позже.");
                return number;
            }
        }

        throw new InvalidOperationException("Не удалось решить проверку входа ALTCHA. Повторите вход позже.");
    }

    private static InvalidOperationException InvalidChallenge() =>
        new("Сайт вернул некорректную проверку входа ALTCHA. Повторите вход позже.");

    private sealed record Challenge(string Algorithm, string Hash, string Salt, string Signature, int MaxNumber);
}
