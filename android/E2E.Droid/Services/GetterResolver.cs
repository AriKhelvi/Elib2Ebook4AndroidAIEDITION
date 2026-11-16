// ============================================================================
// E2E для Android — поиск загрузчика сайта по ссылке (замена GetterFactory ядра для Android)
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Reflection;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;

namespace E2E.Droid.Services;

/// <summary>
/// Аналог GetterFactory из ядра. Тот ищет сборки сайтов через deps.json или папку с dll,
/// а на Android сборки лежат внутри APK — поэтому грузим их по списку имён, известному на этапе сборки.
/// </summary>
public static class GetterResolver
{
    private static readonly object Sync = new();
    private static IReadOnlyList<Type> _getterTypes;
    private static IEnumerable<string> _assemblyNames = [];

    public static void Configure(IEnumerable<string> assemblyNames)
    {
        lock (Sync)
        {
            _assemblyNames = assemblyNames.ToArray();
            _getterTypes = null;
        }
    }

    public static IReadOnlyList<Type> GetterTypes
    {
        get
        {
            lock (Sync)
            {
                return _getterTypes ??= LoadGetterTypes();
            }
        }
    }

    private static List<Type> LoadGetterTypes()
    {
        var assemblies = new HashSet<Assembly>(AppDomain.CurrentDomain.GetAssemblies());
        foreach (var name in _assemblyNames)
        {
            try
            {
                assemblies.Add(Assembly.Load(new AssemblyName(name)));
            }
            catch
            {
                // сборка сайта не загрузилась — остальные сайты продолжат работать
            }
        }

        return assemblies
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    return ex.Types.Where(t => t != null);
                }
                catch
                {
                    return [];
                }
            })
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(GetterBase)))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<GetterBase> _probes;

    /// <summary>
    /// Есть ли в этой сборке загрузчик для ссылки — для подсказки «сайт не поддерживается» прямо при вставке.
    /// Загрузчики создаются с пустым конфигом: их конструкторы только запоминают его, сеть не трогается.
    /// </summary>
    public static bool IsSupported(Uri url)
    {
        lock (Sync)
        {
            if (_probes == null)
            {
                var config = new BookGetterConfig(new Options(), null, null, null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                var probes = new List<GetterBase>();
                foreach (var type in GetterTypes)
                {
                    try
                    {
                        probes.Add((GetterBase)Activator.CreateInstance(type, config)!);
                    }
                    catch
                    {
                        // загрузчик с нестандартным конструктором — для подсказки не важен
                    }
                }

                _probes = probes;
            }
        }

        return _probes.Any(p =>
        {
            try
            {
                return p.IsSameUrl(url);
            }
            catch
            {
                return false;
            }
        });
    }

    public static GetterBase Create(BookGetterConfig config, Uri url)
    {
        // Неподошедшие геттеры не диспозим: GetterBase.Dispose() освобождает общий BookGetterConfig
        foreach (var type in GetterTypes)
        {
            var getter = (GetterBase)Activator.CreateInstance(type, config)!;
            if (getter.IsSameUrl(url))
            {
                return getter;
            }
        }

        throw new ArgumentException($"Сайт {url.Host} не поддерживается");
    }

    /// <summary>Основной сайт загрузчика: его зеркала делят ограничение запросов в одном запуске.</summary>
    internal static string SiteHost(Uri url)
    {
        var site = url;
        try
        {
            // Только распознаём ссылку: Init/Authorize/Get не вызываются, сетевых запросов нет.
            using var config = new BookGetterConfig(new Options(), null, null, null,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            var getter = Create(config, url);
            if (typeof(GetterBase).GetProperty("SystemUrl", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(getter) is Uri systemUrl)
                site = systemUrl;
        }
        catch
        {
            // Неподдерживаемый адрес или загрузчик с нестандартным конструктором: сравниваем сам домен.
        }
        var host = site.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        // MangaLibOrgGetter выделен в отдельный класс, но использует тот же сайт API (SiteId=1).
        return host == "mangalib.org" ? "mangalib.me" : host;
    }
}
