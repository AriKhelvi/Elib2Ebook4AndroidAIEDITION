// ============================================================================
// E2E для Android — логгер ядра, передающий строки журнала в интерфейс
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using Microsoft.Extensions.Logging;

namespace E2E.Droid.Services;

/// <summary>Логгер ядра, передающий строки в UI (аналог ActionLogger из Elib2EbookWeb).</summary>
public sealed class CallbackLogger(Action<string> write) : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        var message = formatter(state, exception);
        if (exception != null && !message.Contains(exception.Message, StringComparison.Ordinal))
        {
            message += " " + exception.Message;
        }

        write(message);
    }
}
