using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc;
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
        Config.Logger.LogDebug("Rulate parser: этап=вход, credentials={Credentials}", Config.HasCredentials);
        if (!Config.HasCredentials)
        {
            return;
        }

        var main = await Config.Client.GetHtmlDocWithTriesAsync(SystemUrl);
        LogPageStructure(main, "форма входа");
        using var authData = GetAuthData(main);
        var doc = await Config.Client.PostHtmlDocWithTriesAsync(SystemUrl, authData);
        LogPageStructure(doc, "ответ входа");
        if (doc.ParsedText.TrimStart().StartsWith('{'))
        {
            RulateAuthResponse response;
            try
            {
                response = doc.ParsedText.Deserialize<RulateAuthResponse>();
            }
            catch (JsonException ex)
            {
                Config.Logger.LogDebug("Rulate parser: auth JSON invalid, line={Line}, byte={Byte}", ex.LineNumber, ex.BytePositionInLine);
                throw new InvalidOperationException("Не удалось подтвердить вход: сайт вернул непонятный ответ. Повторите попытку позже.");
            }
            Config.Logger.LogDebug("Rulate parser: auth JSON errorPresent={ErrorPresent}", !string.IsNullOrWhiteSpace(response?.Error));
            if (!string.IsNullOrWhiteSpace(response?.Error))
                throw new InvalidOperationException("Не удалось авторизоваться: сайт отклонил вход. Проверьте логин и пароль; возможно, сайт требует проверку в браузере.");

            // Отсутствие error в AJAX-ответе ещё не означает, что сессия действительно создана.
            doc = await Config.Client.GetHtmlDocWithTriesAsync(SystemUrl);
            LogPageStructure(doc, "проверка сессии");
        }

        var alertBlock = doc.QuerySelector("div.alert.in.alert-block, .alert-danger, .alert-error, .errorSummary");
        if (alertBlock != null)
            throw new InvalidOperationException("Не удалось авторизоваться: сайт сообщил об ошибке входа. Проверьте логин и пароль; возможно, сайт требует проверку в браузере.");
        if (GetLoginForm(doc) != null)
            throw new InvalidOperationException("Не удалось авторизоваться: сайт снова показал форму входа. Проверьте логин и пароль; возможно, сайт требует проверку в браузере.");
        if (string.IsNullOrWhiteSpace(GetCsrfToken(doc)) && !HasSignOutAction(doc))
            throw new InvalidOperationException("Не удалось подтвердить вход: вместо страницы сайта получен неизвестный ответ или страница проверки. Проверьте доступность сайта в браузере и повторите попытку.");

        Config.Logger.LogInformation("Успешно авторизовались");
    }

    private FormUrlEncodedContent GetAuthData(HtmlDocument doc)
    {
        var form = GetLoginForm(doc);
        var token = GetCsrfToken(doc, form);
        Config.Logger.LogDebug("Rulate parser: loginForm={LoginForm}, csrfPresent={CsrfPresent}", form != null, !string.IsNullOrWhiteSpace(token));
        if (form == null || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Не удалось открыть форму входа: сайт не передал форму или защитный токен. Возможно, вместо неё открылась проверка сайта. Проверьте доступность сайта в браузере и повторите попытку.");

        var data = new Dictionary<string, string>
        {
            ["login[login]"] = Config.Options.Login,
            ["login[pass]"] = Config.Options.Password,
            ["csrf_token"] = token
        };

        return new FormUrlEncodedContent(data);
    }

    private static HtmlNode GetLoginForm(HtmlDocument doc)
        => doc.QuerySelectorAll("form").FirstOrDefault(form =>
            form.QuerySelectorAll("input[name]").Any(input => input.GetAttributeValue("name", "") == "login[login]") &&
            form.QuerySelectorAll("input[name]").Any(input => input.GetAttributeValue("name", "") == "login[pass]"));

    private static string GetCsrfToken(HtmlDocument doc, HtmlNode form = null)
    {
        // В новой шапке токен есть и внутри формы; meta может отсутствовать в сокращённом ответе.
        var token = form?.QuerySelector("input[name=csrf_token]")?.GetAttributeValue("value", "");
        if (string.IsNullOrWhiteSpace(token))
            token = doc.QuerySelector("meta[name=csrf-token]")?.GetAttributeValue("content", "");
        return HttpUtility.HtmlDecode(token);
    }

    private bool HasSignOutAction(HtmlDocument doc)
        => doc.QuerySelectorAll("a[href], form[action]").Any(node =>
        {
            var target = HttpUtility.HtmlDecode(node.GetAttributeValue(node.Name == "form" ? "action" : "href", ""));
            return Uri.TryCreate(SystemUrl, target, out var uri) &&
                   uri.Scheme == SystemUrl.Scheme && uri.Authority == SystemUrl.Authority &&
                   uri.AbsolutePath.TrimEnd('/') is "/logout" or "/site/logout" or "/user/logout";
        });

    private void LogPageStructure(HtmlDocument doc, string stage)
    {
        if (!Config.Logger.IsEnabled(LogLevel.Debug)) return;
        // Только структура первых узлов: значения полей, script, текст книги и идентификаторы сессии не выводятся.
        var nodes = doc.DocumentNode.Descendants().Take(4097).ToArray();
        var sample = nodes.Take(4096).ToArray();
        Config.Logger.LogDebug("Rulate DOM: stage={Stage}, htmlChars={Chars}, nodes={Nodes}, truncated={Truncated}, forms={Forms}, inputs={Inputs}, scripts={Scripts}, links={Links}, tocMarker={Toc}, loginField={Login}, passwordField={Password}, csrfMeta={CsrfMeta}, csrfInput={CsrfInput}, challengeMarker={Challenge}",
            stage, doc.ParsedText.Length, sample.Length, nodes.Length > sample.Length,
            sample.Count(n => n.Name == "form"), sample.Count(n => n.Name == "input"), sample.Count(n => n.Name == "script"), sample.Count(n => n.Name == "a"),
            sample.Any(n => n.GetAttributeValue("id", "") == "Chapters"),
            sample.Any(n => n.Name == "input" && n.GetAttributeValue("name", "") == "login[login]"),
            sample.Any(n => n.Name == "input" && n.GetAttributeValue("name", "") == "login[pass]"),
            sample.Any(n => n.Name == "meta" && n.GetAttributeValue("name", "") == "csrf-token"),
            sample.Any(n => n.Name == "input" && n.GetAttributeValue("name", "") == "csrf_token"),
            sample.Any(n => n.GetAttributeValue("id", "").Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
                            n.GetAttributeValue("class", "").Contains("captcha", StringComparison.OrdinalIgnoreCase)));
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
        Config.Logger.LogDebug("Rulate parser: preview={Preview}, start={Start}, end={End}, startNameSet={StartName}, endNameSet={EndName}, images={Images}",
            Config.Options.NoChapters, Config.Options.Start, Config.Options.End,
            !string.IsNullOrWhiteSpace(Config.Options.StartName), !string.IsNullOrWhiteSpace(Config.Options.EndName), !Config.Options.NoImage);
        if (Config.Options.NoChapters)
        {
            return result;
        }

        var toc = await GetToc(doc, bookUri);
        var cache = new ChapterTextCache(Config.Options.ChapterCachePath, Config.Logger);
        var account = Config.HasCredentials ? Config.Options.Login : null;
        var ct = Config.CancellationToken;
        var selected = SliceToc(toc, c => c.Title);
        if (Config.Logger.IsEnabled(LogLevel.Debug))
            Config.Logger.LogDebug("Rulate parser: tocTotal={Total}, selected={Selected}, chapterCache={Cache}", toc.Count, selected.Count(), cache.Enabled);
        var index = 0;
        foreach (var entry in selected)
        {
            index++;
            ct.ThrowIfCancellationRequested();
            var (id, name, unavailable, available) = entry;
            Config.Logger.LogDebug("Rulate parser: selectedIndex={Index}, denied={Denied}, readable={Readable}, cacheEligible={CacheEligible}", index, unavailable, available, cache.Enabled && available);
            if (unavailable)
            {
                Config.Logger.LogWarning($"Глава «{name}» не загружена: оглавление сайта явно запрещает скачивание для текущей сессии. {SystemUrl.MakeRelativeUri($"/book/{bookId}/{id}")}");
                continue;
            }

            var chapter = new Chapter();
            // Кэш не заменяет проверку текущего доступа: неизвестная разметка идёт через API.
            var cached = available ? await cache.ReadAsync(bookUri, id, account, ct) : null;
            Config.Logger.LogDebug("Rulate parser: selectedIndex={Index}, cacheHit={CacheHit}", index, cached != null);
            HtmlDocument chapterDoc;
            if (cached != null)
            {
                Config.Logger.LogInformation($"Использую сохранённый текст главы {name.CoverQuotes()}; изображения проверяются заново");
                chapterDoc = cached.AsHtmlDoc();
            }
            else
            {
                Config.Logger.LogInformation($"Загружаю главу {name.CoverQuotes()}");
                chapterDoc = await GetChapter(bookId, id);
                ct.ThrowIfCancellationRequested();
                if (available)
                    await cache.WriteAsync(bookUri, id, account, chapterDoc.DocumentNode.InnerHtml, ct);
            }
            chapter.Images = await GetImages(chapterDoc, bookUri);
            ct.ThrowIfCancellationRequested();
            chapter.Content = chapterDoc.DocumentNode.InnerHtml;
            chapter.Title = name;

            result.Add(chapter);
        }

        Config.Logger.LogDebug("Rulate parser: selected={Selected}, returned={Returned}", index, result.Count);

        return result;
    }

    private async Task<HtmlDocument> GetChapter(string bookId, string chapterId)
    {
        var s = await Config.Client.GetFromJsonWithTriesAsync<RulateChapter>(SystemUrl.MakeRelativeUri($"/book/{bookId}/{chapterId}/readyajax"));
        Config.Logger.LogDebug("Rulate parser: chapterResponse canRead={CanRead}, contentPresent={ContentPresent}, contentChars={Chars}", s.CanRead, s.Content != null, s.Content?.Length ?? 0);
        if (!s.CanRead)
        {
            Config.Logger.LogWarning($"Глава {chapterId} не загружена: сайт не предоставил доступ к тексту. {SystemUrl.MakeRelativeUri($"/book/{bookId}/{chapterId}")}");
            return string.Empty.AsHtmlDoc();
        }
        var chapterDoc = s.Content?.AsHtmlDoc();
        var content = chapterDoc?.QuerySelector("div.content-text");
        if (content == null)
        {
            if (chapterDoc != null) LogPageStructure(chapterDoc, "нет div.content-text");
            throw new InvalidOperationException($"Не найден текст доступной главы {chapterId}. Неполная книга не будет сохранена.");
        }
        // Ответ зеркала может содержать служебный адрес той же главы на основном домене.
        var sources = new[] { SystemUrl.Host, "tl.rulate.ru", "bllate.org", "mlate.ru", "erolate.com" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(host => new Uri($"https://{host}/book/{bookId}/{chapterId}")).ToArray();
        ChapterContentCleaner.RemoveTrailingSourceLink(content, sources);
        return content.InnerHtml.AsHtmlDoc();
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

    private sealed record TocChapter(string Id, string Title, bool Unavailable, bool Available);

    private static bool HasExplicitDenial(HtmlNode row)
    {
        // Ссылка в названии и цена есть и у закрытых, и у доступных глав.
        // Пропускаем запрос только по явному сообщению сайта об отсутствии доступа.
        return row.QuerySelectorAll(".disabled[title]").Any(node =>
            string.Equals(HttpUtility.HtmlDecode(node.GetAttributeValue("title", "")).Trim(),
                "Вы не можете скачивать перевод этой главы.", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasEnabledReadAction(HtmlNode row)
        => row.QuerySelectorAll("input[name]").Any(node => IsEnabled(node) &&
                   node.GetAttributeValue("name", "") == "download_chapter[]") ||
               row.QuerySelectorAll("a[title]").Any(node => IsEnabled(node) &&
                   string.Equals(HttpUtility.HtmlDecode(node.GetAttributeValue("title", "")).Trim(),
                       "Получить готовый перевод.", StringComparison.OrdinalIgnoreCase));

    private static bool IsEnabled(HtmlNode node)
        => node.Attributes["disabled"] == null &&
           !node.GetClasses().Contains("disabled", StringComparer.OrdinalIgnoreCase) &&
           !string.Equals(node.GetAttributeValue("aria-disabled", ""), "true", StringComparison.OrdinalIgnoreCase);

    private async Task<List<TocChapter>> GetToc(HtmlDocument firstPage, Uri bookUri)
    {
        var chaptersByPage = new SortedDictionary<int, List<TocChapter>>();
        var chapterIds = new HashSet<string>(StringComparer.Ordinal);
        var visitedPages = new HashSet<int>();
        var pendingPages = new SortedSet<int> { 1 };
        var lastScheduledPage = 1;

        while (pendingPages.Count > 0)
        {
            Config.CancellationToken.ThrowIfCancellationRequested();
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
                LogPageStructure(doc, "нет #Chapters");
                throw new InvalidOperationException($"На странице оглавления {pageUri} не найден список глав. Возможно, сайт ограничил доступ.");
            }

            var countBefore = chapterIds.Count;
            var pageChapters = new List<TocChapter>();
            foreach (var row in doc.QuerySelectorAll("#Chapters tr[data-id]"))
            {
                var id = row.Attributes["data-id"].Value.Trim();
                if (string.IsNullOrEmpty(id))
                {
                    throw new InvalidOperationException($"На странице оглавления {pageUri} найдена глава без идентификатора.");
                }

                chapterIds.Add(id);
                var denied = HasExplicitDenial(row);
                var enabled = HasEnabledReadAction(row);
                // Конфликт проверяем через API: нельзя ни пропустить текст, ни взять старый из кэша.
                pageChapters.Add(new TocChapter(id, row.GetTextBySelector("td.t"), denied && !enabled, enabled && !denied));
            }

            if (Config.Logger.IsEnabled(LogLevel.Debug))
                Config.Logger.LogDebug("Rulate TOC: page={Page}, rows={Rows}, newIds={NewIds}, denied={Denied}, readable={Readable}, unknownAccess={Unknown}",
                    page, pageChapters.Count, chapterIds.Count - countBefore, pageChapters.Count(c => c.Unavailable),
                    pageChapters.Count(c => c.Available), pageChapters.Count(c => !c.Available && !c.Unavailable));

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
            Config.Logger.LogDebug("Rulate TOC: page={Page}, lastScheduled={Last}, pending={Pending}, uniqueTotal={Total}", page, lastScheduledPage, pendingPages.Count, chapterIds.Count);
        }

        // На пересекающихся страницах признаки доступа могут расходиться.
        // Сохраняем первое название и порядок, но пропускаем только однозначно закрытую главу.
        return chaptersByPage.Values.SelectMany(chapters => chapters)
            .GroupBy(chapter => chapter.Id, StringComparer.Ordinal)
            .Select(group => group.First() with
            {
                Unavailable = group.All(chapter => chapter.Unavailable),
                Available = group.All(chapter => chapter.Available)
            })
            .ToList();
    }
}
