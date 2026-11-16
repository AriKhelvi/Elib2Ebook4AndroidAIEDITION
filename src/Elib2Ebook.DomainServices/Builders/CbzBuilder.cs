using System.IO.Compression;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Configs;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Builders;

public class CbzBuilder(Options options, ILogger logger) : BuilderBase(options, logger)
{
    protected override string Extension => "cbz";

    protected override async Task BuildInternal(Book book, string fileName)
    {
        long pages = 0;
        long bytes = 0;
        foreach (var chapter in book.Chapters)
        {
            foreach (var image in chapter.Images ?? [])
            {
                pages++;
                // TempFile.Size читает всю картинку в память; для оценки достаточно метаданных файла.
                var info = new FileInfo(image.FilePath);
                if (info.Exists) bytes += info.Length;
            }
        }
        if (pages >= 500 || bytes >= 250L * 1024 * 1024)
        {
            // Информационный статус, а не ошибка полноты книги: успешное скачивание остаётся успешным.
            Logger.LogInformation("Собираю большой архив CBZ: {Pages} страниц, {Megabytes:F0} МБ. " +
                "Это может занять несколько минут или дольше. Дождитесь завершения, не запускайте скачивание заново.",
                pages, bytes / (1024d * 1024));
        }

        if (File.Exists(fileName))
        {
            File.Delete(fileName);
        }

        using var archive = ZipFile.Open(fileName, ZipArchiveMode.Create);

        var c = 0;
        foreach (var chapter in book.Chapters)
        {
            if (chapter.Images == null)
            {
                continue;
            }

            foreach (var image in chapter.Images)
            {
                var entry = archive.CreateEntry($"{++c}{image.Extension}", CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var fileStream = image.GetStream();
                await fileStream.CopyToAsync(entryStream);
            }
        }
    }
}
