using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Bookuruk;

public class BookurukGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://bookuruk.com/");

    protected override string GetId(Uri url)
    {
        return url.GetSegment(2);
    }

    public override async Task Authorize()
    {
        if (!Config.HasCredentials)
        {
            return;
        }

        var doc = await Config.Client.GetHtmlDocWithTriesAsync(SystemUrl);
        var token = doc.QuerySelector("#loginpopup input[name=_csrf-shop]").Attributes["value"].Value;

        doc = await Config.Client.PostHtmlDocWithTriesAsync(SystemUrl.MakeRelativeUri("/login"), GenerateAuthData(token));
        var alert = doc.QuerySelector("#infoMess")?.GetText();
        if (string.IsNullOrWhiteSpace(alert))
        {
            Config.Logger.LogInformation("Успешно авторизовались");
        }
        else
        {
            throw new Exception($"Не удалось авторизоваться. {alert}");
        }
    }

    private FormUrlEncodedContent GenerateAuthData(string token)
    {
        var payload = new Dictionary<string, string>
        {
            {
                "_csrf-shop", token
            },
            {
                "email", Config.Options.Login
            },
            {
                "password", Config.Options.Password
            },
        };

        return new FormUrlEncodedContent(payload);
    }

    public override async Task<Book> Get(Uri url)
    {
        url = await GetMainUrl(url);
        url = SystemUrl.MakeRelativeUri($"/book/{GetId(url)}");
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        var title = doc.GetTextBySelector("h1");
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException($"Bookuruk не вернул карточку книги ({url}); не найдено название.");

        var book = new Book(url)
        {
            Cover = await GetCover(doc, url),
            Chapters = await FillChapters(doc, url),
            Title = title,
            Author = GetAuthor(doc),
            Annotation = GetAnnotation(doc)
        };

        return book;
    }

    private string GetAnnotation(HtmlDocument doc)
    {
        var annotation = doc.QuerySelector("div.single-prew__desc_anot")?.CloneNode(true);
        if (annotation == null) return null;
        LogContentShape("annotation-before", annotation, 1, 0);
        // Иллюстрации аннотации не загружаются: не оставляем ссылки без файлов в FB2.
        annotation.RemoveNodes(" > b, img, image, script, style, template");
        LogContentShape("annotation-after", annotation, 1, 0);
        return annotation.InnerHtml;
    }

    private Author GetAuthor(HtmlDocument doc)
    {
        var a = doc.QuerySelector("a.bookcard_author + a") ?? doc.QuerySelector("a.bookcard_author[href]");
        if (a == null || string.IsNullOrWhiteSpace(a.GetText()))
        {
            Config.Logger.LogWarning("Bookuruk не указал автора в ожидаемом блоке; в метаданных использовано название сайта");
            return new Author("Bookuruk");
        }
        var href = a.Attributes["href"]?.Value;
        return string.IsNullOrWhiteSpace(href) ?
            new Author(a.GetText()) :
            new Author(a.GetText(), SystemUrl.MakeRelativeUri(href));
    }

    private async Task<Uri> GetMainUrl(Uri url)
    {
        if (url.GetSegment(1) == "book")
        {
            return url;
        }

        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        return url.MakeRelativeUri(doc.QuerySelector("div.content.aside-right li a").Attributes["href"].Value);
    }

    private async Task<IEnumerable<UrlChapter>> GetToc(HtmlDocument doc, Uri url)
    {
        var links = doc.QuerySelectorAll("div.content.aside-right li a[href]");
        if (links.Count < 2)
            throw new InvalidOperationException($"Bookuruk не вернул ссылку на оглавление ({url}); возможно изменение разметки или ограничение доступа.");
        var internalId = SystemUrl.MakeRelativeUri(links[1].Attributes["href"].Value).GetSegment(2);
        doc = await Config.Client.GetHtmlDocWithTriesAsync(SystemUrl.MakeRelativeUri($"/book/chapters/{internalId}"));

        var result = doc
            .QuerySelectorAll("a.read-chapters-item")
            .Select(a => new UrlChapter(url.MakeRelativeUri(a.Attributes["href"].Value), a.GetText()))
            .ToList();

        if (result.Count == 0)
            throw new InvalidOperationException($"Bookuruk не вернул доступных глав ({url}); пустая книга не будет сохранена.");

        return SliceToc(result, c => c.Title);
    }

    private async Task<IEnumerable<Chapter>> FillChapters(HtmlDocument doc, Uri uri)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        foreach (var urlChapter in await GetToc(doc, uri))
        {
            var chapter = new Chapter
            {
                Title = urlChapter.Title
            };

            Config.Logger.LogInformation($"Загружаю главу {urlChapter.Title.CoverQuotes()}");

            var chapterDoc = await GetChapter(urlChapter.Url);
            if (chapterDoc != null)
            {
                chapter.Images = (await GetImages(chapterDoc, urlChapter.Url)).ToList();
                LogContentShape("chapter-after", chapterDoc.DocumentNode, 1, chapter.Images.Count());
                if (string.IsNullOrWhiteSpace(HtmlEntity.DeEntitize(chapterDoc.DocumentNode.InnerText)) && !chapter.Images.Any())
                    throw new InvalidOperationException("Bookuruk не вернул читаемый текст или доступные иллюстрации главы. Возможно ограничение доступа или изменение разметки; пустая глава не будет сохранена.");
                chapter.Content = chapterDoc.DocumentNode.InnerHtml;
            }

            result.Add(chapter);
        }

        return result;
    }

    private async Task<HtmlDocument> GetChapter(Uri url)
    {
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        var containers = doc.QuerySelectorAll("div.read-area");
        var content = containers.FirstOrDefault();
        LogContentShape("chapter-before", content ?? doc.DocumentNode, containers.Count, 0);
        if (content == null)
            throw new InvalidOperationException($"Bookuruk не вернул текст главы ({url}); возможно ограничение доступа или изменение разметки.");
        content.RemoveNodes("script, style, template");
        // Контейнер уже разобран как HTML: повторное декодирование меняет буквальный текст.
        var result = new HtmlDocument();
        result.LoadHtml(content.InnerHtml);
        return result;
    }

    private void LogContentShape(string stage, HtmlNode content, int containers, int savedImages)
    {
        if (!Config.Logger.IsEnabled(LogLevel.Debug)) return;
        const int maxNodes = 20000;
        var nodes = content.DescendantsAndSelf().Take(maxNodes + 1).ToList();
        Config.Logger.LogDebug("Bookuruk DOM: stage={Stage}, containers={Containers}, nodes={Nodes}, truncated={Truncated}, textChars={TextChars}, scripts={Scripts}, styles={Styles}, templates={Templates}, images={Images}, savedImages={SavedImages}",
            stage, containers, Math.Min(nodes.Count, maxNodes), nodes.Count > maxNodes,
            nodes.Take(maxNodes).Where(node => node.NodeType == HtmlNodeType.Text).Sum(node => (long)node.InnerText.Length),
            nodes.Take(maxNodes).Count(node => node.Name == "script"),
            nodes.Take(maxNodes).Count(node => node.Name == "style"),
            nodes.Take(maxNodes).Count(node => node.Name == "template"),
            nodes.Take(maxNodes).Count(node => node.Name is "img" or "image"), savedImages);
    }

    private Task<TempFile> GetCover(HtmlDocument doc, Uri uri)
    {
        var imagePath = doc.QuerySelector("div.single-prew__img img")?.Attributes["src"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(uri.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }
}
