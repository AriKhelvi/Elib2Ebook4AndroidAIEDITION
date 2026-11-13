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

namespace Elib2Ebook.ExternalServices.TwilightRussia;

public class TwilightRussiaGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://twilightrussia.ru/");

    public override async Task<Book> Get(Uri url)
    {
        url = await GetMainUrl(url);
        url = SystemUrl.MakeRelativeUri($"/forum/{GetId(url)}");

        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);

        var book = new Book(url)
        {
            Cover = null, Chapters = await FillChapters(doc, url), Title = doc.GetTextBySelector("a.forumBarA"), Author = new Author(doc.GetTextBySelector("span[class^='forum_nik']"))
        };

        return book;
    }

    private async Task<Uri> GetMainUrl(Uri url)
    {
        if (url.ToString().Contains("/publ/"))
        {
            var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
            var a = doc.QuerySelector("#msgd ~ a[href*='/forum/']")
                ?? throw new InvalidOperationException($"TwilightRussia не вернул ссылку на оглавление ({url}).");
            return ReadLink(url, a.Attributes["href"].Value, "/forum/");
        }

        return url;
    }

    private async Task<IEnumerable<Chapter>> FillChapters(HtmlDocument doc, Uri url)
    {
        if (Config.Options.NoChapters)
        {
            return [];
        }

        var post = doc.QuerySelectorAll("td.content-block-forum");
        var toc = new List<UrlChapter>();
        foreach (var a in post.QuerySelectorAll("a[href*='/publ/']"))
        {
            var title = a.GetText();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            toc.Add(new UrlChapter(ReadLink(url, a.Attributes["href"].Value, "/publ/"), title));
        }
        toc = toc.DistinctBy(c => c.Url).ToList();
        if (toc.Count == 0)
            throw new InvalidOperationException($"TwilightRussia не вернул доступного оглавления ({url}); пустая книга не будет сохранена.");

        var result = new List<Chapter>();
        foreach (var chapter in SliceToc(toc, c => c.Title))
        {
            Config.Logger.LogInformation($"Загружаю главу {chapter.Title}");
            result.Add(await GetChapter(chapter.Url, chapter.Title));
        }
        return result;
    }

    private Uri ReadLink(Uri page, string source, string pathPrefix)
    {
        var link = source.HtmlDecode().Trim();
        // На реальном форуме встретился префикс https://https:// (Uri также
        // мог нормализовать его в https://https//).
        // Исправляем только дублированную схему и только при сохранении домена сайта.
        var corrected = Regex.Replace(link, "^https?://https?:?//", "https://", RegexOptions.IgnoreCase);
        if (!Uri.TryCreate(page, corrected, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || !SystemUrl.IsSameHost(uri) || !uri.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"TwilightRussia: повреждённая ссылка в оглавлении ({page}): {source}");
        if (corrected != link)
            Config.Logger.LogWarning($"TwilightRussia: исправлена дублированная схема ссылки: {link} → {uri}");
        return uri;
    }

    private async Task<Chapter> GetChapter(Uri url, string title)
    {
        var chapter = new Chapter();

        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);

        var text = new StringBuilder();
        var content = doc.QuerySelector("#msgd")
            ?? throw new InvalidOperationException($"TwilightRussia не вернул текст главы ({url}); возможно ограничение доступа или изменение разметки.");
        foreach (var node in content.ChildNodes)
        {
            var img = node.QuerySelector("img[src]");
            if (img != null)
            {
                text.Append($"<img src='{img.Attributes["src"].Value}'/>");
                continue;
            }

            var nodeText = node.GetText();
            if (!string.IsNullOrWhiteSpace(nodeText))
            {
                text.Append(nodeText.CoverTag("p"));
            }
        }

        var chapterDoc = text.AsHtmlDoc();
        chapter.Images = await GetImages(chapterDoc, url);
        chapter.Content = chapterDoc.DocumentNode.InnerHtml;
        chapter.Title = title;

        return chapter;
    }
}
