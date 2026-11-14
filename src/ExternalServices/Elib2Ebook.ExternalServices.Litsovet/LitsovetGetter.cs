using System.Net.Http.Json;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.ExternalServices.Litsovet.Types;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Litsovet;

public class LitsovetGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://litsovet.ru/");

    protected override string GetId(Uri url)
        => url.GetSegment(2);

    public override async Task Authorize()
    {
        if (!Config.HasCredentials)
        {
            return;
        }

        using var post = await Config.Client.PostAsync(SystemUrl.MakeRelativeUri("/ajax/login_signup_remind"), GenerateAuthData());
        post.EnsureDownloadSuccess();
        var response = await post.Content.ReadFromJsonAsync<LitsovetLoginResponse>();
        if (response?.Ok == 1)
        {
            Config.Logger.LogInformation("Успешно авторизовались");
        }
        else
        {
            throw new Exception("Не удалось авторизоваться.");
        }
    }

    private FormUrlEncodedContent GenerateAuthData()
    {
        var payload = new Dictionary<string, string>
        {
            {
                "action", "login"
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
        url = SystemUrl.MakeRelativeUri("/books/" + GetId(url));
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        var title = doc.GetTextBySelector("h1");
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("Литсовет не вернул карточку книги: название отсутствует.");

        var book = new Book(url)
        {
            Cover = await GetCover(doc, url),
            Chapters = await FillChapters(url, doc),
            Title = title,
            Author = GetAuthor(doc),
            Annotation = (doc.QuerySelector("div[itemprop='description']") ?? doc.QuerySelector("div.book-informate div.item-descr"))?.InnerHtml
        };

        return book;
    }

    private Author GetAuthor(HtmlDocument doc)
    {
        var a = doc.QuerySelector("div.p-book-author a") ?? doc.QuerySelector("div.card-book.big .item-author a");
        var name = a?.GetText();
        var href = a?.GetAttributeValue("href", null);
        return string.IsNullOrWhiteSpace(name) ? new Author("Автор не указан") : new Author(name, string.IsNullOrWhiteSpace(href) ? null : SystemUrl.MakeRelativeUri(href));
    }

    private async Task<IEnumerable<UrlChapter>> GetToc(Uri url, HtmlDocument card)
    {
        var currentRows = card.QuerySelectorAll("#book-sections .card-glava .item-name");
        if (currentRows.Count > 0)
        {
            var chapters = currentRows.Select(row =>
            {
                var href = row.QuerySelector("a")?.GetAttributeValue("href", null);
                return new UrlChapter(string.IsNullOrWhiteSpace(href) ? null : url.MakeRelativeUri(href), row.GetText().ReplaceNewLine());
            }).ToList();
            return SliceToc(chapters, chapter => chapter.Title);
        }

        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url.AppendSegment("/read/"));

        var result = new List<UrlChapter>();
        foreach (var a in doc.QuerySelectorAll("div.book-navi-list li a"))
        {
            var href = a.Attributes["onclick"]?.Value?.Split(" ").FirstOrDefault(s => s.Contains("books"))?.Trim('\'');
            result.Add(
                string.IsNullOrEmpty(href)
                    ? new UrlChapter(null, a.GetText().ReplaceNewLine())
                    : new UrlChapter(url.MakeRelativeUri(href), a.GetText().ReplaceNewLine()));
        }

        if (result.Count == 0) throw new InvalidOperationException("Литсовет не вернул оглавление в поддерживаемой разметке.");
        return SliceToc(result, c => c.Title);
    }

    private async Task<IEnumerable<Chapter>> FillChapters(Uri url, HtmlDocument card)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        foreach (var urlChapter in await GetToc(url, card))
        {
            Config.CancellationToken.ThrowIfCancellationRequested();
            if (urlChapter.Url == null) throw ChapterAccessDenied();
            Config.Logger.LogInformation($"Загружаю главу {urlChapter.Title.CoverQuotes()}");
            var chapter = new Chapter
            {
                Title = urlChapter.Title
            };

            if (urlChapter.Url != null)
            {
                var chapterDoc = await GetChapter(urlChapter);
                chapter.Images = await GetImages(chapterDoc, urlChapter.Url);
                chapter.Content = chapterDoc.DocumentNode.InnerHtml;
            }

            result.Add(chapter);
        }

        return result;
    }

    private async Task<HtmlDocument> GetChapter(UrlChapter urlChapter)
    {
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(urlChapter.Url);
        var content = doc.QuerySelector("div.reader-text") ?? doc.QuerySelector("div.reader");
        if (content?.QuerySelectorAll(".card-alert .item-descr").Any(alert =>
                alert.GetText().Contains("Для продолжения чтения книги", StringComparison.OrdinalIgnoreCase)) == true)
            throw ChapterAccessDenied();
        if (content == null || (string.IsNullOrWhiteSpace(content.GetText()) && content.QuerySelector("img") == null))
            throw new InvalidOperationException("Литсовет не вернул текст выбранной главы в поддерживаемой разметке.");
        var chapter = new HtmlDocument();
        chapter.LoadHtml(content.InnerHtml);
        return chapter;
    }

    private static SiteAccessException ChapterAccessDenied() => new(SiteAccessReason.AccessDenied,
        "Литсовет не предоставил доступ к выбранной главе. Проверьте вход и покупку книги.");

    private Task<TempFile> GetCover(HtmlDocument doc, Uri bookUri)
    {
        var imagePath = (doc.QuerySelector("div.p-book-cover img") ?? doc.QuerySelector("div.card-book.big .item-cover img"))?.Attributes["src"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(bookUri.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }
}
