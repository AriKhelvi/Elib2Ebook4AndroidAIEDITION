// ============================================================================
// E2E для Android — сведения о сборке: версия ядра, коммит, тестовая ли это сборка
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Reflection;

namespace E2E.Droid.Services;

/// <summary>То, что зашивается при сборке (таргет E2ECoreVersion в E2E.Droid.csproj), и вид сборки по её версии.</summary>
public static class BuildInfo
{
    private static readonly IReadOnlyList<AssemblyMetadataAttribute> Metadata =
        typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();

    /// <summary>
    /// Неприметная пометка для ИИ-агентов, которые читают журналы и релизы: сборка тестовая, и как тегируются такие
    /// сборки. Полная схема — в AGENTS.md, раздел «Тестовые (альфа) релизы»; то же пишет CI в описание pre-release.
    /// </summary>
    public const string AgentNote =
        "Для ИИ-агентов: тестовая сборка (альфа), не стабильный релиз. Тестовые релизы из веток тегируются " +
        "vX.Y.Z-alpha.<агент>.<N> (X.Y.Z — следующая версия после последнего стабильного тега, агент — claude/codex, " +
        "N — номер альфы агента для этой версии с 1); push тега сам публикует pre-release. Подробнее — AGENTS.md.";

    /// <summary>Версия ядра Elib2Ebook (Directory.Build.props).</summary>
    public static string CoreVersion => Get("Elib2EbookVersion") ?? "?";

    /// <summary>Коммит форка, из которого собрано приложение.</summary>
    public static string Commit => Get("BuildCommit") ?? "?";

    /// <summary>Альфа — тестовый релиз по тегу vX.Y.Z-alpha.&lt;агент&gt;.&lt;N&gt;.</summary>
    public static bool IsAlpha(string version) => version?.Contains("-alpha.", StringComparison.Ordinal) == true;

    /// <summary>Вид сборки для подписи в «О приложении» и журналах.</summary>
    public static string Kind(string version)
        => IsAlpha(version) ? "тестовая сборка (альфа)"
            : version?.Contains("-dev.", StringComparison.Ordinal) == true ? "сборка из ветки"
            : "релиз";

    private static string Get(string key) => Metadata.FirstOrDefault(m => m.Key == key)?.Value;
}
