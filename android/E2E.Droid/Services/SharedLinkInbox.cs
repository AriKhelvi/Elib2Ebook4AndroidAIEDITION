// ============================================================================
// E2E для Android — приём ссылок из «Поделиться» других приложений
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Text.RegularExpressions;

namespace E2E.Droid.Services;

/// <summary>Ссылки, пришедшие через «Поделиться» из браузера или другого приложения.</summary>
public static partial class SharedLinkInbox
{
    private static string _pending;

    public static event Action Arrived;

    public static void Publish(string sharedText)
    {
        var urls = ExtractUrls(sharedText);
        if (urls.Count == 0)
        {
            return;
        }

        _pending = string.Join('\n', urls);
        Arrived?.Invoke();
    }

    /// <summary>Забирает последнюю пришедшую ссылку (или null).</summary>
    public static string Take() => Interlocked.Exchange(ref _pending, null);

    /// <summary>Браузеры часто присылают «Заголовок страницы\nhttps://...», вытаскиваем только ссылки.</summary>
    public static IReadOnlyList<string> ExtractUrls(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return UrlRegex().Matches(text)
            .Select(m => m.Value.TrimEnd('.', ',', ';', ')', '»', '"', '\''))
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
            .Distinct()
            .ToList();
    }

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
