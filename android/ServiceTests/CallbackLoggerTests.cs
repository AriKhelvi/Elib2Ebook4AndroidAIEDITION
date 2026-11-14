using E2E.Droid.Services;
using Microsoft.Extensions.Logging;

namespace ServiceTests;

public class CallbackLoggerTests
{
    [Fact]
    public void NormalLogDoesNotFormatOrWriteDebugMessages()
    {
        var messages = new List<string>();
        var logger = new CallbackLogger(messages.Add);
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        logger.Log(LogLevel.Debug, default, "hidden", null, (_, _) => throw new Exception("must not format"));
        logger.LogInformation("Загрузка");
        Assert.Equal(new[] { "Загрузка" }, messages);
    }

    [Fact]
    public void DebugGoesOnlyToDetailedLogAndRedactsMultilineSecrets()
    {
        var normal = new List<string>();
        var warnings = new List<string>();
        var debug = new List<string>();
        var logger = new CallbackLogger(normal.Add, warnings.Add, debug.Add);
        logger.LogDebug("{Dump}", "этап\nAuthorization: Bearer secret-one\n secret-two\nsid=secret-three\nконец");
        Assert.True(logger.IsEnabled(LogLevel.Debug));
        Assert.Empty(normal);
        Assert.Empty(warnings);
        Assert.Equal(5, debug.Count);
        Assert.Equal("конец", debug[^1]);
        Assert.DoesNotContain("secret-", string.Join("\n", debug));
        logger.LogWarning("Ошибка");
        Assert.Single(normal);
        Assert.Single(warnings);
    }
}
