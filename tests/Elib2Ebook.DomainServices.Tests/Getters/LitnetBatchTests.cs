using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Elib2Ebook.ExternalServices.Litnet.Getters;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LitnetBatchTests
{
    private static readonly Uri BookUri = new("https://litnet.com/ru/book/test-b123");

    [Fact]
    public async Task RequestsAll205ChaptersInBoundedBatchesAndKeepsTocOrder()
    {
        var handler = new FakeHandler();
        using var getter = CreateGetter(new Options(), handler);
        using var book = await getter.Get(BookUri);

        Assert.Equal(Enumerable.Repeat(10, 20).Append(5), handler.Batches.Select(batch => batch.Length));
        Assert.Equal(Enumerable.Range(1, 205).Select(i => (long)i), handler.Batches.SelectMany(batch => batch));
        Assert.Equal(Enumerable.Range(1, 205).Select(i => $"Глава {i}"), book.Chapters.Select(chapter => chapter.Title));
        Assert.All(book.Chapters, chapter => Assert.Equal("<p>Текст главы</p>", chapter.Content));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedRangeContainsOnlySelectedChapters(bool byName)
    {
        var handler = new FakeHandler();
        var options = byName
            ? new Options { StartName = "Глава 99", EndName = "Глава 102" }
            : new Options { Start = 99, End = 102 };
        using var getter = CreateGetter(options, handler);
        using var book = await getter.Get(BookUri);

        Assert.Equal(new long[] { 99, 100, 101, 102 }, Assert.Single(handler.Batches));
        Assert.Equal(new[] { "Глава 99", "Глава 100", "Глава 101", "Глава 102" }, book.Chapters.Select(chapter => chapter.Title));
    }

    [Fact]
    public async Task MissingChapterIdStopsDownloadInsteadOfCreatingEmptyChapter()
    {
        var handler = new FakeHandler { OmitLastChapter = true };
        using var getter = CreateGetter(new Options(), handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(BookUri));
        Assert.Contains("не вернул 1 из 10", error.Message);
        Assert.Single(handler.Batches);
    }

    [Fact]
    public async Task PreviewDoesNotRequestContentsOrChapterBatches()
    {
        var handler = new FakeHandler();
        using var getter = CreateGetter(new Options { NoChapters = true }, handler);
        using var book = await getter.Get(BookUri);

        Assert.Empty(book.Chapters);
        Assert.Empty(handler.Batches);
        Assert.Equal(0, handler.ContentsRequests);
    }

    [Fact]
    public async Task ShowsRequestProgressAndProcessesEachBatchBeforeRequestingTheNext()
    {
        var log = new ProgressLogger();
        var handler = new FakeHandler
        {
            ChapterCount = 12,
            BeforeBatch = number =>
            {
                Assert.Contains(log.Lines, line => line.StartsWith("Получаю текст глав Litnet"));
                Assert.Equal(number == 1 ? 0 : 10, log.Lines.Count(line => line.StartsWith("Загружаю главу")));
            }
        };
        using var getter = new LitnetGetter(new BookGetterConfig(new Options(), new HttpClient(handler),
            new CookieContainer(), TempFolderFactory.Create(null), log));

        using var book = await getter.Get(BookUri);

        Assert.Equal(12, book.Chapters.Count());
        Assert.Contains("Получаю текст глав Litnet 1–10 из 12", log.Lines);
        Assert.Contains("Получаю текст глав Litnet 11–12 из 12", log.Lines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad-encoding")]
    public async Task MissingOrMalformedTextStopsWithoutStartingTheNextBatch(string text)
    {
        var handler = new FakeHandler { BrokenText = text, BrokenChapter = 4 };
        using var getter = CreateGetter(new Options(), handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(BookUri));

        Assert.Contains("Глава 4", error.Message);
        Assert.Contains("Неполная книга не будет сохранена", error.Message);
        Assert.Single(handler.Batches);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[\"<p><br /></p>\"]")]
    public async Task EmptyOrMalformedDecryptedChapterIsNotReportedAsSuccess(string json)
    {
        var handler = new FakeHandler { BrokenChapter = 4, BrokenText = Encrypt(json) };
        using var getter = CreateGetter(new Options(), handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(BookUri));

        Assert.Contains("Неполная книга не будет сохранена", error.Message);
        Assert.Single(handler.Batches);
    }

    [Fact]
    public async Task EmptyContentsFailInsteadOfCreatingAnEmptyBook()
    {
        var handler = new FakeHandler { ChapterCount = 0 };
        using var getter = CreateGetter(new Options(), handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => getter.Get(BookUri));

        Assert.Contains("оглавление", error.Message);
        Assert.Empty(handler.Batches);
    }

    [Fact]
    public async Task CancellationAfterFirstBatchDoesNotRequestTheSecond()
    {
        using var cancellation = new CancellationTokenSource();
        var log = new ProgressLogger { OnLine = line => { if (line == "Загружаю главу \"Глава 10\"") cancellation.Cancel(); } };
        var handler = new FakeHandler();
        var config = new BookGetterConfig(new Options(), new HttpClient(handler), new CookieContainer(), TempFolderFactory.Create(null), log)
        { CancellationToken = cancellation.Token };
        using var getter = new LitnetGetter(config);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => getter.Get(BookUri));

        Assert.Single(handler.Batches);
    }

    private static string Encrypt(string json)
    {
        using var aes = Aes.Create();
        // Тест использует тот же публичный протокол API, не учётные данные пользователя.
        var key = (string)typeof(LitnetGetterBase).GetField("SECRET", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue();
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.IV = new byte[16];
        using var encryptor = aes.CreateEncryptor();
        var bytes = new byte[16].Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        return Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length));
    }

    private sealed class ProgressLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public Action<string> OnLine { get; init; }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var line = formatter(state, exception);
            Lines.Add(line);
            OnLine?.Invoke(line);
        }
    }

    private static LitnetGetter CreateGetter(Options options, HttpMessageHandler handler)
        => new(new BookGetterConfig(options, new HttpClient(handler), new CookieContainer(), TempFolderFactory.Create(null), NullLogger.Instance));

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<long[]> Batches { get; } = [];
        public int ContentsRequests { get; private set; }
        public bool OmitLastChapter { get; init; }
        public int ChapterCount { get; init; } = 205;
        public long? BrokenChapter { get; init; }
        public string BrokenText { get; init; }
        public Action<int> BeforeBatch { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            object result;
            switch (uri.AbsolutePath)
            {
                case "/v1/book/get/123":
                    result = new { title = "Книга", url = BookUri.ToString(), author_name = "Автор", lang = "ru" };
                    break;
                case "/v1/book/contents":
                    ContentsRequests++;
                    result = Enumerable.Range(1, ChapterCount).Select(i => new { id = i, title = $"Глава {i}" });
                    break;
                case "/v1/book/get-chapters-texts/":
                    var ids = HttpUtility.ParseQueryString(uri.Query).GetValues("chapter_ids[]")!.Select(long.Parse).ToArray();
                    Batches.Add(ids);
                    BeforeBatch?.Invoke(Batches.Count);
                    // Сервер может вернуть главы в другом порядке и обрезать большой запрос.
                    result = ids.Take(OmitLastChapter ? ids.Length - 1 : 100).Reverse().Select(id => new
                    {
                        id, text = id == BrokenChapter ? BrokenText : Encrypt("[\"<p>Текст главы</p>\"]")
                    });
                    break;
                case "/ru/book/test-b123":
                    result = new { };
                    break;
                default:
                    throw new InvalidOperationException($"Неожиданный путь {uri.AbsolutePath}");
            }

            return Task.FromResult(TestHttpResponse.Create(HttpStatusCode.OK, request, new StringContent(JsonSerializer.Serialize(result))));
        }
    }
}
