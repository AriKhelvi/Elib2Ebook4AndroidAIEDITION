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
/// <param name="HasGetter">В этой сборке есть загрузчик для этого домена.</param>
/// <param name="DisabledReason">Сайт отключён как нерабочий (см. E2EDeadSite в E2E.Droid.csproj); null — не отключён.</param>
public sealed record SiteEntry(string Url, string Name, bool InReadme, bool HasGetter, string DisabledReason = null)
{
    /// <summary>Сайт можно качать: загрузчик есть и сайт не отмечен нерабочим.</summary>
    public bool Available => HasGetter && DisabledReason == null;
}

/// <summary>
/// Список сайтов: README.md Elib2Ebook (раздел «Поддерживаемые сайты»), сверенный с загрузчиками этой сборки,
/// плюс сайты, которые ядро умеет, но в README их нет. README вшивается в сборку из upstream.
/// </summary>
public static class SupportedSites
{
    public const string ResourceName = "Elib2Ebook.README.md";
    private const string SectionTitle = "## Поддерживаемые сайты";

    private static IReadOnlyList<string> _urls;
    private static IReadOnlyList<SiteEntry> _sites;
    private static IReadOnlyDictionary<string, string> _disabled = new Dictionary<string, string>();

    /// <summary>
    /// Нерабочие сайты: домен → причина. Приложение передаёт сюда список, сгенерированный из E2E.Droid.csproj
    /// (загрузчики таких сайтов в APK не попадают, а мёртвые домены живых загрузчиков просто скрываются из списка).
    /// </summary>
    public static void Configure(IEnumerable<(string Host, string Reason)> disabled)
    {
        _disabled = disabled.ToDictionary(d => Key("https://" + d.Host), d => d.Reason);
        _sites = null;
    }

    /// <summary>Ссылки из README в исходном порядке.</summary>
    public static IReadOnlyList<string> Urls => _urls ??= Load();

    /// <summary>Сайты из README плюс сайты, которые ядро умеет, но в README их нет (с пометками).</summary>
    public static IReadOnlyList<SiteEntry> Sites => _sites ??= Resolve(GetterResolver.GetterTypes);

    /// <summary>Отключённые сайты с причинами — для пояснения во вкладке «Сайты».</summary>
    public static IReadOnlyDictionary<string, string> Disabled => _disabled;

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

        // Загрузчик сам решает, его ли это ссылка (IsSameUrl): так учитываются все домены загрузчика, а не только основной.
        // Но некоторые узнают только ссылки на книги, а не главную страницу (books.yandex.ru) — поэтому годится и основной домен.
        var result = readme
            .Select(url => new SiteEntry(url, DisplayName(url), true,
                getterHosts.ContainsKey(Key(url)) || HasGetter(url), _disabled.GetValueOrDefault(Key(url))))
            .ToList();

        result.AddRange(getterHosts
            .Where(h => !readmeHosts.Contains(h.Key))
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => new SiteEntry(h.Value, DisplayName(h.Value), false, true, _disabled.GetValueOrDefault(h.Key))));

        return result;
    }

    private static bool HasGetter(string url)
    {
        try
        {
            return GetterResolver.IsSupported(new Uri(url));
        }
        catch
        {
            return false;
        }
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
