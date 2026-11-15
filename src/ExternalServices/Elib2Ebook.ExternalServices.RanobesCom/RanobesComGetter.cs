using System.Text;
using System.Text.RegularExpressions;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.RanobesCom;

public class RanobesComGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://ranobes.com/");

    protected override string GetId(Uri url)
        => base.GetId(url).Split(".")[0];

    public override async Task<Book> Get(Uri url)
    {
        url = await GetMainUrl(url);
        url = SystemUrl.MakeRelativeUri($"/ranobe/{GetId(url)}.html");
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        // Старый antibot8/ab.php удалён сайтом. Нельзя посылать туда запрос на каждой
        // обычной карточке и принимать HTML ошибки за JSON; проверку браузера не обходим.
        var titleNode = doc.QuerySelector("h1.title");
        var title = titleNode?.FirstChild?.InnerText?.Trim().HtmlDecode();
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("Ranobes не вернул карточку книги. Возможна проверка браузера, ограничение доступа или изменение разметки.");

        var book = new Book(url)
        {
            Cover = await GetCover(doc, url),
            Chapters = await FillChapters(doc, url),
            Title = title,
            Author = new Author(doc.GetTextBySelector("span[itemprop=creator]") ?? "Ranobes"),
            Annotation = doc.QuerySelector("div[itemprop=description]")?.RemoveNodes("style")?.InnerHtml
        };

        return book;
    }

    private async Task<Uri> GetMainUrl(Uri url)
    {
        if (url.GetSegment(1) == "chapters" || !url.Segments.Last().EndsWith(".html"))
        {
            var doc = await Config.Client.GetHtmlDocWithTriesAsync(SystemUrl.MakeRelativeUri(url.AbsolutePath));
            return url.MakeRelativeUri(doc.QuerySelector("a[rel=up]").Attributes["href"].Value);
        }

        return url;
    }

    private async Task<IEnumerable<Chapter>> FillChapters(HtmlDocument doc, Uri url)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        foreach (var ranobeChapter in await GetToc(GetTocLink(doc, url)))
        {
            Config.Logger.LogInformation($"Загружаю главу {ranobeChapter.Title.CoverQuotes()}");
            var chapter = new Chapter();
            var chapterDoc = await GetChapter(ranobeChapter);
            chapter.Images = await GetImages(chapterDoc, url);
            chapter.Content = chapterDoc.DocumentNode.InnerHtml;
            chapter.Title = ranobeChapter.Title;

            result.Add(chapter);
        }

        return result;
    }

    private async Task<HtmlDocument> GetChapter(UrlChapter chapter)
    {
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(chapter.Url);
        var sb = new StringBuilder();
        foreach (var node in doc.QuerySelectorAll("#arrticle > :not(.splitnewsnavigation)"))
        {
            var tag = node.Name == "#text" ? "p" : node.Name;
            if (tag == "img")
            {
                sb.Append(node.OuterHtml.Trim().CoverTag(tag));
            }
            else
            {
                if (node.InnerHtml?.Contains("window.yaContextCb") == false)
                {
                    sb.Append(node.InnerHtml.CoverTag(tag));
                }
            }
        }

        return sb.AsHtmlDoc().RemoveNodes(t => t.Name is "script" or "br" || t.Id?.Contains("yandex_rtb") == true);
    }

    private Task<TempFile> GetCover(HtmlDocument doc, Uri bookUri)
    {
        var imagePath = doc.QuerySelector("div.poster img")?.Attributes["src"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(bookUri.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }

    private Uri GetTocLink(HtmlDocument doc, Uri uri)
    {
        var relativeUri = doc.QuerySelector("div.r-fullstory-btns a[title~=оглавление]").Attributes["href"].Value.HtmlDecode();
        if (!relativeUri.Contains("chapters"))
        {
            relativeUri = $"/chapters/{string.Join("-", GetId(uri).Split(".")[0].Split("-").Skip(1))}";
        }

        return uri.MakeRelativeUri(relativeUri.AsUri().AbsolutePath.Trim('/'));
    }

    private async Task<IEnumerable<UrlChapter>> GetToc(Uri tocUri)
    {
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(tocUri);
        Config.Logger.LogInformation("Получаю оглавление");
        var result = new List<UrlChapter>();
        var urls = new HashSet<Uri>();
        var pages = 1;
        for (var i = 1; i <= pages; i++)
        {
            if (i > 1) doc = await Config.Client.GetHtmlDocWithTriesAsync(tocUri.AppendSegment($"/page/{i}"));
            foreach (var link in doc.QuerySelectorAll("div.pages a[href]"))
            {
                if (!Uri.TryCreate(tocUri, link.GetAttributeValue("href", string.Empty), out var next) ||
                    next.Scheme != tocUri.Scheme || next.Authority != tocUri.Authority) continue;
                var match = Regex.Match(next.AbsolutePath,
                    "^" + Regex.Escape(tocUri.AbsolutePath.TrimEnd('/')) + @"/page/(?<page>\d+)/?$");
                if (!match.Success || !int.TryParse(match.Groups["page"].Value, out var page)) continue;
                if (page > 10000) throw new InvalidOperationException("Слишком много страниц оглавления Ranobes. Загрузка остановлена.");
                pages = Math.Max(pages, page);
            }

            var chapters = doc
                .QuerySelectorAll("#dle-content > .cat_block.cat_line a[title]")
                .Select(a => new UrlChapter(new Uri(tocUri, a.Attributes["href"].Value), string.IsNullOrWhiteSpace(a.Attributes["title"].Value) ? "Без названия" : a.Attributes["title"].Value))
                .ToList();

            var newChapters = chapters.Where(chapter => urls.Add(chapter.Url)).ToList();
            if (newChapters.Count == 0)
                throw new InvalidOperationException($"Оглавление Ranobes на странице {i} пусто или повторяется. Неполная книга не будет сохранена.");
            result.AddRange(newChapters);
        }

        Config.Logger.LogInformation($"Получено {result.Count} глав");

        result.Reverse();
        return SliceToc(result, c => c.Title);
    }
}
