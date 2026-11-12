using System.Text;
using System.Web;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Libbox;

public class LibboxGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://libbox.ru/");

    public override async Task<Book> Get(Uri url)
    {
        var bookId = GetId(url);
        url = SystemUrl.MakeRelativeUri($"/book/{bookId}");

        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);

        var title = doc.GetTextBySelector("h1.product__title");
        var book = new Book(url)
        {
            Cover = await GetCover(doc, url),
            Chapters = await FillChapters(bookId, title),
            Title = title,
            Author = GetAuthor(doc, url),
            Annotation = doc.QuerySelector("div.woocommerce-Tabs-panel--description div.container")?.InnerHtml,
            Seria = GetSeria(doc, url)
        };

        return book;
    }

    private static Seria GetSeria(HtmlDocument doc, Uri url)
    {
        var a = doc.QuerySelector("div.product__meta a[href*=/book-series/]");
        if (string.IsNullOrWhiteSpace(a.GetText()))
        {
            return null;
        }

        return new Seria
        {
            Name = a.GetText(), Url = url.MakeRelativeUri(a.Attributes["href"].Value)
        };
    }

    private async Task AddChapter(ICollection<Chapter> chapters, Chapter chapter, StringBuilder text)
    {
        if (chapter == null)
        {
            return;
        }

        var chapterDoc = text.AsHtmlDoc();
        chapter.Images = await GetImages(chapterDoc, SystemUrl);
        chapter.Content = chapterDoc.DocumentNode.InnerHtml;
        chapters.Add(chapter);
    }

    private static bool IsSingleChapter(IEnumerable<HtmlNode> nodes)
    {
        var firstNode = nodes.First();
        return firstNode.Name != "h2" || string.IsNullOrWhiteSpace(firstNode.InnerText);
    }

    private static Chapter CreateChapter(string title)
    {
        return new Chapter
        {
            Title = title
        };
    }

    private async Task<int> GetPages(string bookId)
    {
        var url = SystemUrl.MakeRelativeUri($"/books/{bookId}");
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        if (doc.QuerySelector("div.entry-content") == null)
            throw new InvalidOperationException("Libbox не вернул текст книги. Проверьте доступ к книге.");
        return GetPageCount(doc, url);
    }

    private static int GetPageCount(HtmlDocument doc, Uri url)
    {
        var pages = 1;
        foreach (var link in doc.QuerySelectorAll("ul.pagination a.page-numbers"))
        {
            if (int.TryParse(link.GetText(), out var number)) pages = Math.Max(pages, number);
            if (Uri.TryCreate(url, link.GetAttributeValue("href", string.Empty), out var next)
                && next.Authority == url.Authority && next.AbsolutePath.TrimEnd('/') == url.AbsolutePath.TrimEnd('/')
                && int.TryParse(HttpUtility.ParseQueryString(next.Query)["page_book"], out number))
                pages = Math.Max(pages, number);
        }
        if (pages > 10000) throw new InvalidOperationException("Libbox вернул слишком много страниц; загрузка остановлена.");
        return pages;
    }

    private async Task<IEnumerable<Chapter>> FillChapters(string bookId, string title)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        Chapter chapter = null;
        var singleChapter = true;
        var text = new StringBuilder();
        var pages = await GetPages(bookId);

        for (var i = 1; i <= pages; i++)
        {
            Config.Logger.LogInformation($"Получаю страницу {i}/{pages}");
            var page = await Config.Client.GetHtmlDocWithTriesAsync(SystemUrl.MakeRelativeUri($"/books/{bookId}?page_book={i}"));
            pages = Math.Max(pages, GetPageCount(page, SystemUrl.MakeRelativeUri($"/books/{bookId}")));

            var content = page.QuerySelector("div.entry-content");
            if (content == null) throw new InvalidOperationException($"Libbox не вернул текст страницы {i}. Неполная книга не будет сохранена.");
            var nodes = content.QuerySelectorAll("> h2, > p, > img");
            nodes = nodes.Count == 0 ? content.RemoveNodes("script, ins").ChildNodes : nodes;
            if (nodes.Count == 0) throw new InvalidOperationException($"Libbox вернул пустую страницу {i}.");
            singleChapter = i == 1 ? IsSingleChapter(nodes) : singleChapter;

            foreach (var node in nodes)
            {
                if (singleChapter || node.Name != "h2")
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
                    chapter = CreateChapter(node.InnerHtml.HtmlDecode());
                }
            }
        }

        await AddChapter(result, chapter ?? CreateChapter(title), text);
        return result;
    }

    private static Author GetAuthor(HtmlDocument doc, Uri url)
    {
        var author = doc.QuerySelector("div.author a");
        return new Author(author.InnerText.HtmlDecode(), url.MakeRelativeUri(author.Attributes["href"]?.Value ?? string.Empty));
    }

    private Task<TempFile> GetCover(HtmlDocument doc, Uri bookUri)
    {
        var imagePath = doc.QuerySelector("div.book-images img")?.Attributes["src"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(bookUri.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }
}
