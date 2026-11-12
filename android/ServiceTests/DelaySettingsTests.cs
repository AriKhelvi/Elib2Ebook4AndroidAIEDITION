using E2E.Droid.Services;
using Microsoft.Maui.Storage;

namespace ServiceTests;

public class DelaySettingsTests
{
    [Fact]
    public void StandardDelayIsDefaultIncludingOldInstallations()
    {
        var previous = Preferences.Default;
        try
        {
            Preferences.Default = new MemoryPreferences();
            var settings = new AppSettings();
            settings.Delay = 0;
            Assert.Equal(2, settings.DelayMode);
            Assert.Equal(1d, settings.CustomDelaySeconds);
        }
        finally { Preferences.Default = previous; }
    }

    [Fact]
    public void FractionalCustomDelayPersistsAndIsBounded()
    {
        var previous = Preferences.Default;
        try
        {
            Preferences.Default = new MemoryPreferences();
            var settings = new AppSettings { DelayMode = 4, CustomDelaySeconds = 0.25 };
            var reopened = new AppSettings();
            Assert.Equal(4, reopened.DelayMode);
            Assert.Equal(0.25, reopened.CustomDelaySeconds);
            reopened.CustomDelaySeconds = 0;
            Assert.Equal(0.01, reopened.CustomDelaySeconds);
            reopened.CustomDelaySeconds = 121;
            Assert.Equal(120, reopened.CustomDelaySeconds);
            reopened.CustomDelaySeconds = double.NaN;
            Assert.Equal(1, reopened.CustomDelaySeconds);
        }
        finally { Preferences.Default = previous; }
    }

    private sealed class MemoryPreferences : IPreferences
    {
        private readonly Dictionary<string, object> _values = [];
        public T Get<T>(string key, T defaultValue, string sharedName = null)
            => _values.TryGetValue(key, out var value) ? (T)value : defaultValue;
        public void Set<T>(string key, T value, string sharedName = null) => _values[key] = value;
    }
}
