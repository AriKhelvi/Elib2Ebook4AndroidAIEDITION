// ============================================================================
// E2E для Android — действия с записью о книге: скачать заново, поделиться, обложка для экрана
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

namespace E2E.Droid.Services;

/// <summary>Общие действия вкладки «Книги» и экрана книги.</summary>
public sealed class BookActions(
    DownloadManager downloads,
    CredentialStore credentials,
    FormState form,
    AppSettings settings,
    IBookStorage storage,
    ToastService toasts)
{
    /// <summary>
    /// Скачать книгу ещё раз с теми же форматом и настройками (и сохранённым логином для сайта, если он есть).
    /// </summary>
    /// <returns>true — загрузка началась (пора показать главный экран).</returns>
    public async Task<bool> RetryAsync(BookRecord record)
    {
        if (downloads.IsRunning)
        {
            toasts.Show("Сейчас идёт другое скачивание — повторите, когда оно закончится");
            return false;
        }

        var credential = await credentials.GetAsync(CredentialStore.NormalizeHost(record.Url));
        var formats = record.Formats.Where(FormatSupport.All.Contains).ToList();

        // Главный экран покажет, что качаем
        form.Urls = record.Url;
        await downloads.StartAsync(new DownloadRequest
        {
            Urls = [record.Url],
            Formats = formats.Count > 0 ? formats : [settings.Format],
            NoImage = record.NoImage,
            Login = credential?.Login,
            Password = credential?.Password
        });

        return downloads.IsRunning;
    }

    /// <summary>«Поделиться» всеми файлами книги, которые ещё на месте.</summary>
    public void Share(BookRecord record)
    {
        var files = record.Files.Where(storage.Exists).ToList();
        if (files.Count == 0)
        {
            toasts.Show("Файлов этой книги больше нет — скачайте её заново", ToastKind.Error);
            return;
        }

        storage.Share(files);
    }

    /// <summary>Миниатюра обложки как data:-URL (встроенный браузер не читает файлы из личной папки приложения).</summary>
    public static string CoverDataUrl(BookRecord record)
    {
        try
        {
            return !string.IsNullOrEmpty(record.CoverPath) && File.Exists(record.CoverPath)
                ? "data:image/jpeg;base64," + Convert.ToBase64String(File.ReadAllBytes(record.CoverPath))
                : null;
        }
        catch
        {
            return null;
        }
    }
}
