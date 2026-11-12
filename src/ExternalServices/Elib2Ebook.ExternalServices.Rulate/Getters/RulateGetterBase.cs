using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.ExternalServices.Rulate.Types;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Rulate.Getters;

public abstract class RulateGetterBase(BookGetterConfig config) : GetterBase(config)
{
    private const int MaxTocPages = 10000;

    protected override string GetId(Uri url)
        => url.Segments.Length == 3 ? base.GetId(url) : url.GetSegment(2);

    protected abstract string Mature { get; }

    public override async Task Init()
    {
        await base.Init();
        Config.Client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        Config.CookieContainer.Add(SystemUrl, new Cookie("mature", Mature));
    }

    public override async Task Authorize()
    {
        if (!Config.HasCredentials)
        {
            return;
        }

        var main = await Config.Client.GetHtmlDocWithTriesAsync(SystemUrl);
        var doc = await Config.Client.PostHtmlDocWithTriesAsync(SystemUrl, GetAuthData(main));
        var alertBlock = doc.QuerySelector("div.alert.in.alert-block");

        if (alertBlock == null)
        {
            RulateAuthResponse error = null;
            try
            {
                error = doc.ParsedText.Deserialize<RulateAuthResponse>();
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                throw new Exception($"Не удалось авторизоваться. {error.Error}");
            }

            Config.Logger.LogInformation("Успешно авторизовались");
        }
        else
        {
            throw new Exception($"Не удалось авторизоваться. {alertBlock.GetText()}");
        }
    }

    private FormUrlEncodedContent GetAuthData(HtmlDocument doc)
    {
        var data = new Dictionary<string, string>
        {
            ["login[login]"] = Config.Options.Login,
            ["login[pass]"] = Config.Options.Password,
            ["csrf_token"] = doc.QuerySelector("meta[name=csrf-token]").Attributes["content"].Value
        };

        return new FormUrlEncodedContent(data);
    }

    public override async Task<Book> Get(Uri url)
    {
        var bookId = GetId(url);
        url = SystemUrl.MakeRelativeUri($"/book/{bookId}");
        var doc = await GetBookPage(url);

        var book = new Book(url)
        {
            Cover = await GetCover(doc, url),
            Chapters = await FillChapters(doc, url, bookId),
            Title = GetTitle(doc),
            Author = GetAuthor(doc),
            Annotation = doc.QuerySelector("#Info div.btn-toolbar + div")?.InnerHtml
        };

        return book;
    }

    private static string GetTitle(HtmlDocument doc)
    {
        var match = Regex.Match(doc.ParsedText, "t_title: '(?<title>.*?)',");
        return match.Success ? match.Groups["title"].Value : doc.GetTextBySelector("h1");
    }

    private Author GetAuthor(HtmlDocument doc)
    {
        var def = new Author("rulate");
        foreach (var p in doc.QuerySelectorAll("#Info p"))
        {
            var strong = p.QuerySelector("strong");
            if (strong != null && strong.InnerText.Contains("Автор"))
            {
                var em = p.QuerySelector("em");
                if (em == null)
                {
                    return def;
                }

                var a = em.QuerySelector("a[href]");
                return a == null ?
                    new Author(em.GetText()) :
                    new Author(a.GetText(), SystemUrl.MakeRelativeUri(a.Attributes["href"].Value));
            }
        }

        return def;
    }

