// ============================================================================
// E2E для Android — цвета Material You (из обоев телефона) для тем интерфейса
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using AndroidX.Core.Content;

namespace E2E.Droid;

/// <summary>
/// Палитра Material You: Android 12+ строит её из обоев и отдаёт приложениям как системные цвета.
/// Интерфейс работает во встроенном браузере, который этих цветов не видит, поэтому мы читаем их здесь
/// и передаём в CSS теми же переменными, что в wwwroot/css/m3.css (--md-primary и т. д.).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Android 14+: готовые роли Material 3 — system_primary_light, system_surface_container_dark и т. п.</item>
/// <item>Android 12–13: только тональные палитры (system_accent1_600 = тон 40 и т. п.) — роли собираются из них
/// по таблице Material 3; промежуточные тона поверхностей — смешиванием соседних.</item>
/// <item>Ниже Android 12 палитры нет — остаются фиксированные синие цвета из m3.css.</item>
/// </list>
/// Цвета ищутся по имени (GetIdentifier), а не константами: так не нужны привязки под каждую версию Android,
/// а отсутствующий на прошивке цвет просто пропускается.
/// </remarks>
internal static class DynamicColors
{
    public static bool IsSupported => OperatingSystem.IsAndroidVersionAtLeast(31);

    /// <summary>CSS-переменные (--md-…) → #RRGGBB для светлой или тёмной схемы; null — палитры нет.</summary>
    public static Dictionary<string, string> GetScheme(Android.Content.Context context, bool dark)
    {
        if (!IsSupported)
        {
            return null;
        }

        var reader = new Reader(context);
        var result = new Dictionary<string, string>();
        var suffix = dark ? "dark" : "light";

        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            // Готовые роли Material 3
            foreach (var (css, role) in Roles)
            {
                reader.Put(result, css, $"system_{role}_{suffix}");
            }
        }

        // Android 12–13 (и всё, чего не нашлось среди ролей на 14+) — из тональных палитр
        foreach (var (css, light, darkTone) in Tones)
        {
            if (!result.ContainsKey(css))
            {
                reader.Put(result, css, dark ? darkTone : light);
            }
        }

