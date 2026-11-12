using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Younettranslate;

public class YounettranslateGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://younettranslate.com/");

    protected override string GetId(Uri url)
    {
        return url.GetSegment(2);
    }

    public override async Task<Book> Get(Uri url)
    {
        url = SystemUrl.MakeRelativeUri($"/projects/{GetId(url)}");
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);

        var book = new Book(url)
        {
            Cover = await GetCover(url),
            Chapters = await FillChapters(url),
            Title = GetTitle(doc),
            Author = GetAuthor(doc),
            Annotation = doc.QuerySelector("p.short-p")?.InnerHtml
        };

        return book;
    }

    private Author GetAuthor(HtmlDocument doc)
    {
        foreach (var div in doc.QuerySelectorAll("div.col-md-3"))
        {
            var label = div.QuerySelector("p.small-label");
            if (label != null && label.GetText().StartsWith("Автор"))
            {
                return new Author(label.GetTextBySelector("+ p"));
            }
        }

        return new Author("Younettranslate");
    }

    private static string GetTitle(HtmlDocument doc)
    {
        return doc.GetTextBySelector("h1");
    }

    private async Task<IEnumerable<Chapter>> FillChapters(Uri url)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        foreach (var urlChapter in await GetToc(url))
        {
            Config.Logger.LogInformation($"Загружаю главу {urlChapter.Title.CoverQuotes()}");
            var chapter = new Chapter
            {
                Title = urlChapter.Title
            };

            var chapterDoc = await GetChapter(urlChapter);

            if (chapterDoc != null)
            {
                chapter.Images = await GetImages(chapterDoc, SystemUrl);
                chapter.Content = chapterDoc.DocumentNode.InnerHtml;
            }

            result.Add(chapter);
        }

        return result;
    }

    private async Task<IEnumerable<UrlChapter>> GetToc(Uri url)
    {
        var result = new List<UrlChapter>();
        var chapterUrls = new HashSet<Uri>();

        for (var i = 1;; i++)
        {
            var doc = await Config.Client.GetHtmlDocWithTriesAsync(url.AppendQueryParameter("page", i));
            if (doc.QuerySelector("table.catposts-table") == null)
            {
                if (i == 1)
                    throw new InvalidOperationException("Не найдено оглавление Younettranslate на первой странице. Возможно, сайт ограничил доступ.");
                // Сайт может убирать таблицу после последней страницы. Без подтверждённого
                // признака конца нельзя ни считать книгу полной, ни отбрасывать собранные главы.
                Config.Logger.LogWarning($"Не удалось подтвердить полноту оглавления Younettranslate: на странице {i} нет таблицы глав. Получено {result.Count} глав; проверьте их количество на сайте.");
                break;
            }
            var links = doc.QuerySelectorAll("table.catposts-table td.hidden-sm a[href]").Select(a => new UrlChapter(url.MakeRelativeUri(a.Attributes["href"].Value), a.GetText())).ToList();
            if (links.Count == 0)
            {
                break;
            }

            var newLinks = links.Where(chapter => chapterUrls.Add(chapter.Url)).ToList();
            if (newLinks.Count == 0 || i > 10000)
                throw new InvalidOperationException($"Оглавление Younettranslate повторяется или содержит слишком много страниц (страница {i}). Загрузка остановлена.");
            result.AddRange(newLinks);
        }

        return SliceToc(result, c => c.Title);
    }

    private async Task<HtmlDocument> GetChapter(UrlChapter urlChapter)
    {
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(urlChapter.Url);
        return doc.QuerySelector("div.postdata-content").RemoveNodes(n => n.InnerText.Contains("ЗАПРЕЩЕНО ПУБЛИКОВАТЬ")).InnerHtml.AsHtmlDoc();
    }

    private async Task<TempFile> GetCover(Uri bookUri)
    {
        var doc = await Config.Client.GetHtmlDocWithTriesAsync("https://younettranslate.com/projects/".AsUri());

        var imagePath = doc.QuerySelector($"div.category-item a[href='{bookUri}'] img")?.Attributes["src"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? await SaveImage(bookUri.MakeRelativeUri(imagePath)) : null;
    }
}
