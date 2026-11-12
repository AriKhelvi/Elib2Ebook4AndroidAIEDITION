using System.Diagnostics;

namespace Elib2Ebook.DomainServices.Configs;

/// <summary>Общий интервал между началами запросов, включая параллельные картинки и второй клиент сайта.</summary>
public sealed class RequestPacer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<TimeSpan> _interval;
    private readonly Func<TimeSpan> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private TimeSpan? _lastStart;

    public RequestPacer(Options options)
        : this(() => GetInterval(options, Random.Shared.NextDouble())) { }

    /// <summary>Монотонные часы и ожидание можно подменить для проверки без настоящих пауз.</summary>
    public RequestPacer(Func<TimeSpan> interval, Func<TimeSpan> now = null,
        Func<TimeSpan, CancellationToken, Task> wait = null)
    {
        _interval = interval;
        _now = now ?? (() => Stopwatch.GetElapsedTime(0));
        _wait = wait ?? ((duration, token) => Task.Delay(duration, token));
    }

    public static TimeSpan GetInterval(Options options, double sample)
    {
        sample = Math.Clamp(sample, 0, 1);
        var seconds = options.DelayMode switch
        {
            1 => 0,
            2 => 0.6 + sample * 0.9,
            3 => 5 + sample * 5,
            4 => double.IsFinite(options.CustomDelaySeconds)
                ? Math.Clamp(options.CustomDelaySeconds, 0.01, 120) : 1,
            _ => Math.Max(0, options.Delay)
        };
        return TimeSpan.FromSeconds(seconds);
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_lastStart.HasValue)
            {
                var remaining = _interval() - (_now() - _lastStart.Value);
                if (remaining > TimeSpan.Zero)
                    await _wait(remaining, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _lastStart = _now();
        }
        finally
        {
            _gate.Release();
        }
    }
}
