using Android.App;
using Android.Content;
using E2E.Droid;

namespace ServiceTests;

public sealed class ActivityResultsTests
{
    [Fact]
    public async Task ImmediateResultIsNotLost()
    {
        var result = new Intent();
        Platform.CurrentActivity = new Activity { Start = (_, code) => ActivityResults.Handle(code, Result.Ok, result) };
        Assert.Same(result, await ActivityResults.StartAsync(new Intent()));
    }

    [Fact]
    public async Task BackButtonReturnsNull()
    {
        Platform.CurrentActivity = new Activity { Start = (_, code) => ActivityResults.Handle(code, Result.Canceled, new Intent()) };
        Assert.Null(await ActivityResults.StartAsync(new Intent()));
    }

    [Fact]
    public async Task TimeoutDoesNotKeepRequestOrAcceptLateResponse()
    {
        var firstCode = 0;
        Platform.CurrentActivity = new Activity { Start = (_, code) => firstCode = code };
        await Assert.ThrowsAsync<TimeoutException>(() => ActivityResults.StartAsync(new Intent(), TimeSpan.Zero));

        var nextCode = 0;
        Platform.CurrentActivity.Start = (_, code) => nextCode = code;
        var next = ActivityResults.StartAsync(new Intent());
        ActivityResults.Handle(firstCode, Result.Ok, new Intent());
        Assert.False(next.IsCompleted);
        var result = new Intent();
        ActivityResults.Handle(nextCode, Result.Ok, result);
        Assert.Same(result, await next);
        Assert.Equal(0, PendingCount());
    }

    [Fact]
    public async Task LaunchFailureReleasesRequest()
    {
        Platform.CurrentActivity = new Activity { Start = (_, _) => throw new InvalidOperationException("Ошибка запуска") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ActivityResults.StartAsync(new Intent()));
        Assert.Equal(0, PendingCount());
    }

    [Fact]
    public async Task ConcurrentRequestsAreMatchedByCode()
    {
        var codes = new List<int>();
        Platform.CurrentActivity = new Activity { Start = (_, code) => codes.Add(code) };
        var first = ActivityResults.StartAsync(new Intent());
        var second = ActivityResults.StartAsync(new Intent());
        var result = new Intent();
        ActivityResults.Handle(codes[1], Result.Ok, result);
        Assert.Same(result, await second);
        Assert.False(first.IsCompleted);
        ActivityResults.Handle(codes[0], Result.Canceled, null);
        Assert.Null(await first);
    }

    private static int PendingCount()
    {
        var field = typeof(ActivityResults).GetField("Pending", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return ((System.Collections.IDictionary)field.GetValue(null)!).Count;
    }
}
