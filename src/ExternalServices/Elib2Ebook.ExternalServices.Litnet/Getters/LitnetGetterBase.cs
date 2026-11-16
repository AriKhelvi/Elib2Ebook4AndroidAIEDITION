using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private static readonly LitnetSessionCache Sessions = new();
    // API принимает до 100 глав: меньшие пачки увеличивают нагрузку и риск HTTP 429.
    // Каждую пачку обрабатываем сразу, освобождая зашифрованные тексты перед следующей.
    private const int ChapterBatchSize = 100;
    private const string SECRET = "14a6579a984b3c6abecda6c2dfa83a64";

    private string _token { get; set; }
    private string _sessionCacheKey;
    private bool _sessionRejected;
    private string _cachedBookId;
    private LitnetBookResponse _cachedBook;
    private bool _seriesLoaded;
    private Seria _cachedSeries;
    private bool _coverLoaded;
    private TempFile _cachedCover;

    public override async Task Init()
    {
        await base.Init();
        // В Android-журналах Litnet HTTP/2 возвращал заголовки, затем зависал на теле
        // API/HTML/картинки до общего таймаута. Этот API также поддерживает HTTP/1.1.
        if (SystemUrl.Host.Equals("litnet.com", StringComparison.OrdinalIgnoreCase))
            Config.Client.DefaultRequestVersion = HttpVersion.Version11;
    }

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
        ClearBookCache();
        _token = null;
        _sessionCacheKey = null;
        _sessionRejected = false;
        Config.CancellationToken.ThrowIfCancellationRequested();
        Config.Logger.LogDebug("Litnet: этап входа; сохранённые реквизиты={HasCredentials}", Config.HasCredentials);

        // Android задаёт собственную область кэша. Здесь путь используется только как идентификатор:
        // ни токен, ни отпечаток реквизитов не записываются на диск. Booknet пока не меняем.
        if (SystemUrl.Host.Equals("litnet.com", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(Config.Options.AuthCachePath))
        {
            var identity = JsonSerializer.Serialize(new[]
            {
                Config.Options.AuthCachePath, ApiUrl.Host,
                Config.HasCredentials ? "account" : "guest",
                Config.Options.Login?.TrimStart('+') ?? string.Empty,
                Config.Options.Password ?? string.Empty
            });
            _sessionCacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            var session = await Sessions.GetOrCreateAsync(_sessionCacheKey,
                _ => AuthorizeFresh(), Config.CancellationToken);
            _token = session.Token;
            if (session.Reused)
                Config.Logger.LogInformation("Использую ранее полученный сеанс Litnet без повторного входа");
            return;
        }

        _token = await AuthorizeFresh();
    }

    private async Task<string> AuthorizeFresh()
    {
        var path = Config.HasCredentials ? "user/find-by-login" : "registration/registration-by-device";

        var url = ApiIp.MakeRelativeUri(
            $"v1/{path}?login={HttpUtility.UrlEncode(Config.Options.Login?.TrimStart('+') ?? string.Empty)}&password={HttpUtility.UrlEncode(Config.Options.Password)}&app=android&device_id={DeviceId}&sign={GetSign(string.Empty)}");

        var data = await GetApiData<LitnetAuthResponse>(url);

        if (!string.IsNullOrWhiteSpace(data?.Token))
        {
            Config.Logger.LogInformation("Успешно авторизовались");
            return data.Token;
        }
        else
        {
            throw new Exception($"Не удалось авторизоваться. {data?.Error}");
        }
    }

    private async Task<LitnetBookResponse> GetBook(string token, string bookId)
    {
        if (_cachedBookId == bookId && _cachedBook != null) return _cachedBook;
        ClearBookCache();
        var url = ApiIp.MakeRelativeUri($"/v1/book/get/{bookId}?app=android&device_id={DeviceId}&user_token={token}&sign={GetSign(token)}");
        var data = await GetApiData<LitnetBookResponse>(url);
        if (data == null || string.IsNullOrWhiteSpace(data.Title) || string.IsNullOrWhiteSpace(data.Url))
            throw new InvalidOperationException("Litnet не вернул название или адрес книги. Проверьте доступ к произведению.");
        if (!Config.HasCredentials && data.AdultOnly)
        {
            throw new SiteAccessException(SiteAccessReason.AgeConfirmationRequired, "Произведение 18+. Необходимо добавить логин и пароль.");
        }

        _cachedBookId = bookId;
        _cachedBook = data;
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
        if (typeof(T) != typeof(LitnetAuthResponse)) EnsureSessionAccepted();
        Config.Logger.LogDebug("Litnet API: ожидается={ResponseType}; url={Url}", typeof(T).Name, DownloadDiagnostics.SafeUri(uri));
        using var request = GetDefaultMessage(uri, ApiUrl);
        using var response = await Config.Client.SendAsync(request, Config.CancellationToken);
        if (typeof(T) != typeof(LitnetAuthResponse) && response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionRejected = true;
            Sessions.Invalidate(_sessionCacheKey, _token);
            Config.Logger.LogDebug("Litnet: сеанс отклонён API; следующий запуск потребует нового входа.");
        }
        // 403 может относиться к правам на книгу, а 429 — к лимиту сайта. Новый вход их не исправляет.
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
        EnsureSessionAccepted();
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

    private void EnsureSessionAccepted()
    {
        if (_sessionRejected)
            throw new HttpRequestException("Сеанс Litnet отклонён (HTTP 401). Повторите запуск, чтобы выполнить новый вход.",
                null, HttpStatusCode.Unauthorized);
    }

    private async Task<Seria> GetSeria(Uri url, LitnetBookResponse book)
    {
        if (_seriesLoaded) return _cachedSeries;
        _cachedSeries = await GetSeriaCore(url, book);
        _seriesLoaded = true;
        return _cachedSeries;
    }

    private async Task<Seria> GetSeriaCore(Uri url, LitnetBookResponse book)
    {
        // Серия — необязательные метаданные: не задерживаем книгу на пользовательские 1000+ секунд.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Config.CancellationToken);
        // В бюджет входят пользовательская задержка и возможный переход на канонический URL.
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = GetDefaultMessage(url.ReplaceHost(SiteIp.Host), SystemUrl);
            using var response = await Config.Client.SendAsync(request, timeout.Token);
            response.EnsureDownloadSuccess();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
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
        // Лимит запросов относится к сеансу, а не к необязательному полю серии.
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests) { throw; }
        catch (OperationCanceledException)
        {
            Config.Logger.LogWarning("Серия Litnet не получена за отведённое время. Продолжаю скачивание без названия серии.");
        }
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

    private async Task<TempFile> GetCover(LitnetBookResponse book)
    {
        if (!_coverLoaded)
        {
            _cachedCover = !string.IsNullOrWhiteSpace(book.Cover) ? await SaveImage(book.Cover.AsUri()) : null;
            _coverLoaded = true;
        }
        if (_cachedCover == null) return null;
        // Превью и полная книга владеют разными файлами. Dispose превью не удалит обложку книги.
        await using var stream = _cachedCover.GetStream();
        return await TempFile.Create(_cachedCover.Url, Config.TempFolder.Path,
            Guid.NewGuid() + _cachedCover.Extension, stream);
    }

    protected override HttpRequestMessage GetImageRequestMessage(Uri uri)
    {
        if (uri.IsSameHost(SystemUrl) || uri.IsSameSubDomain(SystemUrl))
        {
            // CDN должен получать запрос на свой адрес: URI, TLS/SNI и Host должны совпадать.
            return GetDefaultMessage(uri, uri);
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
        var inaccessible = selected.Count(content => content.Access == false);
        if (inaccessible > 0)
        {
            selected = selected.Where(content => content.Access != false).ToList();
            if (selected.Count == 0)
                throw new SiteAccessException(SiteAccessReason.AccessDenied,
                    "В выбранном диапазоне нет доступных глав Litnet. Выберите ознакомительный фрагмент или проверьте покупку и вход на сайте.");
            Config.Logger.LogWarning("Litnet: в выбранном диапазоне доступно {Available} глав, закрыто {Closed}. Сохраняю только доступный фрагмент; закрытые главы не запрашиваются.",
                selected.Count, inaccessible);
        }
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

    private void ClearBookCache()
    {
        _cachedBookId = null;
        _cachedBook = null;
        _seriesLoaded = false;
        _cachedSeries = null;
        _coverLoaded = false;
        _cachedCover?.Dispose();
        _cachedCover = null;
    }

    public override void Dispose()
    {
        ClearBookCache();
        base.Dispose();
    }

    private static HtmlDocument GetChapterDoc(string text)
    {
        var rows = text.Deserialize<string[]>();
        if (rows == null || rows.Any(row => row == null))
            throw new InvalidOperationException("Некорректная структура текста Litnet");
        return string.Concat(rows).AsHtmlDoc();
    }
}
