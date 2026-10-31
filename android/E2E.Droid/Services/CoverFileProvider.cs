// ============================================================================
// E2E для Android — провайдер файлов для встроенного браузера: wwwroot + миниатюры обложек (/_covers/…)
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace E2E.Droid.Services;

/// <summary>
/// /_covers/… — из папок миниатюр (CoverThumbnails.TryResolve), всё остальное — штатному провайдеру (wwwroot в APK).
/// Подключается в AppBlazorWebView.CreateFileProvider.
/// </summary>
/// <remarks>
/// Наружу уходят только файлы миниатюр с именами, которые выдаёт приложение: books.json, настройки и прочее
/// содержимое личной папки встроенному браузеру недоступны. Не зависит от MAUI — проверяется и на ПК.
/// </remarks>
public sealed class CoverFileProvider(IFileProvider inner) : IFileProvider
{
    public IFileInfo GetFileInfo(string subpath)
    {
        if (!CoverThumbnails.TryResolve(subpath, out var path))
        {
            return inner.GetFileInfo(subpath);
        }

        var file = path == null ? null : new FileInfo(path);
        return file is { Exists: true } ? new ThumbnailFileInfo(file) : new NotFoundFileInfo(subpath);
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);

    public IChangeToken Watch(string filter) => inner.Watch(filter);

    /// <summary>Файл миниатюры для BlazorWebView: тип ответа (image/jpeg) он определяет по расширению.</summary>
    private sealed class ThumbnailFileInfo(FileInfo file) : IFileInfo
    {
        public bool Exists => true;

        public long Length => file.Length;

        public string PhysicalPath => file.FullName;

        public string Name => file.Name;

        public DateTimeOffset LastModified => file.LastWriteTimeUtc;

        public bool IsDirectory => false;

        public Stream CreateReadStream() => file.OpenRead();
    }
}
