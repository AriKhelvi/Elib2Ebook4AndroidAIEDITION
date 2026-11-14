using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using Elib2Ebook.DomainServices.Getters;

namespace E2E.Droid.Services;

public enum SiteDiagnosticStatus { Ok, Warning, AccessRestricted, Timeout, Error, Skipped, Cancelled, ExampleUnavailable, NetworkError }

public sealed record SiteProbeOutcome(SiteDiagnosticStatus Status, string Detail);

public sealed record SiteDiagnosticResult(SiteDiagnosticCase Case, SiteDiagnosticStatus Status, string Detail, TimeSpan Duration)
{
    public string StatusLabel => Status switch
    {
        SiteDiagnosticStatus.Ok => "OK",
        SiteDiagnosticStatus.Warning => "Предупреждение",
        SiteDiagnosticStatus.AccessRestricted => "Вход или ограничение",
        SiteDiagnosticStatus.Timeout => "Таймаут",
        SiteDiagnosticStatus.Error => "Ошибка",
        SiteDiagnosticStatus.ExampleUnavailable => "Пример недоступен",
        SiteDiagnosticStatus.NetworkError => "Ошибка сети/TLS",
        SiteDiagnosticStatus.Skipped => "Пропуск",
        _ => "Отменено"
    };
}

/// <summary>Последовательные пробы с отдельным итогом каждого примера, даже после ошибки или отмены.</summary>
public static class SiteDiagnosticBatch
{
    public static readonly TimeSpan SiteTimeout = TimeSpan.FromSeconds(90);

