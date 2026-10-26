// ============================================================================
// E2E для Android — список сайтов из README ядра, сверенный с загрузчиками
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Reflection;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;
using Microsoft.Extensions.Logging.Abstractions;

namespace E2E.Droid.Services;

/// <param name="InReadme">Сайт есть в списке README на GitHub.</param>
/// <param name="HasGetter">В ядре есть загрузчик для этого домена.</param>
public sealed record SiteEntry(string Url, string Name, bool InReadme, bool HasGetter);

/// <summary>
/// Список поддерживаемых сайтов из README.md Elib2Ebook (раздел «Поддерживаемые сайты»), сверенный с кодом:
/// README вшивается в сборку из upstream, а домены берутся из SystemUrl загрузчиков той же версии ядра.
/// </summary>
public static class SupportedSites
{
    public const string ResourceName = "Elib2Ebook.README.md";
    private const string SectionTitle = "## Поддерживаемые сайты";

    private static IReadOnlyList<string> _urls;
    private static IReadOnlyList<SiteEntry> _sites;

    /// <summary>Ссылки из README в исходном порядке.</summary>
    public static IReadOnlyList<string> Urls => _urls ??= Load();

    /// <summary>Сайты из README плюс сайты, которые ядро умеет, но в README их нет.</summary>
    public static IReadOnlyList<SiteEntry> Sites => _sites ??= Resolve(GetterResolver.GetterTypes);

    private static List<string> Load()
    {
        using var stream = typeof(SupportedSites).Assembly.GetManifestResourceStream(ResourceName);
        return stream == null ? [] : Parse(new StreamReader(stream).ReadToEnd());
    }

    public static List<string> Parse(string readme)
    {
        var start = readme.IndexOf(SectionTitle, StringComparison.Ordinal);
        if (start < 0)
        {
            return [];
        }

        var end = readme.IndexOf("\n## ", start + SectionTitle.Length, StringComparison.Ordinal);
        var section = end < 0 ? readme[start..] : readme[start..end];

        return section
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("http://", StringComparison.Ordinal) || line.StartsWith("https://", StringComparison.Ordinal))
            .Where(line => Uri.TryCreate(line, UriKind.Absolute, out _))
            .Select(line => line.TrimEnd('/'))
            .Distinct()
            .ToList();
    }

    public static List<SiteEntry> Resolve(IReadOnlyList<Type> getterTypes)
    {
        var getterHosts = GetterHosts(getterTypes);
        var readme = Urls;
        var readmeHosts = readme.Select(Key).ToHashSet();

        var result = readme
            .Select(url => new SiteEntry(url, DisplayName(url), true, getterHosts.ContainsKey(Key(url))))
            .ToList();

        result.AddRange(getterHosts
            .Where(h => !readmeHosts.Contains(h.Key))
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => new SiteEntry(h.Value, DisplayName(h.Value), false, true)));

        return result;
    }

    /// <summary>author.today, ранобэ.рф — как показывать в списке.</summary>
    public static string DisplayName(string url)
    {
        var host = new Uri(url).Host;
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static Dictionary<string, string> GetterHosts(IReadOnlyList<Type> getterTypes)
    {
        // Конструкторы геттеров только запоминают конфиг — сеть и файлы не трогаются
        var config = new BookGetterConfig(new Options(), null, null, null, NullLogger.Instance);
        var systemUrl = typeof(GetterBase).GetProperty("SystemUrl", BindingFlags.Instance | BindingFlags.NonPublic);
        var hosts = new Dictionary<string, string>();

        foreach (var type in getterTypes)
        {
            try
            {
                var getter = Activator.CreateInstance(type, config);
                if (systemUrl?.GetValue(getter) is Uri url)
                {
                    hosts.TryAdd(Key(url.ToString()), url.GetLeftPart(UriPartial.Authority));
                }
            }
            catch
            {
                // геттер без SystemUrl или с нестандартным конструктором — просто не попадёт в сверку
            }
        }

        return hosts;
    }

    private static string Key(string url)
    {
        var host = new Uri(url).IdnHost.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}
