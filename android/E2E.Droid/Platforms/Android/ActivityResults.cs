// ============================================================================
// E2E для Android — ожидание результата системных диалогов (выбор папки) через async/await
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Collections.Concurrent;
using Android.App;
using Android.Content;

namespace E2E.Droid;

/// <summary>Маршрутизация onActivityResult (системный выбор папки) в async-код.</summary>
internal static class ActivityResults
{
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<Intent>> Pending = new();
    private static int _nextCode = 4200;

    /// <returns>Intent с результатом или null, если пользователь отменил.</returns>
    public static async Task<Intent> StartAsync(Intent intent, TimeSpan? timeout = null)
    {
        var code = Interlocked.Increment(ref _nextCode) & 0xFFFF;
        var tcs = new TaskCompletionSource<Intent>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Pending.TryAdd(code, tcs))
        {
            throw new InvalidOperationException("Предыдущий системный диалог ещё не завершён. Повторите выбор папки");
        }

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("Нет активного окна");
                activity.StartActivityForResult(intent, code);
            });

            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Системный выбор папки не ответил за две минуты. Закройте диалог и повторите выбор");
        }
        finally
        {
            // Освобождаем ожидание и при ошибке запуска, и при потерянном ответе Android.
            // Поздний результат истёкшего запроса Handle просто проигнорирует.
            Pending.TryRemove(code, out _);
        }
    }

    public static void Handle(int requestCode, Result resultCode, Intent data)
    {
        if (Pending.TryRemove(requestCode, out var tcs))
        {
            tcs.TrySetResult(resultCode == Result.Ok ? data : null);
        }
    }
}
