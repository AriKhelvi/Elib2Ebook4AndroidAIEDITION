using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc;
using Elib2Ebook.ExternalServices.Litnet.Types;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Litnet.Getters;

public abstract class LitnetGetterBase(BookGetterConfig config) : GetterBase(config)
{
    private Uri ApiUrl => new($"https://api.{SystemUrl.Host}/");

    //cloudflare :(
    protected virtual Uri ApiIp => ApiUrl;

    protected virtual Uri SiteIp => SystemUrl;

    private static readonly string DeviceId = Guid.NewGuid().ToString().ToUpper();
    // Большой ответ из 100 глав долго собирался целиком без прогресса, в том числе на телефонах.
    // Обрабатываем небольшую пачку сразу и освобождаем её зашифрованные тексты перед следующей.
    private const int ChapterBatchSize = 10;
    private const string SECRET = "14a6579a984b3c6abecda6c2dfa83a64";

    private string _token { get; set; }

    protected override string GetId(Uri url)
        => base.GetId(url).Split('-').Last().Replace("b", string.Empty);

    private static byte[] Decrypt(string text)
    {
        using var aes = Aes.Create();
        const int IV_SHIFT = 16;

        aes.Key = Encoding.UTF8.GetBytes(SECRET);
        aes.IV = Encoding.UTF8.GetBytes(text)[..IV_SHIFT];

        var decryptor = aes.CreateDecryptor();
        using var ms = new MemoryStream(Convert.FromBase64String(text));
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);

        var output = new MemoryStream();
        cs.CopyTo(output);

