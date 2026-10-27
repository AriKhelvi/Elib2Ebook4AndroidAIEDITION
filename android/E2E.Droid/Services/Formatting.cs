// ============================================================================
// E2E для Android — мелкие помощники: размер файла, разбор ссылок из поля ввода
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

namespace E2E.Droid.Services;

public static class Formatting
{
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} МБ"
    };

    /// <summary>Число со словом в нужной форме: 1 запись, 2 записи, 5 записей, 21 запись.</summary>
    public static string Plural(int count, string one, string few, string many)
    {
        var n = Math.Abs(count) % 100;
        var word = n is >= 11 and <= 14 ? many : (n % 10) switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many
        };
        return $"{count} {word}";
    }

    /// <summary>Ссылки из поля ввода: по одной в строке, через пробел или запятую; схему https:// можно не писать.</summary>
    public static IReadOnlyList<string> ParseUrls(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split([' ', '\n', '\r', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Contains("://", StringComparison.Ordinal) ? token : "https://" + token)
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri)
                          && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                          && uri.Host.Contains('.'))
            .Distinct()
            .ToList();
    }
}
