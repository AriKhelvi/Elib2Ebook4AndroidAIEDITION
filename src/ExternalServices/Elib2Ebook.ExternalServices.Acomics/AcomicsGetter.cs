using System.Net;
using System.Text;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Acomics;

public class AcomicsGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://acomics.ru");

    protected override string GetId(Uri url)
        => url.GetSegment(1);

    public override async Task Init()
    {
        await base.Init();
        Config.CookieContainer.Add(SystemUrl, new Cookie("ageRestrict", "18"));
    }

    public override async Task<Book> Get(Uri url)
    {
        var id = GetId(url);
        url = SystemUrl.MakeRelativeUri(id);

        var doc = await Config.Client.GetHtmlDocWithTriesAsync(SystemUrl.MakeRelativeUri($"{id}/1"));
        CheckAvailability(doc);
        var title = doc.QuerySelector("meta[property=og:title]")?.GetAttributeValue("content", null);
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("Acomics не вернул карточку комикса. Проверьте доступность сайта в браузере; возможно, изменилась его разметка.");
        var book = new Book(url)
        {
            Chapters = await FillChapters(doc, id, title, url),
            Title = title,
            Author = GetAuthor(doc, url),
            Annotation = doc.QuerySelector("div.serial-description div.s-gap")?.NextSibling?.InnerHtml
        };

        book.Cover = book.Chapters.FirstOrDefault()?.Images.FirstOrDefault();

        return book;
    }

    private static Author GetAuthor(HtmlDocument doc, Uri url)
    {
        var a = doc.QuerySelector("article.authors a[href],a.username[href]");
        return a == null ? new Author("Acomics") : new Author(a.GetText(), url.MakeRelativeUri(a.Attributes["href"].Value));
    }

    private static void CheckAvailability(HtmlDocument doc)
    {
        if (doc.DocumentNode.InnerText.Contains("сайт недоступен в вашем регионе", StringComparison.OrdinalIgnoreCase))
            throw new SiteAccessException(SiteAccessReason.AccessDenied, "Acomics сообщил, что сайт недоступен в вашем регионе. Вместо комикса получена страница ограничения доступа.");
    }

    private async Task<IEnumerable<Chapter>> FillChapters(HtmlDocument doc, string bookId, string title, Uri url)
    {
        if (Config.Options.NoChapters)
        {
            return [];
        }

        var chapter = new Chapter
        {
            Title = title
        };

        var pageCount = doc.GetTextBySelector("span.issueNumber,span.number-without-name,span.number-with-name")?.Split('/').Last();
        if (!int.TryParse(pageCount, out var pages) || pages <= 0 || pages > 100000)
            throw new InvalidOperationException("Acomics не вернул количество страниц комикса. Возможно, изменилась разметка сайта.");
        var sb = new StringBuilder();
        foreach (var i in SliceToc(Enumerable.Range(1, pages).ToArray(), page => page.ToString()))
        {
            Config.Logger.LogInformation($"Получаю страницу {i}/{pages}");
            var response = i == 1 ? doc : await Config.Client.GetHtmlDocWithTriesAsync(SystemUrl.MakeRelativeUri($"{bookId}/{i}"));
            CheckAvailability(response);
            var img = response.QuerySelector("#mainImage") ?? response.QuerySelector("img.issue");
            var imagePath = img?.GetAttributeValue("src", null);
            if (string.IsNullOrWhiteSpace(imagePath))
                throw new InvalidOperationException($"Acomics не вернул изображение страницы {i}. Неполный комикс не будет сохранён.");
            var src = url.MakeRelativeUri(imagePath);
            sb.Append($"<img src='{src}'/>");
        }

        if (sb.Length == 0)
            return [];
        var chapterDoc = sb.AsHtmlDoc();
        chapter.Images = await GetImages(chapterDoc, url);
        chapter.Content = chapterDoc.DocumentNode.InnerHtml;

        return
        [
            chapter,
        ];
    }
}
