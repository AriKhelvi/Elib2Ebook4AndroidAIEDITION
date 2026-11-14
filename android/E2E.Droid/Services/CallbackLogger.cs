// ============================================================================
// E2E для Android — логгер ядра, передающий строки журнала в интерфейс
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using Microsoft.Extensions.Logging;
using Elib2Ebook.DomainServices.Misc;

namespace E2E.Droid.Services;

/// <summary>Логгер ядра, передающий строки в UI (аналог ActionLogger из Elib2EbookWeb).</summary>
public sealed class CallbackLogger(Action<string> write, Action<string> warning = null, Action<string> debug = null) : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && (logLevel >= LogLevel.Information || debug != null);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception != null && !message.Contains(exception.Message, StringComparison.Ordinal))
        {
            message += " " + exception.Message;
        }

        if (logLevel < LogLevel.Information)
        {
            // Сохраняем строки стека отдельно; секреты на продолжении заголовка тоже скрываются.
            foreach (var line in LogText.CleanLines(message.Replace("\r\n", "\n").Split('\n').Take(128)))
                debug?.Invoke(line);
            return;
        }
        message = LogText.Clean(message);
        if (logLevel >= LogLevel.Warning) warning?.Invoke(message);
        write(message);
    }
}
