using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.ExternalServices.Neobook.Types;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Neobook;

public class NeobookGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://neobook.org/");

    private Uri _apiUrl = new("https://api.neobook.org/");

    protected override string GetId(Uri url)
    {
        return url.GetSegment(1) == "book" ? url.GetSegment(2) : url.GetQueryParameter("book");
    }

    public override async Task Authorize()
    {
        Config.Logger.LogDebug("Neobook parser: этап=вход, credentials={Credentials}", Config.HasCredentials);
        if (!Config.HasCredentials)
        {
            return;
        }

        using var content = GenerateAuthData();
        using var response = await Config.Client.PostWithTriesAsync(_apiUrl, content);
        response.EnsureDownloadSuccess();
        NeobookAuth data;
        try
        {
            data = await response.Content.ReadFromJsonAsync<NeobookAuth>(cancellationToken: Config.CancellationToken);
        }
        catch (JsonException ex)
        {
            Config.Logger.LogDebug("Neobook parser: auth JSON invalid, line={Line}, byte={Byte}", ex.LineNumber, ex.BytePositionInLine);
            throw new InvalidOperationException("Neobook вернул некорректный ответ при входе. Авторизация не подтверждена.");
        }
        Config.Logger.LogDebug("Neobook parser: auth errorPresent={Error}, loginPresent={Login}, tokenPresent={Token}, uidPresent={Uid}",
            !string.IsNullOrWhiteSpace(data?.Error), data?.Login != null,
            !string.IsNullOrWhiteSpace(data?.Login?.Utoken), !string.IsNullOrWhiteSpace(data?.Login?.Uid));
        if (!string.IsNullOrWhiteSpace(data?.Error))
        {
            // Ответ сервера может содержать данные запроса: не переносим его в общий журнал.
            throw new InvalidOperationException("Neobook не подтвердил вход. Проверьте логин, пароль и доступ к сайту.");
        }
        if (string.IsNullOrWhiteSpace(data?.Login?.Utoken) || string.IsNullOrWhiteSpace(data.Login.Uid))
            throw new InvalidOperationException("Neobook не вернул данные сеанса. Авторизация не подтверждена.");

        // Сессионные cookies никогда не должны уходить по HTTP до перенаправления на HTTPS.
        try
        {
            var token = new Cookie("utoken", data.Login.Utoken, "/") { Secure = true, HttpOnly = true };
            var uid = new Cookie("uid", data.Login.Uid, "/") { Secure = true, HttpOnly = true };
            Config.CookieContainer.Add(SystemUrl, token);
            Config.CookieContainer.Add(SystemUrl, uid);
        }
        catch (CookieException)
        {
            throw new InvalidOperationException("Neobook вернул некорректные данные сеанса. Авторизация не подтверждена.");
        }
        Config.Logger.LogInformation("Успешно авторизовались");
    }

    private MultipartFormDataContent GenerateAuthData()
    {
        return new()
        {
            {
                new StringContent("3.1"), "version"
            },
            {
                new StringContent("0"), "uid"
            },
            {
                new StringContent(string.Empty), "utoken"
            },
            {
                new StringContent("authorization"), "resource"
            },
            {
                new StringContent("login_by_email"), "action"
            },
            {
                new StringContent(Config.Options.Login), "email"
            },
            {
                new StringContent(Config.Options.Password), "password"
            },
            {
                new StringContent("3"), "platform_id"
            },
        };
    }

    public override async Task<Book> Get(Uri url)
    {
        var bookId = GetId(url);
        url = SystemUrl.MakeRelativeUri("/book/" + bookId + "/");
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        var data = GetPostData(doc);

        var book = new Book(url)
        {
            Cover = await GetCover(data),
            Chapters = await FillChapters(data, bookId),
            Title = data.Title,
            Author = GetAuthor(data),
            Annotation = data.Text
        };

        return book;
    }

    private NeobookPostData GetPostData(HtmlDocument doc)
    {
        var data = ReadScriptData<NeobookPostData>(doc, "postData", "карточки книги");
        Config.Logger.LogDebug("Neobook parser: postData titlePresent={Title}, authorPresent={Author}, chapters={Chapters}, coverPresent={Cover}",
            !string.IsNullOrWhiteSpace(data?.Title), data?.User != null, data?.Chapters?.Length ?? -1,
            !string.IsNullOrWhiteSpace(data?.Attachment?.Cover?.GetValueOrDefault("l")));
        if (string.IsNullOrWhiteSpace(data?.Title) || data.User == null)
            throw new InvalidOperationException("Neobook не вернул название или автора книги. Возможно, требуется вход или изменилась страница сайта.");
        return data;
    }

    private T ReadScriptData<T>(HtmlDocument doc, string variable, string description)
    {
        var assignment = Regex.Match(doc.ParsedText, @"\b(?:var|let|const)\s+" + Regex.Escape(variable) + @"\s*=\s*",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Config.Logger.LogDebug("Neobook parser: variable={Variable}, assignmentFound={Found}, offset={Offset}, htmlChars={Chars}",
            variable, assignment.Success, assignment.Success ? assignment.Index : -1, doc.ParsedText.Length);
        if (!assignment.Success)
        {
            LogPageStructure(doc, variable);
            throw new InvalidOperationException($"Neobook не вернул данные {description}. Возможно, доступ ограничен или формат страницы изменился.");
        }
        try
        {
            // Читаем ровно одно JSON-значение: переносы и ';' внутри строк не являются границей.
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(doc.ParsedText[(assignment.Index + assignment.Length)..]));
            return JsonSerializer.Deserialize<T>(ref reader);
        }
        catch (JsonException ex)
        {
            Config.Logger.LogDebug("Neobook parser: variable={Variable}, JSON invalid, line={Line}, byte={Byte}", variable, ex.LineNumber, ex.BytePositionInLine);
            LogPageStructure(doc, variable);
            throw new InvalidOperationException($"Neobook вернул некорректные данные {description}. Неполная книга не будет сохранена.");
        }
    }

    private void LogPageStructure(HtmlDocument doc, string variable)
    {
        if (!Config.Logger.IsEnabled(LogLevel.Debug)) return;
        // Ограниченная структурная сводка вместо HTML/script с текстом книги и данными сессии.
        var nodes = doc.DocumentNode.Descendants().Take(4097).ToArray();
        var sample = nodes.Take(4096).ToArray();
        Config.Logger.LogDebug("Neobook DOM: variable={Variable}, nodes={Nodes}, truncated={Truncated}, forms={Forms}, inputs={Inputs}, scripts={Scripts}, iframes={Iframes}, passwordField={Password}, challengeMarker={Challenge}",
            variable, sample.Length, nodes.Length > sample.Length,
            sample.Count(n => n.Name == "form"), sample.Count(n => n.Name == "input"), sample.Count(n => n.Name == "script"), sample.Count(n => n.Name == "iframe"),
            sample.Any(n => n.Name == "input" && n.GetAttributeValue("type", "") == "password"),
            sample.Any(n => n.GetAttributeValue("id", "").Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
                            n.GetAttributeValue("class", "").Contains("captcha", StringComparison.OrdinalIgnoreCase)));
    }

    private Author GetAuthor(NeobookPostData data)
    {
        return new Author(data.User.LastName + " " + data.User.FirstName, SystemUrl.MakeRelativeUri(data.User.UserName));
    }

    private async Task<IEnumerable<Chapter>> FillChapters(NeobookPostData data, string bookId)
    {
        var result = new List<Chapter>();
        Config.Logger.LogDebug("Neobook parser: preview={Preview}, start={Start}, end={End}, startNameSet={StartName}, endNameSet={EndName}, images={Images}",
            Config.Options.NoChapters, Config.Options.Start, Config.Options.End,
            !string.IsNullOrWhiteSpace(Config.Options.StartName), !string.IsNullOrWhiteSpace(Config.Options.EndName), !Config.Options.NoImage);
        if (Config.Options.NoChapters)
        {
            return result;
        }
        if (data.Chapters == null || data.Chapters.Length == 0)
            throw new InvalidOperationException("Neobook не вернул оглавление книги. Проверьте доступ к ней на сайте. Пустая книга не будет сохранена.");
        if (data.Chapters.Any(chapter => chapter == null || string.IsNullOrWhiteSpace(chapter.Token)))
            throw new InvalidOperationException("Neobook вернул главу без идентификатора. Неполная книга не будет сохранена.");

        var selected = SliceToc(data.Chapters, c => c.Title);
        if (Config.Logger.IsEnabled(LogLevel.Debug))
            Config.Logger.LogDebug("Neobook parser: tocTotal={Total}, selected={Selected}", data.Chapters.Length, selected.Count());
        var index = 0;
        foreach (var neobookChapter in selected)
        {
            Config.Logger.LogDebug("Neobook parser: selectedIndex={Index}", ++index);
            Config.Logger.LogInformation($"Загружаю главу {neobookChapter.Title.CoverQuotes()}");
            var chapter = new Chapter
            {
                Title = neobookChapter.Title
            };

            var chapterDoc = await GetChapter(neobookChapter, bookId);
            if (chapterDoc != null)
            {
                chapter.Images = await GetImages(chapterDoc, SystemUrl);
                chapter.Content = chapterDoc.DocumentNode.InnerHtml;
            }

            result.Add(chapter);
        }

        return result;
    }

    private async Task<HtmlDocument> GetChapter(NeobookTocChapter neobookTocChapter, string bookId)
    {
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(
            SystemUrl.MakeRelativeUri("reader/").AppendQueryParameter("book", bookId).AppendQueryParameter("chapter", neobookTocChapter.Token));

        var neobookBook = ReadScriptData<NeobookBook>(doc, "data", $"главы «{neobookTocChapter.Title}»");
        Config.Logger.LogDebug("Neobook parser: reader chapters={Chapters}, activeIndex={ActiveIndex}",
            neobookBook?.Chapters?.Length ?? -1, neobookBook?.ActiveChapterIndex ?? -1);
        if (neobookBook?.Chapters == null || neobookBook.ActiveChapterIndex < 0 ||
            neobookBook.ActiveChapterIndex >= neobookBook.Chapters.Length)
        {
            throw new InvalidOperationException($"Neobook вернул неверный индекс главы «{neobookTocChapter.Title}». Неполная книга не будет сохранена.");
        }

        var chapter = neobookBook.Chapters[neobookBook.ActiveChapterIndex];
        Config.Logger.LogDebug("Neobook parser: chapter dataPresent={Data}, htmlPresent={Html}, htmlChars={Chars}",
            chapter?.Data != null, !string.IsNullOrWhiteSpace(chapter?.Data?.Html), chapter?.Data?.Html?.Length ?? 0);
        if (string.IsNullOrWhiteSpace(chapter?.Data?.Html))
        {
            throw new InvalidOperationException($"Neobook не предоставил текст главы «{neobookTocChapter.Title}». Неполная книга не будет сохранена.");
        }

        var content = new HtmlDocument();
        content.LoadHtml(chapter.Data.Html);
        return content;
    }

    private Task<TempFile> GetCover(NeobookPostData data)
    {
        var imagePath = data.Attachment?.Cover?.GetValueOrDefault("l");
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(imagePath.AsUri()) : Task.FromResult(default(TempFile));
    }
}
