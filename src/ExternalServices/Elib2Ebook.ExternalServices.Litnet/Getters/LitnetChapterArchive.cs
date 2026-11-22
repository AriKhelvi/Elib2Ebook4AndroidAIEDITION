using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Elib2Ebook.ExternalServices.Litnet.Getters;

/// <summary>Архив официального API: ZIP → AES/CBC → пропуск первого блока → GZIP → JSON строк главы.</summary>
internal static class LitnetChapterArchive
{
    internal const int MaxArchiveBytes = 128 * 1024 * 1024;
    internal const int MaxEncryptedEntryBytes = 8 * 1024 * 1024;
    internal const int MaxDecodedEntryBytes = 32 * 1024 * 1024;
    internal const int MaxDecodedTotalBytes = 128 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<Dictionary<long, string>> ReadAsync(Stream zipStream,
        IReadOnlyCollection<long> requested, string appKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zipStream);
        ArgumentNullException.ThrowIfNull(requested);
        cancellationToken.ThrowIfCancellationRequested();
        var expected = requested.ToHashSet();
        if (expected.Count == 0 || expected.Count != requested.Count || expected.Any(id => id <= 0))
            throw Failure("Некорректный список запрошенных глав Litnet.");

        try
        {
            // Не доверяем Length/CanSeek входа: ограничение действует и для сетевого потока без длины.
            using var buffer = await ReadBoundedAsync(zipStream, MaxArchiveBytes, cancellationToken);
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
            var entries = new Dictionary<long, ZipArchiveEntry>();
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.FullName;
                if (!name.EndsWith(".gz", StringComparison.Ordinal) ||
                    !long.TryParse(name.AsSpan(0, name.Length - 3), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                    id <= 0 || name != id.ToString(CultureInfo.InvariantCulture) + ".gz")
                    throw Failure("Litnet вернул недопустимое имя записи архива глав.");
                if (!expected.Contains(id)) throw Failure($"Litnet вернул незапрошенную главу {id}.");
                if (!entries.TryAdd(id, entry)) throw Failure($"Litnet вернул главу {id} повторно.");
                if (entry.Length > MaxEncryptedEntryBytes)
                    throw Failure($"Архив главы {id} превышает допустимый размер.");
            }
            var missing = requested.Where(id => !entries.ContainsKey(id)).ToArray();
            if (missing.Length != 0)
                throw new LitnetIncompleteChapterArchiveException(expected.Count, missing);

            var result = new Dictionary<long, string>();
            long decodedTotal = 0;
            foreach (var id in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] encrypted = null;
                byte[] decrypted = null;
                byte[] key = null;
                try
                {
                    using var entryStream = entries[id].Open();
                    using var encryptedBuffer = await ReadBoundedAsync(entryStream, MaxEncryptedEntryBytes, cancellationToken);
                    encrypted = encryptedBuffer.ToArray();
                    if (encrypted.Length < 32 || encrypted.Length % 16 != 0)
                        throw Failure($"Litnet вернул некорректный шифрованный текст главы {id}.");

                    key = StrictUtf8.GetBytes(appKey ?? string.Empty);
                    using var aes = Aes.Create();
                    aes.Key = key;
                    // Совпадает с legacy Decrypt: IV — первые 16 ASCII-символов Base64 шифротекста.
                    // Первый расшифрованный блок служебный и не входит в GZIP.
                    var iv = Encoding.ASCII.GetBytes(Convert.ToBase64String(encrypted.AsSpan(0, 12)));
                    decrypted = aes.DecryptCbc(encrypted, iv, PaddingMode.PKCS7);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (decrypted.Length <= 16) throw Failure($"Litnet вернул пустой текст главы {id}.");
                    using var decryptedStream = new MemoryStream(decrypted, 16, decrypted.Length - 16, writable: false);
                    using var gzip = new GZipStream(decryptedStream, CompressionMode.Decompress);
                    var available = (int)Math.Min(MaxDecodedEntryBytes, MaxDecodedTotalBytes - decodedTotal);
                    using var decoded = await ReadBoundedAsync(gzip, available, cancellationToken);
                    decodedTotal += decoded.Length;
                    var json = StrictUtf8.GetString(decoded.GetBuffer(), 0, checked((int)decoded.Length));
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind != JsonValueKind.Array ||
                        document.RootElement.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.String))
                        throw Failure($"Litnet вернул некорректную структуру текста главы {id}.");
                    result.Add(id, json);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is InvalidDataException or IOException or CryptographicException or
                    DecoderFallbackException or JsonException or ArgumentException)
                {
                    // Не переносим исключения JSON/дешифровщика: они могут содержать серверные данные.
                    throw Failure($"Litnet вернул повреждённый архив главы {id}.");
                }
                finally
                {
                    if (encrypted != null) CryptographicOperations.ZeroMemory(encrypted);
                    if (decrypted != null) CryptographicOperations.ZeroMemory(decrypted);
                    if (key != null) CryptographicOperations.ZeroMemory(key);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException)
        {
            throw Failure("Litnet вернул повреждённый или слишком большой архив глав.");
        }
    }

    private static async Task<MemoryStream> ReadBoundedAsync(Stream source, int limit, CancellationToken cancellationToken)
    {
        var result = new MemoryStream();
        try
        {
            var chunk = new byte[32768];
            int size;
            while ((size = await source.ReadAsync(chunk, cancellationToken)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Length + size > limit) throw new InvalidDataException("Превышен допустимый размер архива глав.");
                result.Write(chunk, 0, size);
            }
            cancellationToken.ThrowIfCancellationRequested();
            result.Position = 0;
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static InvalidOperationException Failure(string message)
        => new(message + " Неполная книга не будет сохранена.");
}
