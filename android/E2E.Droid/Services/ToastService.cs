// ============================================================================
// E2E для Android — всплывающие сообщения внизу экрана
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

namespace E2E.Droid.Services;

public enum ToastKind
{
    Info,
    Success,
    Error
}

public sealed record Toast(int Id, string Text, ToastKind Kind);

/// <summary>Всплывающие сообщения (вместо MudBlazor Snackbar из веб-версии).</summary>
public sealed class ToastService
{
    private readonly List<Toast> _toasts = [];
    private int _nextId;

    public event Action Changed;

    public IReadOnlyList<Toast> Toasts
    {
        get
        {
            lock (_toasts)
            {
                return _toasts.ToList();
            }
        }
    }

    public void Show(string text, ToastKind kind = ToastKind.Info, int seconds = 5)
    {
        Toast toast;
        lock (_toasts)
        {
            toast = new Toast(++_nextId, text, kind);
            _toasts.Add(toast);
            if (_toasts.Count > 3)
            {
                _toasts.RemoveAt(0);
            }
        }

        Changed?.Invoke();
        _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => Dismiss(toast.Id));
    }

    public void Dismiss(int id)
    {
        lock (_toasts)
        {
            if (_toasts.RemoveAll(t => t.Id == id) == 0)
            {
                return;
            }
        }

        Changed?.Invoke();
    }
}
