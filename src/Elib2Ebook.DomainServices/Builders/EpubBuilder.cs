using Elib2Ebook.Domain.Book;
using System.Net;
using System.Text;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using EpubSharp;
using EpubSharp.Format;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Builders;

public class EpubBuilder(Options options, ILogger logger) : BuilderBase(options, logger)
{
    protected override string Extension => "epub";

    private readonly EpubWriter _writer = new();

    private List<TempFile> Images { get; set; } = [];

    /// <summary>
    /// Создание Xhtml документа из кода части
    /// </summary>
    /// <param name="pattern">Шаблон части</param>
    /// <param name="title">Заголовок части</param>
    /// <param name="contentHtml">HTML содержимого главы</param>
    /// <returns></returns>
    private static string ApplyPattern(string pattern, string title, string contentHtml)
    {
        // Content уже содержит HTML. Декодирование до разбора повреждает буквальные теги.
        var document = pattern.Replace("{title}", WebUtility.HtmlEncode(CleanXmlText(title).ReplaceNewLine()))
            .Replace("{body}", CleanXmlText(contentHtml)).AsXHtmlDoc();
        // XmlWriter сам экранирует значения. Декодируем сущности только в готовых узлах,
        // без повторного разбора: буквальный <script> остаётся текстом, а не элементом.
        foreach (var node in document.DocumentNode.DescendantsAndSelf())
        {
            if (node is HtmlTextNode text) text.Text = CleanXmlText(WebUtility.HtmlDecode(text.Text));
            foreach (var attribute in node.Attributes)
                attribute.Value = CleanXmlText(WebUtility.HtmlDecode(attribute.Value));
        }
        return document.AsString();
    }

    private static string CleanXmlText(string text)
    {
        // Удаляем только запрещённые XML-символы, сохраняя & в HTML-сущностях и целые emoji.
        var result = new StringBuilder();
        foreach (var rune in (text ?? string.Empty).EnumerateRunes())
            if (rune.Value is 0x9 or 0xA or 0xD or >= 0x20 and <= 0xD7FF
                or >= 0xE000 and <= 0xFFFD or >= 0x10000 and <= 0x10FFFF)
                result.Append(rune.ToString());
        return result.ToString();
    }

    /// <summary>
    /// Добавление внешних файлов
    /// </summary>
    /// <param name="directory">Путь к директории с файлами</param>
    /// <param name="searchPattern">Шаблон поиска файлов</param>
    /// <returns></returns>
    private void WithFiles(string directory, string searchPattern)
    {
        foreach (var file in FileProvider.Instance.GetFiles(directory, searchPattern))
        {
            var fileName = Path.GetFileName(file.Name);
            var type = file.Name.EndsWith(".ttf") ? EpubContentType.FontTruetype :
                file.Name.EndsWith(".css") ? EpubContentType.Css : throw new Exception($"Неизвестный тип файла {fileName}");

            _writer.AddFile(fileName, file.ReadAllBytes(), type);
        }
    }

    private static ImageFormat GetImageFormat(string path)
    {
        if (path.EndsWith(".jpg", StringComparison.InvariantCultureIgnoreCase))
        {
            return ImageFormat.Jpeg;
        }

        if (path.EndsWith(".gif", StringComparison.InvariantCultureIgnoreCase))
        {
            return ImageFormat.Gif;
        }

        if (path.EndsWith(".png", StringComparison.InvariantCultureIgnoreCase))
        {
            return ImageFormat.Png;
        }

        return path.EndsWith(".svg", StringComparison.InvariantCultureIgnoreCase) ? ImageFormat.Svg : ImageFormat.Jpeg;
    }

    private void Write(Book book)
    {
        _writer.AddAuthor(book.Author.Name);

        foreach (var coAuthor in book.CoAuthors)
        {
            _writer.AddAuthor(coAuthor.Name);
        }

        _writer.SetTitle(book.Title);

        if (book.Cover != null)
        {
            _writer.SetCover(book.Cover.Content, GetImageFormat(book.Cover.FullName));
        }

        if (!string.IsNullOrWhiteSpace(book.Annotation))
        {
            _writer.AddDescription(book.Annotation.AsHtmlDoc().DocumentNode.GetText());
        }

        if (book.Seria != null)
        {
            _writer.AddCollection(book.Seria.Name, book.Seria.Number);
        }

        var pattern = FileProvider.Instance.ReadAllText($"{Options.ResourcesPath}/ChapterPattern.xhtml");
        foreach (var chapter in book.Chapters.Where(c => c.IsValid))
        {
            foreach (var image in chapter.Images)
            {
                _writer.AddFile(image.FullName, [], GetImageFormat(image.FullName).ToEpubContentType());
                Images.Add(image);
            }

            _writer.AddChapter(chapter.Title.HtmlDecode().ReplaceNewLine().RemoveInvalidChars(), ApplyPattern(pattern, chapter.Title, chapter.Content));
        }

        WithFiles(Options.ResourcesPath, "*.ttf");
        WithFiles(Options.ResourcesPath, "*.css");
    }

    protected override async Task BuildInternal(Book book, string fileName)
    {
        Write(book);
        await _writer.Write(fileName, Images.Select(image => new FileMeta(image.FullName, image.FilePath)));
    }
}
