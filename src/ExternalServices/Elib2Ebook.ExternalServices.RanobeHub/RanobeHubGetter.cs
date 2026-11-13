using System.Net.Http.Json;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.ExternalServices.RanobeHub.Types;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.RanobeHub;

public class RanobeHubGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://ranobehub.org/");

    public override async Task<Book> Get(Uri url)
    {
        url = SystemUrl.MakeRelativeUri($"/ranobe/{GetId(url)}");
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        var title = doc.GetTextBySelector("h1.header");
        if (string.IsNullOrWhiteSpace(title) || doc.QuerySelector("html[data-id]") == null)
            throw new InvalidOperationException($"RanobeHub вернул страницу в неподдерживаемой разметке ({url}). " +
                "Не удалось прочитать название и идентификатор книги; загрузчик требует обновления.");

        var book = new Book(url)
        {
            Cover = await GetCover(doc, url), Chapters = await FillChapters(doc, url), Title = title, Author = new Author("RanobeHub")
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

        foreach (var ranobeChapter in await GetToc(doc))
        {
            Config.Logger.LogInformation($"Загружаю главу {ranobeChapter.Name}");
            var chapter = new Chapter();

            var chapterDoc = await GetChapter(ranobeChapter.Url);
            chapter.Images = await GetImages(chapterDoc, url);
            chapter.Content = chapterDoc.DocumentNode.InnerHtml;
            chapter.Title = ranobeChapter.Name;

            result.Add(chapter);
        }

        return result;
    }

    private async Task<HtmlDocument> GetChapter(string url)
    {
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url.AsUri());
        if (doc.QuerySelector("div[data-callback=correctCaptcha]") != null)
            throw new InvalidOperationException($"RanobeHub требует CAPTCHA ({url}); текст главы не получен.");

        var content = doc.QuerySelector("div.container[data-container]")
            ?? throw new InvalidOperationException($"RanobeHub не вернул текст главы ({url}); возможно ограничение доступа или изменение разметки.");
        var result = content.RemoveNodes("div.title-wrapper, div.ads-desktop, div.tablet, div.chapter-hoticons").InnerHtml.AsHtmlDoc();

        foreach (var img in result.QuerySelectorAll("img"))
        {
            var id = img.Attributes["data-media-id"]?.Value;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (img.Attributes.Contains("src"))
            {
                img.Attributes["src"].Value = $"/api/media/{id}";
            }
            else
            {
                img.Attributes.Add("src", $"/api/media/{id}");
            }
        }

        return result;
    }

    private async Task<IEnumerable<RanobeHubChapter>> GetToc(HtmlDocument doc)
    {
        var internalId = doc.QuerySelector("html[data-id]").Attributes["data-id"].Value;
        var response = await Config.Client.GetFromJsonAsync<RanobeHubApiResponse>(SystemUrl.MakeRelativeUri($"/api/ranobe/{internalId}/contents"));
        var chapters = response?.Volumes?.SelectMany(t => t.Chapters ?? []).ToList();
        if (chapters == null || chapters.Count == 0)
            throw new InvalidOperationException("RanobeHub не вернул доступного оглавления; пустая книга не будет сохранена.");
        return SliceToc(chapters, c => c.Name);
    }

    private Task<TempFile> GetCover(HtmlDocument doc, Uri bookUri)
    {
        var imagePath = doc.QuerySelector("div.poster-slider img")?.Attributes["data-src"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(bookUri.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }
}
