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

    [Theory]
    [InlineData("https://reader:sample-secret@example.test/book/1", "reader")]
    [InlineData("socks5://reader:sample-secret@proxy.example.test:1080", "sample-secret")]
    [InlineData("https://example.test/file?art=42&SID=sample-secret&lang=ru", "sample-secret")]
    [InlineData("https://example.test/file?%73%49%64=sample-secret&chapter=101", "sample-secret")]
    [InlineData("https://example.test/file?access%5Ftoken=sample-secret&chapter=101", "sample-secret")]
    [InlineData("https://example.test/file#token=sample-secret", "sample-secret")]
    [InlineData("Authorization: Bearer sample-secret", "sample-secret")]
    [InlineData("auth-token: sample-secret", "sample-secret")]
    [InlineData("X-CSRF-TOKEN: sample-secret", "sample-secret")]
    [InlineData("X-XSRF-TOKEN: sample-secret", "sample-secret")]
    [InlineData("https://example.test/file?auth%2Dtoken=sample-secret&chapter=101", "sample-secret")]
    [InlineData("Proxy-Authorization: Basic sample-secret", "sample-secret")]
    [InlineData("Cookie: a=sample-secret; b=second-secret", "second-secret")]
    [InlineData("Set-Cookie: sid=sample-secret; Secure; HttpOnly", "sample-secret")]
    [InlineData("{\"password\": \"sample-secret with spaces\", \"status\": 403}", "with spaces")]
    [InlineData("{\"refresh_token\": \"sample-secret\\\"more\", \"status\": 403}", "sample-secret")]
    [InlineData("Ошибка: session_id = sample-secret\nHTTP 403", "sample-secret")]
    [InlineData("Пароль: sample-secret with spaces", "with spaces")]
    [InlineData("Exception: Bearer sample-secret", "sample-secret")]
    public void AccessDetailsAreRemovedFromMessages(string text, string forbidden)
    {
        var cleaned = LogText.Clean(text);
        Assert.DoesNotContain(forbidden, cleaned);
        Assert.Contains(LogText.Hidden, cleaned);
        Assert.Equal(cleaned, LogText.Clean(cleaned));
    }

    [Fact]
    public void PublicBookParametersAndHttpStatusRemainUseful()
    {
        const string message = "HTTP 403: https://example.test/book/42?chapter_page=7&art=12&sid=sample-secret&lang=ru";
        var clean = LogText.Clean(message);
        Assert.Contains("HTTP 403", clean);
        Assert.Contains("chapter_page=7&art=12", clean);
        Assert.Contains("&lang=ru", clean);
        Assert.DoesNotContain("sample-secret", clean);
    }

    [Fact]
    public void MultilineHeadersAndOldPrivateKeysAreRedactedOnRead()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "old.log");
        File.WriteAllLines(path,
        [
            "Authorization: Bearer sample-secret", " continued-secret", "HTTP 403",
            "-----BEGIN PRIVATE KEY-----", "encoded-secret", "-----END PRIVATE KEY-----",
            "{\"password\":", "  \"multiline-secret\",", "\"status\": 403}"
        ]);
        var text = string.Join('\n', new BoundedFileLog(path).Read());
        Assert.DoesNotContain("sample-secret", text);
        Assert.DoesNotContain("continued-secret", text);
        Assert.DoesNotContain("encoded-secret", text);
        Assert.DoesNotContain("multiline-secret", text);
        Assert.Contains("HTTP 403", text);
    }

    [Fact]
    public void SecretsAreRemovedBeforePersistenceIncludingTruncatedMessages()
    {
        var path = Path.Combine(_root, "download.log");
        var log = new BoundedFileLog(path);
        log.Append("https://example.test/book?sid=" + new string('Z', 5000));
        log.Append("Authorization: Bearer sample-secret\n continued-secret\nHTTP 403");
        var stored = File.ReadAllText(path);
        Assert.DoesNotContain("ZZZZ", stored);
        Assert.DoesNotContain("sample-secret", stored);
        Assert.DoesNotContain("continued-secret", stored);
        Assert.Contains("HTTP 403", stored);
        Assert.All(File.ReadAllLines(path), line => Assert.True(line.Length <= LogText.MaxLineLength));
        Assert.DoesNotContain("ZZZZ", LogText.Clean("https://reader:" + new string('Z', 5000) + "@example.test/book"));
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
