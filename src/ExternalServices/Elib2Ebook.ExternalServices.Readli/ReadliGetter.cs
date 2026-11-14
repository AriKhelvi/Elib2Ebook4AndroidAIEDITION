using System.Text;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Readli;

public class ReadliGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://readli.net");

    // cloudflare :(
    private readonly Uri _host = new("https://213.227.140.81/");

    public override async Task Init()
    {
        await base.Init();
        Config.Client.DefaultRequestHeaders.Add("Host", SystemUrl.Host);
    }

    protected override HttpRequestMessage GetImageRequestMessage(Uri uri)
    {
        var request = base.GetImageRequestMessage(uri);
        // SaveImage получает публичный адрес: он нужен для проверки TLS и области действия заголовков.
        // Меняем только адрес соединения для собственного HTTPS-хоста; внешние изображения не перенаправляем.
        if (uri.Scheme == SystemUrl.Scheme && uri.IdnHost == SystemUrl.IdnHost && uri.Port == SystemUrl.Port)
        {
            request.RequestUri = uri.ReplaceHost(_host.Host);
            request.Headers.Host = SystemUrl.Host;
        }
        return request;
    }

    public override async Task<Book> Get(Uri url)
    {
        url = await GetMainUrl(url);

        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        var pages = long.Parse(doc.GetTextBySelector("span.button-pages__right").Split(' ')[0]);
        var imageDiv = doc.QuerySelector("div.book-image");
        var href = url.MakeRelativeUri(imageDiv.QuerySelector("a").Attributes["href"].Value);
        var bookId = GetBookId(href);

        var title = doc.GetTextBySelector("h1.main-info__title");
        var book = new Book(url)
        {
            Cover = await GetCover(imageDiv, url),
            Chapters = await FillChapters(bookId, pages, title),
            Title = title,
            Author = GetAuthor(doc, url),
            Annotation = doc.QuerySelector("article.seo__content")?.RemoveNodes("h2")?.InnerHtml,
            Seria = GetSeria(doc, url)
        };

        return book;
    }

    private static Author GetAuthor(HtmlDocument doc, Uri url)
    {
        var author = doc.QuerySelector("a.main-info__link");
        return new Author(author.InnerText.HtmlDecode(), url.MakeRelativeUri(author.Attributes["href"]?.Value ?? string.Empty));
    }

    private static Seria GetSeria(HtmlDocument doc, Uri url)
    {
        var a = doc.QuerySelector("div.book-info a.book-info__link[href^=/serie/]");
        if (string.IsNullOrWhiteSpace(a.GetText()))
        {
            return null;
        }

        var text = a.GetText();
        var uri = url.MakeRelativeUri(a.Attributes["href"].Value);
        if (!text.Contains('#'))
        {
            return new Seria
            {
                Name = text, Url = uri
            };
        }

        var parts = text.Split('#', StringSplitOptions.RemoveEmptyEntries);

        return new Seria
        {
            Name = parts[0].HtmlDecode(), Number = parts[1].HtmlDecode(), Url = uri
        };
    }

    private static string GetBookId(Uri uri)
    {
        return uri.GetQueryParameter("b") ?? throw new InvalidOperationException("Не удалось определить идентификатор книги");
    }

    private async Task<Uri> GetMainUrl(Uri url)
    {
        if (GetId(url).StartsWith("chitat-online", StringComparison.InvariantCultureIgnoreCase))
        {
            var doc = await Config.Client.GetHtmlDocWithTriesAsync(SystemUrl.MakeRelativeUri($"/chitat-online/?b={GetBookId(url)}").ReplaceHost(_host.Host));
            var href = doc.QuerySelector("h1 a").Attributes["href"].Value;
            return url.MakeRelativeUri(href).ReplaceHost(_host.Host);
        }

        return url.ReplaceHost(_host.Host);
    }

    private Task<TempFile> GetCover(HtmlNode doc, Uri bookUri)
    {
        var imagePath = doc.QuerySelector("img")?.Attributes["src"]?.Value;
        if (string.IsNullOrWhiteSpace(imagePath)) return Task.FromResult(default(TempFile));
        var imageUri = bookUri.MakeRelativeUri(imagePath);
        if (imageUri.IdnHost == _host.IdnHost) imageUri = imageUri.ReplaceHost(SystemUrl.Host);
        return SaveImage(imageUri);
    }

    private async Task AddChapter(ICollection<Chapter> chapters, Chapter chapter, StringBuilder text)
    {
        if (chapter == null)
        {
            return;
        }

        var chapterDoc = new HtmlDocument();
        chapterDoc.LoadHtml(text.ToString());
        chapter.Images = await GetImages(chapterDoc, SystemUrl.MakeRelativeUri("/chitat-online/"));
        chapter.Content = chapterDoc.DocumentNode.InnerHtml;
        chapters.Add(chapter);
    }

    private static bool IsSingleChapter(IEnumerable<HtmlNode> nodes)
    {
        var firstNode = nodes.FirstOrDefault();
        if (firstNode == null)
            throw new InvalidOperationException("Readli вернул пустую страницу книги. Неполный файл не будет сохранён.");
        return firstNode.Name != "h3" || string.IsNullOrWhiteSpace(firstNode.InnerText);
    }

    private static Chapter CreateChapter(string title)
    {
        return new Chapter
        {
            Title = title
        };
    }

    private async Task<HtmlDocument> GetChapter(string bookId, int page)
    {
        using var response = await Config.Client.GetWithTriesAsync(SystemUrl.MakeRelativeUri($"/chitat-online/?b={bookId}&pg={page}").ReplaceHost(_host.Host));
        response.EnsureDownloadSuccess();
        await using var stream = await response.Content.ReadAsStreamAsync(Config.CancellationToken);
        return stream.AsHtmlDoc();
    }

    private async Task<List<Chapter>> FillChapters(string bookId, long pages, string name)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        Chapter chapter = null;
        var singleChapter = true;
        var text = new StringBuilder();
        var pending = new List<(Chapter Chapter, StringBuilder Text)>();
        // Отрицательные индексы и поиск по названию требуют полного оглавления.
        var earlyEnd = Config.Options.End is > 0 && Config.Options.Start is not < 0 &&
                       string.IsNullOrWhiteSpace(Config.Options.StartName) && string.IsNullOrWhiteSpace(Config.Options.EndName)
            ? Config.Options.End : null;

        for (var i = 1; i <= pages; i++)
        {
            Config.CancellationToken.ThrowIfCancellationRequested();
            Config.Logger.LogInformation($"Получаю страницу {i}/{pages}");
            var page = await GetChapter(bookId, i);
            var content = page.QuerySelector("div.reading__text");
            if (content == null)
                throw new InvalidOperationException("Readli не вернул текст страницы книги. Проверьте доступность чтения на сайте; неполный файл не будет сохранён.");
            content.RemoveNodes("script, ins, div[id*=adfox]");
            if (string.IsNullOrWhiteSpace(content.InnerText) && content.QuerySelector("img") == null)
                throw new InvalidOperationException("Readli вернул пустую страницу книги. Неполный файл не будет сохранён.");
            // Составной CSS-селектор этой библиотеки группирует h3/p/img по типу,
            // поэтому порядок текста и заголовков берём непосредственно из DOM.
            var nodes = content.ChildNodes.Where(node => node.Name is "h3" or "p" or "img").ToList();
            if (nodes.Count == 0) nodes = content.ChildNodes.ToList();
            singleChapter = i == 1 ? IsSingleChapter(nodes) : singleChapter;

            foreach (var node in nodes)
            {
                if (singleChapter || node.Name != "h3")
                {
                    if (node.Name == "img" && node.Attributes["src"] != null)
                    {
                        text.Append($"<img src='{node.Attributes["src"].Value}'/>");
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(node.InnerHtml))
                        {
                            text.Append(node.InnerHtml.CoverTag("p"));
                        }
                    }
                }
                else
                {
                    if (chapter != null)
                    {
                        pending.Add((chapter, text));
                        // Только следующий заголовок доказывает, что глава закончилась,
                        // даже если её текст занимал несколько страниц сайта.
                        if (earlyEnd.HasValue && pending.Count >= earlyEnd.Value)
                            return await BuildSelectedChapters(pending);
                    }
                    text = new StringBuilder();
                    chapter = CreateChapter(node.InnerHtml.HtmlDecode());
                }
            }
        }

        pending.Add((chapter ?? CreateChapter(name), text));
        return await BuildSelectedChapters(pending);
    }

    private async Task<List<Chapter>> BuildSelectedChapters(List<(Chapter Chapter, StringBuilder Text)> pending)
    {
        var result = new List<Chapter>();
        foreach (var item in SliceToc(pending, item => item.Chapter.Title))
        {
            Config.CancellationToken.ThrowIfCancellationRequested();
            await AddChapter(result, item.Chapter, item.Text);
        }
        return result;
    }
}
