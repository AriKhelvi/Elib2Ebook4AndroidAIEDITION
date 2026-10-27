// ============================================================================
// E2E для Android — очистка HTML аннотаций с сайтов перед показом в интерфейсе
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using HtmlAgilityPack;

namespace E2E.Droid.Services;

/// <summary>
/// Аннотация книги — HTML с чужого сайта, а интерфейс показывает её как разметку (MarkupString).
/// Оставляем только безопасное оформление текста: никаких скриптов, обработчиков событий (onerror=…),
/// стилей, картинок и ссылок кроме http(s). Остальные теги разворачиваются — их текст сохраняется.
/// </summary>
public static class SafeHtml
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "b", "strong", "i", "em", "u", "s", "del", "sub", "sup", "span", "div",
        "ul", "ol", "li", "blockquote", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "a"
    };

    /// <summary>Вырезаются вместе с содержимым.</summary>
    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "form", "input", "button", "select", "textarea",
        "img", "svg", "video", "audio", "picture", "source", "link", "meta", "noscript", "template"
    };

    public static string Clean(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var node in doc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element).ToList())
        {
            if (node.ParentNode == null)
            {
                continue; // уже удалён вместе с родителем
            }

            if (Dropped.Contains(node.Name))
            {
                node.Remove();
                continue;
            }

            if (!Allowed.Contains(node.Name))
            {
                node.ParentNode.RemoveChild(node, keepGrandChildren: true);
                continue;
            }

            var href = node.Name.Equals("a", StringComparison.OrdinalIgnoreCase) ? node.GetAttributeValue("href", null) : null;
            node.Attributes.RemoveAll();
            if (href != null && (href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                node.SetAttributeValue("href", href);
                node.SetAttributeValue("target", "_blank");
                node.SetAttributeValue("rel", "noopener");
            }
        }

        var result = doc.DocumentNode.InnerHtml.Trim();
        return result.Length == 0 ? null : result;
    }
}
