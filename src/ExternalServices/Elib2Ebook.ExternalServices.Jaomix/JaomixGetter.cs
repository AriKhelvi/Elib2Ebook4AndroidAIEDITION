using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Jaomix;

public class JaomixGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://jaomix.ru/");

    protected override string GetId(Uri url)
        => url.GetSegment(1);

    public override async Task<Book> Get(Uri url)
    {
        url = SystemUrl.MakeRelativeUri($"/{GetId(url)}/");
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        var title = doc.GetTextBySelector("h1");
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException($"Jaomix не вернул название книги ({url}); возможно ограничение доступа или изменение разметки.");

        var book = new Book(url)
        {
            Cover = await GetCover(doc, url), Chapters = await FillChapters(doc, url), Title = title, Author = new Author("Jaomix")
        };

        return book;
    }

    private async Task<IEnumerable<Chapter>> FillChapters(HtmlDocument doc, Uri url)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        foreach (var jaomixChapter in await GetToc(doc, url))
        {
            Config.Logger.LogInformation($"Загружаю главу {jaomixChapter.Title.CoverQuotes()}");
            var chapter = new Chapter();
            var chapterDoc = await GetChapter(jaomixChapter);
            chapter.Images = await GetImages(chapterDoc, url);
            chapter.Content = chapterDoc.DocumentNode.InnerHtml;
            chapter.Title = jaomixChapter.Title;

            result.Add(chapter);
        }

        return result;
    }

    private async Task<HtmlDocument> GetChapter(UrlChapter chapter)
    {
        var url = chapter.Url;
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        if (doc.QuerySelector("div.h-captcha, div.but-captcha") != null)
            throw new InvalidOperationException($"Jaomix требует CAPTCHA ({url}); текст главы не получен.");
        var content = doc.QuerySelector("div.themeform") ?? doc.QuerySelector("div.entry-content")
            ?? throw new InvalidOperationException($"Jaomix не вернул текст главы ({url}); возможно ограничение доступа или изменение разметки.");
        content.RemoveNodes("script, .adblock-service, .lazyblock");
        ChapterContentCleaner.RemoveLeadingTitle(content, chapter.Title);
        if (string.IsNullOrWhiteSpace(content.GetText()) && content.QuerySelector("img") == null)
            throw new InvalidOperationException($"Jaomix вернул пустую главу ({url}).");
        return content.InnerHtml.AsHtmlDoc();
    }

    private async Task<IEnumerable<UrlChapter>> GetToc(HtmlDocument doc, Uri url)
    {
        var chapters = ParseChapters(doc, url).ToList();
        // В текущей разметке сайт показывает только 50 новых глав. Остальные порции
        // загружает loadChaptList из собственного скрипта сайта; книгу определяет Referer.
        var pages = doc.QuerySelector("form.download-chapter .hiddenstab") != null
            ? Enumerable.Empty<string>() // Старая разметка уже содержит все скрытые порции.
            : doc.QuerySelectorAll("select.sel-toc option[value]").Select(o => o.Attributes["value"].Value).Distinct();
        foreach (var page in pages)
        {
            if (!int.TryParse(page, out var pageNumber) || pageNumber < 1)
                throw new InvalidOperationException("Jaomix вернул неизвестный формат пагинации оглавления.");
            if (pageNumber == 1) continue;
            using var response = await Config.Client.SendWithTriesAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, SystemUrl.MakeRelativeUri("/wp-admin/admin-ajax.php"))
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["action"] = "loadpagenavchapstt", ["page"] = page
                    })
                };
                request.Headers.Referrer = url;
                return request;
            });
            response.EnsureDownloadSuccess();
            var next = ParseChapters((await response.Content.ReadAsStringAsync()).AsHtmlDoc(), url).ToList();
            if (next.Count == 0 || next.All(c => chapters.Any(existing => existing.Url == c.Url)))
                throw new InvalidOperationException($"Jaomix не вернул новые главы порции {page}; оглавление неполное, загрузка остановлена.");
            chapters.AddRange(next);
        }
        chapters = chapters.DistinctBy(c => c.Url).Reverse().ToList();
        if (chapters.Count == 0)
            throw new InvalidOperationException($"Jaomix не вернул доступного оглавления ({url}); пустая книга не будет сохранена.");
        Config.Logger.LogInformation($"Получено {chapters.Count} глав");

        return SliceToc(chapters, c => c.Title);
    }

    private Task<TempFile> GetCover(HtmlDocument doc, Uri bookUri)
    {
        var imagePath = doc.QuerySelector("div.img-book img")?.Attributes["src"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(bookUri.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }

    private static IEnumerable<UrlChapter> ParseChapters(HtmlDocument doc, Uri url)
    {
        return doc.QuerySelectorAll("form.download-chapter .hiddenstab .flex-dow-txt a[href], #chapters-book .flex-dow-txt a[href], .columns-toc .flex-dow-txt a[href]")
            .Select(a => new UrlChapter(url.MakeRelativeUri(a.Attributes["href"].Value), a.GetText()));
    }
}
