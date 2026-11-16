using Elib2Ebook.ExternalServices.Litnet.Getters;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitnetSessionCacheTests
{
    [Fact]
    public async Task SuccessfulSessionExpiresWithoutSlidingOnEveryBook()
    {
        var now = DateTimeOffset.Parse("2026-11-16T00:00:00Z");
        var cache = new LitnetSessionCache(() => now, TimeSpan.FromMinutes(30));
        var calls = 0;
        Task<string> Login(CancellationToken _) => Task.FromResult("token-" + ++calls);

        var first = await cache.GetOrCreateAsync("account", Login, CancellationToken.None);
        now += TimeSpan.FromMinutes(29);
        var reused = await cache.GetOrCreateAsync("account", Login, CancellationToken.None);
        now += TimeSpan.FromMinutes(1);
        var refreshed = await cache.GetOrCreateAsync("account", Login, CancellationToken.None);

        Assert.False(first.Reused);
        Assert.True(reused.Reused);
        Assert.Equal(first.Token, reused.Token);
        Assert.False(refreshed.Reused);
        Assert.Equal("token-2", refreshed.Token);
        Assert.Equal(2, calls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task CapacityEvictsOldSessionsAndKeepsTheNewestUsable()
    {
        var now = DateTimeOffset.Parse("2026-11-16T00:00:00Z");
        var cache = new LitnetSessionCache(() => now, capacity: 2);
        var calls = 0;
        Task<string> Login(CancellationToken _) => Task.FromResult("token-" + ++calls);

        await cache.GetOrCreateAsync("first", Login, CancellationToken.None);
        now += TimeSpan.FromSeconds(1);
        await cache.GetOrCreateAsync("second", Login, CancellationToken.None);
        now += TimeSpan.FromSeconds(1);
        var third = await cache.GetOrCreateAsync("third", Login, CancellationToken.None);
        Assert.Equal(2, cache.Count);

        var reusedThird = await cache.GetOrCreateAsync("third", Login, CancellationToken.None);
        Assert.True(reusedThird.Reused);
        Assert.Equal(third.Token, reusedThird.Token);
        var recreatedFirst = await cache.GetOrCreateAsync("first", Login, CancellationToken.None);
        Assert.False(recreatedFirst.Reused);
        Assert.Equal("token-4", recreatedFirst.Token);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task LateUnauthorizedFromAnOlderRequestCannotDeleteTheNewSession()
    {
        var cache = new LitnetSessionCache();
        var calls = 0;
        Task<string> Login(CancellationToken _) => Task.FromResult("token-" + ++calls);

        var old = await cache.GetOrCreateAsync("account", Login, CancellationToken.None);
        cache.Invalidate("account", old.Token);
        var fresh = await cache.GetOrCreateAsync("account", Login, CancellationToken.None);
        cache.Invalidate("account", old.Token);
        var reused = await cache.GetOrCreateAsync("account", Login, CancellationToken.None);

        Assert.Equal("token-2", fresh.Token);
        Assert.Equal(fresh.Token, reused.Token);
        Assert.True(reused.Reused);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UnauthorizedForOneAccountDoesNotDeleteAnotherAccountsSession()
    {
        var cache = new LitnetSessionCache();
        var calls = 0;
        Task<string> Login(CancellationToken _) => Task.FromResult("token-" + ++calls);

        var first = await cache.GetOrCreateAsync("first", Login, CancellationToken.None);
        var second = await cache.GetOrCreateAsync("second", Login, CancellationToken.None);
        cache.Invalidate("first", first.Token);
        var reused = await cache.GetOrCreateAsync("second", Login, CancellationToken.None);

        Assert.Equal(second.Token, reused.Token);
        Assert.True(reused.Reused);
        Assert.Equal(2, calls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotAbortOrDiscardOwnersSuccessfulSession()
    {
        var cache = new LitnetSessionCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<string> Login(CancellationToken _)
        {
            calls++;
            entered.TrySetResult();
            return release.Task;
        }
        using var cancelWaiter = new CancellationTokenSource();

        var owner = cache.GetOrCreateAsync("account", Login, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiter = cache.GetOrCreateAsync("account", Login, cancelWaiter.Token);
        cancelWaiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.TrySetResult("token");
        var successful = await owner.WaitAsync(TimeSpan.FromSeconds(5));
        var reused = await cache.GetOrCreateAsync("account", Login, CancellationToken.None);

        Assert.Equal("token", successful.Token);
        Assert.Equal(successful.Token, reused.Token);
        Assert.True(reused.Reused);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PreCancelledCallDoesNotStartAuthenticationOrUseAnExistingSession()
    {
        var cache = new LitnetSessionCache();
        var calls = 0;
        Task<string> Login(CancellationToken _)
        {
            calls++;
            return Task.FromResult("token");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateAsync("account", Login, cancelled.Token));
        Assert.Equal(0, calls);
        await cache.GetOrCreateAsync("account", Login, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateAsync("account", Login, cancelled.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancelledOwnerReleasesWaitersWithoutCachingAndNextAttemptCanAuthenticate()
    {
        var cache = new LitnetSessionCache();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelOwner = new CancellationTokenSource();
        var owner = cache.GetOrCreateAsync("account", token => release.Task.WaitAsync(token), cancelOwner.Token);
        var waiter = cache.GetOrCreateAsync("account", _ => throw new InvalidOperationException("Не должно быть второго входа"), CancellationToken.None);

        cancelOwner.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Equal(0, cache.Count);
        var fresh = await cache.GetOrCreateAsync("account", _ => Task.FromResult("fresh-token"), CancellationToken.None);

        Assert.False(fresh.Reused);
        Assert.Equal("fresh-token", fresh.Token);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task FailedOwnerReleasesWaitersWithoutCachingAndNextAttemptCanAuthenticate()
    {
        var cache = new LitnetSessionCache();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = cache.GetOrCreateAsync("account", _ => release.Task, CancellationToken.None);
        var waiter = cache.GetOrCreateAsync("account", _ => throw new InvalidOperationException("Не должно быть второго входа"), CancellationToken.None);

        release.SetException(new HttpRequestException("Вход отклонён"));
        await Assert.ThrowsAsync<HttpRequestException>(() => owner);
        await Assert.ThrowsAsync<HttpRequestException>(() => waiter);
        Assert.Equal(0, cache.Count);
        var fresh = await cache.GetOrCreateAsync("account", _ => Task.FromResult("fresh-token"), CancellationToken.None);

        Assert.False(fresh.Reused);
        Assert.Equal("fresh-token", fresh.Token);
        Assert.Equal(1, cache.Count);
    }
}
