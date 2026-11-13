using System.Text;
using System.Text.Json;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.FictionBook;

public class FictionBookGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://fictionbook.ru");

    private Uri GetMainUrl(Uri url)
    {
        var reader = Array.FindIndex(url.Segments, s => s.TrimEnd('/') is "read_online" or "read_online.html");
        if (reader >= 0)
        {
            url = SystemUrl.MakeRelativeUri(string.Join(string.Empty, url.Segments[..reader]));
        }

        return url;
    }

    public override async Task<Book> Get(Uri url)
    {
        url = GetMainUrl(url);

        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        if (doc.QuerySelector("script#__NEXT_DATA__") != null)
            return await GetModernBook(doc, url);

        var name = doc.GetTextBySelector("h1 span[itemprop=name]");
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"FictionBook не вернул название книги ({url}); возможно ограничение доступа или изменение разметки.");

        var book = new Book(url)
        {
            Cover = await GetCover(doc, url),
            Chapters = await FillChapters(url, name),
            Title = name,
            Author = GetAuthor(doc),
            Annotation = doc.QuerySelector("div.biblio_book__annotation")?.InnerHtml,
        };

        return book;
    }

    private static JsonElement ReadPageProps(HtmlDocument doc, Uri url)
    {
        var json = doc.QuerySelector("script#__NEXT_DATA__")?.InnerText;
        try
        {
            using var parsed = JsonDocument.Parse(json ?? "{}");
            return parsed.RootElement.GetProperty("props").GetProperty("pageProps").Clone();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException($"FictionBook не вернул данные страницы ({url}); возможно ограничение доступа или изменение разметки.", ex);
        }
    }

    private async Task<Book> GetModernBook(HtmlDocument doc, Uri url)
    {
        var props = ReadPageProps(doc, url);
        if (!props.TryGetProperty("book", out var item) || item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("book", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("title", out var titleValue) || titleValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(titleValue.GetString()))
            throw new InvalidOperationException($"FictionBook не вернул карточку книги ({url}).");
        var title = titleValue.GetString();
        var authorName = "FictionBook";
        Uri authorUrl = null;
        if (item.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
        {
            var parts = new[] { "first_name", "middle_name", "last_name" }
                .Select(key => author.TryGetProperty(key, out var part) && part.ValueKind == JsonValueKind.String ? part.GetString() : null)
                .Where(value => !string.IsNullOrWhiteSpace(value));
            var name = string.Join(" ", parts);
            if (!string.IsNullOrWhiteSpace(name)) authorName = name;
            if (author.TryGetProperty("alias", out var alias) && alias.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(alias.GetString()))
                authorUrl = SystemUrl.MakeRelativeUri("/author/" + Uri.EscapeDataString(alias.GetString()) + "/");
        }
        return new Book(url)
        {
            Title = title,
            Author = new Author(authorName, authorUrl),
            Annotation = metadata.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null,
            Cover = await GetCover(doc, url),
            Chapters = await GetModernChapters(url, title)
        };
    }

    private async Task<IEnumerable<Chapter>> GetModernChapters(Uri url, string title)
    {
        if (Config.Options.NoChapters) return [];
        var content = new StringBuilder();
        var pages = 1;
        for (var page = 1; page <= pages; page++)
        {
            var readerUrl = url.AppendSegment(page == 1 ? "read_online/" : $"read_online/page-{page}/");
            var props = ReadPageProps(await Config.Client.GetHtmlDocWithTriesAsync(readerUrl), readerUrl);
            if (!props.TryGetProperty("read", out var read) || read.ValueKind != JsonValueKind.Object ||
                !read.TryGetProperty("page", out var currentPage) || !currentPage.TryGetInt32(out var actualPage) || actualPage != page ||
                !read.TryGetProperty("pages", out var pageCount) || !pageCount.TryGetInt32(out var count) || count < page ||
                !read.TryGetProperty("content", out var html) || html.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(html.GetString()))
                throw new InvalidOperationException($"FictionBook не вернул ожидаемую страницу текста {page} ({readerUrl}); книга не будет сохранена неполной.");
            if (page == 1) pages = count;
            else if (count != pages)
                throw new InvalidOperationException("FictionBook изменил число страниц во время загрузки; повторите скачивание книги.");
            Config.Logger.LogInformation($"Получаю страницу {page}/{pages}");
            // Страница может начинаться продолжением section с предыдущей страницы.
            // Разбирать HTML нужно после соединения всех частей, иначе теряется структура глав.
            content.Append(html.GetString());
        }
        var document = content.AsHtmlDoc();
        var sections = document.DocumentNode.ChildNodes.Where(n => n.Name == "section").ToList();
        var chapters = new List<Chapter>();
        if (sections.Count == 0)
            chapters.Add(new Chapter { Title = title, Content = document.DocumentNode.InnerHtml });
        else
        {
            var preface = new StringBuilder();
            foreach (var node in document.DocumentNode.ChildNodes)
            {
                if (node.Name != "section")
                {
                    // Текст вне section сохраняется вместе с соседней главой.
                    if (chapters.Count == 0) preface.Append(node.OuterHtml);
                    else chapters[^1].Content += node.OuterHtml;
                    continue;
                }
                var heading = node.ChildNodes.FirstOrDefault(n => n.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6");
                var name = heading?.GetText();
                if (!string.IsNullOrWhiteSpace(name)) ChapterContentCleaner.RemoveNodePreservingAnchors(heading);
                chapters.Add(new Chapter { Title = string.IsNullOrWhiteSpace(name) ? title : name, Content = preface + node.InnerHtml });
                preface.Clear();
            }
        }
        var selected = SliceToc(chapters, c => c.Title).ToList();
        foreach (var chapter in selected)
        {
            var chapterDoc = chapter.Content.AsHtmlDoc();
            chapter.Images = await GetImages(chapterDoc, SystemUrl);
            chapter.Content = chapterDoc.DocumentNode.InnerHtml;
        }
        return selected;
    }

    private Author GetAuthor(HtmlDocument doc)
    {
        var a = doc.QuerySelector("div.info a[data-widget-litres-author][href]");
        return a == null ?
            new Author("FictionBook") :
            new Author(a.GetText(), SystemUrl.MakeRelativeUri(a.Attributes["href"].Value));
    }

    private Task<TempFile> GetCover(HtmlDocument doc, Uri bookUri)
    {
        var imagePath = (doc.QuerySelector("div.cover_float img") ?? doc.QuerySelector("img[class^='BookInfoImg_bookInfoImg__image']"))?.Attributes["src"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(bookUri.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }

    private async Task AddChapter(ICollection<Chapter> chapters, Chapter chapter, StringBuilder text)
    {
        if (chapter == null)
        {
            return;
        }

        var chapterDoc = text.AsHtmlDoc();
        chapter.Images = await GetImages(chapterDoc, SystemUrl.MakeRelativeUri("/br/"));
        chapter.Content = chapterDoc.DocumentNode.InnerHtml;
        chapters.Add(chapter);
    }

    private static bool IsChapterHeader(HtmlNode node)
    {
        return node.Name == "h2";
    }

    private static bool IsSingleChapter(IEnumerable<HtmlNode> nodes)
    {
        var firstNode = nodes.First();
        return !IsChapterHeader(firstNode) || string.IsNullOrWhiteSpace(firstNode.InnerText);
    }

    private static Chapter CreateChapter(string title)
    {
        return new Chapter
        {
            Title = title
        };
    }

    private Task<HtmlDocument> GetChapter(Uri url, int page)
    {
        return Config.Client.GetHtmlDocWithTriesAsync(url.AppendSegment("read_online.html").AppendQueryParameter("page", page));
    }

    private async Task<int> GetPages(Uri url)
    {
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url.AppendSegment("read_online.html"));
        var pages = doc.QuerySelectorAll("div.reader_pager_bottom > a[href]")
            .Where(a => int.TryParse(a.GetText(), out _))
            .Select(a => int.Parse(a.GetText()))
            .ToList();

        return pages.Any() ? pages.Max() : 1;
    }

    private async Task<List<Chapter>> FillChapters(Uri url, string name)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        Chapter chapter = null;
        var singleChapter = true;
        var text = new StringBuilder();
        var pages = await GetPages(url);

        for (var i = 1; i <= pages; i++)
        {
            Config.Logger.LogInformation($"Получаю страницу {i}/{pages}");
            var page = await GetChapter(url, i);

            var reader = page.QuerySelector("#onlineread")
                ?? throw new InvalidOperationException($"FictionBook не вернул текст страницы {i} ({url}); возможно ограничение доступа или изменение разметки.");
            var nodes = reader.RemoveNodes("div.biblio_book__wrap").ChildNodes;
            if (nodes.Count == 0)
                throw new InvalidOperationException($"FictionBook вернул пустую страницу {i} ({url}).");
            singleChapter = i == 1 ? IsSingleChapter(nodes) : singleChapter;

            foreach (var node in nodes)
            {
                if (singleChapter || !IsChapterHeader(node))
                {
                    if (node.Name == "img" && node.Attributes["src"] != null)
                    {
                        text.Append($"<img src='{node.Attributes["src"].Value}'/>");
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(node.InnerHtml))
                        {
                            text.Append(node.InnerHtml.HtmlDecode().CoverTag("p"));
                        }
                    }
                }
                else
                {
                    await AddChapter(result, chapter, text);
                    text.Clear();
                    chapter = CreateChapter(node.InnerText.HtmlDecode());
                }
            }
        }

        await AddChapter(result, chapter ?? CreateChapter(name), text);
        return result;
    }
}
