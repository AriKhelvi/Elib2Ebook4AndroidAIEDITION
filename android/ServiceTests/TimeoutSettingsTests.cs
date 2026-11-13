using E2E.Droid.Services;
using Microsoft.Maui.Storage;

namespace ServiceTests;

public sealed class TimeoutSettingsTests : IDisposable
{
    private readonly IPreferences _previous = Preferences.Default;

    public TimeoutSettingsTests() => Preferences.Default = new MemoryPreferences();
    public void Dispose() => Preferences.Default = _previous;

    [Fact]
    public void NewInstallationUsesStandard120Seconds()
    {
        var settings = new AppSettings();
        Assert.Equal(120, settings.Timeout);
        Assert.Equal(2, settings.TimeoutMode);
        Assert.Equal(120, settings.CustomTimeoutSeconds);
    }

    [Theory]
    [InlineData(60, 1)]
    [InlineData(120, 2)]
    [InlineData(240, 3)]
    [InlineData(30, 4)]
    [InlineData(5, 4)]
    [InlineData(3600, 4)]
    public void ExistingTimeoutIsKeptAfterUpgrade(int seconds, int expectedMode)
    {
        Preferences.Default.Set("Timeout", seconds);
        var settings = new AppSettings();
        Assert.Equal(expectedMode, settings.TimeoutMode);
        Assert.Equal(seconds, settings.Timeout);
        Assert.Equal(seconds, settings.CustomTimeoutSeconds);
        Assert.Equal(seconds, new AppSettings().Timeout);
    }

    [Fact]
    public void SwitchingPresetsDoesNotLoseExistingCustomTimeout()
    {
        Preferences.Default.Set("Timeout", 37);
        var settings = new AppSettings();
        settings.TimeoutMode = 1;
        Assert.Equal(60, settings.Timeout);
        settings.TimeoutMode = 3;
        Assert.Equal(240, settings.Timeout);
        settings.TimeoutMode = 2;
        Assert.Equal(120, settings.Timeout);
        var reopened = new AppSettings { TimeoutMode = 4 };
        Assert.Equal(37, reopened.Timeout);
        Assert.Equal(37, reopened.CustomTimeoutSeconds);
    }

    [Fact]
    public void CustomValueEqualToPresetRemainsCustomAfterRestart()
    {
        // Режим «Свое» старой установки ещё не записан отдельным ключом.
        Preferences.Default.Set("Timeout", 37);
        var settings = new AppSettings { CustomTimeoutSeconds = 120 };
        Assert.Equal(120, settings.Timeout);
        Assert.Equal(4, new AppSettings().TimeoutMode);
        settings.TimeoutMode = 1;
        settings.TimeoutMode = 4;
        Assert.Equal(120, settings.Timeout);
        Assert.Equal(4, settings.TimeoutMode);
    }

    [Fact]
    public void InactiveCustomValueDoesNotChangeNetworkTimeout()
    {
        var settings = new AppSettings { CustomTimeoutSeconds = 600 };
        Assert.Equal(120, settings.Timeout);
        settings.TimeoutMode = 4;
        Assert.Equal(600, settings.Timeout);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-10, 5)]
    [InlineData(99999, 3600)]
    public void TimeoutAndCustomInputAreBounded(int supplied, int expected)
    {
        var settings = new AppSettings { Timeout = supplied };
        Assert.Equal(expected, settings.Timeout);
        settings.TimeoutMode = 4;
        settings.CustomTimeoutSeconds = supplied;
        Assert.Equal(expected, settings.Timeout);
        Assert.Equal(expected, settings.CustomTimeoutSeconds);
    }

    [Fact]
    public void InvalidOldValuesCannotEscapeNetworkBounds()
    {
        Preferences.Default.Set("Timeout", -1);
        Preferences.Default.Set("CustomTimeoutSeconds", 10000);
        var settings = new AppSettings();
        Assert.Equal(5, settings.Timeout);
        Assert.Equal(3600, settings.CustomTimeoutSeconds);
    }

    private sealed class MemoryPreferences : IPreferences
    {
        private readonly Dictionary<string, object> _values = [];
        public T Get<T>(string key, T fallback, string sharedName = null)
            => _values.TryGetValue(key, out var value) ? (T)value : fallback;
        public void Set<T>(string key, T value, string sharedName = null) => _values[key] = value;
    }
}
