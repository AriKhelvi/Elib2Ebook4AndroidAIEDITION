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
    public static Task<Intent> StartAsync(Intent intent)
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("Нет активного окна");
        var code = Interlocked.Increment(ref _nextCode) & 0xFFFF;
        var tcs = new TaskCompletionSource<Intent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[code] = tcs;
        try
        {
            activity.StartActivityForResult(intent, code);
        }
        catch
        {
            // Например, ActivityNotFoundException: системного диалога на устройстве нет
            Pending.TryRemove(code, out _);
            throw;
        }

        return tcs.Task;
    }

    public static void Handle(int requestCode, Result resultCode, Intent data)
    {
        if (Pending.TryRemove(requestCode, out var tcs))
        {
            tcs.TrySetResult(resultCode == Result.Ok ? data : null);
        }
    }
}
