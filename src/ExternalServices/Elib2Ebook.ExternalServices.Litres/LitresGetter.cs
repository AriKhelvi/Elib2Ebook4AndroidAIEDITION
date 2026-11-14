using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.Domain.Common;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Getters;
using Elib2Ebook.DomainServices.Misc;
using Elib2Ebook.ExternalServices.Litres.Types;
using Elib2Ebook.ExternalServices.Litres.Types.Requests;
using Elib2Ebook.ExternalServices.Litres.Types.Response;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.ExternalServices.Litres;

public class LitresGetter(BookGetterConfig config) : GetterBase(config)
{
    private const string SECRET_KEY = "AsAAfdV000-1kksn6591x:[}A{}<><DO#Brn`BnB6E`^s\"ivP:RY'4|v\"h/r^]";
    private const string APP = "13";

    protected override Uri SystemUrl => new("https://www.litres.ru");

    private static Uri GetShortUri(LitresArt art)
        => new($"https://catalit.litres.ru/pub/t/{art.Id}.fb3");

    private static long GetCurrentMilli()
    {
        var jan1970 = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var javaSpan = DateTime.UtcNow - jan1970;
        return (long)javaSpan.TotalMilliseconds / 1000;
    }

    public override async Task Init()
    {
        await base.Init();
        Config.Client.DefaultRequestHeaders.Add("app-id", "1");
    }

    private Uri GetFullUri(LitresArt art, string path, LitresFile file, string host = null)
    {
        var ts = GetCurrentMilli();

        var inputBytes = Encoding.ASCII.GetBytes($"{ts}:{art.Id}:{SECRET_KEY}");
        var hashBytes = MD5.HashData(inputBytes);

        // Текстовая книга
        if (art.ArtType != LitresArtTypeEnum.Audio)
        {
            var result = new Uri(
                $"https://{host ?? "catalit.litres.ru"}/pages/{path}?art={art.Id}&sid={_authData.Sid}&uilang=ru&libapp={APP}&timestamp={ts}&md5={Convert.ToHexString(hashBytes).ToLower()}");

            if (file == null)
            {
                return result.AppendQueryParameter("type", "fb3");
            }

            result = result.AppendQueryParameter("file", file.Id);
            if (!string.IsNullOrWhiteSpace(file.Extension))
            {
                result = result.AppendQueryParameter("type", file.Extension);
            }

            return result;
        }

        // Аудиокнига
        return new Uri(
            $"https://{host ?? "ios.litres.ru"}/pages/{path}/{art.Id}/{file.Id}.mp3?sid={_authData.Sid}&uilang=ru&libapp={APP}&timestamp={ts}&md5={Convert.ToHexString(hashBytes).ToLower()}");
    }

    private LitresAuthResponseData _authData = new()
    {
        Sid = "6ufp4b2wbx1acc4k1f0wdo571762c0fn"
    };

    private LitresMe _me;

    public override async Task Authorize()
    {
        Config.CancellationToken.ThrowIfCancellationRequested();
        Config.Client.DefaultRequestHeaders.Remove("Session-Id");
        _me = null;
        if (!Config.HasCredentials)
        {
            Config.Client.DefaultRequestHeaders.Add("Session-Id", _authData.Sid);
            return;
        }

        // Кэш уменьшает число входов, но отсутствие прав на диск не должно мешать скачиванию.
        // На Android рабочая директория может быть корнем файловой системы, доступным только для чтения.
        _authData = null;
        var cacheFile = GetAuthCacheFile();
        try
        {
            var cached = await ReadAuthCache(cacheFile);
            if (IsValidSession(cached))
            {
                SetSession(cached.Sid);
                _me = await ReadCurrentUser(allowExpiredSession: true);
                if (_me != null)
                {
                    _authData = cached;
                    Config.Logger.LogDebug("Литрес: сохранённая сессия подтверждена.");
                    return;
                }
                Config.Logger.LogDebug("Литрес: сохранённая сессия больше не подтверждается, выполняю новый вход.");
            }

            // Старый SID нельзя отправлять вместе с новым логином и паролем.
            Config.Client.DefaultRequestHeaders.Remove("Session-Id");
            Config.Logger.LogDebug("Литрес: выполняю вход по сохранённым учётным данным.");
            using var content = JsonContent.Create(new LitresAuthData
            {
                Login = Config.Options.Login,
                Password = Config.Options.Password,
            });
            using var response = await Config.Client.PostAsync(
                "https://api.litres.ru/foundation/api/auth/login", content, Config.CancellationToken);
            Config.Logger.LogDebug("Литрес: ответ входа HTTP {Status}.", (int)response.StatusCode);
            response.EnsureDownloadSuccess();
            var auth = await ReadAuthResponse<LitresAuthResponseData>(response);
            if (!string.IsNullOrWhiteSpace(auth?.Error) || !IsValidSession(auth?.Payload?.Data))
                throw new InvalidOperationException("Литрес не подтвердил вход. Проверьте логин и пароль.");

            SetSession(auth.Payload.Data.Sid);
            _me = await ReadCurrentUser(allowExpiredSession: false);
            _authData = auth.Payload.Data;
            Config.Logger.LogDebug("Литрес: новый вход и профиль подтверждены.");
            await WriteAuthCache(cacheFile, _authData);
        }
        finally
        {
            if (_authData == null)
                Config.Client.DefaultRequestHeaders.Remove("Session-Id");
        }
    }

