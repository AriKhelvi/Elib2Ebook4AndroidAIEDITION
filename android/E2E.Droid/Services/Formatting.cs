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
