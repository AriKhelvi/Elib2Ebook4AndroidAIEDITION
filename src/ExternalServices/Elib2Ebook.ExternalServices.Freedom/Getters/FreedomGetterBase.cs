using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Freedom.Getters;

public abstract class FreedomGetterBase(BookGetterConfig config) : GetterBase(config)
{
    public override async Task<Book> Get(Uri url)
    {
        url = await GetMainUrl(url);
        url = SystemUrl.MakeRelativeUri($"/ranobe/{GetId(url)}/");
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        var title = (doc.QuerySelector("h1.entry-title") ?? doc.QuerySelector("div.book-info h1"))?.GetText();
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException($"{url.Host}: не найдено название книги ({url}); " +
                "возможно ограничение доступа или изменение разметки. Пустая книга не будет сохранена.");

        var book = new Book(url)
        {
            Cover = await GetCover(doc, url), Chapters = await FillChapters(doc, url), Title = title, Author = GetAuthor()
        };

        return book;
    }

    private async Task<Uri> GetMainUrl(Uri url)
    {
        if (url.GetSegment(1) == "ranobe")
        {
            return url;
        }

        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        var link = (doc.QuerySelector("div.bun2 a[href]") ?? doc.QuerySelector("#homepage[data-page=chapter] a[href*='/ranobe/']"))?.Attributes["href"]?.Value;
        if (string.IsNullOrWhiteSpace(link))
            throw new InvalidOperationException($"{url.Host}: не найдена ссылка на карточку книги ({url}).");
        return url.MakeRelativeUri(link);
    }

    private IEnumerable<UrlChapter> GetToc(HtmlDocument doc, Uri url)
    {
        var result = doc
            .QuerySelectorAll("div.li-col1-ranobe > a[href], div.chapterlinks div.chapterinfo > a[href]")
            .Select(a => new UrlChapter(url.MakeRelativeUri(a.Attributes["href"].Value), a.GetText()))
            .Reverse()
            .ToList();

        if (result.Count == 0)
            throw new InvalidOperationException($"{url.Host}: нет доступного оглавления ({url}); пустая книга не будет сохранена.");

        return SliceToc(result, c => c.Title);
    }

    private async Task<IEnumerable<Chapter>> FillChapters(HtmlDocument doc, Uri uri)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        foreach (var urlChapter in GetToc(doc, uri))
        {
            // Платные пункты остаются в полном оглавлении до SliceToc: иначе
            // запрошенные позиции сдвинутся. Страница подписки не является главой.
            if (urlChapter.Url.AbsolutePath.TrimEnd('/') == "/podpiska")
            {
                Config.Logger.LogWarning($"Глава {urlChapter.Title.CoverQuotes()} требует подписки и не включена в книгу");
                continue;
            }
            var chapter = new Chapter
            {
                Title = urlChapter.Title
            };

            Config.Logger.LogInformation($"Загружаю главу {urlChapter.Title.CoverQuotes()}");

            var chapterDoc = await GetChapter(urlChapter.Url);
            if (chapterDoc == null) continue;
            chapter.Images = await GetImages(chapterDoc, urlChapter.Url);
            chapter.Content = chapterDoc.DocumentNode.InnerHtml;

            result.Add(chapter);
        }

        if (result.Count == 0)
            throw new InvalidOperationException($"{uri.Host}: в выбранном диапазоне нет доступного текста; пустая книга не будет сохранена.");

        return result;
    }

    private async Task<HtmlDocument> GetChapter(Uri url)
    {
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        var content = doc.QuerySelector("div.entry-content") ?? doc.QuerySelector("div.chapter-content")
            ?? throw new InvalidOperationException($"{url.Host}: не найден текст главы ({url}); возможно ограничение доступа или изменение разметки.");
        var notice = content.QuerySelector("div.single-notice");
        if (notice?.GetText() == "Для чтения купите главу.")
        {
            Config.Logger.LogWarning($"Глава {url} требует покупки и не включена в книгу");
            return null;
        }
        return content.InnerHtml.AsHtmlDoc().RemoveNodes("div[class*=adv]");
    }

    private static Author GetAuthor()
    {
        return new Author("Ifreedom");
    }

    private Task<TempFile> GetCover(HtmlDocument doc, Uri uri)
    {
        var imagePath = (doc.QuerySelector("div.img-ranobe img") ?? doc.QuerySelector("div.book-img img"))?.Attributes["src"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(uri.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }
}
