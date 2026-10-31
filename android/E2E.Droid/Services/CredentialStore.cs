// ============================================================================
// E2E для Android — логины и пароли к сайтам в зашифрованном хранилище Android
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Text.Json;
using Microsoft.Maui.Storage;

namespace E2E.Droid.Services;

public sealed record SiteCredential(string Login, string Password);

/// <summary>Логины к сайтам в зашифрованном хранилище Android (SecureStorage / Keystore).</summary>
public sealed class CredentialStore(ISecureStorage secureStorage, IPreferences preferences, ToastService toasts)
{
    private const string Prefix = "cred:";
    private const string HostsKey = "cred-hosts";
    private int _readErrorReported;

    public static string NormalizeHost(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    public async Task<SiteCredential> GetAsync(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        try
        {
            var json = await secureStorage.GetAsync(Prefix + host);
            return json == null ? null : JsonSerializer.Deserialize<SiteCredential>(json);
        }
        catch
        {
            ReportReadError();
            return null;
        }
    }

    /// <summary>false — хранилище отказало; вызывающий код не должен показывать успешное сохранение.</summary>
    public async Task<bool> SaveAsync(string host, SiteCredential credential)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        try
        {
            await secureStorage.SetAsync(Prefix + host, JsonSerializer.Serialize(credential));
            var hosts = ReadHosts().ToHashSet();
            if (hosts.Add(host))
            {
                preferences.Set(HostsKey, string.Join('\n', hosts.Order()));
            }
            return true;
        }
        catch
        {
            // Сообщения исключений платформы не показываем: в них могут оказаться данные входа.
            toasts.Show("Не удалось сохранить вход в защищённом хранилище. Данные можно использовать для текущей загрузки", ToastKind.Error, 10);
            return false;
        }
    }

    public bool Remove(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;

        try
        {
            // Отсутствующий ключ тоже можно убрать из списка, но при ошибке Keystore список сохраняем.
            secureStorage.Remove(Prefix + host);
            preferences.Set(HostsKey, string.Join('\n', ReadHosts().Where(h => h != host)));
            return true;
        }
        catch
        {
            toasts.Show("Не удалось удалить вход из защищённого хранилища. Повторите попытку после разблокировки устройства", ToastKind.Error, 10);
            return false;
        }
    }

    public IReadOnlyList<string> Hosts
    {
        get
        {
            try { return ReadHosts(); }
            catch
            {
                ReportReadError();
                return [];
            }
        }
    }

    private string[] ReadHosts()
        => preferences.Get(HostsKey, string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private void ReportReadError()
    {
        // При открытии настроек читается сразу несколько записей: одного предупреждения достаточно.
        if (Interlocked.Exchange(ref _readErrorReported, 1) == 0)
        {
            toasts.Show("Не удалось прочитать сохранённый вход. Разблокируйте устройство или введите логин и пароль заново", ToastKind.Error, 10);
        }
    }
}
