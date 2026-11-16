using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Extensions;
using StringTokenFormatter;

namespace Elib2Ebook.DomainServices;

public class BookNameBuilder
{
    public static string Build(string pattern, Book book)
    {
        var resolver = new InterpolatedStringResolver(StringTokenFormatterSettings.Default);
        var combinedContainer = resolver
            .Builder()
            .AddPrefixedObject("Book", book)
            .AddPrefixedObject("Author", book.Author)
            .AddPrefixedSingle("Seria", "HasSeria", book.Seria is not null)
            // Серия необязательна: общий шаблон должен работать и для книг без цикла.
            .AddPrefixedSingle("Seria", "Name", book.Seria?.Name ?? string.Empty)
            .AddPrefixedSingle("Seria", "Number", book.Seria?.Number ?? string.Empty)
            .AddPrefixedSingle("Seria", "Url", (object)book.Seria?.Url ?? string.Empty);

        var name = resolver.FromContainer(pattern, combinedContainer.CombinedResult()).RemoveInvalidChars();
        if (string.IsNullOrWhiteSpace(name))
        {
            // Например, шаблон только из {Seria.Number} не должен дать файл с одним расширением.
            name = book.Title?.RemoveInvalidChars();
        }

        return string.IsNullOrWhiteSpace(name) ? "Книга" : name;
    }
}