    private static bool IsValidSession(LitresAuthResponseData auth)
        => !string.IsNullOrWhiteSpace(auth?.Sid) && !auth.Sid.Any(char.IsControl);

    private void SetSession(string sid)
    {
        Config.Client.DefaultRequestHeaders.Remove("Session-Id");
        Config.Client.DefaultRequestHeaders.Add("Session-Id", sid);
    }

    private async Task<LitresMe> ReadCurrentUser(bool allowExpiredSession)
    {
        using var response = await Config.Client.GetAsync(
            "https://api.litres.ru/foundation/api/users/me/detailed", Config.CancellationToken);
        Config.Logger.LogDebug("Литрес: проверка профиля HTTP {Status}, сохранённая сессия: {Cached}.",
            (int)response.StatusCode, allowExpiredSession);
        if (allowExpiredSession && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return null;

        // 429, 5xx и сетевые ошибки не означают, что SID истёк. Не делаем лишний вход.
        response.EnsureDownloadSuccess();
        var me = await ReadAuthResponse<LitresMe>(response);
        if (string.IsNullOrWhiteSpace(me?.Error) && me?.Payload?.Data != null)
            return me.Payload.Data;
        if (allowExpiredSession)
            return null;
        throw new InvalidOperationException("Литрес не подтвердил сессию после входа. Попробуйте позже.");
    }

    private async Task<LitresStaticResponse<T>> ReadAuthResponse<T>(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<LitresStaticResponse<T>>(cancellationToken: Config.CancellationToken);
        }
        catch (JsonException)
        {
            // Тело ответа и исходное исключение могут содержать пароль или SID.
            throw new InvalidOperationException("Литрес вернул некорректный ответ при проверке входа. Попробуйте позже.");
        }
    }

    private string GetAuthCacheFile()
    {
        try
        {
            var directory = Config.Options.AuthCachePath;
            if (directory == null)
            {
                var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localData) || !Path.IsPathFullyQualified(localData))
                    return null;
                directory = Path.Combine(localData, "Elib2Ebook", "auth-cache");
            }
            if (string.IsNullOrWhiteSpace(directory))
                return null;

