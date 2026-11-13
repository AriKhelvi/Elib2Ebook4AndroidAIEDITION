// ============================================================================
// E2E для Android — мост JS → .NET для смены темы системных панелей
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using Microsoft.JSInterop;

namespace E2E.Droid.Services;

public static class ThemeInterop
{
    /// <summary>Вызывается из app.js при каждой смене темы (в том числе когда «как в системе» переключилась вместе с системой).</summary>
    /// <param name="dark">Тёмная тема (тёмная или AMOLED).</param>
    /// <param name="background">Цвет фона темы (#RRGGBB).</param>
    [JSInvokable("ApplyNativeTheme")]
    public static void ApplyNativeTheme(bool dark, string background)
        => MauiProgram.Services?.GetService<IPlatformServices>()?.ApplyTheme(dark, background);

    /// <summary>Повторно читает системную палитру после возвращения из фона: обои могли измениться.</summary>
    [JSInvokable("ReadDynamicPalette")]
    public static object? ReadDynamicPalette()
    {
        var platform = MauiProgram.Services?.GetService<IPlatformServices>();
        return platform?.SupportsDynamicColor == true
            ? new { light = platform.GetDynamicColorScheme(false), dark = platform.GetDynamicColorScheme(true) }
            : null;
    }
}
