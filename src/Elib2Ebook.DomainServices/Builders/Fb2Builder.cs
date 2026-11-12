using System.Text;
using System.Xml;
using System.Xml.Linq;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Builders;

public class Fb2Builder : BuilderBase
{
    protected override string Extension => "fb2";

    private readonly XNamespace _ns = "http://www.gribuser.ru/xml/fictionbook/2.0";
    private readonly XNamespace _xlink = "http://www.w3.org/1999/xlink";

    private readonly XElement _description;
    private readonly XElement _titleInfo;
    private readonly XElement _body;
    private readonly XElement _documentInfo;
    private readonly List<TempFile> _images = [];

    private static readonly Dictionary<string, string> InlineTags = new()
    {
        ["strong"] = "strong", ["b"] = "strong",
        ["i"] = "emphasis", ["em"] = "emphasis", ["emphasis"] = "emphasis",
        ["del"] = "strikethrough", ["s"] = "strikethrough", ["strikethrough"] = "strikethrough",
        ["sub"] = "sub", ["sup"] = "sup", ["code"] = "code",
    };

    public Fb2Builder(Options options, ILogger logger) : base(options, logger)
    {
        _description = CreateXElement("description");
        _titleInfo = CreateXElement("title-info");
        _documentInfo = CreateXElement("document-info");
        _body = CreateXElement("body");
    }

    private XElement CreateXElement(string name)
    {
        return new XElement(_ns + name);
    }

    private XElement CreateAuthor(Author author)
    {
        var authorElem = CreateXElement("author");

        var name = CleanText(author?.Name ?? "Автор не указан");
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            var firstName = CreateXElement("first-name");
            firstName.Value = parts[0];
            authorElem.Add(firstName);

            var lastName = CreateXElement("last-name");
            lastName.Value = parts[1];
            authorElem.Add(lastName);
        }
        else
        {
            // Псевдоним не требует обязательной пары first-name / last-name.
            authorElem.Add(new XElement(_ns + "nickname", name));
        }

        if (author?.Url != null)
        {
            var homePageElem = CreateXElement("home-page");
            homePageElem.Value = author.Url.ToString();
            authorElem.Add(homePageElem);
        }

