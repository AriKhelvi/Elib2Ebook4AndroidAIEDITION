using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Elib2Ebook.DomainServices.Misc;

/// <summary>Удаление только распознанных служебных элементов, без удаления авторских подзаголовков и ссылок.</summary>
public static class ChapterContentCleaner
{
    public static void RemoveLeadingTitle(HtmlNode content, string title)
    {
        var expected = Normalize(title);
        if (expected.Length == 0) return;
        var first = FirstContent(content);
        if (first == null || !Regex.IsMatch(first.Name, "^h[1-6]$") || Normalize(first.InnerText) != expected)
            return;
        if (first.Descendants().Any(node => node.Name is "img" or "image" or "svg" or "video" or "audio" or "math" or "iframe"))
            return;
        // Ссылка с собственным адресом может быть полезной частью заголовка.
        if (first.Descendants("a").Any(node => node.GetAttributeValue("href", string.Empty) is { Length: > 0 } href && !href.StartsWith('#')))
            return;
        RemoveNodePreservingAnchors(first);
    }

    public static void RemoveTrailingSourceLink(HtmlNode content, params Uri[] sources)
    {
        var last = content.ChildNodes.LastOrDefault(node => !IsEmptyTailNode(node));
        if (last?.Name != "p" || !IsSourceUri(Normalize(last.InnerText), sources)) return;
        var elements = last.ChildNodes.Where(node => node.NodeType == HtmlNodeType.Element).ToList();
        if (elements.Count == 0 && last.ChildNodes.All(IsTextOrComment))
        {
            RemoveNodePreservingAnchors(last);
            return;
        }
        if (elements.Count == 1 && elements[0].Name == "a" && elements[0].ChildNodes.All(IsTextOrComment)
            && last.ChildNodes.Where(node => node != elements[0]).All(IsWhitespaceOrComment)
            && IsSourceUri(HtmlEntity.DeEntitize(elements[0].GetAttributeValue("href", string.Empty)).Trim(), sources))
            RemoveNodePreservingAnchors(last);
    }

    public static void RemoveNodePreservingAnchors(HtmlNode node)
    {
        if (node?.ParentNode == null) return;
        var anchors = node.DescendantsAndSelf()
            .Where(child => child.NodeType == HtmlNodeType.Element)
            .Select(child => (Id: child.GetAttributeValue("id", string.Empty), Name: child.Name == "a" ? child.GetAttributeValue("name", string.Empty) : string.Empty))
            .Where(anchor => anchor.Id.Length > 0 || anchor.Name.Length > 0).Distinct().ToList();
        foreach (var anchor in anchors)
        {
            var replacement = node.OwnerDocument.CreateElement("a");
            if (anchor.Id.Length > 0) replacement.SetAttributeValue("id", anchor.Id);
            if (anchor.Name.Length > 0) replacement.SetAttributeValue("name", anchor.Name);
            node.ParentNode.InsertBefore(replacement, node);
        }
        node.Remove();
    }

    private static HtmlNode FirstContent(HtmlNode content)
    {
        foreach (var child in content.ChildNodes)
        {
            if (IsWhitespaceOrComment(child) || child.Name is "br" or "head" or "script" or "style" or "meta" or "link") continue;
            if (child.Name is "#document" or "html" or "body" or "div" or "section" or "article" or "header")
            {
                var nested = FirstContent(child);
                if (nested != null) return nested;
                continue;
            }
            if (child.Name is "p" or "a" or "span" && child.ChildNodes.All(IsWhitespaceOrComment)) continue;
            return child;
        }
        return null;
    }

    private static string Normalize(string text)
        => Regex.Replace(HtmlEntity.DeEntitize(text ?? string.Empty), @"\s+", " ").Trim();

    private static bool IsTextOrComment(HtmlNode node) => node.NodeType is HtmlNodeType.Text or HtmlNodeType.Comment;
    private static bool IsWhitespaceOrComment(HtmlNode node)
        => node.NodeType == HtmlNodeType.Comment || node.NodeType == HtmlNodeType.Text && string.IsNullOrWhiteSpace(HtmlEntity.DeEntitize(node.InnerText));
    private static bool IsEmptyTailNode(HtmlNode node)
        => IsWhitespaceOrComment(node) || node.Name == "br" || node.Name == "p" && node.ChildNodes.All(child => IsWhitespaceOrComment(child) || child.Name == "br");

    private static bool IsSourceUri(string value, IEnumerable<Uri> sources)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.IsDefaultPort
           && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
           && sources.Any(source => source != null && string.Equals(uri.Host, source.Host, StringComparison.OrdinalIgnoreCase)
               && string.Equals(uri.AbsolutePath, source.AbsolutePath, StringComparison.Ordinal));
}
