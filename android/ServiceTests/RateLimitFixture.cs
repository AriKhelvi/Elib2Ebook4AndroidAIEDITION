using System.Net;
using System.Reflection;
using E2E.Droid.Services;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Getters;

namespace ServiceTests;

/// <summary>Настоящий конвейер и распознавание зеркал с загрузчиком без сети.</summary>
internal static class RateLimitFixture
{
    internal const string Site = "https://rate-limit.example.test";
    internal const string Mirror = "https://mirror.example.test";
    internal const string Other = "https://independent.example.test";
    internal static readonly List<string> Requests = [];

    internal static IDisposable Use()
    {
        Requests.Clear();
        // Состав загрузчиков как в APK; другие тесты содержат геттеры с тестовыми конструкторами.
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var types = typeof(GetterResolver).GetField("_getterTypes", flags)!;
        var probes = typeof(GetterResolver).GetField("_probes", flags)!;
        var oldTypes = types.GetValue(null);
        var oldProbes = probes.GetValue(null);
        types.SetValue(null, new[] { typeof(IndependentRateLimitFixtureGetter), typeof(RateLimitFixtureGetter) });
        probes.SetValue(null, null);
        return new RestoreResolver(() =>
        {
            types.SetValue(null, oldTypes);
            probes.SetValue(null, oldProbes);
        });
    }

    private sealed class RestoreResolver(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    internal sealed class Preferences : IPreferences
    {
        private readonly Dictionary<string, object> _values = [];
        public T Get<T>(string key, T defaultValue, string sharedName = null)
            => _values.TryGetValue(key, out var value) ? (T)value : defaultValue;
        public void Set<T>(string key, T value, string sharedName = null) => _values[key] = value;
    }

    internal sealed class Storage : IBookStorage
    {
        public string DestinationLabel => "Тестовая папка";
        public Task<IReadOnlyList<SavedFile>> SaveTreeAsync(string sourceDir, Func<SavedFile, SavedFile> decorate,
            CancellationToken cancellationToken = default, IProgress<StorageProgress> progress = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<SavedFile>>(Directory.GetFiles(sourceDir).Select(path => decorate(new SavedFile
            {
                Name = Path.GetFileName(path), Size = new FileInfo(path).Length, Uri = "content://test/" + Path.GetFileName(path)
            })).ToList());
        }
        public Task<bool> EnsureWriteAccessAsync() => Task.FromResult(true);
        public Task<bool> PickFolderAsync() => Task.FromResult(false);
        public void ResetFolder() { }
        public bool Exists(SavedFile file) => true;
        public bool Delete(SavedFile file) => true;
        public bool Open(SavedFile file) => true;
        public void Share(IReadOnlyCollection<SavedFile> files) { }
    }

    internal sealed class Platform : IPlatformServices
    {
        public string AppVersion => "test";
        public string BuildNumber => "1";
        public bool IsInForeground => true;
        public bool SupportsDynamicColor => false;
        public bool IsBackgroundWorkRunning => true;
        public bool IsIgnoringBatteryOptimizations => true;
        public Task RequestNotificationPermissionAsync() => Task.CompletedTask;
        public void StartBackgroundWork(string text) { }
        public void UpdateBackgroundWork(string text) { }
        public void StopBackgroundWork() { }
        public void NotifyFinished(string title, string text) { }
        public void ApplyTheme(bool dark, string background) { }
        public IReadOnlyDictionary<string, string> GetDynamicColorScheme(bool dark) => null;
        public string SaveThumbnail(byte[] image, string path) => null;
        public IReadOnlyList<KeyValuePair<string, string>> DescribeDevice() => [];
        public Task<bool> RequestIgnoreBatteryOptimizationsAsync() => Task.FromResult(true);
    }
}

public class RateLimitFixtureGetter(BookGetterConfig config) : GetterBase(config)
{
    protected override Uri SystemUrl => new(RateLimitFixture.Site);

    public override bool IsSameUrl(Uri url) => url.IdnHost == SystemUrl.IdnHost ||
        (SystemUrl.IdnHost == new Uri(RateLimitFixture.Site).IdnHost && url.IdnHost == new Uri(RateLimitFixture.Mirror).IdnHost);

    public override Task Init() => Task.CompletedTask;

    public override Task<Book> Get(Uri url)
    {
        RateLimitFixture.Requests.Add(url.ToString());
        if (url.AbsolutePath == "/limited")
            throw new HttpRequestException("private-payload", null, HttpStatusCode.TooManyRequests);
        if (url.AbsolutePath == "/forbidden")
            throw new HttpRequestException("private-payload", null, HttpStatusCode.Forbidden);
        return Task.FromResult(new Book(url)
        {
            Title = "Пробная книга", Author = new Author("Автор"),
            Chapters = [new Chapter { Title = "Глава", Content = "<p>Содержимое главы без сети.</p>" }]
        });
    }
}

// Тот же базовый загрузчик не делает два разных сайта одним источником лимита.
public sealed class IndependentRateLimitFixtureGetter(BookGetterConfig config) : RateLimitFixtureGetter(config)
{
    protected override Uri SystemUrl => new(RateLimitFixture.Other);
}
