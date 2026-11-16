namespace Elib2Ebook.ExternalServices.Litnet.Getters;

/// <summary>
/// Короткий кэш успешных сеансов в памяти процесса. Не хранит реквизиты и ничего не пишет на диск.
/// Одновременные загрузки одного аккаунта ожидают один вход; неудачный вход не сохраняется.
/// </summary>
internal sealed class LitnetSessionCache
{
    private sealed record Entry(string Token, DateTimeOffset Expires);
    internal sealed record SessionResult(string Token, bool Reused);

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<string>> _pending = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;

    public LitnetSessionCache(Func<DateTimeOffset> utcNow = null, TimeSpan? lifetime = null, int capacity = 32)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _lifetime = lifetime ?? TimeSpan.FromMinutes(30);
        if (_lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                RemoveExpired();
                return _sessions.Count;
            }
        }
    }

    public async Task<SessionResult> GetOrCreateAsync(string key,
        Func<CancellationToken, Task<string>> authenticate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<string> owner = null;
        Task<string> pending;
        lock (_sync)
        {
            RemoveExpired();
            if (_sessions.TryGetValue(key, out var session))
                return new SessionResult(session.Token, true);
            if (!_pending.TryGetValue(key, out pending))
            {
                owner = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = owner.Task;
                _pending.Add(key, pending);
            }
        }

        if (owner == null)
            return new SessionResult(await pending.WaitAsync(cancellationToken), true);

        try
        {
            var token = await authenticate(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Litnet не подтвердил вход.");
            lock (_sync)
            {
                RemoveExpired();
                while (_sessions.Count >= _capacity)
                    _sessions.Remove(_sessions.MinBy(pair => pair.Value.Expires).Key);
                _sessions[key] = new Entry(token, _utcNow() + _lifetime);
                _pending.Remove(key);
                owner.SetResult(token);
            }
            return new SessionResult(token, false);
        }
        catch (Exception error)
        {
            lock (_sync)
            {
                _pending.Remove(key);
                if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    owner.SetCanceled(cancellationToken);
                else
                {
                    owner.SetException(error);
                    // Когда ожидающих нет, исключение всё равно наблюдает инициатор входа ниже.
                    _ = owner.Task.Exception;
                }
            }
            throw;
        }
    }

    public void Invalidate(string key, string token)
    {
        if (key == null || token == null) return;
        lock (_sync)
        {
            // Старый запрос не должен удалить более свежий сеанс другого экземпляра загрузчика.
            if (_sessions.TryGetValue(key, out var entry) && entry.Token == token)
                _sessions.Remove(key);
        }
    }

    private void RemoveExpired()
    {
        var now = _utcNow();
        foreach (var key in _sessions.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
            _sessions.Remove(key);
    }
}
