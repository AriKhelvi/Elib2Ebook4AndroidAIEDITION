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
    private bool IsLitnet => SystemUrl.Host.Equals("litnet.com", StringComparison.OrdinalIgnoreCase);
    private Uri ApiUrl => new(IsLitnet ? "https://sapi.litnet.com/" : $"https://api.{SystemUrl.Host}/");

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
    private string _apiDeviceId = DeviceId;
    private bool _browserSession;
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
        _apiDeviceId = DeviceId;
        _browserSession = false;
        Config.CancellationToken.ThrowIfCancellationRequested();
        if (IsLitnet && Config.Options.LitnetSessionProvider != null)
        {
            var session = await Config.Options.LitnetSessionProvider(null, Config.CancellationToken);
            if (session != null)
            {
                SetBrowserSession(session);
                Config.Logger.LogInformation("Использую вход через Litnet ID");
                return;
            }
        }
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
        if (!Config.HasCredentials && !_browserSession && data.AdultOnly)
        {
            throw new SiteAccessException(SiteAccessReason.AgeConfirmationRequired, IsLitnet
                ? "Произведение 18+. Выполните вход в Litnet через кнопку «Войти в Litnet»."
                : "Произведение 18+. Необходимо добавить логин и пароль.");
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

    private async Task<Dictionary<long, LitnetChapterResponse>> GetChapterBatch(string token, LitnetContentsResponse[] batch,
        IReadOnlyDictionary<long, int> chapterPositions)
    {
        var chapters = string.Join("&", batch.Select(t => $"chapter_ids[]={t.Id}"));
        if (IsLitnet)
        {
            // Рабочая ветка официального приложения 4.0.16: архив выбранных глав.
            // Соседний JSON endpoint на живом сервере возвращал 500 даже с новым OAuth-сеансом.
            var archiveUrl = ApiIp.MakeRelativeUri($"/v1/book/text/get-chapters?{chapters}");
            // ResponseHeadersRead завершает штатный HttpClient.Timeout на заголовках.
            // Один бюджет охватывает также обновление сеанса и всё последующее чтение архива.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(Config.CancellationToken);
            if (Config.Client.Timeout != Timeout.InfiniteTimeSpan) budget.CancelAfter(Config.Client.Timeout);
            try
            {
                using var response = await SendApiAsync(archiveUrl, true, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                response.EnsureDownloadSuccess();
                await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
                var texts = await LitnetChapterArchive.ReadAsync(stream, batch.Select(x => x.Id).ToArray(), SECRET, budget.Token);
                return texts.ToDictionary(x => x.Key, x => new LitnetChapterResponse { Id = x.Key, Text = x.Value });
            }
            catch (LitnetIncompleteChapterArchiveException error)
            {
                var batchById = batch.ToDictionary(chapter => chapter.Id);
                var missingChapters = error.MissingChapterIds.Select(id => batchById[id]).ToArray();
                // Сводка нужна и без подробного журнала. Она не содержит глав, реквизитов или URL API.
                Config.Logger.LogInformation("Litnet: неполный архив глав; запрошено {Requested}, получено {Received}, отсутствует {Missing}. Доступ отсутствующих глав: access=true — {Granted}, access=false — {Denied}, access=null (поле отсутствует или не задано) — {Unknown}.",
                    error.RequestedCount, error.ReceivedCount, missingChapters.Length,
                    missingChapters.Count(chapter => chapter.Access == true), missingChapters.Count(chapter => chapter.Access == false),
                    missingChapters.Count(chapter => chapter.Access == null));
                if (Config.Logger.IsEnabled(LogLevel.Debug))
                {
                    const int maxDetails = 50;
                    var details = missingChapters.Take(maxDetails).Select(chapter =>
                        $"позиция {chapterPositions[chapter.Id]}, ID {chapter.Id}, access={AccessLabel(chapter.Access)}");
                    Config.Logger.LogDebug("Litnet: отсутствующие главы (показано {Shown} из {Total}): {Chapters}",
                        Math.Min(missingChapters.Length, maxDetails), missingChapters.Length, string.Join("; ", details));
                }
                throw;
            }
            catch (OperationCanceledException) when (!Config.CancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
            {
                throw new TaskCanceledException("Litnet не передал архив глав за отведённое время.",
                    new TimeoutException("Превышено время ожидания архива Litnet."), budget.Token);
            }
        }
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
        using var response = await SendApiAsync(uri, typeof(T) != typeof(LitnetAuthResponse));
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

    private void SetBrowserSession(BrowserAuthSession session)
    {
        if (string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.DeviceId))
            throw new InvalidOperationException("Вход Litnet ID недействителен. Войдите через браузер заново.");
        _token = session.AccessToken;
        _apiDeviceId = session.DeviceId;
        _browserSession = true;
    }

    private Uri PrepareApiUri(Uri uri)
    {
        if (!IsLitnet) return uri;
        var replaced = new HashSet<string>(["app", "version", "lang_content", "lang_interface", "device_id", "sign", "user_token"], StringComparer.Ordinal);
        // Сохраняем повторяющиеся chapter_ids[] отдельными полями, без объединения в строку через запятую.
        var parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !replaced.Contains(HttpUtility.UrlDecode(part.Split('=')[0]))).ToList();
        void Add(string key, string value) => parts.Add(key + "=" + Uri.EscapeDataString(value));
        Add("app", "android");
        Add("version", "400001699"); // Версия проверенного протокола Litnet, не версия E2E.
        Add("lang_content", "ru");
        Add("lang_interface", "ru");
        Add("device_id", _apiDeviceId);
        Add("sign", Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(_apiDeviceId + SECRET + (_token ?? "")))).ToLowerInvariant());
        if (!string.IsNullOrEmpty(_token)) Add("user_token", _token);
        return new UriBuilder(uri) { Query = string.Join("&", parts) }.Uri;
    }

    private async Task<HttpResponseMessage> SendApiAsync(Uri uri, bool requiresSession,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, CancellationToken? cancellationToken = null)
    {
        var ct = cancellationToken ?? Config.CancellationToken;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (requiresSession) EnsureSessionAccepted();
            using var request = GetDefaultMessage(PrepareApiUri(uri), ApiUrl);
            if (IsLitnet)
            {
                request.Headers.Accept.Clear();
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.UserAgent.Clear();
                request.Headers.UserAgent.ParseAdd("Dart/3.10 (dart:io)");
            }
            var response = await Config.Client.SendAsync(request, completion, ct);
            bool rejected;
            try { rejected = requiresSession && _browserSession && await NeedsTokenRefresh(response, ct); }
            catch { response.Dispose(); throw; }
            if (rejected && attempt == 0)
            {
                response.Dispose();
                var session = await Config.Options.LitnetSessionProvider(_token, ct);
                if (session == null) throw new InvalidOperationException("Вход Litnet ID удалён. Войдите через браузер заново.");
                SetBrowserSession(session);
                Config.Logger.LogInformation("Сеанс Litnet ID обновлён");
                continue;
            }
            if (requiresSession && (response.StatusCode == HttpStatusCode.Unauthorized || rejected))
            {
                _sessionRejected = true;
                Sessions.Invalidate(_sessionCacheKey, _token);
            }
            return response;
        }
    }

    private async Task<bool> NeedsTokenRefresh(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) return true;
        if (response.StatusCode != HttpStatusCode.Forbidden) return false;
        try
        {
            await response.Content.LoadIntoBufferAsync(65536, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("error_code", out var code) && code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt32(out var value) && value == 105;
        }
        catch (JsonException) { return false; }
        catch (HttpRequestException) { return false; }
    }

    public override Task<Book> Get(Uri url) => GetCore(url, includeSeries: true);

    // HTML-страница цикла не нужна для карточки: запрашиваем её только после получения глав.
    // Обычный Get с NoChapters сохраняет полный набор метаданных для экспорта без глав.
    public override Task<Book> GetPreview(Uri url) => GetCore(url, includeSeries: false);

    private async Task<Book> GetCore(Uri url, bool includeSeries)
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
            Seria = includeSeries ? await GetSeria(uri, litnetBook) : null,
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
            // В проверенной Angular-карточке Litnet цикл отображается текстом без ссылки.
            // Берём только поле заголовка текущей книги, а не циклы из рекомендаций ниже.
            var cycleName = doc.QuerySelector(".view-book-page-header-desktop__cycle span")?.GetText()?.Trim();
            if (!string.IsNullOrWhiteSpace(cycleName))
            {
                return new Seria
                {
                    Name = cycleName,
                    Number = book.CyclePriority is > 0 ? book.CyclePriority.Value.ToString() : string.Empty
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
        // CDN должен получать запрос на свой адрес: URI, TLS/SNI и Host должны совпадать.
        var request = uri.IsSameHost(SystemUrl) || uri.IsSameSubDomain(SystemUrl)
            ? GetDefaultMessage(uri, uri)
            : base.GetImageRequestMessage(uri);
        // Как при переходе из браузера на внешний хост картинки: только origin сайта,
        // без пути книги, параметров ссылки и данных входа.
        request.Headers.Referrer = new Uri(SystemUrl.GetLeftPart(UriPartial.Authority) + "/");
        return request;
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

        var uniqueContents = contents.DistinctBy(content => content.Id).ToList();
        var chapterPositions = uniqueContents.Select((chapter, index) => (chapter.Id, Position: index + 1))
            .ToDictionary(chapter => chapter.Id, chapter => chapter.Position);
        var selected = SliceToc(uniqueContents, content => content.Title).ToList();
        Config.Logger.LogDebug("Litnet: оглавление — {Total} уникальных глав; выбрано {Selected}. Доступ выбранных глав: access=true — {Granted}, access=false — {Denied}, access=null (поле отсутствует или не задано) — {Unknown}.",
            uniqueContents.Count, selected.Count, selected.Count(content => content.Access == true),
            selected.Count(content => content.Access == false), selected.Count(content => content.Access == null));
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
            var map = await GetChapterBatch(token, batch, chapterPositions);
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
                    chapterDoc = GetChapterDoc(IsLitnet ? litnetChapter.Text : Encoding.UTF8.GetString(Decrypt(litnetChapter.Text)));
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

    private static string AccessLabel(bool? access) => access switch
    {
        true => "true",
        false => "false",
        null => "null (поле отсутствует или не задано)"
    };

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