        return authorElem;
    }

    private XElement CreateTitle(string text)
    {
        var p = CreateXElement("p");
        p.Value = CleanText(text).ReplaceNewLine();

        var title = CreateXElement("title");

        title.Add(p);
        return title;
    }

    private static string ImageId(string fileName) => "image_" + XmlConvert.EncodeLocalName(fileName);

    private static async Task WriteBinary(XmlWriter writer, TempFile tempFile)
    {
        const int bufferSize = 1000;
        var buffer = new byte[bufferSize];
        int readBytes;

        await using var inputFile = tempFile.GetStream();
        await writer.WriteStartElementAsync(null, "binary", null);
        await writer.WriteAttributeStringAsync(null, "id", null, ImageId(tempFile.FullName));
        var imageType = tempFile.Extension.TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" => "jpeg",
            "svg" => "svg+xml",
            var extension => extension,
        };
        await writer.WriteAttributeStringAsync(null, "content-type", null, "image/" + imageType);
        using var br = new BinaryReader(inputFile);

        do
        {
            readBytes = br.Read(buffer, 0, bufferSize);
            await writer.WriteBase64Async(buffer, 0, readBytes);
        } while (bufferSize <= readBytes);

        await writer.WriteEndElementAsync();
    }

    /// <summary>
    /// Добавление автора книги
    /// </summary>
    /// <param name="author">Автор</param>
    /// <returns></returns>
    private void AddAuthor(Author author)
    {
        var authorElem = CreateAuthor(author);
        _titleInfo.Add(authorElem);
        _documentInfo.Add(authorElem);
    }

    /// <summary>
    /// Добавление со-авторов книги
    /// </summary>
    /// <param name="coAuthors">Со-авторы</param>
    /// <returns></returns>
    private void AddCoAuthors(IEnumerable<Author> coAuthors)
    {
        foreach (var coAuthor in coAuthors)
        {
            var coAuthorElem = CreateAuthor(coAuthor);
            _titleInfo.Add(coAuthorElem);
            _documentInfo.Add(coAuthorElem);
        }
    }

    /// <summary>
    /// Указание названия книги
    /// </summary>
    /// <param name="title">Название книги</param>
    /// <returns></returns>
    private void WithTitle(string title)
    {
        var bookTitle = CreateXElement("book-title");
        bookTitle.Value = CleanText(title).ReplaceNewLine();

        _titleInfo.Add(bookTitle);
        _body.Add(CreateTitle(title));
    }

    /// <summary>
    /// Добавление обложки книги
    /// </summary>
    /// <param name="cover">Обложка</param>
    /// <returns></returns>
    private void WithCover(TempFile cover)
    {
        if (cover != null)
        {
            var coverPage = CreateXElement("coverpage");

            var imageElem = CreateXElement("image");
            imageElem.SetAttributeValue(_xlink + "href", "#" + ImageId(cover.FullName));

            coverPage.Add(imageElem);
            _titleInfo.Add(coverPage);
            _images.Add(cover);
        }
    }

    private void WithBookUrl(Uri url)
    {
        if (url != null)
        {
            var srcUrlElem = CreateXElement("src-url");
            srcUrlElem.Value = CleanText(url.ToString());
            _documentInfo.Add(srcUrlElem);
        }
    }

    private void WithAnnotation(string annotation)
    {
        if (!string.IsNullOrWhiteSpace(annotation))
        {
            _titleInfo.Add(CreateAnnotation(annotation));
        }
    }

    private XElement CreateAnnotation(string annotation)
    {
        var a = CreateXElement("annotation");

        var doc = new HtmlDocument();
        doc.LoadHtml(annotation);
        AppendFlow(a, doc.DocumentNode.ChildNodes);

        return a;
    }

    /// <summary>
    /// Добавление списка частей книги
    /// </summary>
    /// <param name="chapters">Список частей</param>
    /// <returns></returns>
    private void WithChapters(IEnumerable<Chapter> chapters)
    {
        foreach (var chapter in chapters.Where(c => c.IsValid))
        {
            var section = CreateXElement("section");
            section.Add(CreateTitle(chapter.Title));

            var doc = new HtmlDocument();
            doc.LoadHtml(chapter.Content);
            AppendFlow(section, doc.DocumentNode.ChildNodes);
            if (section.Elements().Count() == 1)
                section.Add(CreateXElement("empty-line"));

            _body.Add(section);

            foreach (var image in chapter.Images)
            {
                _images.Add(image);
            }
        }
    }

    private void WithSeria(Seria seria)
    {
        if (seria != null)
        {
            var sequenceElem = CreateXElement("sequence");
            sequenceElem.SetAttributeValue("name", CleanText(seria.Name));
            if (System.Numerics.BigInteger.TryParse(seria.Number, out var number))
                sequenceElem.SetAttributeValue("number", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _titleInfo.Add(sequenceElem);
        }
    }

    private void WithLang(string lang)
    {
        var langElem = CreateXElement("lang");
        langElem.Value = string.IsNullOrWhiteSpace(lang) ? "und" : lang;
        _titleInfo.Add(langElem);
    }

    private static string CleanText(string text)
    {
        // XElement сам экранирует &, < и >. Удаляем только недопустимые XML-символы.
        var result = new StringBuilder();
        foreach (var rune in (text ?? string.Empty).EnumerateRunes())
        {
            if (rune.Value is 0x9 or 0xA or 0xD or >= 0x20 and <= 0xD7FF
                or >= 0xE000 and <= 0xFFFD or >= 0x10000 and <= 0x10FFFF)
                result.Append(rune.ToString());
        }
        return result.ToString();
    }

    private void AppendFlow(XElement parent, IEnumerable<HtmlNode> nodes, string paragraphTag = "p")
    {
        XElement paragraph = null;
        var formats = new List<XElement>();
        var outputFormats = new List<XElement>();

        XElement InlineParent()
        {
            paragraph ??= CreateXElement(paragraphTag);
            var target = outputFormats.Count == 0 ? paragraph : outputFormats[^1];
            while (outputFormats.Count < formats.Count)
            {
                var template = formats[outputFormats.Count];
                var copy = new XElement(template.Name, template.Attributes());
                target.Add(copy);
                outputFormats.Add(copy);
                target = copy;
            }
            return target;
        }

        void Flush(bool emptyLine = false)
        {
            if (paragraph != null && (!string.IsNullOrWhiteSpace(paragraph.Value)
                || paragraph.Descendants(_ns + "image").Any()))
                parent.Add(paragraph);
            else if (emptyLine)
                parent.Add(CreateXElement("empty-line"));
            paragraph = null;
            outputFormats.Clear();
        }

        void Visit(HtmlNode node)
        {
            if (node.NodeType == HtmlNodeType.Comment || node.Name is "script" or "style")
                return;

            if (node.NodeType == HtmlNodeType.Text)
            {
                var text = CleanText(HtmlEntity.DeEntitize(node.InnerText));
                if (text.Length > 0)
                    InlineParent().Add(new XText(text));
                return;
            }

            if (node.Name is "br" or "hr")
            {
                // Перенос разрывает абзац, а следующий перенос даёт пустую строку.
                // Шаблоны inline-элементов сохраняются для следующего абзаца.
                Flush(emptyLine: true);
                return;
            }

            if (node.Name == "img")
            {
                var src = node.GetAttributeValue("src", string.Empty);
                if (!string.IsNullOrWhiteSpace(src))
                    InlineParent().Add(new XElement(_ns + "image",
                        new XAttribute(_xlink + "href", "#" + ImageId(src))));
                return;
            }

            if (node.Name == "blockquote" && parent.Name.LocalName != "cite")
            {
                Flush();
                var cite = CreateXElement("cite");
                AppendFlow(cite, node.ChildNodes);
                parent.Add(cite);
                return;
            }

            if (node.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                Flush();
                AppendFlow(parent, node.ChildNodes, "subtitle");
                return;
            }

            if (node.Name is "p" or "div" or "section" or "article" or "li" or "ul" or "ol"
                or "blockquote" or "table" or "tr" or "td" or "th")
            {
                Flush();
                foreach (var child in node.ChildNodes)
                    Visit(child);
                Flush(emptyLine: node.Name == "p" && !node.HasChildNodes);
                return;
            }

            XElement format = null;
            if (InlineTags.TryGetValue(node.Name, out var tag))
                format = CreateXElement(tag);
            else if (node.Name == "a" && !formats.Any(f => f.Name.LocalName == "a"))
            {
                var href = node.GetAttributeValue("href", string.Empty);
                if (!string.IsNullOrWhiteSpace(href))
                    format = new XElement(_ns + "a",
                        new XAttribute(_xlink + "href", CleanText(HtmlEntity.DeEntitize(href))));
            }

            // У неподдерживаемых тегов (включая u) сохраняем всё содержимое.
            if (format != null)
                formats.Add(format);
            foreach (var child in node.ChildNodes)
                Visit(child);
            if (format != null)
            {
                formats.RemoveAt(formats.Count - 1);
                if (outputFormats.Count > formats.Count)
                    outputFormats.RemoveRange(formats.Count, outputFormats.Count - formats.Count);
            }
        }

        foreach (var node in nodes)
            Visit(node);
        Flush();
    }

    private XElement GetDateElement(DateTime date)
    {
        var today = date.ToString("yyyy-MM-dd");

        var dateElem = CreateXElement("date");
        dateElem.SetAttributeValue("value", today);
        dateElem.Value = today;

        return dateElem;
    }

    protected override async Task BuildInternal(Book book, string fileName)
    {
        await using var file = File.Create(fileName);

        _description.RemoveNodes();
        _titleInfo.RemoveNodes();
        _documentInfo.RemoveNodes();
        _body.RemoveNodes();
        _images.Clear();

        // Модель книги пока не содержит жанр, но FB2 требует хотя бы один.
        _titleInfo.Add(new XElement(_ns + "genre", "unrecognised"));
        AddAuthor(book.Author);
        AddCoAuthors(book.CoAuthors);
        WithTitle(book.Title);
        WithAnnotation(book.Annotation);
        WithCover(book.Cover);
        WithLang(book.Lang);
        WithSeria(book.Seria);
        WithChapters(book.Chapters);
        if (!_body.Elements(_ns + "section").Any())
            _body.Add(new XElement(_ns + "section", CreateXElement("empty-line")));

        var programUsed = CreateXElement("program-used");
        programUsed.Value = "Elib2Ebook";
        _documentInfo.Add(programUsed);
        _documentInfo.Add(GetDateElement(DateTime.Today));
        WithBookUrl(book.Url);
        _documentInfo.Add(new XElement(_ns + "id", book.Url?.ToString() ?? Guid.NewGuid().ToString()));
        _documentInfo.Add(new XElement(_ns + "version", "1.0"));

        _description.Add(_titleInfo);
        _description.Add(_documentInfo);

        var xws = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(false),
            Indent = true,
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = "\r\n",
        };

        await using var writer = XmlWriter.Create(file, xws);
        var cancellationToken = CancellationToken.None;

        await writer.WriteStartElementAsync(string.Empty, "FictionBook", _ns.NamespaceName);
        await writer.WriteAttributeStringAsync("xmlns", "l", null, _xlink.NamespaceName);

        await _description.WriteToAsync(writer, cancellationToken);
        await _body.WriteToAsync(writer, cancellationToken);
        foreach (var image in _images.DistinctBy(image => ImageId(image.FullName)))
        {
            await WriteBinary(writer, image);
        }

        await writer.WriteEndElementAsync();
        await writer.FlushAsync();
    }
}
