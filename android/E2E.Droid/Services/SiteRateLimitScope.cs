using System.Net;

namespace E2E.Droid.Services;

/// <summary>После HTTP 429 не начинает новые книги и пробы того же сайта до следующего запуска.</summary>
internal sealed class SiteRateLimitScope
{
    private readonly HashSet<string> _sites = new(StringComparer.OrdinalIgnoreCase);

    internal bool Record(string url, Exception error)
        => IsRateLimited(error) && Record(url, (int)HttpStatusCode.TooManyRequests);

    internal bool Record(string url, int? status)
    {
        if (status != (int)HttpStatusCode.TooManyRequests || SiteKey(url) is not { } site) return false;
        lock (_sites) _sites.Add(site);
        return true;
    }

    internal bool TryGetReason(string url, out string reason)
    {
        reason = null;
        if (SiteKey(url) is not { } site) return false;
        lock (_sites)
            if (!_sites.Contains(site)) return false;
        reason = $"Пропущено: {site} уже ограничил частоту запросов (HTTP 429). " +
                 "Оставшиеся книги и проверки этого сайта в текущем запуске пропускаются. Повторите позже.";
        return true;
    }

    internal static bool IsRateLimited(Exception error) => error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsRateLimited),
        { InnerException: { } nested } => IsRateLimited(nested),
        _ => false
    };

    private static string SiteKey(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? GetterResolver.SiteHost(uri) : null;
}