        return output.ToArray()[IV_SHIFT..];
    }

    private static string GetSign(string token)
    {
        var inputBytes = Encoding.ASCII.GetBytes(DeviceId + SECRET + (token ?? string.Empty));
        var hashBytes = MD5.HashData(inputBytes);

        return Convert.ToHexString(hashBytes).ToLower();
    }

    /// <summary>
    /// Авторизация в системе
    /// </summary>
    /// <exception cref="Exception"></exception>
    public override async Task Authorize()
    {
        Config.Logger.LogDebug("Litnet: этап входа; сохранённые реквизиты={HasCredentials}", Config.HasCredentials);
        var path = Config.HasCredentials ? "user/find-by-login" : "registration/registration-by-device";

        var url = ApiIp.MakeRelativeUri(
            $"v1/{path}?login={HttpUtility.UrlEncode(Config.Options.Login?.TrimStart('+') ?? string.Empty)}&password={HttpUtility.UrlEncode(Config.Options.Password)}&app=android&device_id={DeviceId}&sign={GetSign(string.Empty)}");

        var data = await GetApiData<LitnetAuthResponse>(url);

        if (!string.IsNullOrWhiteSpace(data?.Token))
        {
            Config.Logger.LogInformation("Успешно авторизовались");
            _token = data.Token;
        }
        else
        {
            throw new Exception($"Не удалось авторизоваться. {data?.Error}");
        }
    }

    private async Task<LitnetBookResponse> GetBook(string token, string bookId)
    {
        var url = ApiIp.MakeRelativeUri($"/v1/book/get/{bookId}?app=android&device_id={DeviceId}&user_token={token}&sign={GetSign(token)}");
        var data = await GetApiData<LitnetBookResponse>(url);
        if (data == null || string.IsNullOrWhiteSpace(data.Title) || string.IsNullOrWhiteSpace(data.Url))
            throw new InvalidOperationException("Litnet не вернул название или адрес книги. Проверьте доступ к произведению.");
        if (!Config.HasCredentials && data.AdultOnly)
        {
            throw new Exception("Произведение 18+. Необходимо добавить логин и пароль.");
        }

        return data;
    }

    private Task<LitnetContentsResponse[]> GetBookContents(string token, string bookId)
    {
        var url = ApiIp.MakeRelativeUri($"/v1/book/contents?bookId={bookId}&app=android&device_id={DeviceId}&user_token={token}&sign={GetSign(token)}");
        return GetApiData<LitnetContentsResponse[]>(url);
    }

    private async Task<Dictionary<long, LitnetChapterResponse>> GetChapterBatch(string token, LitnetContentsResponse[] batch)
    {
        var chapters = string.Join("&", batch.Select(t => $"chapter_ids[]={t.Id}"));
        var url = ApiIp.MakeRelativeUri($"/v1/book/get-chapters-texts/?{chapters}&app=android&device_id={DeviceId}&sign={GetSign(token)}&user_token={token}");
        var data = await GetApiData<LitnetChapterResponse[]>(url) ?? [];
        var requested = batch.Select(chapter => chapter.Id).ToHashSet();
        var received = data.Where(chapter => chapter != null && requested.Contains(chapter.Id))
            .DistinctBy(chapter => chapter.Id).ToDictionary(chapter => chapter.Id);
        var missing = requested.Count - received.Count;
        if (missing > 0)
            throw new InvalidOperationException($"Litnet не вернул {missing} из {batch.Length} запрошенных глав. Проверьте доступ к главам и повторите загрузку. Неполная книга не будет сохранена.");
        return received;
    }

    private async Task<T> GetApiData<T>(Uri uri)
    {
        Config.Logger.LogDebug("Litnet API: ожидается={ResponseType}; url={Url}", typeof(T).Name, DownloadDiagnostics.SafeUri(uri));
        using var request = GetDefaultMessage(uri, ApiUrl);
        using var response = await Config.Client.SendAsync(request, Config.CancellationToken);
        response.EnsureDownloadSuccess();
        try
        {
            var data = await response.Content.ReadFromJsonAsync<T>(Config.CancellationToken);
            Config.Logger.LogDebug("Litnet API разобран: тип={ResponseType}; empty={Empty}; items={Count}",
                typeof(T).Name, data == null, data is Array array ? array.Length : (int?)null);
            return data;
        }
        catch (System.Text.Json.JsonException error)
        {
            Config.Logger.LogDebug("Litnet API не разобран: тип={ResponseType}; status={Status}; line={Line}; byte={Byte}",
                typeof(T).Name, (int)response.StatusCode, error.LineNumber, error.BytePositionInLine);
            // JSON Path/Message могут включать серверные данные; в общий журнал они не переносятся.
            throw new InvalidOperationException("Litnet вернул некорректный JSON. Проверьте доступ к сайту и повторите попытку.");
        }
    }

    public override async Task<Book> Get(Uri url)
    {
        var bookId = GetId(url);

        var litnetBook = await GetBook(_token, bookId);

        var uri = SystemUrl.MakeRelativeUri(litnetBook.Url.AsUri().AbsolutePath);
        var book = new Book(uri)
        {
            Cover = await GetCover(litnetBook),
            Chapters = await FillChapters(_token, litnetBook, bookId),
            Title = litnetBook.Title.Trim(),
            Author = GetAuthor(litnetBook),
            CoAuthors = GetCoAuthors(litnetBook),
            Annotation = GetAnnotation(litnetBook),
            Seria = await GetSeria(uri, litnetBook),
            Lang = litnetBook.Lang
        };

        return book;
    }

    private async Task<Seria> GetSeria(Uri url, LitnetBookResponse book)
    {
        try
        {
            using var request = GetDefaultMessage(url.ReplaceHost(SiteIp.Host), SystemUrl);
            using var response = await Config.Client.SendAsync(request, Config.CancellationToken);
            response.EnsureDownloadSuccess();
            await using var stream = await response.Content.ReadAsStreamAsync(Config.CancellationToken);
            var doc = stream.AsHtmlDoc();
            var a = doc.QuerySelector("div.book-view-info-coll a[href*='sort=cycles']")
                // В новой карточке подпись «Цикл» осталась, но старой обёртки больше нет.
                ?? doc.QuerySelectorAll("span.meta-name")
                    .FirstOrDefault(label => string.Equals(label.GetText()?.Trim().TrimEnd(':').Trim(), "Цикл", StringComparison.OrdinalIgnoreCase))
                    ?.ParentNode.QuerySelector("a[href*='sort=cycles']");
            if (a != null && !string.IsNullOrWhiteSpace(a.GetText()))
            {
                return new Seria
                {
                    Name = a.GetText(), Url = url.MakeRelativeUri(WebUtility.HtmlDecode(a.Attributes["href"].Value)), Number = book.CyclePriority is > 0 ? book.CyclePriority.Value.ToString() : string.Empty
                };
            }
            if (book.CyclePriority is > 0)
                Config.Logger.LogWarning("Litnet сообщил номер книги в цикле ({Number}), но название серии не найдено. Книга будет сохранена без серии.", book.CyclePriority);
        }
        catch (OperationCanceledException) when (Config.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Config.Logger.LogWarning("Не удалось получить серию Litnet: {Reason}. Книга будет сохранена без серии.", ex.Message);
        }

        return null;
    }

    private Author GetAuthor(LitnetBookResponse book)
    {
        return new Author((book.AuthorName ?? SystemUrl.Host).Trim(), SystemUrl.MakeRelativeUri($"/ru/{book.AuthorId}"));
    }

    private IEnumerable<Author> GetCoAuthors(LitnetBookResponse book)
    {
        var result = new List<Author>();
        if (!string.IsNullOrWhiteSpace(book.CoAuthorName) && book.CoAuthorId.HasValue)
        {
            result.Add(new Author((book.CoAuthorName ?? SystemUrl.Host).Trim(), SystemUrl.MakeRelativeUri($"/ru/{book.CoAuthorId.Value}")));
        }

        return result;
    }

    private static string GetAnnotation(LitnetBookResponse book)
    {
        return string.IsNullOrWhiteSpace(book.Annotation) ?
            string.Empty :
            string.Join("", book.Annotation.Split("\n", StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().CoverTag("p")));
    }

    private Task<TempFile> GetCover(LitnetBookResponse book)
    {
        return !string.IsNullOrWhiteSpace(book.Cover) ? SaveImage(book.Cover.AsUri()) : Task.FromResult(default(TempFile));
    }

    protected override HttpRequestMessage GetImageRequestMessage(Uri uri)
    {
        if (uri.IsSameHost(SystemUrl) || uri.IsSameSubDomain(SystemUrl))
        {
            return GetDefaultMessage(SystemUrl.MakeRelativeUri(uri.AbsolutePath), uri);
        }

        return base.GetImageRequestMessage(uri);
    }

    private HttpRequestMessage GetDefaultMessage(Uri uri, Uri host, HttpContent content = null)
    {
        var message = new HttpRequestMessage(content == null ? HttpMethod.Get : HttpMethod.Post, uri);
        message.Content = content;
        message.Version = Config.Client.DefaultRequestVersion;

        foreach (var header in Config.Client.DefaultRequestHeaders)
        {
            message.Headers.Add(header.Key, header.Value);
        }

        message.Headers.Host = host.Host;

        return message;
    }

    private async Task<List<Chapter>> FillChapters(string token, LitnetBookResponse book, string bookId)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        Config.Logger.LogInformation("Получаю оглавление Litnet");
        var contents = await GetBookContents(token, bookId);
        if (contents == null || contents.Length == 0)
            throw new InvalidOperationException("Litnet не вернул оглавление. Проверьте доступ к книге. Пустая книга не будет сохранена.");
        if (contents.Any(content => content == null || content.Id <= 0))
            throw new InvalidOperationException("Litnet вернул некорректное оглавление. Неполная книга не будет сохранена.");

        var selected = SliceToc(contents.DistinctBy(content => content.Id).ToList(), content => content.Title).ToList();
        foreach (var batch in selected.Chunk(ChapterBatchSize))
        {
            Config.CancellationToken.ThrowIfCancellationRequested();
            Config.Logger.LogInformation("Получаю текст глав Litnet {Start}–{End} из {Total}", result.Count + 1, result.Count + batch.Length, selected.Count);
            var map = await GetChapterBatch(token, batch);
            foreach (var content in batch)
            {
                Config.CancellationToken.ThrowIfCancellationRequested();
                var litnetChapter = map[content.Id];
                var title = (content.Title ?? book.Title).Trim();
                Config.Logger.LogInformation("Загружаю главу {Title}", title.CoverQuotes());
                if (string.IsNullOrWhiteSpace(litnetChapter.Text))
                    throw new InvalidOperationException($"Litnet не вернул текст главы {title.CoverQuotes()}. Проверьте доступ к главе. Неполная книга не будет сохранена.");

                HtmlDocument chapterDoc;
                try
                {
                    chapterDoc = GetChapterDoc(Encoding.UTF8.GetString(Decrypt(litnetChapter.Text)));
                }
                catch (Exception ex) when (ex is FormatException or CryptographicException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
                {
                    // Не выводим тело ответа, токен сеанса или адрес API с учётными данными.
                    throw new InvalidOperationException($"Litnet вернул некорректный текст главы {title.CoverQuotes()}. Неполная книга не будет сохранена.");
                }
                if (string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(chapterDoc.DocumentNode.InnerText)) && chapterDoc.QuerySelector("img, image") == null)
                    throw new InvalidOperationException($"Litnet вернул пустую главу {title.CoverQuotes()}. Неполная книга не будет сохранена.");
                result.Add(new Chapter
                {
                    Title = title,
                    Images = await GetImages(chapterDoc, SystemUrl),
                    Content = chapterDoc.DocumentNode.InnerHtml
                });
            }
        }

        return result;
    }

    private static HtmlDocument GetChapterDoc(string text)
    {
        var rows = text.Deserialize<string[]>();
        if (rows == null || rows.Any(row => row == null))
            throw new InvalidOperationException("Некорректная структура текста Litnet");
        return string.Concat(rows).AsHtmlDoc();
    }
}