        // Поверхности между тонами палитры (Material 3: светлая 98/96/94/92, тёмная 6/10/12/17/22)
        foreach (var (css, a, b, t) in dark ? DarkSurfaces : LightSurfaces)
        {
            if (!result.ContainsKey(css) && reader.Mix(a, b, t) is { } mixed)
            {
                result[css] = mixed;
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>CSS-переменная → роль Material 3 (Android 14+: system_&lt;роль&gt;_light / _dark).</summary>
    private static readonly (string Css, string Role)[] Roles =
    [
        ("--md-primary", "primary"),
        ("--md-on-primary", "on_primary"),
        ("--md-primary-container", "primary_container"),
        ("--md-on-primary-container", "on_primary_container"),
        ("--md-secondary-container", "secondary_container"),
        ("--md-on-secondary-container", "on_secondary_container"),
        ("--md-tertiary-container", "tertiary_container"),
        ("--md-on-tertiary-container", "on_tertiary_container"),
        ("--md-surface", "surface"),
        ("--md-on-surface", "on_surface"),
        ("--md-on-surface-variant", "on_surface_variant"),
        ("--md-surface-container-low", "surface_container_low"),
        ("--md-surface-container", "surface_container"),
        ("--md-surface-container-high", "surface_container_high"),
        ("--md-surface-container-highest", "surface_container_highest"),
        ("--md-outline", "outline"),
        ("--md-outline-variant", "outline_variant")
    ];

    /// <summary>
    /// CSS-переменная → цвет тональной палитры для светлой и тёмной схем.
    /// Номера — «100 − тон×10»: _0 = тон 100, _50 = 95, _100 = 90, _200 = 80 … _900 = 10, _1000 = 0.
    /// </summary>
    private static readonly (string Css, string Light, string Dark)[] Tones =
    [
        ("--md-primary", "system_accent1_600", "system_accent1_200"),
        ("--md-on-primary", "system_accent1_0", "system_accent1_800"),
        ("--md-primary-container", "system_accent1_100", "system_accent1_700"),
        ("--md-on-primary-container", "system_accent1_900", "system_accent1_100"),
        ("--md-secondary-container", "system_accent2_100", "system_accent2_700"),
        ("--md-on-secondary-container", "system_accent2_900", "system_accent2_100"),
        ("--md-tertiary-container", "system_accent3_100", "system_accent3_700"),
        ("--md-on-tertiary-container", "system_accent3_900", "system_accent3_100"),
        ("--md-on-surface", "system_neutral1_900", "system_neutral1_100"),
        ("--md-on-surface-variant", "system_neutral2_700", "system_neutral2_200"),
        ("--md-outline", "system_neutral2_500", "system_neutral2_400"),
        ("--md-outline-variant", "system_neutral2_200", "system_neutral2_700"),
        ("--md-inverse-surface", "system_neutral1_800", "system_neutral1_100"),
        ("--md-inverse-on-surface", "system_neutral1_50", "system_neutral1_800"),
        ("--md-inverse-primary", "system_accent1_200", "system_accent1_600")
    ];

    /// <summary>Поверхность = смесь двух тонов палитры (доля второго — t).</summary>
    private static readonly (string Css, string A, string B, double T)[] LightSurfaces =
    [
        ("--md-surface", "system_neutral1_10", "system_neutral1_50", 0.25),              // 98
        ("--md-surface-container-low", "system_neutral1_10", "system_neutral1_50", 0.75), // 96
        ("--md-surface-container", "system_neutral1_50", "system_neutral1_100", 0.2),     // 94
        ("--md-surface-container-high", "system_neutral1_50", "system_neutral1_100", 0.6),// 92
        ("--md-surface-container-highest", "system_neutral1_100", "system_neutral1_100", 0) // 90
    ];

    private static readonly (string Css, string A, string B, double T)[] DarkSurfaces =
    [
        ("--md-surface", "system_neutral1_900", "system_neutral1_1000", 0.4),               // 6
        ("--md-surface-container-low", "system_neutral1_900", "system_neutral1_900", 0),    // 10
        ("--md-surface-container", "system_neutral1_900", "system_neutral1_800", 0.2),      // 12
        ("--md-surface-container-high", "system_neutral1_900", "system_neutral1_800", 0.7), // 17
        ("--md-surface-container-highest", "system_neutral1_800", "system_neutral1_700", 0.2) // 22
    ];

    private sealed class Reader(Android.Content.Context context)
    {
        private readonly Android.Content.Res.Resources _resources = context.Resources;

        public void Put(Dictionary<string, string> target, string css, string name)
        {
            if (Get(name) is { } color)
            {
                target[css] = Hex(color);
            }
        }

        public string Mix(string a, string b, double t)
        {
            if (Get(a) is not { } ca || Get(b) is not { } cb)
            {
                return null;
            }

            int Channel(int shift) => (int)Math.Round(((ca >> shift) & 0xFF) * (1 - t) + ((cb >> shift) & 0xFF) * t);
            return $"#{Channel(16):X2}{Channel(8):X2}{Channel(0):X2}";
        }

        private int? Get(string name)
        {
            try
            {
#pragma warning disable CA1422 // GetIdentifier не рекомендуют ради скорости; здесь он нужен ради независимости от версии Android
                var id = _resources?.GetIdentifier(name, "color", "android") ?? 0;
#pragma warning restore CA1422
                return id == 0 ? null : ContextCompat.GetColor(context, id);
            }
            catch
            {
                return null;
            }
        }

        private static string Hex(int argb) => $"#{(argb >> 16) & 0xFF:X2}{(argb >> 8) & 0xFF:X2}{argb & 0xFF:X2}";
    }
}
