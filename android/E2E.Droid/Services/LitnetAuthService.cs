using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.Litnet;
using Microsoft.Maui.Storage;

namespace E2E.Droid.Services;

public interface ILitnetAuthorizationBrowser
{
    Task<Uri> AuthorizeAsync(LitnetAuthorizationRequest request, CancellationToken ct);
}

public sealed class LitnetAuthStorageException(string message) : InvalidOperationException(message);

/// <summary>Сеанс Litnet ID хранится отдельно от логинов и паролей остальных сайтов.</summary>
public sealed class LitnetAuthService : ILitnetSessionProvider
{
    private const string SessionKey = "litnet:oauth-session:v1";
    private const string DeviceKey = "litnet:oauth-device-id:v1";
    private readonly ISecureStorage _storage;
    private readonly IPreferences _preferences;
    private readonly ILitnetAuthorizationBrowser _browser;
    private readonly Func<HttpClient> _clientFactory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly CredentialStore _legacyCredentials;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LitnetOAuthSession _session;
    private bool _loaded;
    private bool _pendingPersistence;
    private long _version;
    private int _loginInProgress;
    private CancellationTokenSource _activeLogin;
    private bool _removeFailed;
    private string _legacyCleanupWarning;
    private string _refreshFailureToken;
    private HttpStatusCode? _refreshFailureStatus;
    private DateTimeOffset _refreshNotBefore;

    public LitnetAuthService(ISecureStorage secureStorage, IPreferences preferences, ILitnetAuthorizationBrowser browser,
        Func<HttpClient> clientFactory = null, Func<DateTimeOffset> utcNow = null, CredentialStore legacyCredentials = null)
    {
        _storage = secureStorage;
        _preferences = preferences;
        _browser = browser;
        _clientFactory = clientFactory ?? CreateHttpClient;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _legacyCredentials = legacyCredentials;
    }

