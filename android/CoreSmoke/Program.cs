// ============================================================================
// E2E для Android — десктопная проверка конвейера скачивания и списка сайтов
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

// Использование: CoreSmoke <url> <formats через запятую> [--start N] [--end N] [--no-image]
//   [--expected-chapters N] [--limit-minutes N] [--delay-seconds N] [--diagnostics] [--verbose]
// Качает книгу через BookPipeline/GetterResolver из приложения и проверяет получившиеся файлы.
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using E2E.Droid.Services;
using Elib2Ebook.DomainServices.Configs;

string work = null;
try
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    Console.OutputEncoding = Encoding.UTF8;

    // Проверка сертификатов — как в приложении (MauiProgram): ядро само принимает любой
    BookGetterConfig.ServerCertificateValidator = CertificatePolicy.Validate;
    var roots = CertificatePolicy.Roots;
    Check(roots.Count >= 20, $"встроенных корней: {roots.Count}");
    Check(roots.All(r => r.NotAfter > DateTime.Now.AddYears(1)), "встроенные корни действуют ещё минимум год");
    var bridges = CertificatePolicy.Bridges;
    Check(bridges.Count >= 3 && bridges.All(b => b.NotAfter > DateTime.Now.AddYears(1)), $"мостовых сертификатов Let's Encrypt: {bridges.Count}, действуют ещё минимум год");

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

    // Только сверка каталога с настоящими загрузчиками: никаких HTTP-запросов и входа.
    var catalog = SiteDiagnosticCatalog.Entries;
    Check(catalog.Select(test => test.SiteHost).Distinct().Count() >= sites.Select(site => SupportedSites.DisplayName(site.Url)).Distinct().Count(),
        "диагностический каталог охватывает все сайты реестра");
    Check(catalog.Select(test => test.Id).Distinct().Count() == catalog.Count, "стабильные идентификаторы диагностических примеров уникальны");
    Check(catalog.All(test => test.CanRun || !string.IsNullOrWhiteSpace(test.SkipReason)), "каждый пропуск диагностики имеет причину");
    foreach (var test in catalog.Where(test => test.CanRun))
        Check(GetterResolver.IsSupported(new Uri(test.BookUrl)), $"диагностический пример распознаётся загрузчиком: {test.Id}");
    Console.WriteLine($"Диагностический каталог {SiteDiagnosticCatalog.Version}: {catalog.Count} случаев, " +
        $"{catalog.Count(test => test.CanRun)} для запуска, {catalog.Count(test => !test.CanRun)} с причиной пропуска.");

    if (args.Length == 0)
    {
        return;
    }

    if (args.Length < 2 || !Uri.TryCreate(args[0], UriKind.Absolute, out var inputUrl) || inputUrl.Scheme is not ("http" or "https"))
        throw new ArgumentException("Укажите HTTP(S)-адрес книги и форматы через запятую.");
    var formats = args[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (formats.Length == 0) throw new ArgumentException("Нужен хотя бы один формат.");
    int? start = null, end = null, expectedChapters = null;
    double limitMinutes = 3;
    double? delaySeconds = null;
    var noImage = false;
    var verbose = false;
    var diagnostics = false;
    for (var i = 2; i < args.Length; i++)
    {
        var option = args[i];
        switch (option)
        {
            case "--no-image": noImage = true; break;
            case "--verbose": verbose = true; break;
            case "--diagnostics": diagnostics = true; break;
            case "--start": start = ReadInteger(args, ref i, option); break;
            case "--end": end = ReadInteger(args, ref i, option); break;
            case "--expected-chapters": expectedChapters = ReadInteger(args, ref i, option); break;
            case "--limit-minutes": limitMinutes = ReadNumber(args, ref i, option); break;
            case "--delay-seconds": delaySeconds = ReadNumber(args, ref i, option); break;
            default: throw new ArgumentException("Неизвестный параметр CoreSmoke.");
        }
    }
    if (start == 0 || end == 0 || (start is > 0 && end is > 0 && start > end))
        throw new ArgumentException("Номер главы не может быть нулём; положительный --start не должен превышать --end.");
    if (expectedChapters is <= 0)
        throw new ArgumentException("--expected-chapters должен быть положительным целым числом.");
    // Эти форматы позволяют проверить число глав непосредственно в готовом файле.
    if (expectedChapters.HasValue && formats.Any(format => !format.Equals("fb2", StringComparison.OrdinalIgnoreCase) && !format.Equals("epub", StringComparison.OrdinalIgnoreCase)))
        throw new ArgumentException("--expected-chapters поддерживает проверку готовых файлов FB2 и EPUB.");
    if (limitMinutes <= 0 || limitMinutes > (uint.MaxValue - 1d) / 60000)
        throw new ArgumentException("--limit-minutes должен задавать положительный лимит в пределах таймера отмены.");
    if (delaySeconds.HasValue && (delaySeconds < 0 || delaySeconds > 120 || delaySeconds is > 0 and < 0.01))
        throw new ArgumentException("--delay-seconds: 0 без паузы либо значение от 0.01 до 120 секунд.");

    work = Path.Combine(Path.GetTempPath(), "e2e-smoke-" + Guid.NewGuid().ToString("N")[..8]);
    var options = new Options
    {
        Url = [args[0]],
        Format = formats,
        End = end,
        Start = start,
        NoImage = noImage,
        DelayMode = delaySeconds.HasValue ? (delaySeconds == 0 ? 1 : 4) : null,
        CustomDelaySeconds = delaySeconds ?? 1,
        Timeout = 60,
        SavePath = Path.Combine(work, "out"),
        TempPath = Path.Combine(work, "tmp")
    };

    BookPreview preview = null;
    var lines = 0;
    var logger = new CallbackLogger(line =>
    {
        lines++;
        if (lines <= 8 || verbose || line.Contains("сохран", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  | " + line);
        }
    }, debug: diagnostics ? line =>
    {
        lines++;
        Console.WriteLine("  | DEBUG " + line);
    } : null);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var probeLimit = new CancellationTokenSource(TimeSpan.FromMinutes(limitMinutes));
    var result = await BookPipeline.RunAsync(options, logger, p => preview = p, probeLimit.Token);
    Console.WriteLine($"Готово за {sw.Elapsed.TotalSeconds:0.0} с, строк лога: {lines}");
    Console.WriteLine($"Превью: «{preview?.Title}» / {preview?.Author}, обложка: {(preview?.Cover == null ? "нет" : $"{preview.Cover.Length} байт, {preview.CoverMime}")}");
    Console.WriteLine($"Книга: «{result.Title}» / {result.Author}");
    var bookInfo = result.Info;
    if (expectedChapters.HasValue)
        Check(bookInfo.Chapters == expectedChapters.Value, $"получено глав {bookInfo.Chapters}, ожидается {expectedChapters.Value}");
    Console.WriteLine($"Для «Книг»: глав {bookInfo.Chapters}, последняя «{bookInfo.LastChapter}», серия «{bookInfo.SeriaName}» #{bookInfo.SeriaNumber} ({bookInfo.SeriaUrl}), " +
                      $"соавторы: {string.Join(", ", bookInfo.CoAuthors)}, аннотация: {bookInfo.Annotation?.Length ?? 0} симв., обложка: {bookInfo.Cover?.Length ?? 0} байт");

    foreach (var file in result.Files)
    {
        var info = new FileInfo(file);
        Console.WriteLine($"  {Path.GetRelativePath(result.OutputDir, file)} — {info.Length:N0} байт");
        IReadOnlyList<DiagnosticCheck> checks = info.Extension.ToLowerInvariant() switch
        {
            ".fb2" or ".epub" => DiagnosticBookChecks.CheckFile(file, expectedChapters),
            // Та же проверка страниц, что в Android «Проверить все сайты».
            ".cbz" => SiteDiagnosticBatch.CheckOutput(new SiteDiagnosticCase(
                "core-smoke", inputUrl.Host, inputUrl.AbsoluteUri, "Выбранный диапазон", null, Images: true),
                bookInfo.Chapters, [file], probeLimit.Token),
            _ => Array.Empty<DiagnosticCheck>()
        };
        foreach (var check in checks)
        {
            if (check.Warning) Console.WriteLine($"  [warn] {check.Message}");
            else Check(check.Passed, check.Message);
        }
        switch (info.Extension)
        {
            case ".cbz":
                using (var zip = ZipFile.OpenRead(file))
                {
                    // Читаем каждую запись целиком: одного корректного заголовка картинки недостаточно.
                    foreach (var entry in zip.Entries)
                    {
                        await using var stream = entry.Open();
                        await stream.CopyToAsync(Stream.Null, probeLimit.Token);
                    }
                    Check(zip.Entries.Count > 0, $"CBZ: полностью прочитано записей {zip.Entries.Count}");
                }
                break;
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
    Console.WriteLine("OK");
}
catch (Exception ex)
{
    // Консольная проба сообщает сбой в stderr, не оставляя необработанное исключение диалогу Windows.
    foreach (var line in Elib2Ebook.DomainServices.Misc.LogText.CleanLines(ex.ToString().Split('\n')))
        Console.Error.WriteLine(line);
    Environment.ExitCode = 1;
}
finally
{
    if (work != null && Directory.Exists(work))
    {
        try { Directory.Delete(work, true); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось удалить временную папку проверки ({ex.GetType().Name}).");
            Environment.ExitCode = 1;
        }
    }
}

static string ReadValue(string[] arguments, ref int index, string option)
{
    if (++index >= arguments.Length) throw new ArgumentException($"Для {option} нужно значение.");
    return arguments[index];
}

static int ReadInteger(string[] arguments, ref int index, string option)
{
    if (!int.TryParse(ReadValue(arguments, ref index, option), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        throw new ArgumentException($"Для {option} нужно целое число.");
    return value;
}

static double ReadNumber(string[] arguments, ref int index, string option)
{
    if (!double.TryParse(ReadValue(arguments, ref index, option), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        throw new ArgumentException($"Для {option} нужно конечное число с точкой в качестве десятичного разделителя.");
    return value;
}

static void Check(bool condition, string what)
{
    Console.WriteLine($"  [{(condition ? "ok" : "FAIL")}] {what}");
    if (!condition)
    {
        throw new InvalidOperationException(what);
    }
}
