// ============================================================================
// E2E для Android — мост JS → .NET для смены темы системных панелей
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using Microsoft.JSInterop;

namespace E2E.Droid.Services;

public static class ThemeInterop
{
    /// <summary>Вызывается из app.js, когда тема «как в системе» переключилась вместе с системой.</summary>
    [JSInvokable("ApplyNativeTheme")]
    public static void ApplyNativeTheme(bool dark)
        => MauiProgram.Services?.GetService<IPlatformServices>()?.ApplyTheme(dark);
}
