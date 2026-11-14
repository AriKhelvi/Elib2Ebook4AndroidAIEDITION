using System.Text;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.HogwartsNet;

public class HogwartsNetGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://hogwartsnet.ru/");

    private static readonly Encoding _encoding = Encoding.GetEncoding(1251);

    protected override string GetId(Uri url)
        => url.GetQueryParameter("fid");

    public override async Task Authorize()
    {
        if (!Config.HasCredentials)
        {
            return;
        }

        var payload = new Dictionary<string, string>
        {
            ["id"] = Config.Options.Login, ["pwd"] = Config.Options.Password
        };

        var doc = await Config.Client.PostHtmlDocWithTriesAsync("https://hogwartsnet.ru/mfanf/member.php".AsUri(), new FormUrlEncodedContent(payload), _encoding);
        var anketa = doc.QuerySelector("tr.top_fanf a[href*=anketa.php]");

        if (anketa == null)
        {
            throw new Exception("Не удалось авторизоваться. Проверьте правильность ID/пароля");
        }

        Config.Logger.LogInformation("Успешно авторизовались");
    }

    public override async Task<Book> Get(Uri url)
    {
        url = $"https://hogwartsnet.ru/mfanf/ffshowfic.php?fid={GetId(url)}".AsUri();
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url, _encoding);
        CheckAvailability(doc);

        var book = new Book(url)
        {
            Chapters = await FillChapters(doc, url),
            Title = doc.GetTextBySelector("td.fichead a[href*=ffshowfic.php]"),
            Author = GetAuthor(doc, url),
            Annotation = doc.QuerySelector("td.fichead div")?.InnerHtml,
            Seria = GetSeria(doc, url)
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

        foreach (var urlChapter in GetToc(doc, url))
        {
            Config.Logger.LogInformation($"Загружаю главу {urlChapter.Title.CoverQuotes()}");
            var chapter = new Chapter
            {
                Title = urlChapter.Title
            };

            // Одночастные фанфики уже содержат текст на странице карточки.
            var chapterDoc = ParseChapter(urlChapter.Url == url ? doc : await Config.Client.GetHtmlDocWithTriesAsync(urlChapter.Url, _encoding));
            chapter.Images = await GetImages(chapterDoc, urlChapter.Url);
            chapter.Content = chapterDoc.DocumentNode.InnerHtml;

            result.Add(chapter);
        }

        return result;
    }

    private static void CheckAvailability(HtmlDocument doc)
    {
        const string mature = "Текст фанфика доступен только зарегистрированным пользователям старше 18 лет";
        if (doc.DocumentNode.InnerText.Contains(mature, StringComparison.OrdinalIgnoreCase))
        {
            throw new SiteAccessException(SiteAccessReason.AgeConfirmationRequired, $"{mature}. Добавьте авторизацию по ID и паролю.");
        }
    }

    private static HtmlDocument ParseChapter(HtmlDocument doc)
    {
        CheckAvailability(doc);
        var content = doc.QuerySelector("#chap_text");
        if (content == null)
            throw new InvalidOperationException("HogwartsNet не вернул текст главы. Проверьте доступ к фанфику на сайте; возможно, изменилась его разметка.");
        content = content.CloneNode(true).RemoveNodes("center, div[id*=yandex], script, style");
        foreach (var lineBreak in content.QuerySelectorAll("br").ToArray())
            lineBreak.ParentNode.ReplaceChild(HtmlNode.CreateNode("\n"), lineBreak);

        var text = new StringBuilder();
        using var sr = new StringReader(System.Net.WebUtility.HtmlDecode(content.InnerText));
        while (true)
        {
            var line = sr.ReadLine();
            if (line == null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            text.Append(line.HtmlEncode().CoverTag("p"));
        }

        if (text.Length == 0)
            throw new InvalidOperationException("HogwartsNet вернул пустой текст главы. Неполный файл не будет сохранён.");
        var chapter = new HtmlDocument();
        chapter.LoadHtml(text.ToString());
        return chapter;
    }

    private IEnumerable<UrlChapter> GetToc(HtmlDocument doc, Uri url)
    {
        var result = new List<UrlChapter>();

        foreach (var option in doc.QuerySelectorAll("td.fichead_chapters select[name=chapter] option"))
        {
            var chapterId = option.GetAttributeValue("value", null);
            if (string.IsNullOrWhiteSpace(chapterId))
                continue;
            var chapterUrl = url.AppendQueryParameter("chapter", chapterId);
            var chapterTitle = option.GetText().StartsWith("глава") ? $"Глава {chapterId}" : option.GetText();

            result.Add(new UrlChapter(chapterUrl, chapterTitle));
        }

        if (result.Count == 0)
        {
            var title = doc.GetTextBySelector("td.fichead a[href*=ffshowfic.php]");
            if (doc.QuerySelector("#chap_text") == null || string.IsNullOrWhiteSpace(title))
                throw new InvalidOperationException("HogwartsNet не вернул оглавление или текст фанфика. Проверьте доступность книги на сайте.");
            result.Add(new UrlChapter(url, title));
        }

        return SliceToc(result, c => c.Title);
    }

    private static Seria GetSeria(HtmlDocument doc, Uri url)
    {
        var a = doc.QuerySelector("td.fichead a[href*=findex.php]");
        if (a != null)
        {
            return new Seria
            {
                Name = a.GetText(), Url = url.MakeRelativeUri(a.Attributes["href"].Value)
            };
        }

        return null;
    }

    private static Author GetAuthor(HtmlDocument doc, Uri url)
    {
        var a = doc.QuerySelector("td.fichead a[href*=member.php]");
        return a != null ?
            new Author(a.GetText().ReplaceNewLine(), url.MakeRelativeUri(a.Attributes["href"].Value)) :
            new Author("HogwartsNet");
    }
}
