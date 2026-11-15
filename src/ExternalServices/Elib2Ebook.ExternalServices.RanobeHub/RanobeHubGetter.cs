using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        var modern = doc.QuerySelector("html[data-site-version=modern]") != null;
        var title = doc.GetTextBySelector(modern ? "h1" : "h1.header");
        if (string.IsNullOrWhiteSpace(title) || (!modern && doc.QuerySelector("html[data-id]") == null))
            throw new InvalidOperationException($"RanobeHub вернул страницу в неподдерживаемой разметке ({url}). " +
                "Не удалось прочитать название и идентификатор книги; загрузчик требует обновления.");

        var authors = doc.QuerySelectorAll(".book-author-byline a.is-author")
            .Select(node => (Name: node.GetTextBySelector("strong"), Url: node.GetAttributeValue("href", null)))
            .Where(author => !string.IsNullOrWhiteSpace(author.Name))
            .Select(author => new Author(author.Name, string.IsNullOrWhiteSpace(author.Url) ? null : SystemUrl.MakeRelativeUri(author.Url)))
            .ToList();

        var book = new Book(url)
        {
            Cover = await GetCover(doc, url), Chapters = await FillChapters(doc, url), Title = title,
            Author = authors.FirstOrDefault() ?? new Author("RanobeHub"), CoAuthors = authors.Skip(1).ToList(),
            Annotation = doc.QuerySelector(".book-description-copy")?.InnerHtml
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

        var toc = doc.QuerySelector("html[data-site-version=modern]") != null
            ? await GetModernToc(url) : await GetToc(doc);
        foreach (var ranobeChapter in toc)
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

        var chapterId = Regex.Match(new Uri(url).AbsolutePath, @"/chapter/(?<id>\d+)/?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var content = chapterId.Success
            ? doc.QuerySelector("#reader-content-" + chapterId.Groups["id"].Value)
            : doc.QuerySelector("div.container[data-container]");
        content = content
            ?? throw new InvalidOperationException($"RanobeHub не вернул текст главы ({url}); возможно ограничение доступа или изменение разметки.");
        var result = new HtmlDocument();
        result.LoadHtml(content.RemoveNodes("div.title-wrapper, div.ads-desktop, div.tablet, div.chapter-hoticons").InnerHtml);
        if (string.IsNullOrWhiteSpace(result.DocumentNode.InnerText.HtmlDecode()) && result.QuerySelector("img") == null)
            throw new InvalidOperationException($"RanobeHub вернул пустой текст главы ({url}); пустая глава не будет сохранена.");

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

    private async Task<IEnumerable<RanobeHubChapter>> GetModernToc(Uri bookUrl)
    {
        var idMatch = Regex.Match(bookUrl.AbsolutePath, @"^/ranobe/(?<id>\d+)(?:-[^/]+)?/?$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!idMatch.Success || !long.TryParse(idMatch.Groups["id"].Value, out var bookId) || bookId <= 0)
            throw new InvalidOperationException("RanobeHub: не удалось определить книгу для современного оглавления.");

        var result = new List<RanobeHubChapter>();
        var seen = new HashSet<long>();
        var offset = 0;
        for (var page = 0; page < 10000; page++)
        {
            // Штатный открытый endpoint читалки; Translator API требует отдельных прав и здесь не используется.
            var uri = SystemUrl.MakeRelativeUri($"/api/books/{bookId}/chapters?sort=asc&offset={offset}&limit=180");
            using var response = await Config.Client.GetAsync(uri, Config.CancellationToken);
            response.EnsureDownloadSuccess();
            RanobeHubReaderContents contents;
            try
            {
                contents = await response.Content.ReadFromJsonAsync<RanobeHubReaderContents>(cancellationToken: Config.CancellationToken);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("RanobeHub вернул неподдерживаемый ответ оглавления; проверьте доступ к сайту.");
            }

            if (contents?.Items == null || contents.Total is null or < 0)
                throw new InvalidOperationException("RanobeHub не вернул корректное оглавление современной читалки.");
            Config.Logger.LogDebug("RanobeHub: оглавление offset={Offset}, получено={Count}, всего={Total}, next={Next}.",
                offset, contents.Items.Length, contents.Total, contents.NextOffset);
            foreach (var item in contents.Items)
            {
                if (item == null || item.Id <= 0 || item.BookId != bookId || item.IsDraft)
                    throw new InvalidOperationException("RanobeHub вернул недоступную или некорректную главу; оглавление не будет сохранено частично.");
                if (item.ScheduledFor != null)
                {
                    if (!DateTimeOffset.TryParse(item.ScheduledFor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var scheduledFor))
                        throw new InvalidOperationException("RanobeHub вернул некорректную дату публикации главы; доступность не подтверждена.");
                    if (scheduledFor > DateTimeOffset.UtcNow)
                        throw new InvalidOperationException("RanobeHub вернул главу с будущей датой публикации; оглавление не будет сохранено частично.");
                }
                if (!seen.Add(item.Id))
                    throw new InvalidOperationException("RanobeHub повторил главу в оглавлении; полнота списка не подтверждена.");
                var name = string.IsNullOrWhiteSpace(item.Title) ? $"Глава {item.Number}" : item.Title;
                result.Add(new RanobeHubChapter
                {
                    Name = $"Том {item.Volume}. {name}",
                    Url = bookUrl.MakeRelativeUri(bookUrl.AbsolutePath.TrimEnd('/') + "/chapter/" + item.Id).ToString()
                });
            }

            if (contents.NextOffset == null)
            {
                if (result.Count == 0)
                    throw new InvalidOperationException("RanobeHub не вернул доступных глав; проверьте публикацию и доступ к книге.");
                if (result.Count != contents.Total)
                    throw new InvalidOperationException($"RanobeHub вернул неполное оглавление: {result.Count} из {contents.Total} глав.");
                return SliceToc(result, chapter => chapter.Name);
            }
            if (contents.Items.Length == 0 || contents.NextOffset <= offset)
                throw new InvalidOperationException("RanobeHub не продвинулся к следующей странице оглавления.");
            offset = contents.NextOffset.Value;
        }
        throw new InvalidOperationException("RanobeHub превысил предел страниц оглавления; полнота списка не подтверждена.");
    }

    private async Task<IEnumerable<RanobeHubChapter>> GetToc(HtmlDocument doc)
    {
        var internalId = doc.QuerySelector("html[data-id]").Attributes["data-id"].Value;
        using var response = await Config.Client.GetAsync(SystemUrl.MakeRelativeUri($"/api/ranobe/{internalId}/contents"), Config.CancellationToken);
        response.EnsureDownloadSuccess();
        var contents = await response.Content.ReadFromJsonAsync<RanobeHubApiResponse>(cancellationToken: Config.CancellationToken);
        var chapters = contents?.Volumes?.SelectMany(t => t.Chapters ?? []).ToList();
        if (chapters == null || chapters.Count == 0)
            throw new InvalidOperationException("RanobeHub не вернул доступного оглавления; пустая книга не будет сохранена.");
        return SliceToc(chapters, c => c.Name);
    }

    private Task<TempFile> GetCover(HtmlDocument doc, Uri bookUri)
    {
        var imagePath = doc.QuerySelector("div.poster-slider img")?.Attributes["data-src"]?.Value;
        imagePath ??= doc.QuerySelector("img.book-poster-gallery-image")?.GetAttributeValue("src", null)?.HtmlDecode();
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            var imageUri = bookUri.MakeRelativeUri(imagePath);
            var original = imageUri.GetQueryParameter("url");
            if (imageUri.AbsolutePath == "/_next/image" && original?.StartsWith("/api/media/", StringComparison.Ordinal) == true)
                imagePath = original;
        }
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(bookUri.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }
}
