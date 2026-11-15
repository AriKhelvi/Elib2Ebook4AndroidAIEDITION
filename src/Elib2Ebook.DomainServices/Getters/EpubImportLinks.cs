using System.Security.Cryptography;
using System.Text;
using EpubSharp;
using HtmlAgilityPack;

namespace Elib2Ebook.DomainServices.Getters;

/// <summary>Стабильные якоря исходного EPUB переживают разрезание и новые имена файлов глав.</summary>
internal static class EpubImportLinks
{
    private static readonly Uri Origin = new("https://epub.invalid/");

    internal sealed class PreparedLinks
    {
        public Dictionary<string, string[]> FileAnchors { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> SourceAnchorCounts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> WrittenAnchors { get; } = new(StringComparer.Ordinal);
    }

    public static PreparedLinks Prepare(EpubBook book)
    {
        var prepared = new PreparedLinks();
        var paths = book.Resources.Html.Select(file => FilePath(new Uri(Origin, file.AbsolutePath)))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var file in book.Resources.Html)
        {
            var source = new Uri(Origin, file.AbsolutePath);
            var path = FilePath(source);
            var document = Parse(file.TextContent);
            var body = document.DocumentNode.Descendants("body").FirstOrDefault();
            if (body == null) continue;
            var html = document.DocumentNode.Descendants("html").FirstOrDefault();
            var fileAnchors = new List<string> { Anchor(path, "") };
            foreach (var container in new[] { html, body }.Where(node => node != null))
            {
                if (container.Attributes["id"] is not { } id) continue;
                fileAnchors.Add(Anchor(path, System.Net.WebUtility.HtmlDecode(id.Value)));
                container.Attributes.Remove(id);
            }
            foreach (var node in body.DescendantsAndSelf())
            {
                foreach (var attribute in node.Attributes.Where(attribute => attribute.Name == "id" || node.Name == "a" && attribute.Name == "name"))
                    attribute.Value = Anchor(path, System.Net.WebUtility.HtmlDecode(attribute.Value));
                var href = node.Attributes["href"];
                if (href == null || !Uri.TryCreate(source, System.Net.WebUtility.HtmlDecode(href.Value), out var target)
                    || target.Scheme != Origin.Scheme || target.Host != Origin.Host || !paths.Contains(FilePath(target))) continue;
                href.Value = "#" + Anchor(FilePath(target), Uri.UnescapeDataString(target.Fragment.TrimStart('#')));
            }
            // SliceBook переносит только детей body, поэтому якоря html/body должны жить внутри него.
            prepared.FileAnchors[path] = fileAnchors.Distinct(StringComparer.Ordinal).ToArray();
            foreach (var id in prepared.FileAnchors[path].Reverse())
            {
                var start = document.CreateElement("span");
                start.SetAttributeValue("id", id);
                body.PrependChild(start);
            }
            foreach (var node in body.DescendantsAndSelf())
                foreach (var id in AnchorAttributes(node).Select(attribute => attribute.Value).Distinct(StringComparer.Ordinal))
                    prepared.SourceAnchorCounts[id] = prepared.SourceAnchorCounts.GetValueOrDefault(id) + 1;
            file.TextContent = document.DocumentNode.OuterHtml;
        }

        // EpubSharp связывает все уровни оглавления через Next; сохраняем точки разрезания после смены id.
        for (var chapter = book.TableOfContents.FirstOrDefault(); chapter != null; chapter = chapter.Next)
            chapter.HashLocation = Anchor(FilePath(new Uri(Origin, chapter.AbsolutePath)), Uri.UnescapeDataString(chapter.HashLocation ?? ""));
        return prepared;
    }

    private static string FilePath(Uri uri) => Uri.UnescapeDataString(uri.AbsolutePath);
    public static HtmlDocument Parse(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document;
    }

    public static void EnsureFileAnchors(HtmlDocument document, string path, PreparedLinks prepared)
    {
        if (!prepared.FileAnchors.TryGetValue(FilePath(new Uri(Origin, path)), out var ids)) return;
        foreach (var id in ids.Reverse())
        {
            if (document.DocumentNode.DescendantsAndSelf().Any(node => node.GetAttributeValue("id", "") == id)) continue;
            var anchor = document.CreateElement("span");
            anchor.SetAttributeValue("id", id);
            document.DocumentNode.PrependChild(anchor);
        }
    }

    public static void RemoveClonedAnchors(HtmlDocument document, PreparedLinks prepared)
    {
        foreach (var node in document.DocumentNode.DescendantsAndSelf())
            foreach (var group in AnchorAttributes(node).GroupBy(attribute => attribute.Value, StringComparer.Ordinal).ToArray())
                // Повторные id в самом исходнике остаются ошибкой; убираем только копии уникальных исходных целей.
                if (prepared.SourceAnchorCounts.GetValueOrDefault(group.Key) == 1 && !prepared.WrittenAnchors.Add(group.Key))
                    foreach (var attribute in group.ToArray()) node.Attributes.Remove(attribute);
    }

    private static IEnumerable<HtmlAttribute> AnchorAttributes(HtmlNode node)
        => node.Attributes.Where(attribute => attribute.Name == "id" || node.Name == "a" && attribute.Name == "name");

    private static string Anchor(string path, string fragment)
        => "epub-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + "#" + fragment))).ToLowerInvariant();
}
