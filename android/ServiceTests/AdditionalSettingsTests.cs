using E2E.Droid.Services;

namespace ServiceTests;

public sealed class AdditionalSettingsTests : IDisposable
{
    private readonly IPreferences _previous = Preferences.Default;
    private readonly MemoryPreferences _preferences = new();

    public AdditionalSettingsTests() => Preferences.Default = _preferences;

    [Fact]
    public void FreshSettingsKeepAdditionalFilesDisabledWithExplicitAudioAndImageChoices()
    {
        var settings = new AppSettings();

        Assert.False(settings.Additional);
        Assert.Equal(new[] { "audio", "images" }, settings.AdditionalTypes);
        Assert.True(settings.Images);
        Assert.False(settings.Cover);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyEmptySelectionKeepsEnabledStateAndMigratesToOnlyAudioAndImages(bool enabled)
    {
        Seed("", enabled);
        var settings = new AppSettings();

        Assert.Equal(new[] { "audio", "images" }, settings.AdditionalTypes);
        Assert.Equal(enabled, settings.Additional);
        Assert.Equal("audio,images", _preferences.Get("AdditionalTypes", "missing"));
    }

    [Theory]
    [InlineData("books")]
    [InlineData("Books,books")]
    [InlineData("unknown,books")]
    public void LegacyBooksOnlySelectionDisablesExtrasInsteadOfEnablingNewDownloads(string previousTypes)
    {
        Seed(previousTypes, true);
        _preferences.Set("Images", false);
        _preferences.Set("Cover", true);
        _preferences.Set("TextFormat", "fb2");
        var settings = new AppSettings();

        Assert.False(settings.Additional);
        Assert.Empty(settings.AdditionalTypes);
        Assert.False(_preferences.Get("Additional", true));
        Assert.Equal("", _preferences.Get("AdditionalTypes", "missing"));
        Assert.False(settings.Images);
        Assert.True(settings.Cover);
        Assert.Equal("fb2", settings.TextFormat);
        var reopened = new AppSettings();
        Assert.False(reopened.Additional);
        Assert.Empty(reopened.AdditionalTypes);
    }

    [Theory]
    [InlineData("books,audio", "audio")]
    [InlineData("images,books", "images")]
    [InlineData("books,audio,images", "audio,images")]
    [InlineData("AUDIO,books,audio", "audio")]
    public void MixedLegacySelectionPreservesOnlyPreviouslySelectedSupportedTypes(string previousTypes, string expected)
    {
        Seed(previousTypes, true);
        var settings = new AppSettings();

        Assert.Equal(expected.Split(','), settings.AdditionalTypes);
        Assert.True(settings.Additional);
        Assert.DoesNotContain("books", _preferences.Get("AdditionalTypes", ""));
    }

    [Fact]
    public void RemovingLastTypeStaysDisabledAfterRestartUntilExplicitlyEnabled()
    {
        var settings = new AppSettings { Additional = true, AdditionalTypes = ["images"] };

        settings.AdditionalTypes = [];

        Assert.False(settings.Additional);
        Assert.Empty(settings.AdditionalTypes);
        var reopened = new AppSettings();
        Assert.False(reopened.Additional);
        Assert.Empty(reopened.AdditionalTypes);

        reopened.Additional = true;

        Assert.True(reopened.Additional);
        Assert.Equal(new[] { "audio", "images" }, reopened.AdditionalTypes);
    }

    [Fact]
    public void UnsupportedTypesCannotBeRestoredBySettingTheOldValue()
    {
        var settings = new AppSettings { Additional = true };

        settings.AdditionalTypes = ["books", " AUDIO ", "audio", "images", "unknown"];

        Assert.Equal(new[] { "audio", "images" }, settings.AdditionalTypes);
        Assert.True(settings.Additional);

        settings.AdditionalTypes = ["books"];

        Assert.Empty(settings.AdditionalTypes);
        Assert.False(settings.Additional);
    }

    private void Seed(string types, bool enabled)
    {
        _preferences.Set("AdditionalTypes", types);
        _preferences.Set("Additional", enabled);
    }

    public void Dispose() => Preferences.Default = _previous;

    private sealed class MemoryPreferences : IPreferences
    {
        private readonly Dictionary<string, object> _values = [];
        public T Get<T>(string key, T fallback, string sharedName = null) => _values.TryGetValue(key, out var value) ? (T)value : fallback;
        public void Set<T>(string key, T value, string sharedName = null) => _values[key] = value!;
    }
}
