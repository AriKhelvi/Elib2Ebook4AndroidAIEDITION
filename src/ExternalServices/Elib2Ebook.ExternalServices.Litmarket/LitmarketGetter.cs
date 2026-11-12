using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.ExternalServices.Litmarket.Types;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Litmarket;

public class LitmarketGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://litmarket.ru");

    public override async Task Init()
    {
        await base.Init();

        Config.Client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

        using var response = await Config.Client.GetWithTriesAsync(SystemUrl);
        response.EnsureDownloadSuccess();
        await using var stream = await response.Content.ReadAsStreamAsync();
        var doc = stream.AsHtmlDoc();

        var csrf = doc.QuerySelector("[name=csrf-token]")?.Attributes["content"]?.Value;
        if (string.IsNullOrWhiteSpace(csrf))
        {
            throw new ArgumentException("Не удалось получить csrf-token", nameof(csrf));
        }

        var cookies = response.Headers.SingleOrDefault(header => header.Key == "Set-Cookie").Value;
        var xsrfCookie = cookies?.FirstOrDefault(c => c.StartsWith("XSRF-TOKEN="));
        if (xsrfCookie == null)
        {
            throw new ArgumentException("Не удалось получить XSRF-TOKEN", nameof(xsrfCookie));
        }

        var xsrf = HttpUtility.UrlDecode(xsrfCookie.Split(";")[0].Replace("XSRF-TOKEN=", ""));

        Config.Client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf);
        Config.Client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", xsrf);
    }

    public override async Task<Book> Get(Uri url)
    {
        var bookId = GetId(url);
        url = SystemUrl.MakeRelativeUri($"/books/{bookId}");

        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
        var content = await GetMainData(bookId);

        var blocks = await GetBlocks(content.Book.EbookId);

        var title = doc.GetTextBySelector("h1.card-title").ReplaceNewLine();
        var book = new Book(url.ReplaceHost(SystemUrl.Host))
        {
            Cover = await GetCover(doc, url),
            Chapters = await FillChapters(GetToc(content, title), blocks, url, content.Book.EbookId),
            Title = title,
            Author = GetAuthor(doc, url),
            Annotation = doc.QuerySelector("div.card-description")?.InnerHtml,
            Seria = GetSeria(doc, url)
        };

        return book;
    }

    private static Seria GetSeria(HtmlDocument doc, Uri url)
    {
        var a = doc.QuerySelector("div.card-cycle a");
        if (a == null || !a.GetText().Contains('#'))
        {
            return null;
        }

        var parts = a.GetText().Split('#', StringSplitOptions.RemoveEmptyEntries);

        return new Seria
        {
            Name = parts[0].HtmlDecode(), Number = parts[1].HtmlDecode(), Url = url.MakeRelativeUri(a.Attributes["href"].Value)
        };
    }

    private Author GetAuthor(HtmlDocument doc, Uri url)
    {
        var a = doc.QuerySelector("div.card-author a");
        return a == null ?
            new Author(doc.GetTextBySelector("div.card-author").Replace("Автор:", "").ReplaceNewLine()) :
            new Author(a.GetText().ReplaceNewLine(), url.MakeRelativeUri(a.Attributes["href"].Value).ReplaceHost(SystemUrl.Host));
    }

    /// <summary>
    /// Авторизация в системе
    /// </summary>
    /// <exception cref="Exception"></exception>
    public override async Task Authorize()
    {
        if (!Config.HasCredentials)
        {
            return;
        }

        var payload = new
        {
            email = Config.Options.Login, password = Config.Options.Password
        };

        using var post = await Config.Client.PostAsJsonAsync(SystemUrl.MakeRelativeUri("/auth/login"), payload);
        try
        {
            var data = await post.Content.ReadFromJsonAsync<AuthResponse>();
            if (data is { Success: false })
            {
                throw new Exception("Не удалось авторизоваться");
            }
        }
        catch
        {
            throw new Exception("Не удалось авторизоваться");
        }
    }

    private Task<TempFile> GetCover(HtmlDocument doc, Uri uri)
    {
        var imagePath = doc.QuerySelector("img.img.bookCardSkeleton")?.Attributes["src"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(uri.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }

    private List<Block> GetToc(Response response, string title)
    {
        var toc = response.Toc.Deserialize<List<Block>>();
        if (toc == null || toc.Count == 0)
        {
            toc =
            [
                new()
                {
                    Index = 0,
                    Chunk = new Chunk
                    {
                        Mods =
                        [
                            new Mod
                            {
                                Text = title
                            }
                        ]
                    }
                }
            ];
        }

        return toc;
    }

    private static string NormalizeTitle(string text)
        => Regex.Replace(HttpUtility.HtmlDecode(text ?? string.Empty), @"\s+", " ").Trim();

    private static string ModText(Mod mod)
        => mod.Text ?? string.Concat((mod.Mods ?? []).Select(ModText));

    private static string BlockTitle(Block block)
        => NormalizeTitle(string.Concat((block.Chunk.Mods ?? []).Select(ModText)));

    private static string ChapterTitle(Block block)
        => BlockTitle(block) is { Length: > 0 } title ? title : "Без названия";

    private static bool ContainsImage(Mod mod)
        => mod.Type == "IMAGE" || (mod.Mods?.Any(ContainsImage) ?? false);

    private async Task<List<Chapter>> FillChapters(List<Block> toc, Block[] blocks, Uri bookUri, long eBookId)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        // Граница последней выбранной главы берётся из полного оглавления, иначе
        // при заданном End в неё попадает весь оставшийся текст книги.
        var ranges = toc.Select((block, index) => (
            Block: block,
            NextIndex: index + 1 < toc.Count ? (int?)toc[index + 1].Index : null)).ToList();
        foreach (var range in SliceToc(ranges, r => ChapterTitle(r.Block)))
        {
            var chapterTitle = ChapterTitle(range.Block);
            Config.Logger.LogInformation($"Загружаю главу {chapterTitle.CoverQuotes()}");
            var sb = new StringBuilder();
            var chapter = new Chapter();

            foreach (var block in blocks.Where(b => b.Index >= range.Block.Index && (!range.NextIndex.HasValue || b.Index < range.NextIndex.Value)))
            {
                var mods = block.Chunk?.Mods
                    ?? throw new InvalidOperationException($"Литмаркет не вернул содержимое блока {block.Index}. Неполная книга не будет сохранена.");
                // Название уже выводит сборщик книги. Убираем только совпавший
                // блок типа chapter на его позиции в оглавлении; обычную прозу
                // и блоки с изображением сохраняем, даже если их текст совпал.
                if (block.Index == range.Block.Index && block.Chunk.Type == "chapter"
                    && BlockTitle(block) == chapterTitle && !mods.Any(ContainsImage))
                {
                    continue;
                }

                var p = new StringBuilder();

                foreach (var mod in mods)
                {
                    switch (mod.Type)
                    {
                        case "IMAGE":
                            p.Append($"<img src='https://{SystemUrl.Host}/uploads/ebook/{eBookId}/{mod.Data.GetProperty("src").GetString()}' />");
                            break;
                        case "LINK":
                            p.Append($"<a href='{mod.Data.GetProperty("url").GetString()}'>{mod.Mods?.FirstOrDefault()?.Text ?? string.Empty}</a>");
                            break;
                        default:
                            var text = $"{mod.Text ?? string.Empty}";
                            if (mod.Styles?.Length > 0)
                            {
                                foreach (var style in mod.Styles)
                                {
                                    switch (style)
                                    {
                                        case "ITALIC":
                                            text = text.CoverTag("em");
                                            break;
                                        case "BOLD":
                                            text = text.CoverTag("b");
                                            break;
                                        case "UNDERLINE":
                                            text = text.CoverTag("u");
                                            break;
                                        default:
                                            Config.Logger.LogInformation(style);
                                            break;
                                    }
                                }
                            }

                            p.Append(text);
                            break;
                    }
                }

                sb.Append(p.ToString().CoverTag("p"));
            }

            var chapterDoc = sb.AsHtmlDoc();
            chapter.Images = await GetImages(chapterDoc, bookUri);
            chapter.Content = chapterDoc.DocumentNode.InnerHtml;
            chapter.Title = chapterTitle;

            result.Add(chapter);
        }

        return result;
    }

    private async Task<Response> GetMainData(string bookId)
    {
        using var data = await Config.Client.GetWithTriesAsync(SystemUrl.MakeRelativeUri($"/reader/data/{bookId}"));
        data.EnsureDownloadSuccess();
        return await data.Content.ReadFromJsonAsync<Response>()
            ?? throw new InvalidOperationException("Литмаркет не вернул сведения о книге.");
    }

    private async Task<Block[]> GetBlocks(int eBookId)
    {
        using var resp = await Config.Client.GetWithTriesAsync(SystemUrl.MakeRelativeUri($"/reader/blocks/{eBookId}"));
        resp.EnsureDownloadSuccess();
        return await resp.Content.ReadFromJsonAsync<Block[]>()
            ?? throw new InvalidOperationException("Литмаркет не вернул блоки текста книги.");
    }
}
