// ============================================================================
// E2E для Android — логины и пароли к сайтам в зашифрованном хранилище Android
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Text.Json;

namespace E2E.Droid.Services;

public sealed record SiteCredential(string Login, string Password);

/// <summary>Логины к сайтам в зашифрованном хранилище Android (SecureStorage / Keystore).</summary>
public sealed class CredentialStore
{
    private const string Prefix = "cred:";
    private const string HostsKey = "cred-hosts";

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
            var json = await SecureStorage.Default.GetAsync(Prefix + host);
            return json == null ? null : JsonSerializer.Deserialize<SiteCredential>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(string host, SiteCredential credential)
    {
        if (string.IsNullOrEmpty(host))
        {
            return;
        }

        await SecureStorage.Default.SetAsync(Prefix + host, JsonSerializer.Serialize(credential));
        var hosts = Hosts.ToHashSet();
        if (hosts.Add(host))
        {
            Preferences.Default.Set(HostsKey, string.Join('\n', hosts.Order()));
        }
    }

    public void Remove(string host)
    {
        SecureStorage.Default.Remove(Prefix + host);
        Preferences.Default.Set(HostsKey, string.Join('\n', Hosts.Where(h => h != host)));
    }

    public IReadOnlyList<string> Hosts
        => Preferences.Default.Get(HostsKey, string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