            // Логин не попадает в имя файла и не может превратиться в путь за пределами кэша.
            var account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Config.Options.Login))).ToLowerInvariant();
            return Path.Combine(Path.GetFullPath(directory), "litres-" + account + ".json");
        }
        catch (Exception error) when (IsCacheError(error))
        {
            LogCacheUnavailable();
            return null;
        }
    }

    private async Task<LitresAuthResponseData> ReadAuthCache(string path)
    {
        if (path == null) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, Config.CancellationToken);
            var cached = JsonSerializer.Deserialize<LitresAuthResponseData>(json);
            Config.Logger.LogDebug("Литрес: кэш прочитан, непустая сессия: {Valid}.", IsValidSession(cached));
            return cached;
        }
        catch (FileNotFoundException) { Config.Logger.LogDebug("Литрес: сохранённой сессии пока нет."); return null; }
        catch (DirectoryNotFoundException) { Config.Logger.LogDebug("Литрес: папки сохранённых сессий пока нет."); return null; }
        catch (JsonException) { Config.Logger.LogDebug("Литрес: повреждённый кэш сессии будет заменён после входа."); return null; }
        catch (Exception error) when (IsCacheError(error))
        {
            LogCacheUnavailable();
            return null;
        }
    }

    private async Task WriteAuthCache(string path, LitresAuthResponseData auth)
    {
        if (path == null) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(auth), Config.CancellationToken);
            Config.CancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            Config.Logger.LogDebug("Литрес: проверенная сессия сохранена для следующих запусков.");
        }
        catch (Exception error) when (IsCacheError(error))
        {
            LogCacheUnavailable();
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (IsCacheError(error)) { }
        }
    }

    private static bool IsCacheError(Exception error)
        => error is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or NotSupportedException;

    private void LogCacheUnavailable()
        => Config.Logger.LogWarning("Литрес: кэш сессии недоступен. Вход работает без сохранения сессии.");

    private async Task<T> GetResponse<T>(Uri url)
    {
        var resp = await Config.Client.GetWithTriesAsync(url);
        if (resp == null)
        {
            return default;
        }

        return await resp.Content.ReadAsStringAsync().ContinueWith(t => t.Result.Deserialize<LitresStaticResponse<T>>().Payload.Data);
    }

    public override async Task<Book> Get(Uri url)
    {
        var bookId = GetBookId(url);

        var art = await GetResponse<LitresArt>($"https://api.litres.ru/foundation/api/arts/{bookId}".AsUri());

        var book = new Book(SystemUrl.MakeRelativeUri(bookId))
        {
            Cover = await GetCover(art.Cover),
            Title = art.Title,
            Author = await GetAuthor(art),
            CoAuthors = await GetCoAuthors(art),
            Annotation = art.Annotation,
            Seria = GetSeria(art)
        };

        await FillAdditional(book, art);
        foreach (var linked in art.LinkedArts ?? [])
        {
            await FillAdditional(book, linked);
        }

        var fb3File = book.AdditionalFiles.Get(AdditionalTypeEnum.Books).FirstOrDefault(f => f.Extension == ".fb3");

        if (fb3File == null)
        {
            Config.Logger.LogInformation("Нет файла fb3. Сформировать файл книги невозможно");
        }
        else
        {
            try
            {
                await using var stream = fb3File.GetStream();
                var litresBook = await GetBook(stream);
                book.Chapters = await FillChapters(litresBook, art.Title);
            }
            catch (Exception)
            {
                Config.Logger.LogInformation($"Не удалось обработать оригинальный файл {fb3File.FullName}");
            }
        }

        return book;
    }

    private async Task FillAdditional(Book book, LitresArt art)
    {
        if (_authData != null)
        {
            var type = art.ArtType == LitresArtTypeEnum.Audio ? AdditionalTypeEnum.Audio : AdditionalTypeEnum.Books;

            if (Config.Options.HasAdditionalType(type))
            {
                var files = await GetResponse<LitresFiles[]>($"https://api.litres.ru/foundation/api/arts/{art.Id}/files/grouped".AsUri());

                foreach (var file in files.SelectMany(f => f.Files))
                {
                    using var fileResponse = await GetFileResponse(art, file);
                    if (fileResponse != null)
                    {
                        var tempFile = await CreateTempFile(fileResponse);
                        book.AdditionalFiles.Add(art.ArtType != LitresArtTypeEnum.Audio ? AdditionalTypeEnum.Books : AdditionalTypeEnum.Audio, tempFile);
                    }
                }
            }
            else if (art.ArtType != LitresArtTypeEnum.Audio)
            {
                using var fileResponse = await GetFileResponse(art, null);
                if (fileResponse != null)
                {
                    var tempFile = await CreateTempFile(fileResponse);
                    book.AdditionalFiles.Add(AdditionalTypeEnum.Books, tempFile);
                }
            }
        }

        if (_authData == null || (book.AdditionalFiles.Get(AdditionalTypeEnum.Books).Count == 0 && art.ArtType != LitresArtTypeEnum.Audio))
        {
            var fileResponse = await GetShortBook(art);
            if (fileResponse != null)
            {
                book.AdditionalFiles.Add(AdditionalTypeEnum.Books, await CreateTempFile(fileResponse));
            }
        }
    }

    private async Task<TempFile> CreateTempFile(HttpResponseMessage response)
    {
        return await TempFile.Create(
            response.RequestMessage.RequestUri,
            Config.TempFolder.Path,
            response.Content.Headers.ContentDisposition?.FileName?.Trim('\"') ?? response.RequestMessage.RequestUri.GetFileName(),
            await response.Content.ReadAsStreamAsync());
    }

    private static string GetBookId(Uri url)
    {
        var art = url.GetQueryParameter("art");
        if (!string.IsNullOrWhiteSpace(art))
        {
            return art;
        }

        art = url.GetSegment(url.Segments.Length - 1).Split("-").Last();
        if (long.TryParse(art, out _))
        {
            return art;
        }

        throw new Exception("Не удалось определить artId");
    }

    private Seria GetSeria(LitresArt art)
    {
        return art
            .Sequences
            .Select(s => new Seria
            {
                Name = s.Name, Url = SystemUrl.MakeRelativeUri("serii-knig/").AppendQueryParameter("id", s.Id), Number = s.SequenceNumber
            }).FirstOrDefault();
    }

    private async Task<Author> GetAuthor(LitresArt art)
    {
        var person = art.Persons.FirstOrDefault(a => a.Role == "author");
        var author = person == null ? null : await GetResponse<LitresPerson<long>>($"https://api.litres.ru/foundation/api/persons/{person.Id}".AsUri());
        return author == null ?
            new Author("Litres") :
            new Author(author.FullName, SystemUrl.MakeRelativeUri(author.Url));
    }

    private async Task<IEnumerable<Author>> GetCoAuthors(LitresArt art)
    {
        var result = new List<Author>();
        foreach (var person in art.Persons.Where(a => a.Role == "author").Skip(1))
        {
            var author = person == null ? null : await GetResponse<LitresPerson<long>>($"https://api.litres.ru/foundation/api/persons/{person.Id}".AsUri());
            if (author != null)
            {
                result.Add(new Author(author.FullName, SystemUrl.MakeRelativeUri(author.Url)));
            }
        }

        return result;
    }

    private Task<TempFile> GetCover(string imagePath)
    {
        return !string.IsNullOrWhiteSpace(imagePath) ? SaveImage(SystemUrl.MakeRelativeUri(imagePath)) : Task.FromResult(default(TempFile));
    }

    private async Task<IEnumerable<Chapter>> FillChapters(LitresBook book, string title)
    {
        var result = new List<Chapter>();
        if (Config.Options.NoChapters)
        {
            return result;
        }

        foreach (var section in book.Content.QuerySelectorAll("section"))
        {
            if (section.QuerySelector("> section") != null)
            {
                continue;
            }

            var chapter = new Chapter
            {
                Title = (section.GetTextBySelector("title") ?? title).ReplaceNewLine()
            };

            section.RemoveNodes("title, note, clipped");
            chapter.Images = await GetImages(section, book);
            chapter.Content = section.InnerHtml;
            result.Add(chapter);
        }

        return result;
    }

    private async Task<IEnumerable<TempFile>> GetImages(HtmlNode doc, LitresBook book)
    {
        var images = new List<TempFile>();
        foreach (var img in doc.QuerySelectorAll("img"))
        {
            var path = img.Attributes["src"]?.Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                img.Remove();
                continue;
            }

            if (!book.Targets.TryGetValue(path, out var t))
            {
                img.Remove();
                continue;
            }

            if (t?.Content == null || t.Content.Length == 0)
            {
                img.Remove();
                continue;
            }

            var fileName = t.Target.Split("/").Last().Trim('/');
            var image = await TempFile.Create(null, Config.TempFolder.Path, fileName, t.Content);
            img.Attributes["src"].Value = image.FullName;
            images.Add(image);
        }

        return images;
    }

    private async Task<HttpResponseMessage> GetFileResponse(LitresArt art, LitresFile file)
    {
        var paths = new[]
        {
            "download_book_j", "download_book_subscr", "download_my_book_j"
        };

        var hosts = new List<string>
        {
            null, SystemUrl.Host
        };

        if (_me?.PartnerSubscriptions?.Subscriptions != null)
        {
            hosts.AddRange(
                _me.PartnerSubscriptions.Subscriptions.Where(s => s.IsActive && s.Type == (art.ArtType == LitresArtTypeEnum.Audio ? "audio" : "text")).Select(s => s.Host).Distinct());
        }

        if (_authData != null)
        {
            foreach (var host in hosts)
            {
                foreach (var path in paths)
                {
                    var uri = GetFullUri(art, path, file, host);
                    var response = await Config.Client.GetAsync(uri);
                    if (response.StatusCode == HttpStatusCode.OK && response.Headers.AcceptRanges.Count != 0)
                    {
                        Config.Logger.LogInformation(LogText.Clean($"Дополнительный файл доступен по ссылке {uri}"));
                        return response;
                    }
                }
            }
        }

        return null;
    }

    private async Task<HttpResponseMessage> GetShortBook(LitresArt art)
    {
        var shortUri = GetShortUri(art);
        Config.Logger.LogInformation(LogText.Clean($"Дополнительный файл доступен по ссылке {shortUri}"));
        return await Config.Client.GetAsync(shortUri);
    }

    private async Task<LitresBook> GetBook(Stream stream)
    {
        var result = new LitresBook();

        var map = new Dictionary<string, byte[]>();
        using var zip = new ZipArchive(stream);

        foreach (var entry in zip.Entries)
        {
            using var ms = new MemoryStream();
            await entry.Open().CopyToAsync(ms);
            map[entry.FullName.Replace("fb3/", string.Empty)] = ms.ToArray();
        }

        foreach (var (key, value) in map)
        {
            if (key.EndsWith("body.xml"))
            {
                result.Content = Encoding.UTF8.GetString(value).AsXHtmlDoc();
            }
            else if (key.EndsWith("body.xml.rels"))
            {
                foreach (var r in Encoding.UTF8.GetString(value).AsXHtmlDoc().QuerySelectorAll("Relationship"))
                {
                    var id = r.Attributes["Id"]?.Value;
                    var target = r.Attributes["Target"]?.Value;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(target))
                    {
                        continue;
                    }

                    if (!map.TryGetValue(target, out var t))
                    {
                        continue;
                    }

                    result.Targets[id] = new LitresTarget
                    {
                        Id = id, Target = target, Content = t
                    };
                }
            }
        }

        return result;
    }
}