    public event Action Changed;
    public bool HasSession => Volatile.Read(ref _session) != null;
    public bool IsBusy => Volatile.Read(ref _loginInProgress) != 0;
    public bool NeedsSignOutRetry => Volatile.Read(ref _removeFailed);
    public string LegacyCleanupWarning => Volatile.Read(ref _legacyCleanupWarning);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await LoadAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task<BrowserAuthSession> GetAsync(string rejectedToken, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadAsync().ConfigureAwait(false);
            if (_session == null) return null;
            if (_pendingPersistence)
            {
                await PersistAsync(_session).ConfigureAwait(false);
                _pendingPersistence = false;
            }
            var current = _session;
            // Другой запрос уже обновил сеанс или пользователь сменил аккаунт.
            if (rejectedToken != null && !string.Equals(rejectedToken, current.AccessToken, StringComparison.Ordinal))
                return new BrowserAuthSession(current.AccessToken, current.DeviceId);
            if (rejectedToken == null && (current.ExpiresAtUtc == null || current.ExpiresAtUtc > _utcNow().AddSeconds(60)))
                return new BrowserAuthSession(current.AccessToken, current.DeviceId);
            if (_refreshFailureStatus.HasValue && _refreshFailureToken == current.AccessToken && _utcNow() < _refreshNotBefore)
                throw new HttpRequestException($"Litnet временно отклонил обновление входа (HTTP {(int)_refreshFailureStatus.Value}). Повторите попытку через минуту.", null, _refreshFailureStatus);

            LitnetOAuthSession refreshed;
            try
            {
                using var client = _clientFactory();
                refreshed = await LitnetOAuth.RefreshAsync(client, current, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                ClearUnderLock();
                throw new InvalidOperationException("Сеанс Litnet истёк. Выполните вход в Litnet заново.");
            }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                _refreshFailureToken = current.AccessToken;
                _refreshFailureStatus = error.StatusCode;
                _refreshNotBefore = _utcNow().AddMinutes(1);
                throw;
            }
            // При 403, 429 и сетевой ошибке нет повторного входа, повторного refresh или удаления сеанса.
            _session = refreshed;
            ClearRefreshFailure();
            _pendingPersistence = true;
            await PersistAsync(refreshed).ConfigureAwait(false);
            _pendingPersistence = false;
            return new BrowserAuthSession(refreshed.AccessToken, refreshed.DeviceId);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async Task SignInAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _loginInProgress, 1, 0) != 0)
            throw new InvalidOperationException("Окно входа Litnet уже открыто.");
        using var login = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            Changed?.Invoke();
            long version;
            LitnetAuthorizationRequest request;
            await _gate.WaitAsync(login.Token).ConfigureAwait(false);
            try
            {
                version = ++_version;
                _activeLogin = login;
                request = LitnetOAuth.CreateAuthorization(GetDeviceId());
            }
            finally { _gate.Release(); }

            Uri callback;
            try { callback = await _browser.AuthorizeAsync(request, login.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw new OperationCanceledException("Вход в Litnet отменён.", login.Token); }
            catch { throw new InvalidOperationException("Не удалось открыть или завершить окно входа Litnet."); }
            login.Token.ThrowIfCancellationRequested();
            var code = LitnetOAuth.ValidateCallback(request, callback);
            using var client = _clientFactory();
            var session = await LitnetOAuth.ExchangeAsync(client, request, code, login.Token).ConfigureAwait(false);

            await _gate.WaitAsync(login.Token).ConfigureAwait(false);
            try
            {
                if (version != _version) throw new OperationCanceledException("Попытка входа Litnet отменена.", login.Token);
                login.Token.ThrowIfCancellationRequested();
                // Старый аккаунт остаётся доступным, пока новый вход не сохранён успешно.
                await PersistAsync(session).ConfigureAwait(false);
                _session = session;
                _loaded = true;
                _pendingPersistence = false;
                _removeFailed = false;
                ClearRefreshFailure();
                TryRemoveLegacyCredentials();
            }
            finally { _gate.Release(); }
        }
        catch (Exception error) when (error is not (OperationCanceledException or HttpRequestException
                                                       or TimeoutException or LitnetAuthStorageException or InvalidDataException))
        {
            throw new InvalidOperationException("Не удалось завершить вход в Litnet. Повторите попытку.");
        }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { if (ReferenceEquals(_activeLogin, login)) _activeLogin = null; }
            finally { _gate.Release(); }
            Interlocked.Exchange(ref _loginInProgress, 0);
            Changed?.Invoke();
        }
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ++_version;
            _activeLogin?.Cancel();
            ClearUnderLock();
            if (_legacyCleanupWarning != null) TryRemoveLegacyCredentials();
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async Task RetryLegacyCleanupAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session != null || _legacyCleanupWarning != null) TryRemoveLegacyCredentials();
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    private async Task LoadAsync()
    {
        if (_loaded) return;
        try
        {
            var json = await _storage.GetAsync(SessionKey).ConfigureAwait(false);
            var session = json == null ? null : JsonSerializer.Deserialize<LitnetOAuthSession>(json);
            if (json != null && (session == null || string.IsNullOrWhiteSpace(session.AccessToken)
                || string.IsNullOrWhiteSpace(session.RefreshToken) || string.IsNullOrWhiteSpace(session.DeviceId)))
                throw new InvalidDataException();
            _session = session;
            _loaded = true;
            if (session != null) TryRemoveLegacyCredentials();
        }
        catch
        {
            throw new LitnetAuthStorageException("Не удалось прочитать сеанс Litnet из защищённого хранилища. Разблокируйте устройство или выполните вход заново.");
        }
    }

    private async Task PersistAsync(LitnetOAuthSession session)
    {
        try { await _storage.SetAsync(SessionKey, JsonSerializer.Serialize(session)).ConfigureAwait(false); }
        catch
        {
            throw new LitnetAuthStorageException("Не удалось сохранить сеанс Litnet в защищённом хранилище. Разблокируйте устройство и повторите попытку.");
        }
    }

    private void ClearUnderLock()
    {
        // Даже при отказе Keystore запрещаем пользоваться старым токеном в текущем процессе.
        _session = null;
        _loaded = true;
        _pendingPersistence = false;
        ClearRefreshFailure();
        try { _storage.Remove(SessionKey); _removeFailed = false; }
        catch
        {
            _removeFailed = true;
            throw new LitnetAuthStorageException("Вход Litnet отключён, но не удалось удалить сеанс из защищённого хранилища. Разблокируйте устройство и повторите выход.");
        }
    }

    private void TryRemoveLegacyCredentials()
    {
        if (_legacyCredentials == null) return;
        _legacyCleanupWarning = _legacyCredentials.Remove("litnet.com") ? null
            : "Старый пароль Litnet не удалось удалить из защищённого хранилища. Разблокируйте устройство и повторите удаление.";
    }

    private void ClearRefreshFailure()
    {
        _refreshFailureToken = null;
        _refreshFailureStatus = null;
        _refreshNotBefore = default;
    }

    private string GetDeviceId()
    {
        try
        {
            var id = _preferences.Get(DeviceKey, string.Empty);
            if (id.Length == 16 && id.All(Uri.IsHexDigit)) return id.ToLowerInvariant();
            id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            _preferences.Set(DeviceKey, id);
            return id;
        }
        catch
        {
            throw new LitnetAuthStorageException("Не удалось сохранить идентификатор для входа Litnet. Повторите попытку после разблокировки устройства.");
        }
    }

    private static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
        // Системная проверка TLS обязательна: настройка AllowInsecureTls здесь не применяется.
    }) { Timeout = TimeSpan.FromSeconds(30) };
}
