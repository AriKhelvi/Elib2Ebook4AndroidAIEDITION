using System.Net.Http.Json;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc;
using Elib2Ebook.ExternalServices.Boosty.Types;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Boosty;

public class BoostyGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new("https://boosty.to/");

    private static Uri ApiUrl => new(" https://api.boosty.to/");

    protected override string GetId(Uri url)
    {
        return url.GetSegment(1);
    }

    public override async Task<Book> Get(Uri url)
    {
        var bookId = GetId(url);
        url = SystemUrl.MakeRelativeUri($"/{bookId}");
        var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);

        var book = new Book(url)
        {
            Cover = await GetCover(doc, url),
            Chapters = await FillChapters(url, bookId),
            Title = "Exclusive content on Boosty",
            Author = GetAuthor(doc, url),
            Annotation = doc.QuerySelector("div.AboutAuthor_content_HprOc")?.InnerHtml,
        };

        return book;
    }

    private static Author GetAuthor(HtmlDocument doc, Uri url)
    {
        var a = doc.QuerySelector("h1");
        return new Author(a.GetText(), url);
    }

    private async Task<IEnumerable<Chapter>> FillChapters(Uri uri, string bookId)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters) return result;

        foreach (var post in await GetToc(bookId))
        {
            Config.Logger.LogInformation($"Загружаю главу {post.Title.CoverQuotes()}");
            var chapter = new Chapter
            {
                Title = post.Title
            };

            var doc = await GetChapter(post, bookId);
            if (doc != null)
            {
                chapter.Images = await GetImages(doc, uri);
                chapter.Content = doc.DocumentNode.InnerHtml;
            }

            result.Add(chapter);
        }

        return result;
    }

    private async Task<HtmlDocument> GetChapter(BoostyPost post, string bookId)
    {
        if (!post.HasAccess)
        {
            Config.Logger.LogWarning($"Пост «{post.Title}» недоступен на Boosty и не включён в книгу");
            return null;
        }

        var doc = await Config.Client.GetHtmlDocWithTriesAsync(SystemUrl.MakeRelativeUri($"/{bookId}/posts/{post.Id}"));
        doc = doc.QuerySelector("article.Post_content_ETkyT").InnerHtml.AsHtmlDoc();
        ChapterContentCleaner.RemoveLeadingTitle(doc.DocumentNode, post.Title);

        foreach (var img in doc.QuerySelectorAll("img"))
        {
            if (img.ParentNode.Name == "a")
            {
                img.ParentNode.Name = "div";
                img.ParentNode.Attributes.RemoveAll();
            }
        }

        foreach (var div in doc.QuerySelectorAll("div.BlockRenderer_markup_Wtipg"))
        {
            if (string.IsNullOrWhiteSpace(div.InnerHtml))
            {
                div.InnerHtml = "<br />";
            }
        }

        return doc;
    }

    private async Task<IEnumerable<BoostyPost>> GetToc(string bookId)
    {
        var result = new List<BoostyPost>();
        var seenPosts = new HashSet<Guid>();
        var seenOffsets = new HashSet<string>(StringComparer.Ordinal);
        string offset = null;
        while (true)
        {
            var uri = ApiUrl.MakeRelativeUri($"/v1/blog/{bookId}/post/").AppendQueryParameter("limit", 10);
            if (offset != null) uri = uri.AppendQueryParameter("offset", offset);
            using var response = await Config.Client.GetWithTriesAsync(uri);
            response.EnsureDownloadSuccess();
            var content = await response.Content.ReadFromJsonAsync<BoostyApiResponse<BoostyPost[]>>();
            if (content?.Data == null || content.Extra == null)
                throw new InvalidOperationException("Boosty не вернул список постов. Неполная книга не будет сохранена.");
            var added = 0;
            foreach (var post in content.Data)
            {
                if (post.Id == Guid.Empty) throw new InvalidOperationException("Boosty вернул пост без идентификатора");
                if (!seenPosts.Add(post.Id)) continue;
                result.Add(post);
                added++;
            }
            if (content.Extra.IsLast) break;
            offset = content.Extra.Offset;
            if (added == 0 || string.IsNullOrWhiteSpace(offset) || !seenOffsets.Add(offset))
                throw new InvalidOperationException("Boosty повторил или не вернул следующую страницу постов. Неполная книга не будет сохранена.");
        }

        result.Reverse();
        return SliceToc(result, c => c.Title);
    }

    private Task<TempFile> GetCover(HtmlDocument doc, Uri uri)
    {
        var imagePath = doc.QuerySelector("link[rel=image_src]")?.Attributes["href"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(uri.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }
}
