// ============================================================================
// E2E для Android — BlazorWebView, который вдобавок к wwwroot отдаёт миниатюры обложек (/_covers/…)
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using E2E.Droid.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.FileProviders;

namespace E2E.Droid;

/// <summary>
/// Обычный BlazorWebView (используется в MainPage.xaml) со своим провайдером файлов: так встроенный браузер
/// получает миниатюры обложек из личной папки приложения файлами, без data:-URL (см. CoverThumbnails,
/// CoverFileProvider). Штатный способ MAUI для статических файлов не из wwwroot — переопределить CreateFileProvider.
/// </summary>
public sealed class AppBlazorWebView : BlazorWebView
{
    public override IFileProvider CreateFileProvider(string contentRootDir)
        => new CoverFileProvider(base.CreateFileProvider(contentRootDir));
}
