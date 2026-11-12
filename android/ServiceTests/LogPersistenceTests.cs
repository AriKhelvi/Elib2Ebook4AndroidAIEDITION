using E2E.Droid.Services;
using Elib2Ebook.DomainServices.Misc;

namespace ServiceTests;

public sealed class LogPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "e2e-log-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void HugeImagePayloadDoesNotReachLog()
    {
        var line = LogText.Clean("Картинка data:image/png;base64," + new string('A', 5_000_000));
        Assert.True(line.Length < 100);
        Assert.DoesNotContain("AAAA", line);
        Assert.DoesNotContain("AAAA", LogText.Clean("Картинка data:image/png;base64,AA\nAAAA AA"));
        Assert.True(LogText.Clean(new string('Ж', 30000)).Length <= LogText.MaxLineLength);
    }

    [Fact]
    public void LogSurvivesRecreationAndRetainsRecentLinesWithinByteLimit()
    {
        var path = Path.Combine(_root, "download.log");
        var log = new BoundedFileLog(path, 16000);
        for (var i = 0; i < 100; i++) log.Append($"{i}: " + new string('Я', 200));
        var reopened = new BoundedFileLog(path, 16000).Read();
        Assert.StartsWith("99: ", reopened.Last());
        Assert.DoesNotContain(reopened, l => l.StartsWith("0: "));
        Assert.InRange(new FileInfo(path).Length, 1, 16000);
    }

    [Fact]
    public void SuccessfulStartupDeletesCurrentLogAndKeepsPreviousFailure()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "startup.log"), "WebView: ошибка прошлого запуска\n");
        StartupLog.Begin(_root);
        StartupLog.Write("MAUI готов");
        Assert.True(File.Exists(Path.Combine(_root, "startup.log")));
        StartupLog.MarkReady();
        Assert.False(File.Exists(Path.Combine(_root, "startup.log")));
        Assert.Contains(StartupLog.FailedLines(), l => l.Contains("ошибка прошлого запуска"));
        StartupLog.Write("После запуска");
        Assert.False(File.Exists(Path.Combine(_root, "startup.log")));
    }

    [Theory]
    [InlineData("0.2.0", false)]
    [InlineData("0.2.1-dev.5", false)]
    [InlineData("0.2.1-alpha.codex.1", true)]
    public void OnlyAlphaLogsClaimToBeAlpha(string version, bool alpha)
        => Assert.Equal(alpha, BuildInfo.AgentNote(version).Contains("тестовая сборка (альфа)"));

    public void Dispose()
    {
        StartupLog.MarkReady();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
