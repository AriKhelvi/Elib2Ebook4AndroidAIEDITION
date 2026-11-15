using System.Net;
using System.Text;
using System.Text.Json;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Ficbook;

public class FicbookGetter(BookGetterConfig config) : GetterBase(config)
{
    private static readonly Uri AppUrl = new("https://fanficlets.xyz/");
    private readonly HashSet<Uri> _unavailableServers = [];
    private Uri _server = AppUrl;

    protected override Uri SystemUrl => new("https://ficbook.net/");

    public override bool IsSameUrl(Uri url) => base.IsSameUrl(url) || AppUrl.IsSameHost(url);

    protected override string GetId(Uri url) => Uri.UnescapeDataString(url.GetSegment(2));

    public override async Task Init()
    {
        await base.Init();
        Config.Client.DefaultRequestHeaders.Remove("User-Agent");
        // Не смешиваем профиль мобильного API с браузерными заголовками общего загрузчика.
        Config.Client.DefaultRequestHeaders.Remove("Accept");
        Config.Client.DefaultRequestHeaders.Remove("Accept-Language");
        // Шаблон Android-клиента 1.7.0; значения устройства синтетические, идентификаторы телефона не читаются.
        Config.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.6723.102 Mobile Safari/537.36 ||| ficbook (1.7.0) | Android (35) | Pixel 8 | ao2gvvnhkv0t22lo");
    }

    public override async Task<Book> Get(Uri url)
    {
        _server = AppUrl;
        _unavailableServers.Clear();
        var slug = GetId(url);
        var path = $"/readfic/{Uri.EscapeDataString(slug)}";
        var doc = new HtmlDocument();
        doc.LoadHtml(await ReadAsync(path));
        EnsurePage(doc);

        var title = WebUtility.HtmlDecode(doc.QuerySelector("h1").InnerText).Trim();
        var author = GetAuthor(doc);
        // Метаданные пока берём из HTML карточки. Оглавление и текст — как в Android-клиенте 1.7.0.
        List<Chapter> chapters = Config.Options.NoChapters ? [] : await FillChapters(slug, title);
        return new Book(SystemUrl.MakeRelativeUri(path))
        {
            Cover = await GetCover(doc),
            Chapters = chapters,
            Title = title,
            Author = author,
            Annotation = doc.QuerySelector("div[itemprop=description]")?.InnerHtml
        };
    }

    private static void EnsurePage(HtmlDocument doc)
    {
        if (doc.QuerySelector("h1") != null && doc.QuerySelector("a[itemprop=author]") != null)
            return;

        throw new InvalidOperationException(
            "Ficbook вернул служебную страницу вместо карточки книги. Возможно, требуется вход или проверка сайта в браузере.");
    }