    public static async Task<IReadOnlyList<SiteDiagnosticResult>> RunAsync(
        IEnumerable<SiteDiagnosticCase> cases,
        Func<SiteDiagnosticCase, CancellationToken, Task<SiteProbeOutcome>> probe,
        Action<SiteDiagnosticResult> completed,
        CancellationToken cancellationToken,
        TimeSpan? siteTimeout = null)
    {
        var results = new List<SiteDiagnosticResult>();
        foreach (var item in cases)
        {
            var elapsed = Stopwatch.StartNew();
            SiteProbeOutcome outcome;
            if (cancellationToken.IsCancellationRequested)
                outcome = new(SiteDiagnosticStatus.Cancelled, "Не проверено: диагностику остановили");
            else if (!item.CanRun)
                outcome = new(SiteDiagnosticStatus.Skipped, item.SkipReason ?? "Нет проверяемого примера");
            else
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(siteTimeout ?? SiteTimeout);
                try
                {
                    // Не оставляем загрузчик работать в фоне: отмена кооперативная, ждём освобождения ресурсов.
                    outcome = await probe(item, limit.Token) ?? new(SiteDiagnosticStatus.Error, "Проверка не вернула результат");
                    if (cancellationToken.IsCancellationRequested)
                        outcome = new(SiteDiagnosticStatus.Cancelled, "Текущая проверка отменена");
                    else if (limit.IsCancellationRequested)
                        outcome = new(SiteDiagnosticStatus.Timeout, "Истёк лимит проверки; завершение отмены могло занять дополнительное время");
                }
                catch (Exception ex)
                {
                    outcome = cancellationToken.IsCancellationRequested
                        ? new(SiteDiagnosticStatus.Cancelled, "Текущая проверка отменена")
                        : limit.IsCancellationRequested || ex is OperationCanceledException or TimeoutException
                            ? new(SiteDiagnosticStatus.Timeout, "Истёк лимит проверки или сетевого запроса")
                            : ClassifyFailure(ex);
                }
            }
            var result = new SiteDiagnosticResult(item, outcome.Status, outcome.Detail, elapsed.Elapsed);
            results.Add(result);
            completed?.Invoke(result);
        }
        return results;
    }

    public static SiteProbeOutcome ClassifyFailure(Exception error)
    {
        if (error is SiteAccessException access)
            return new(SiteDiagnosticStatus.AccessRestricted, access.Reason == SiteAccessReason.AgeConfirmationRequired
                ? "Возрастное ограничение 18+: требуется вход; анонимная диагностика не использует сохранённые учётные данные"
                : "Сайт требует входа или не предоставил доступ; сохранённые учётные данные не использовались");
        if (error is OperationCanceledException or TimeoutException)
            return new(SiteDiagnosticStatus.Timeout, "Истёк лимит проверки или сетевого запроса");
        if (error is HttpRequestException http && http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return new(SiteDiagnosticStatus.AccessRestricted, $"HTTP {(int)http.StatusCode.Value}: сайт ограничил доступ; анонимная проба не доказывает дефект загрузчика");
        if (error is HttpRequestException unavailable && unavailable.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return new(SiteDiagnosticStatus.ExampleUnavailable, $"HTTP {(int)unavailable.StatusCode.Value}: пример или ресурс книги недоступен/удалён; это не доказывает поломку загрузчика или закрытие сайта");
        if (error.InnerException != null)
        {
            var nested = ClassifyFailure(error.InnerException);
            if (nested.Status != SiteDiagnosticStatus.Error) return nested;
        }
        if (error is HttpRequestException { StatusCode: null } || error is System.Security.Authentication.AuthenticationException or System.Net.Sockets.SocketException)
            return new(SiteDiagnosticStatus.NetworkError, "Запрос не завершился из-за сети/TLS; это не подтверждает ошибку сертификата. Подробности — в журнале");
        if (error.Message.Contains("авторизац", StringComparison.OrdinalIgnoreCase) ||
            error.Message.Contains("требуется вход", StringComparison.OrdinalIgnoreCase) ||
            error.Message.Contains("не предоставил доступ", StringComparison.OrdinalIgnoreCase))
            return new(SiteDiagnosticStatus.AccessRestricted, "Сайт требует входа или не предоставил доступ; сохранённые учётные данные не использовались");
        // Сообщение парсера может содержать текст страницы или данные сеанса; в таблице только тип и HTTP-код.
        return new(SiteDiagnosticStatus.Error, error is HttpRequestException request && request.StatusCode.HasValue
            ? $"HTTP {(int)request.StatusCode.Value}; подробности этапа — в журнале"
            : $"{error.GetType().Name}; подробности этапа — в журнале");
    }

    public static string DescribeSummary(IReadOnlyList<SiteDiagnosticResult> results)
    {
        int Count(SiteDiagnosticStatus status) => results.Count(result => result.Status == status);
        var skipped = Count(SiteDiagnosticStatus.Skipped);
        var cancelled = Count(SiteDiagnosticStatus.Cancelled);
        return $"Сайтов {results.Select(result => result.Case.SiteHost).Distinct(StringComparer.OrdinalIgnoreCase).Count()}, примеров {results.Count}: " +
               $"завершено проб {results.Count - skipped - cancelled}; OK {Count(SiteDiagnosticStatus.Ok)}, предупреждений {Count(SiteDiagnosticStatus.Warning)}, " +
               $"вход/ограничение {Count(SiteDiagnosticStatus.AccessRestricted)}, пример недоступен {Count(SiteDiagnosticStatus.ExampleUnavailable)}, " +
               $"сеть/TLS {Count(SiteDiagnosticStatus.NetworkError)}, таймаутов {Count(SiteDiagnosticStatus.Timeout)}, ошибок {Count(SiteDiagnosticStatus.Error)}, " +
               $"пропущено {skipped}, отменено {cancelled}";
    }

    public static IReadOnlyList<DiagnosticCheck> CheckOutput(SiteDiagnosticCase item, int chapters, IReadOnlyList<string> files, CancellationToken cancellationToken = default)
    {
        var checks = new List<DiagnosticCheck>();
        var expected = item.Images ? new[] { ".cbz" } : new[] { ".fb2", ".epub" };
        if (!item.Images) checks.Add(new(chapters > 0, $"Непустых глав: {chapters}"));
        foreach (var extension in expected)
        {
            var matches = files.Where(file => Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase)).ToArray();
            checks.Add(new(matches.Length == 1, $"{extension}: файлов {matches.Length}, ожидается 1"));
            foreach (var file in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(file) || new FileInfo(file).Length == 0)
                {
                    checks.Add(new(false, $"{extension}: файл отсутствует или пуст"));
                    continue;
                }
                if (item.Images) CheckComic(file, checks, cancellationToken);
                else checks.AddRange(DiagnosticBookChecks.CheckFile(file, chapters));
            }
        }
        return checks;
    }

    public static SiteProbeOutcome Evaluate(IReadOnlyList<DiagnosticCheck> checks, int chapters, bool hadWarnings)
    {
        if (checks.Count == 0 || checks.Any(check => !check.Passed && !check.Warning))
            return new(SiteDiagnosticStatus.Error, "Готовые файлы или содержимое не прошли проверку; см. подробности");
        if (hadWarnings || checks.Any(check => check.Warning) || chapters > 1)
            return new(SiteDiagnosticStatus.Warning, chapters > 1
                ? $"Получено глав: {chapters}, хотя запрошена одна; сайт мог вернуть книгу целиком"
                : "Файлы созданы, но есть предупреждения; см. подробности");
        return new(SiteDiagnosticStatus.Ok, "Пример скачан, файлы и содержимое проверены; полнота других книг не проверялась");
    }

    private static void CheckComic(string path, List<DiagnosticCheck> checks, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var images = archive.Entries.Where(entry => Path.GetExtension(entry.FullName).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp").ToArray();
            checks.Add(new(images.Length > 0, $"CBZ: изображений {images.Length}"));
            var invalid = 0;
            Span<byte> prefix = stackalloc byte[12];
            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = image.Open();
                var count = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
                var png = count >= 8 && prefix[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                var jpeg = count >= 3 && prefix[0] == 255 && prefix[1] == 216 && prefix[2] == 255;
                var gif = count >= 6 && (prefix[..6].SequenceEqual("GIF87a"u8) || prefix[..6].SequenceEqual("GIF89a"u8));
                var webp = count >= 12 && prefix[..4].SequenceEqual("RIFF"u8) && prefix[8..12].SequenceEqual("WEBP"u8);
                var bmp = count >= 2 && prefix[..2].SequenceEqual("BM"u8);
                if (!(png || jpeg || gif || webp || bmp)) invalid++;
            }
            checks.Add(new(invalid == 0, $"CBZ: неверных сигнатур изображений {invalid}; декодирование всех пикселей не проверялось"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            checks.Add(new(false, $"CBZ: архив не прочитан ({ex.GetType().Name})"));
        }
    }
}
