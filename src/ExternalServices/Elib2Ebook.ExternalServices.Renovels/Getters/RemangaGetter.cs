using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.ExternalServices.Renovels.Types;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Renovels.Getters;

public class RemangaGetter(BookGetterConfig config) : RenovelsGetterBase(config)
{
    protected override Uri SystemUrl => new("https://remanga.org/");

    protected override string Segment => "manga";

    protected override async Task<IEnumerable<TempFile>> GetImages(HtmlDocument doc, Uri baseUri)
    {
        Config.CancellationToken.ThrowIfCancellationRequested();
        if (Config.Options.NoImage) return await base.GetImages(doc, baseUri);

        var images = new List<TempFile>();
        try
        {
            // Страницы обязательны: после отказа не запускаем остальные запросы
            // и не сохраняем частичный CBZ. Последовательная загрузка не оставляет хвостов в очереди.
            foreach (var node in doc.QuerySelectorAll("img"))
            {
                Config.CancellationToken.ThrowIfCancellationRequested();
                var path = node.GetAttributeValue("src", null) ?? node.GetAttributeValue("data-src", null);
                if (string.IsNullOrWhiteSpace(path) || !Uri.TryCreate(baseUri, path, out var uri))
                    throw new FormatException("Remanga не вернула корректный адрес страницы комикса.");
                var image = await SaveRequiredImage(uri)
                    ?? throw new FormatException("Remanga вернула пустую страницу комикса.");
                images.Add(image);
                node.Attributes.RemoveAll();
                node.Attributes.Add("src", image.FullName);
            }
            return images;
        }
        catch
        {
            foreach (var image in images)
            {
                try { image.Dispose(); }
                catch (Exception error)
                {
                    Config.Logger.LogWarning("Не удалось удалить временную страницу комикса: {Type}", error.GetType().Name);
                }
            }
            throw;
        }
    }

    protected override HtmlDocument GetChapterAsHtml(RenovelsChapter response)
    {
        var sb = new StringBuilder();

        foreach (var obj in response.Pages)
        {
            switch (obj)
            {
                case JsonObject:
                    sb.Append($"<img src='{obj.Deserialize<RenovelsPage>().Link}'/>");
                    break;
                case JsonArray pages:
                {
                    foreach (var page in pages)
                    {
                        sb.Append($"<img src='{page.Deserialize<RenovelsPage>().Link}'/>");
                    }

                    break;
                }
            }
        }

        return sb.AsHtmlDoc();
    }
}
