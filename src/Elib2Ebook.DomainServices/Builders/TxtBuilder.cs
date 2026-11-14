using Elib2Ebook.Domain.Book;
using System.Net;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Builders;

public class TxtBuilder(Options options, ILogger logger) : BuilderBase(options, logger)
{
    protected override string Extension => "txt";

    protected override async Task BuildInternal(Book book, string fileName)
    {
        if (File.Exists(fileName))
        {
            File.Delete(fileName);
        }

        await using var file = File.CreateText(fileName);

        foreach (var chapter in book.Chapters.Where(c => c.IsValid))
        {
            await file.WriteLineAsync("   " + chapter.Title);
            await file.WriteLineAsync();

            // Разбираем HTML без предварительного декодирования: &lt;герой&gt; — текст, а не тег.
            // Декодируем сущности после разбора ровно один раз, без расширения HtmlDecode/GetText:
            // оно удаляет &lt;/&gt; и повторяет декодирование до неизменности строки.
            var document = new HtmlDocument();
            document.LoadHtml(chapter.Content.PrettyHtml());
            var prettyText = WebUtility.HtmlDecode(document.DocumentNode.InnerText);
            using var sr = new StringReader(prettyText);

            while (true)
            {
                var line = await sr.ReadLineAsync();
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                await file.WriteLineAsync("   " + line.Trim());
            }

            await file.WriteLineAsync();
        }
    }
}
