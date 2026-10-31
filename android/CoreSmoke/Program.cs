// ============================================================================
// E2E для Android — десктопная проверка конвейера скачивания и списка сайтов
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

// Использование: CoreSmoke <url> <formats через запятую> [--end N] [--no-image]
// Качает книгу через BookPipeline/GetterResolver из приложения и проверяет получившиеся файлы.
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using E2E.Droid.Services;
using Elib2Ebook.DomainServices.Configs;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.OutputEncoding = Encoding.UTF8;

// Самопроверка разбора ссылок, которые прилетают через «Поделиться» и из поля ввода
Check(SharedLinkInbox.ExtractUrls("Мастер Ветров — Author.Today\nhttps://author.today/work/212721").SequenceEqual(["https://author.today/work/212721"]), "ExtractUrls: заголовок + ссылка");
Check(Formatting.ParseUrls("author.today/work/1\nhttps://litnet.com/ru/book/x-b1, мусор").Count == 2, "ParseUrls: без схемы и с запятой");

var dlls = Directory.GetFiles(AppContext.BaseDirectory, "Elib2Ebook.ExternalServices.*.dll").Select(Path.GetFileNameWithoutExtension).ToList();
GetterResolver.Configure(dlls);
Console.WriteLine($"Сборок сайтов: {dlls.Count}, геттеров: {GetterResolver.GetterTypes.Count}");
Check(GetterResolver.GetterTypes.Count >= dlls.Count, "каждая сборка сайта даёт хотя бы один геттер");

// Список сайтов без карточки до скачивания задан именами классов — сверяем, что они есть в ядре
var getterNames = GetterResolver.GetterTypes.Select(t => t.FullName).ToHashSet();
foreach (var name in BookPipeline.NoPreviewGetters)
{
    Check(getterNames.Contains(name), $"без превью: {name}");
}

// Вкладка «Сайты»: README с GitHub, сверенный с доменами геттеров
var sites = SupportedSites.Sites;
var inReadme = sites.Count(s => s.InReadme);
var working = sites.Count(s => s.HasGetter);
Check(inReadme > 50, $"README: {inReadme} сайтов");
Check(working >= GetterResolver.GetterTypes.Count - 10, $"доменов у геттеров: {working}");
Console.WriteLine($"  в README, но без геттера: {string.Join(", ", sites.Where(s => !s.HasGetter).Select(s => s.Name))}");
Console.WriteLine($"  есть геттер, но нет в README: {string.Join(", ", sites.Where(s => !s.InReadme).Select(s => s.Name))}");

if (args.Length < 2)
{
    return;
}

var end = args.SkipWhile(a => a != "--end").Skip(1).Select(int.Parse).Cast<int?>().FirstOrDefault();
var work = Path.Combine(Path.GetTempPath(), "e2e-smoke-" + Guid.NewGuid().ToString("N")[..8]);
var options = new Options
{
    Url = [args[0]],
    Format = args[1].Split(','),
    End = end,
    NoImage = args.Contains("--no-image"),
    Timeout = 60,
    SavePath = Path.Combine(work, "out"),
    TempPath = Path.Combine(work, "tmp")
};

BookPreview preview = null;
var lines = 0;
var logger = new CallbackLogger(line =>
{
    lines++;
    if (lines <= 8 || args.Contains("--verbose") || line.Contains("сохран", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("  | " + line);
    }
});

var sw = System.Diagnostics.Stopwatch.StartNew();
var result = await BookPipeline.RunAsync(options, logger, p => preview = p, CancellationToken.None);
Console.WriteLine($"Готово за {sw.Elapsed.TotalSeconds:0.0} с, строк лога: {lines}");
Console.WriteLine($"Превью: «{preview?.Title}» / {preview?.Author}, обложка: {(preview?.Cover == null ? "нет" : $"{preview.Cover.Length} байт, {preview.CoverMime}")}");
Console.WriteLine($"Книга: «{result.Title}» / {result.Author}");
var bookInfo = result.Info;
Console.WriteLine($"Для «Книг»: глав {bookInfo.Chapters}, последняя «{bookInfo.LastChapter}», серия «{bookInfo.SeriaName}» #{bookInfo.SeriaNumber} ({bookInfo.SeriaUrl}), " +
                  $"соавторы: {string.Join(", ", bookInfo.CoAuthors)}, аннотация: {bookInfo.Annotation?.Length ?? 0} симв., обложка: {bookInfo.Cover?.Length ?? 0} байт");

foreach (var file in result.Files)
{
    var info = new FileInfo(file);
    Console.WriteLine($"  {Path.GetRelativePath(result.OutputDir, file)} — {info.Length:N0} байт");
    switch (info.Extension)
    {
        case ".epub":
            using (var zip = ZipFile.OpenRead(file))
            {
                var mimetype = zip.GetEntry("mimetype");
                using var reader = new StreamReader(mimetype!.Open());
                Check(reader.ReadToEnd() == "application/epub+zip", "EPUB: mimetype");
                Console.WriteLine("    " + string.Join(", ", zip.Entries.Select(e => e.FullName)));
                Check(zip.Entries.Count(e => e.FullName.EndsWith("html", StringComparison.OrdinalIgnoreCase)) > 0, $"EPUB: главы ({zip.Entries.Count} записей)");
            }
            break;
        case ".fb2":
            var doc = XDocument.Load(file);
            XNamespace ns = "http://www.gribuser.ru/xml/fictionbook/2.0";
            Check(doc.Root?.Name == ns + "FictionBook", "FB2: корень FictionBook");
            Check(doc.Descendants(ns + "section").Any(), $"FB2: секции ({doc.Descendants(ns + "section").Count()})");
            break;
    }
}

Check(result.Files.Count >= options.Format.Count(), "по файлу на каждый формат");
Directory.Delete(work, true);
Console.WriteLine("OK");

static void Check(bool condition, string what)
{
    Console.WriteLine($"  [{(condition ? "ok" : "FAIL")}] {what}");
    if (!condition)
    {
        Environment.Exit(1);
    }
}
