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
    private const string Suffix = ".chapter.json";
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
            var path = Path.Combine(directory, key + Suffix);
            if (!File.Exists(path)) return null;
            var entry = await ReadEntryAsync(path, key, ct);
            ct.ThrowIfCancellationRequested();
            // Возраст сам по себе не требует повторного скачивания; вытесняются давно не использованные главы.
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch (Exception ex) when (IsCacheError(ex))
            { logger.LogDebug("Не обновлено время использования кэша ({Reason}).", ex.GetType().Name); }
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
            var path = Path.Combine(directory, key + Suffix);
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

    private bool Prune(int incomingBytes, string replacementPath, DateTime? incomingLastUsed = null)
    {
        var files = new List<FileInfo>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*" + Suffix))
        {
            if (file.Length > MaxEntryBytes) file.Delete();
            else if (!string.Equals(file.FullName, Path.GetFullPath(replacementPath), StringComparison.OrdinalIgnoreCase)) files.Add(file);
        }
        long size = files.Sum(file => file.Length) + incomingBytes;
        var count = files.Count + 1;
        var remove = new List<FileInfo>();
        foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
        {
            if (size <= MaxBytes && count <= MaxEntries) break;
            // Старый кэш не вытесняет более свежие главы, уже перенесённые/скачанные новой версией.
            if (incomingLastUsed.HasValue && file.LastWriteTimeUtc >= incomingLastUsed.Value) return false;
            size -= file.Length;
            count--;
            remove.Add(file);
        }
        foreach (var file in remove) file.Delete();
        // Gate не допускает параллельных записей этого приложения; это остатки прерванных попыток.
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.tmp")) file.Delete();
        return true;
    }

    /// <summary>Переносит валидные записи v1 из прежней папки; формат и ключи не зависят от версии приложения.</summary>
    public static async Task MigrateAsync(string previousDirectory, string directory, ILogger logger, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(previousDirectory) || string.IsNullOrWhiteSpace(directory)) return;
        await Gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(previousDirectory) ||
                string.Equals(Path.GetFullPath(previousDirectory), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(directory);
            var cache = new ChapterTextCache(directory, logger);
            foreach (var source in new DirectoryInfo(previousDirectory).EnumerateFiles("*" + Suffix).OrderByDescending(file => file.LastWriteTimeUtc))
            {
                ct.ThrowIfCancellationRequested();
                string temporary = null;
                try
                {
                    if ((source.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var key = source.Name[..^Suffix.Length];
                    var parts = key.Split('-');
                    if (parts.Length != 2 || parts.Any(part => part.Length != 64 || part.Any(character => !Uri.IsHexDigit(character)))) continue;
                    var entry = await ReadEntryAsync(source.FullName, key, ct);
                    var path = Path.Combine(directory, source.Name);
                    if (File.Exists(path))
                    {
                        try
                        {
                            await ReadEntryAsync(path, key, ct);
                            source.Delete(); // Действующая запись назначения всегда имеет приоритет.
                            continue;
                        }
                        catch (Exception ex) when (ex is InvalidDataException or JsonException) { }
                    }
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
                    if (bytes.Length > MaxEntryBytes) throw new InvalidDataException("неверный размер записи после переноса");
                    var lastUsed = source.LastWriteTimeUtc > DateTime.UtcNow ? DateTime.UtcNow : source.LastWriteTimeUtc;
                    if (!cache.Prune(bytes.Length, path, lastUsed))
                    {
                        source.Delete(); // Вытеснение по лимиту: более свежие главы остаются в новой папке.
                        continue;
                    }
                    temporary = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
                    await File.WriteAllBytesAsync(temporary, bytes, ct);
                    ct.ThrowIfCancellationRequested();
                    File.Move(temporary, path, true);
                    temporary = null;
                    File.SetLastWriteTimeUtc(path, lastUsed);
                    source.Delete();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (IsCacheError(ex))
                { logger.LogWarning("Старая запись кэша не перенесена ({Reason}); скачивание продолжается.", ex.GetType().Name); }
                finally
                {
                    if (temporary != null)
                    {
                        try { File.Delete(temporary); }
                        catch (Exception ex) when (IsCacheError(ex)) { }
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsCacheError(ex))
        { logger.LogWarning("Старый кэш не перенесён ({Reason}); скачивание продолжается.", ex.GetType().Name); }
        finally { Gate.Release(); }
    }

    private static async Task<Entry> ReadEntryAsync(string path, string key, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > MaxEntryBytes) throw new InvalidDataException("неверный размер записи");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        var entry = JsonSerializer.Deserialize<Entry>(bytes);
        if (entry == null || entry.Version != 1 || entry.Key != key || string.IsNullOrWhiteSpace(entry.Html) ||
            entry.Hash != Hash(entry.Html) || entry.Created > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("повреждённая запись");
        return entry;
    }

    /// <summary>Явная инвалидация книги. account — логин либо null; успешное сохранение книги кэш не удаляет.</summary>
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