    private Task<TempFile> GetCover(HtmlDocument doc, Uri bookUri)
    {
        var imagePath = doc.QuerySelector("div.slick img")?.Attributes["src"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(bookUri.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }

    private async Task<List<Chapter>> FillChapters(HtmlDocument doc, Uri bookUri, string bookId)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        var toc = await GetToc(doc, bookUri);
        foreach (var (id, name) in SliceToc(toc, c => c.Title))
        {
            Config.Logger.LogInformation($"Загружаю главу {name.CoverQuotes()}");
            var chapter = new Chapter();

            var chapterDoc = await GetChapter(bookId, id);
            chapter.Images = await GetImages(chapterDoc, bookUri);
            chapter.Content = chapterDoc.DocumentNode.InnerHtml;
            chapter.Title = name;

            result.Add(chapter);
        }

        return result;
    }

    private async Task<HtmlDocument> GetChapter(string bookId, string chapterId)
    {
        var s = await Config.Client.GetFromJsonWithTriesAsync<RulateChapter>(SystemUrl.MakeRelativeUri($"/book/{bookId}/{chapterId}/readyajax"));
        return (s.CanRead ? s.Content.AsHtmlDoc().QuerySelector("div.content-text")?.InnerHtml ?? string.Empty : string.Empty).AsHtmlDoc();
    }

    private async Task<HtmlDocument> GetBookPage(Uri url)
    {
        using var response = await Config.Client.GetWithTriesAsync(url);
        if (response == null)
        {
            throw new InvalidOperationException($"Не удалось загрузить страницу оглавления {url}");
        }

        response.EnsureDownloadSuccess();
        await using var stream = await response.Content.ReadAsStreamAsync();
        return stream.AsHtmlDoc();
    }

    private async Task<List<IdChapter>> GetToc(HtmlDocument firstPage, Uri bookUri)
    {
        var chaptersByPage = new SortedDictionary<int, List<IdChapter>>();
        var chapterIds = new HashSet<string>(StringComparer.Ordinal);
        var visitedPages = new HashSet<int>();
        var pendingPages = new SortedSet<int> { 1 };
        var lastScheduledPage = 1;

        while (pendingPages.Count > 0)
        {
            var page = pendingPages.Min;
            pendingPages.Remove(page);
            visitedPages.Add(page);
            // Ограничение защищает от бесконечного оглавления при ошибке сайта.
            if (visitedPages.Count > MaxTocPages)
            {
                throw new InvalidOperationException("Оглавление содержит слишком много страниц. Загрузка остановлена, чтобы не сохранить неполную книгу.");
            }

            var pageUri = page == 1 ? bookUri : new UriBuilder(bookUri) { Query = $"Chapter_page={page}" }.Uri;
            var doc = page == 1 ? firstPage : await GetBookPage(pageUri);
            if (doc.QuerySelector("#Chapters") == null)
            {
                throw new InvalidOperationException($"На странице оглавления {pageUri} не найден список глав. Возможно, сайт ограничил доступ.");
            }

            var countBefore = chapterIds.Count;
            var pageChapters = new List<IdChapter>();
            foreach (var row in doc.QuerySelectorAll("#Chapters tr[data-id]"))
            {
                var id = row.Attributes["data-id"].Value.Trim();
                if (string.IsNullOrEmpty(id))
                {
                    throw new InvalidOperationException($"На странице оглавления {pageUri} найдена глава без идентификатора.");
                }

                chapterIds.Add(id);
                pageChapters.Add(new IdChapter(id, row.GetTextBySelector("td.t")));
            }

            if (page != 1 && chapterIds.Count == countBefore)
            {
                throw new InvalidOperationException($"Страница оглавления {pageUri} пуста или повторяет уже загруженные главы. Книга не будет сохранена частично.");
            }

            chaptersByPage.Add(page, pageChapters);

            foreach (var link in doc.QuerySelectorAll("a[href]"))
            {
                var href = HttpUtility.HtmlDecode(link.Attributes["href"].Value);
                if (!Uri.TryCreate(pageUri, href, out var next) ||
                    !string.Equals(next.Scheme, bookUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(next.Authority, bookUri.Authority, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(next.AbsolutePath.TrimEnd('/'), bookUri.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal))
                {
                    continue;
                }

                var value = HttpUtility.ParseQueryString(next.Query)["Chapter_page"];
                if (!int.TryParse(value, out var nextPage) || nextPage <= lastScheduledPage)
                {
                    continue;
                }

                if (nextPage > MaxTocPages)
                {
                    throw new InvalidOperationException("Оглавление содержит слишком много страниц. Загрузка остановлена, чтобы не сохранить неполную книгу.");
                }

                // Ссылка на последнюю страницу подтверждает и промежуточные номера,
                // даже если сайт скрывает их в сокращённом пагинаторе.
                for (var number = lastScheduledPage + 1; number <= nextPage; number++)
                {
                    pendingPages.Add(number);
                }
                lastScheduledPage = nextPage;
            }
        }

        return chaptersByPage.Values.SelectMany(chapters => chapters).DistinctBy(chapter => chapter.Id).ToList();
    }
}
