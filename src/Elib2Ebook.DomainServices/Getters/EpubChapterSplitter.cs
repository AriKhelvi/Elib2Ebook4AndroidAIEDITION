using System.Net;
using System.Text.RegularExpressions;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Misc;
using HtmlAgilityPack;

namespace Elib2Ebook.DomainServices.Getters;

/// <summary>Дополнительное разбиение MyBook, когда EPUB перечисляет в оглавлении только крупные части.</summary>
internal static class EpubChapterSplitter
{
    private static readonly Regex ChapterHeading = new(
        @"^(?:(?:Пролог|Эпилог)[.!:]?|Глава\s+(?:\d+|[IVXLCDM]+)(?:\s*[.:](?:\s+.+)?|\s+[–—-]\s+.+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NotesHeading = new(@"^(?:Примечания|Сноски)(?:\s|[.!:]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static List<Chapter> Split(IEnumerable<Chapter> chapters, string bookTitle)
        => chapters.SelectMany(chapter => SplitChapter(chapter, bookTitle)).ToList();

    private static IEnumerable<Chapter> SplitChapter(Chapter source, string bookTitle)
    {
        if (NotesHeading.IsMatch(Normalize(source.Title))) return [source];
        var document = EpubImportLinks.Parse(source.Content);
        var boundaries = document.DocumentNode.Descendants()
            .Where(node => node.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            .Where(node => !node.Ancestors("nav").Any())
            .Where(node => Normalize(node.InnerText) is { Length: > 0 and <= 200 } title && ChapterHeading.IsMatch(title))
            .ToHashSet();
        // Единственный подзаголовок не доказывает, что исходный раздел следует делить.
        if (boundaries.Count < 2) return [source];

        var fragments = new List<(string Title, HtmlDocument Document)>();
        var writtenNodes = new HashSet<HtmlNode>();
        Dictionary<HtmlNode, HtmlNode> copies = null;
        HtmlDocument current = null;
        void Start(string title)
        {
            current = EpubImportLinks.Parse(string.Empty);
            fragments.Add((title, current));
            copies = new Dictionary<HtmlNode, HtmlNode> { [document.DocumentNode] = current.DocumentNode };
        }
        HtmlNode Copy(HtmlNode node)
        {
            if (copies.TryGetValue(node, out var existing)) return existing;
            var parent = Copy(node.ParentNode);
            var clone = node.CloneNode(false);
            // Внешняя обёртка может попасть в несколько разделов, но её цель ссылки остаётся одна.
            if (!writtenNodes.Add(node))
                foreach (var attribute in clone.Attributes.Where(attribute => attribute.Name == "id"
                    || clone.Name == "a" && attribute.Name == "name").ToArray())
                    clone.Attributes.Remove(attribute);
            parent.AppendChild(clone);
            copies.Add(node, clone);
            return clone;
        }
        void Visit(HtmlNode node)
        {
            if (boundaries.Contains(node)) Start(Normalize(node.InnerText));
            Copy(node);
            foreach (var child in node.ChildNodes) Visit(child);
        }

        Start(source.Title);
        foreach (var node in document.DocumentNode.ChildNodes) Visit(node);
        var intro = fragments[0].Document;
        ChapterContentCleaner.RemoveLeadingTitle(intro.DocumentNode, source.Title);
        ChapterContentCleaner.RemoveLeadingTitle(intro.DocumentNode, bookTitle);
        if (!HasVisibleContent(intro))
        {
            // Пустое вступление содержит адреса начала файла/обёрток, на которые могут вести ссылки.
            var anchors = intro.DocumentNode.Descendants().SelectMany(node => node.Attributes
                    .Where(attribute => attribute.Name == "id" || node.Name == "a" && attribute.Name == "name")
                    .Select(attribute => (attribute.Name, attribute.Value))).ToArray();
            var first = fragments[1].Document;
            foreach (var (name, value) in anchors.Reverse())
            {
                var anchor = first.CreateElement(name == "name" ? "a" : "span");
                anchor.SetAttributeValue(name, value);
                first.DocumentNode.PrependChild(anchor);
            }
            fragments.RemoveAt(0);
        }

        return fragments.Select(fragment =>
        {
            ChapterContentCleaner.RemoveLeadingTitle(fragment.Document.DocumentNode, fragment.Title);
            var imageNames = fragment.Document.DocumentNode.Descendants("img")
                .Select(node => WebUtility.HtmlDecode(node.GetAttributeValue("src", string.Empty))).ToHashSet(StringComparer.Ordinal);
            return new Chapter
            {
                Title = fragment.Title,
                Content = fragment.Document.DocumentNode.InnerHtml,
                Images = source.Images?.Where(image => imageNames.Contains(image.FullName)).ToList() ?? []
            };
        }).ToList();
    }

    private static bool HasVisibleContent(HtmlDocument document)
        => document.DocumentNode.Descendants().Any(node => node.Name is "img" or "svg" or "math" or "video" or "audio" or "object"
            || node.NodeType == HtmlNodeType.Text && !node.Ancestors().Any(parent => parent.Name is "head" or "script" or "style")
            && !string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(node.InnerText)));

    private static string Normalize(string text) => Regex.Replace(WebUtility.HtmlDecode(text ?? string.Empty), @"\s+", " ").Trim();
}
