namespace Elib2Ebook.DomainServices.Misc;

/// <summary>Данные изображений не должны попадать в журнал или уведомления.</summary>
public static class LogText
{
    public const int MaxLineLength = 2048;

    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        // Сначала ограничиваем вход: даже многомегабайтный data URL не создаст огромную копию.
        var truncated = text.Length > MaxLineLength;
        var prefix = truncated ? text[..MaxLineLength] : text;
        var embedded = prefix.IndexOf("data:", StringComparison.OrdinalIgnoreCase);
        // Base64 допускает пробелы и переносы: убираем весь остаток сообщения.
        if (embedded >= 0) prefix = prefix[..embedded] + "[встроенное изображение]";
        return truncated ? prefix[..Math.Min(prefix.Length, MaxLineLength - 1)] + "…" : prefix;
    }
}
