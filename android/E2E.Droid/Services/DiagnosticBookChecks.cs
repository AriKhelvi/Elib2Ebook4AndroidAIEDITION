using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Builders;
using Elib2Ebook.DomainServices.Configs;
using Microsoft.Extensions.Logging.Abstractions;

namespace E2E.Droid.Services;

public sealed record DiagnosticCheck(bool Passed, string Message, bool Warning = false);

/// <summary>Проверяет созданные приложением файлы без сети и без записи текста книги в журнал.</summary>
public static class DiagnosticBookChecks
{
    private static readonly XNamespace Fb = "http://www.gribuser.ru/xml/fictionbook/2.0";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Html = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Epub = "http://www.idpf.org/2007/ops";

    public static IReadOnlyList<DiagnosticCheck> CheckFile(string path, int? expectedChapters = null)
    {
        var checks = new List<DiagnosticCheck>();
        var format = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (format == ".fb2") CheckFb2(path, expectedChapters, checks);
            else if (format == ".epub") CheckEpub(path, expectedChapters, checks);
            else checks.Add(new(false, "Формат не поддерживается проверкой структуры", true));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or InvalidOperationException or ArgumentException or FormatException)
        {
            // XmlException.Message может цитировать фрагмент книги; в отчёт попадает только тип ошибки.
            checks.Add(new(false, $"{format.ToUpperInvariant()}: структура не прочитана ({ex.GetType().Name})"));
        }
        return checks;
    }

    private static XDocument ReadXml(Stream stream, bool html = false)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = html ? DtdProcessing.Ignore : DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024,
        });
        return XDocument.Load(reader);
    }

    private static void CheckFb2(string path, int? expected, List<DiagnosticCheck> checks)
    {
        using var input = File.OpenRead(path);
        var doc = ReadXml(input);
        var root = doc.Root;
        if (root?.Name != Fb + "FictionBook")
        {
            checks.Add(new(false, "FB2: неверный корневой элемент или пространство имён"));
            return;
        }
        checks.Add(new(true, "FB2: XML прочитан; это структурная проверка, не полная проверка XSD"));
        var title = root.Element(Fb + "description")?.Element(Fb + "title-info");
        checks.Add(new(!string.IsNullOrWhiteSpace(title?.Element(Fb + "book-title")?.Value), "FB2: название в title-info"));
        CheckIds(root, "FB2", checks);

        var binaries = root.Elements(Fb + "binary").Select(x => (string)x.Attribute("id")).Where(x => x != null).ToHashSet(StringComparer.Ordinal);
        var images = root.Descendants(Fb + "image").ToList();
        var missing = images.Count(x => (string)x.Attribute(Xlink + "href") is not { } href || !href.StartsWith('#') || !binaries.Contains(href[1..]));
        checks.Add(new(missing == 0, $"FB2: изображений {images.Count}, отсутствующих binary-ссылок {missing}"));

        var chapters = root.Elements(Fb + "body").Where(x => x.Attribute("name") == null).Elements(Fb + "section").ToList();
        CheckCount(chapters.Count, expected, "FB2", checks);
        var chapterTexts = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            var body = chapter.Elements().Where(x => x.Name != Fb + "title").ToList();
            var substantive = body.Any(x => !string.IsNullOrWhiteSpace(x.Value) || x.DescendantsAndSelf(Fb + "image").Any());
            checks.Add(new(substantive, $"FB2: глава {i + 1}, содержимое после заголовка"));
            CheckContent(body, chapter.Element(Fb + "title")?.Value, $"FB2: глава {i + 1}", checks);
            CheckRepeatedText(body, chapterTexts, $"FB2: глава {i + 1}", checks);
        }
    }

    private static void CheckEpub(string path, int? expected, List<DiagnosticCheck> checks)
    {
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.ToDictionary(x => x.FullName, StringComparer.Ordinal);
        var mime = zip.GetEntry("mimetype");
        using (var reader = mime == null ? null : new StreamReader(mime.Open()))
            checks.Add(new(reader?.ReadToEnd() == "application/epub+zip", "EPUB: mimetype"));
        if (!entries.TryGetValue("META-INF/container.xml", out var containerEntry))
        {
            checks.Add(new(false, "EPUB: нет META-INF/container.xml"));
            return;
        }
        using var containerStream = containerEntry.Open();
        var container = ReadXml(containerStream);
        XNamespace containerNs = "urn:oasis:names:tc:opendocument:xmlns:container";
        var packagePath = (string)container.Descendants(containerNs + "rootfile").FirstOrDefault()?.Attribute("full-path");
        if (packagePath == null || !entries.TryGetValue(packagePath, out var packageEntry))
        {
            checks.Add(new(false, "EPUB: container не указывает на существующий OPF"));
            return;
        }
        using var packageStream = packageEntry.Open();
        var package = ReadXml(packageStream);
        if (package.Root?.Name != Opf + "package")
        {
            checks.Add(new(false, "EPUB: неверный корень OPF"));
            return;
        }
        var items = package.Root.Element(Opf + "manifest")?.Elements(Opf + "item").ToList() ?? [];
        var spine = package.Root.Element(Opf + "spine")?.Elements(Opf + "itemref").ToList() ?? [];
        checks.Add(new(items.Count > 0 && spine.Count > 0, $"EPUB: manifest {items.Count}, spine {spine.Count}"));
        var manifest = new Dictionary<string, (XElement Item, string Path)>(StringComparer.Ordinal);
        var manifestErrors = 0;
        foreach (var item in items)
        {
            var id = (string)item.Attribute("id");
            var target = Resolve(packagePath, (string)item.Attribute("href"));
            if (id == null || target == null || !entries.ContainsKey(target.Value.Path) || !manifest.TryAdd(id, (item, target.Value.Path))) manifestErrors++;
        }
        checks.Add(new(manifestErrors == 0, $"EPUB: отсутствующих/повторных элементов manifest {manifestErrors}"));
        var documents = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        foreach (var item in manifest.Values.Where(x => (string)x.Item.Attribute("media-type") is "application/xhtml+xml" or "image/svg+xml"))
        {
            if (!entries.TryGetValue(item.Path, out var entry)) continue;
            using var stream = entry.Open();
            var doc = ReadXml(stream, html: true);
            documents[item.Path] = doc;
            CheckIds(doc.Root, "EPUB: XML-документ", checks);
        }
        checks.Add(new(documents.Count > 0, $"EPUB: XML-документов прочитано {documents.Count}; DTD и внешние ресурсы не загружались"));

        var missingFiles = 0;
        var missingAnchors = 0;
        foreach (var pair in documents)
        {
            foreach (var attribute in pair.Value.Descendants().Attributes().Where(a => a.Name.LocalName is "href" or "src"))
            {
                var target = Resolve(pair.Key, attribute.Value);
                if (target == null) continue; // Внешняя авторская ссылка не должна существовать в ZIP.
                if (!entries.ContainsKey(target.Value.Path)) missingFiles++;
                else if (target.Value.Fragment.Length > 0 && documents.TryGetValue(target.Value.Path, out var targetDoc)
                         && !targetDoc.Descendants().Any(x => (string)x.Attribute("id") == target.Value.Fragment || (x.Name.LocalName == "a" && (string)x.Attribute("name") == target.Value.Fragment))) missingAnchors++;
            }
        }
        checks.Add(new(missingFiles == 0 && missingAnchors == 0, $"EPUB: отсутствующих внутренних файлов {missingFiles}, якорей {missingAnchors}"));

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in package.Descendants(Opf + "reference").Where(x => (string)x.Attribute("type") is "cover" or "toc"))
        {
            var target = Resolve(packagePath, (string)reference.Attribute("href"));
            if (target != null) excluded.Add(target.Value.Path);
        }
        var chapters = new List<XElement>();
        var unresolvedSpine = 0;
        foreach (var itemref in spine)
        {
            if (!manifest.TryGetValue((string)itemref.Attribute("idref") ?? "", out var item)) { unresolvedSpine++; continue; }
            if ((string)itemref.Attribute("linear") == "no" || Tokens((string)item.Item.Attribute("properties")).Contains("nav") || excluded.Contains(item.Path)) continue;
            if (!documents.TryGetValue(item.Path, out var doc)) { unresolvedSpine++; continue; }
            var body = doc.Root?.Element(Html + "body");
            if (body == null) { unresolvedSpine++; continue; }
            if (Tokens((string)body.Attribute(Epub + "type")).Any(x => x is "cover" or "toc")) continue;
            chapters.Add(body);
        }
        checks.Add(new(unresolvedSpine == 0, $"EPUB: неразрешённых записей spine {unresolvedSpine}"));
        CheckCount(chapters.Count, expected, "EPUB", checks);
        var chapterTexts = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < chapters.Count; i++)
        {
            var nodes = chapters[i].Elements().ToList();
            var title = nodes.FirstOrDefault(x => x.Name == Html + "h1");
            var body = title == nodes.FirstOrDefault() ? nodes.Skip(1).ToList() : nodes;
            var contentNodes = chapters[i].Nodes().Where(node => node != (title == nodes.FirstOrDefault() ? title : null)).ToList();
            checks.Add(new(contentNodes.Any(node => node is XText text && !string.IsNullOrWhiteSpace(text.Value)
                || node is XElement element && (!string.IsNullOrWhiteSpace(element.Value) || element.DescendantsAndSelf().Any(y => y.Name.LocalName is "img" or "image" or "svg"))), $"EPUB: глава {i + 1}, содержимое после заголовка"));
            CheckContent(body, title?.Value, $"EPUB: глава {i + 1}", checks);
            CheckRepeatedText(contentNodes, chapterTexts, $"EPUB: глава {i + 1}", checks);
        }
    }

    private static void CheckRepeatedText(IEnumerable<XNode> nodes, HashSet<string> seen, string label, List<DiagnosticCheck> checks)
    {
        var text = Normalize(string.Join(' ', nodes.Select(node => node is XElement element ? element.Value : node is XText value ? value.Value : "")));
        if (text.Length == 0) return; // У комиксов без текста сравнивать нечего.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (!seen.Add(hash)) checks.Add(new(false, $"{label}: возможный повтор текста другой главы; нормализованное содержимое совпало", true));
    }

    private static void CheckIds(XElement root, string label, List<DiagnosticCheck> checks)
    {
        var duplicates = root?.DescendantsAndSelf().Attributes("id").GroupBy(x => x.Value, StringComparer.Ordinal).Count(g => g.Count() > 1) ?? 0;
        checks.Add(new(duplicates == 0, $"{label}: повторных id {duplicates}"));
    }

    private static void CheckCount(int actual, int? expected, string label, List<DiagnosticCheck> checks)
        => checks.Add(new(actual > 0 && (!expected.HasValue || expected.Value == actual), $"{label}: глав {actual}" + (expected.HasValue ? $", ожидается {expected.Value}" : "")));

    private static IEnumerable<string> Tokens(string value) => (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    private static string Normalize(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

    private static void CheckContent(List<XElement> nodes, string title, string label, List<DiagnosticCheck> checks)
    {
        var visible = nodes.Where(x => !string.IsNullOrWhiteSpace(x.Value) || x.DescendantsAndSelf().Any(y => y.Name.LocalName is "img" or "image" or "svg")).ToList();
        var first = visible.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(title) && first != null && Normalize(first.Value) == Normalize(title)
            && first.Name.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "subtitle" or "p")
            checks.Add(new(false, $"{label}: возможный повтор названия в первом блоке, требуется просмотр", true));
        var last = visible.LastOrDefault();
        if (last?.Name.LocalName == "p" && Uri.TryCreate(Normalize(last.Value), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && !last.Descendants().Any(x => x.Name.LocalName is "img" or "image" or "svg"))
            checks.Add(new(false, $"{label}: отдельный URL в последнем абзаце; может быть авторской ссылкой", true));
    }

    private static (string Path, string Fragment)? Resolve(string source, string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var origin = new Uri("https://epub.invalid/");
        var target = new Uri(new Uri(origin, source), href);
        if (target.Scheme != origin.Scheme || target.Host != origin.Host) return null;
        return (Uri.UnescapeDataString(target.AbsolutePath.TrimStart('/')), Uri.UnescapeDataString(target.Fragment.TrimStart('#')));
    }

    public static async Task<IReadOnlyList<DiagnosticCheck>> RunSelfTestAsync(string work, CancellationToken ct)
    {
        const string title = "Самопроверка 🐠 👩🏽‍💻";
        const string marker = "Проверка кириллицы, emoji 😀 и сохранности текста";
        Directory.CreateDirectory(work);
        ct.ThrowIfCancellationRequested();
        using var image = await TempFile.Create(new Uri("https://example.invalid/image.png"), work, "diagnostic.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        using var book = new Book(new Uri("https://example.invalid/book"))
        {
            Title = title, Author = new Author("Проверочный автор"),
            Chapters = [
                new Chapter { Title = "Глава 1 🐠", Content = $"<p>{marker}</p><h2>Внутренний подзаголовок</h2><p>Продолжение</p><img src='{image.FullName}'/>", Images = [image] },
                new Chapter { Title = "Глава 2", Content = "<p>Вторая часть проверочной книги.</p>" }
            ]
        };
        var checks = new List<DiagnosticCheck>();
        foreach (var format in new[] { "fb2", "epub" })
        {
            ct.ThrowIfCancellationRequested();
            var output = Path.Combine(work, format);
            var options = new Options { SavePath = output, BookNamePattern = "{Book.Title}" };
            BuilderBase builder = format == "fb2" ? new Fb2Builder(options, NullLogger.Instance) : new EpubBuilder(options, NullLogger.Instance);
            await builder.Build(book);
            ct.ThrowIfCancellationRequested();
            var file = Directory.GetFiles(output, "*." + format).Single();
            checks.AddRange(CheckFile(file, 2));
            string serialized;
            if (format == "fb2") serialized = await File.ReadAllTextAsync(file, ct);
            else
            {
                using var zip = ZipFile.OpenRead(file);
                var parts = new List<string>();
                foreach (var entry in zip.Entries.Where(x => x.FullName.EndsWith(".opf") || x.FullName.EndsWith(".xhtml") || x.FullName.EndsWith(".html")))
                {
                    using var stream = entry.Open();
                    parts.Add(ReadXml(stream, html: true).Root?.Value ?? "");
                }
                serialized = string.Join('\n', parts);
            }
            checks.Add(new(serialized.Contains(title, StringComparison.Ordinal) && serialized.Contains(marker, StringComparison.Ordinal)
                && serialized.Contains("Внутренний подзаголовок", StringComparison.Ordinal), $"{format.ToUpperInvariant()}: кириллица, emoji и внутренний подзаголовок сохранены"));
        }
        return checks;
    }
}