    private Author GetAuthor(HtmlDocument doc)
    {
        var author = doc.QuerySelector("a[itemprop=author]");
        var href = author.Attributes["href"]?.Value;
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(SystemUrl, href, out var url))
            throw new InvalidOperationException("Ficbook вернул карточку без ссылки на автора.");
        if (AppUrl.IsSameHost(url)) url = url.ReplaceHost(SystemUrl.Host);
        return new Author(WebUtility.HtmlDecode(author.InnerText).Trim(), url);
    }

    private Task<TempFile> GetCover(HtmlDocument doc)
    {
        var imagePath = doc.QuerySelector("fanfic-cover")?.Attributes["src-original"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath)
            ? SaveImage(_server.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }

    private async Task<List<Chapter>> FillChapters(string slug, string title)
    {
        var data = await ReadApi("fanfic_parts", new { fanfic_slug = slug });
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            throw InvalidApi("список глав");

        var toc = new List<FicbookPart>();
        var seen = new HashSet<long>();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) throw InvalidApi("элемент оглавления");
            // Неопубликованные главы не читаем. Отсутствие флага само по себе не означает запрет;
            // фактический доступ к тексту проверяет сервер в fanfic_part_text.
            if (part.TryGetProperty("is_public", out var isPublic) && isPublic.ValueKind == JsonValueKind.False)
                continue;
            if (!part.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var partId) || partId <= 0 ||
                !part.TryGetProperty("order_num", out var order) || order.ValueKind != JsonValueKind.Number || !order.TryGetInt32(out var orderNum) || orderNum < 0)
                throw InvalidApi("идентификатор или порядок главы");
            if (!seen.Add(partId)) continue;
            if (!part.TryGetProperty("part_title", out var partTitle) || partTitle.ValueKind != JsonValueKind.String)
                throw InvalidApi("название главы");
            var name = WebUtility.HtmlDecode(partTitle.GetString()).Trim();
            toc.Add(new FicbookPart(partId, orderNum, string.IsNullOrWhiteSpace(name) ? title : name));
        }

        if (toc.Count == 0)
            throw new InvalidOperationException("Ficbook не вернул доступных опубликованных глав. Пустая книга не будет сохранена.");

        // order_num может иметь пропуски: диапазон задаётся позициями в доступном оглавлении, не id главы.
        toc = toc.OrderBy(part => part.Order).ToList();
        var result = new List<Chapter>();
        foreach (var part in SliceToc(toc, part => part.Title))
        {
            Config.CancellationToken.ThrowIfCancellationRequested();
            Config.Logger.LogInformation("Загружаю главу {Title}", part.Title.CoverQuotes());
            var text = await ReadApi("fanfic_part_text", new { part_id = part.Id });
            if (text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString()))
                throw InvalidApi($"текст главы {part.Id}");

            var content = FicbookContent.ToHtml(text.GetString());
            var document = new HtmlDocument();
            document.LoadHtml(content);
            if (string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(document.DocumentNode.InnerText)))
                throw InvalidApi($"пустой текст главы {part.Id}");
            result.Add(new Chapter { Title = part.Title, Content = content });
        }
        return result;
    }

    private async Task<JsonElement> ReadApi(string endpoint, object payload)
    {
        var content = await ReadAsync($"/api_mobile/{endpoint}", JsonSerializer.Serialize(payload));
        try
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var result) ||
                result.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw InvalidApi(endpoint);
            if (!result.GetBoolean())
                throw new InvalidOperationException(
                    $"Ficbook не разрешил получение данных ({endpoint}, result=false). Проверьте доступ к работе на сайте. Неполная книга не будет сохранена.");
            if (!root.TryGetProperty("data", out var data)) throw InvalidApi(endpoint);
            return data.Clone();
        }
        catch (JsonException)
        {
            // Тело ответа не попадает в журнал: там могут быть текст произведения и данные сеанса.
            throw InvalidApi($"JSON {endpoint}; возможно, вместо API пришла страница входа или защиты");
        }
    }

    private static InvalidOperationException InvalidApi(string field) => new(
        $"Ficbook вернул некорректный ответ: {field}. Неполная книга не будет сохранена.");

    private async Task<string> ReadAsync(string path, string json = null)
    {
        // Проверяем реальным запросом; отдельные пробы увеличивали бы число обращений к защите.
        // После сбоя не дёргаем недоступное зеркало перед каждой следующей главой.
        var servers = new[] { _server, _server == AppUrl ? SystemUrl : AppUrl }
            .Where(server => !_unavailableServers.Contains(server)).ToArray();
        for (var i = 0; i < servers.Length; i++)
        {
            Config.CancellationToken.ThrowIfCancellationRequested();
            var server = servers[i];
            try
            {
                using var request = new HttpRequestMessage(json == null ? HttpMethod.Get : HttpMethod.Post, new Uri(server, path));
                if (json != null)
                {
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    request.Headers.Accept.ParseAdd("application/json");
                }
                using var response = await Config.Client.SendAsync(request, Config.CancellationToken);
                if (response.Headers.TryGetValues("Cf-Mitigated", out var values) &&
                    values.Any(value => value.Equals("challenge", StringComparison.OrdinalIgnoreCase)))
                    throw new ChallengeException(response.StatusCode);
                response.EnsureDownloadSuccess();
                var content = await response.Content.ReadAsStringAsync(Config.CancellationToken);
                // Cloudflare добавляет challenge-platform/scripts/precursor и в рабочие HTML-страницы.
                // Само наличие такого скрипта не означает блокировку: ориентируемся на Cf-Mitigated.
                _server = server;
                return content;
            }
            catch (HttpRequestException ex) when (i + 1 < servers.Length &&
                (ex is ChallengeException || ex.StatusCode == null || (int)ex.StatusCode >= 500))
            {
                Config.CancellationToken.ThrowIfCancellationRequested();
                _unavailableServers.Add(server);
                Config.Logger.LogWarning("Ficbook: {Host} недоступен ({Reason}); пробую {Fallback}.",
                    server.Host, ex is ChallengeException ? "проверка Cloudflare" : "сетевая ошибка или ошибка сервера", servers[i + 1].Host);
            }
            catch (OperationCanceledException ex) when (i + 1 < servers.Length &&
                !Config.CancellationToken.IsCancellationRequested && ex.InnerException is TimeoutException)
            {
                _unavailableServers.Add(server);
                Config.Logger.LogWarning("Ficbook: таймаут {Host}; пробую {Fallback}.", server.Host, servers[i + 1].Host);
            }
        }
        throw new HttpRequestException("Ficbook: доступные серверы не ответили.");
    }

    private sealed record FicbookPart(long Id, int Order, string Title);

    private sealed class ChallengeException(HttpStatusCode status) : HttpRequestException(
        "Ficbook вернул проверку безопасности Cloudflare (Cf-Mitigated: challenge). " +
        "Текст книги не получен. Попробуйте другую сеть или повторите позже.", null, status);
}
