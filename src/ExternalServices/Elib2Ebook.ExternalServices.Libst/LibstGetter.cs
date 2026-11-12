using System.Net;
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

namespace Elib2Ebook.ExternalServices.Libst;

public class LibstGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://libst.ru/");

    protected override string GetId(Uri url)
        => url.GetQueryParameter("BookID") ?? base.GetId(url);

    public override async Task<Book> Get(Uri url)
    {
        var bookId = GetId(url);
        url = SystemUrl.MakeRelativeUri($"/Detail/BookView/{bookId}");
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);

        var book = new Book(url)
        {
            Cover = await GetCover(doc, url), Chapters = await FillChapters(doc), Title = doc.GetTextBySelector("h1"), Author = GetAuthor(doc, url),
        };

        return book;
    }

    public override async Task Authorize()
    {
        if (!Config.HasCredentials)
        {
            return;
        }

        var doc = await Config.Client.GetHtmlDocWithTriesAsync(SystemUrl);
        var token = doc.QuerySelector("input[name=__RequestVerificationToken]").Attributes["value"].Value;

        doc = await Config.Client.PostHtmlDocWithTriesAsync(SystemUrl.MakeRelativeUri("/Account/Login?ReturnUrl=/"), GenerateAuthData(token));
        var error = doc.GetTextBySelector("div.validation-summary-errors");
        if (string.IsNullOrEmpty(error))
        {
            Config.Logger.LogInformation("Авторизация прошла успешно");
        }
        else
        {
            throw new Exception($"Не удалось авторизоваться. {error}");
        }
    }

    private FormUrlEncodedContent GenerateAuthData(string token)
    {
        var data = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["username"] = Config.Options.Login, ["password"] = Config.Options.Password
        };

        return new FormUrlEncodedContent(data);
    }

    private async Task<IEnumerable<Chapter>> FillChapters(HtmlDocument doc)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        foreach (var bookChapter in GetToc(doc))
        {
            Config.Logger.LogInformation($"Загружаю главу {bookChapter.Title.CoverQuotes()}");

            var chapter = new Chapter
            {
                Title = bookChapter.Title
            };

            var chapterDoc = await GetChapter(bookChapter);
            if (chapterDoc != null)
            {
                chapter.Content = chapterDoc.DocumentNode.InnerHtml;
            }

            result.Add(chapter);
        }

        return result;
    }

    private async Task<HtmlDocument> GetChapter(IdChapter bookChapter)
    {
        if (string.IsNullOrWhiteSpace(bookChapter.Id))
        {
            Config.Logger.LogWarning($"Глава {bookChapter.Title.CoverQuotes()} недоступна: сайт не предоставил ссылку на текст.");
            return null;
        }

        var sb = new StringBuilder();

        for (var i = 1;; i++)
        {
            if (i > 10000) throw new InvalidOperationException("Libst вернул слишком много страниц главы; загрузка остановлена.");
            using var response = await Config.Client.GetAsync(SystemUrl.MakeRelativeUri($"/Detail/GetChapterTextForCanvas?ChapID={bookChapter.Id}&k=0&page={i}"));
            if (i > 1 && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            {
                break;
            }
            response.EnsureDownloadSuccess();
            if (response.StatusCode == HttpStatusCode.NoContent)
                throw new InvalidOperationException($"Libst не вернул текст главы {bookChapter.Title.CoverQuotes()}.");

            var data = await response.Content.ReadAsStringAsync().ContinueWith(t => t.Result.Deserialize<string[]>());
            if (data == null) throw new InvalidOperationException("Libst вернул некорректный ответ страницы главы.");
            if (data.Length == 0)
            {
                if (i == 1) throw new InvalidOperationException($"Libst не вернул текст главы {bookChapter.Title.CoverQuotes()}.");
                break;
            }
            sb.AppendJoin(string.Empty, data.Select(r => r.Trim().CoverTag("p")));
        }

        return sb.AsHtmlDoc();
    }

    private IEnumerable<IdChapter> GetToc(HtmlDocument doc)
    {
        var result = new List<IdChapter>();

        foreach (var span in doc.QuerySelectorAll("span.mt-action-author"))
        {
            var onclick = span.Attributes["onclick"]?.Value;
            if (string.IsNullOrWhiteSpace(onclick))
            {
                result.Add(new IdChapter(string.Empty, span.GetText()));
            }
            else
            {
                var id = Regex.Match(onclick, @"ChapID=(?<id>\d+)").Groups["id"].Value;
                result.Add(new IdChapter(id, span.GetText()));
            }
        }

        return SliceToc(result, c => c.Title);
    }

    private Task<TempFile> GetCover(HtmlDocument doc, Uri uri)
    {
        var imagePath = doc.QuerySelector("img.coverBook")?.Attributes["src"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(uri.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }

    private static Author GetAuthor(HtmlDocument doc, Uri uri)
    {
        var a = doc.QuerySelector("h2.author_name a");
        return new Author(a.GetText(), uri.MakeRelativeUri(a.Attributes["href"].Value));
    }
}
