using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Elib2Ebook.ExternalServices.Ficbook;

/// <summary>Строка мобильного API: обычный текст или ограниченная разметка читалки.</summary>
public static partial class FicbookContent
{
    private static readonly HashSet<string> FormattingTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "span", "b", "strong", "i", "em", "u", "s", "strike", "del",
        "sub", "sup", "br", "hr", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6",
        "ul", "ol", "li", "a", "footnote"
    };

    // Токенизируем только цельные теги; неизвестные конструкции остаются буквальным текстом.
    // Линейный режим не допускает дорогого перебора на повреждённом ответе сервера.
    [GeneratedRegex("""<(?<close>/)?(?<name>[a-z][a-z0-9]*)(?:\s+(?:[^<>"']|"[^"]*"|'[^']*')*)?\s*/?>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Tags();

    public static string ToHtml(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;

        var tokens = Tags().Matches(content);
        if (!tokens.Cast<Match>().Any(token => FormattingTags.Contains(token.Groups["name"].Value)))
        {
            // Сохраняем абзацы прежнего загрузчика. Не пропускаем текст между угловыми скобками.
            var paragraphs = new StringBuilder();
            using var reader = new StringReader(content);
            while (reader.ReadLine() is { } line)
                if (!string.IsNullOrWhiteSpace(line))
                    paragraphs.Append("<p>").Append(EncodeText(line)).Append("</p>");
            return paragraphs.ToString();
        }

        var html = new StringBuilder("<div>");
        var position = 0;
        foreach (Match token in tokens)
        {
            html.Append(EncodeText(content[position..token.Index]));
            var name = token.Groups["name"].Value.ToLowerInvariant();
            if (!FormattingTags.Contains(name))
            {
                html.Append(EncodeText(token.Value));
            }
            else if (name == "footnote")
            {
                // В EPUB/FB2 примечание остаётся рядом с текстом без JS-обработчика мобильной читалки.
                html.Append(token.Groups["close"].Success ? "]</span>" : "<span>[");
            }
            else
            {
                // Все атрибуты отбрасываем: стили, обработчики, ссылки и URL загрузки не переносятся.
                if (name == "a") name = "span";
                if (name is "br" or "hr")
                {
                    if (!token.Groups["close"].Success) html.Append('<').Append(name).Append(" />");
                }
                else
                {
                    html.Append('<');
                    if (token.Groups["close"].Success) html.Append('/');
                    html.Append(name).Append('>');
                }
            }
            position = token.Index + token.Length;
        }
        return html.Append(EncodeText(content[position..])).Append("</div>").ToString();
    }

    private static string EncodeText(string value)
    {
        // Читалка 1.7.0 вставляет строку в HTML: сущности декодируются ровно один раз.
        // Повторное экранирование сохраняет буквальные <теги> и не превращает их в разметку.
        return WebUtility.HtmlEncode(WebUtility.HtmlDecode(value))
            .Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "<br />")
            .Replace("\t", "&#160;&#160;&#160;&#160;&#160;&#160;");
    }
}
