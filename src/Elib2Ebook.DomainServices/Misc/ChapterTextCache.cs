using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Misc;

/// <summary>Необязательный кэш проверенного текста. Права доступа проверяет загрузчик до чтения кэша.</summary>
public sealed class ChapterTextCache(string directory, ILogger logger)
{
    public const long MaxBytes = 64 * 1024 * 1024;
    public const int MaxEntryBytes = 2 * 1024 * 1024;
    public const int MaxEntries = 2048;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public bool Enabled => !string.IsNullOrWhiteSpace(directory);

    public async Task<string> ReadAsync(Uri book, string chapterId, string account, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enabled) return null;
        await Gate.WaitAsync(ct);
        try
        {
            var key = Key(book, chapterId, account);
            var path = Path.Combine(directory, key + ".chapter.json");
            if (!File.Exists(path)) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaxEntryBytes)
                throw new InvalidDataException("неверный размер записи");
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, ct);
            var entry = JsonSerializer.Deserialize<Entry>(bytes);
            if (entry == null || entry.Version != 1 || entry.Key != key || string.IsNullOrWhiteSpace(entry.Html) ||
                entry.Hash != Hash(entry.Html))
                throw new InvalidDataException("повреждённая запись");
            if (DateTimeOffset.UtcNow - entry.Created > Lifetime || entry.Created > DateTimeOffset.UtcNow.AddMinutes(5))
                return null;
            ct.ThrowIfCancellationRequested();
            return entry.Html;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsCacheError(ex))
        {
            logger.LogWarning("Кэш главы не прочитан ({Reason}); текст будет запрошен с сайта.", ex.GetType().Name);
            return null;
        }
        finally { Gate.Release(); }
    }

    public async Task WriteAsync(Uri book, string chapterId, string account, string html, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enabled || string.IsNullOrWhiteSpace(html)) return;
        if (Encoding.UTF8.GetByteCount(html) > MaxEntryBytes)
        {
            logger.LogWarning("Текст главы слишком велик для кэша; при повторе он будет загружен снова.");
            return;
        }
        await Gate.WaitAsync(ct);
        string temporary = null;
        try
        {
            var key = Key(book, chapterId, account);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Entry(1, key, DateTimeOffset.UtcNow, Hash(html), html));
            if (bytes.Length > MaxEntryBytes)
            {
                logger.LogWarning("Текст главы слишком велик для кэша; при повторе он будет загружен снова.");
                return;
            }
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, key + ".chapter.json");
            // Резервируем место до записи: если старые файлы удалить нельзя, кэш не растёт без предела.
            Prune(bytes.Length, path);
            temporary = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            temporary = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsCacheError(ex))
        {
            logger.LogWarning("Кэш главы не сохранён ({Reason}); скачивание продолжается.", ex.GetType().Name);
        }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (IsCacheError(ex))
                { logger.LogWarning("Временный файл кэша не удалён ({Reason}).", ex.GetType().Name); }
            }
            Gate.Release();
        }
    }

    private void Prune(int incomingBytes, string replacementPath)
    {
        var cutoff = DateTime.UtcNow - Lifetime;
        var files = new List<FileInfo>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.chapter.json"))
        {
            if (file.LastWriteTimeUtc < cutoff || file.Length > MaxEntryBytes) file.Delete();
            else if (!string.Equals(file.FullName, Path.GetFullPath(replacementPath), StringComparison.OrdinalIgnoreCase)) files.Add(file);
        }
        long size = files.Sum(file => file.Length) + incomingBytes;
        var count = files.Count + 1;
        foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
        {
            if (size <= MaxBytes && count <= MaxEntries) break;
            size -= file.Length;
            count--;
            file.Delete();
        }
        // Gate не допускает параллельных записей этого приложения; это остатки прерванных попыток.
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.tmp")) file.Delete();
    }

    /// <summary>Вызывать после успешного копирования книги. account — логин либо null для скачивания без входа.</summary>
    public static async Task ClearBookAsync(string directory, Uri book, string account, ILogger logger, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(directory)) return;
        await Gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, BookKey(book, account) + "-*.chapter.json"))
            {
                ct.ThrowIfCancellationRequested();
                File.Delete(path);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsCacheError(ex))
        { logger.LogWarning("Кэш сохранённой книги не очищен ({Reason}).", ex.GetType().Name); }
        finally { Gate.Release(); }
    }

    private static string Key(Uri book, string chapterId, string account)
        => BookKey(book, account) + "-" + Hash(chapterId);

    private static string BookKey(Uri book, string account)
    {
        // Rulate принимает ссылки на книгу, страницу оглавления и отдельную главу.
        var path = book.Segments.Length >= 3 && book.Segments[1].TrimEnd('/') == "book"
            ? "/book/" + book.Segments[2].TrimEnd('/') : book.AbsolutePath.TrimEnd('/');
        return Hash(JsonSerializer.Serialize(new[] { book.GetLeftPart(UriPartial.Authority) + path, account ?? "" }));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsCacheError(Exception ex)
        => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;

    private sealed record Entry(int Version, string Key, DateTimeOffset Created, string Hash, string Html);
}
