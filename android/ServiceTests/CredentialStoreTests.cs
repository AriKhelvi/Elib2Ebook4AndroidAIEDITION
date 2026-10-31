using System.Security.Cryptography;
using E2E.Droid.Services;
using Microsoft.Maui.Storage;

namespace ServiceTests;

public sealed class CredentialStoreTests
{
    private readonly FakeSecureStorage _secure = new();
    private readonly FakePreferences _preferences = new();
    private readonly ToastService _toasts = new();
    private CredentialStore Store() => new(_secure, _preferences, _toasts);

    [Fact]
    public async Task SaveAndReadRoundTripAndIndexContainsNoCredentials()
    {
        var store = Store();
        var credential = new SiteCredential("user", "secret");
        Assert.True(await store.SaveAsync("example.com", credential));
        Assert.Equal(credential, await store.GetAsync("example.com"));
        Assert.Equal(["example.com"], store.Hosts);
        Assert.Equal("example.com", _preferences.Value);
        Assert.Empty(_toasts.Toasts);
    }

    [Fact]
    public async Task FailedWriteKeepsExistingIndexAndDoesNotLeakErrorDetails()
    {
        _preferences.Value = "old.example";
        _secure.WriteError = new CryptographicException("secret-password");
        var store = Store();
        Assert.False(await store.SaveAsync("new.example", new SiteCredential("user", "secret-password")));
        Assert.Equal(["old.example"], store.Hosts);
        Assert.DoesNotContain("secret-password", Assert.Single(_toasts.Toasts).Text);
    }

    [Fact]
    public async Task UnavailableKeystoreDoesNotEraseCredentialsAndWarnsOnce()
    {
        _secure.ReadError = new CryptographicException("secret-password");
        _preferences.Value = "example.com";
        var store = Store();
        Assert.Null(await store.GetAsync("example.com"));
        Assert.Null(await store.GetAsync("other.example"));
        Assert.Equal(0, _secure.RemoveCalls);
        Assert.Equal(["example.com"], store.Hosts);
        Assert.DoesNotContain("secret-password", Assert.Single(_toasts.Toasts).Text);
    }

    [Fact]
    public async Task InvalidJsonIsReportedWithoutDeletingOtherEntries()
    {
        _secure.Values["cred:example.com"] = "{broken";
        var store = Store();
        Assert.Null(await store.GetAsync("example.com"));
        Assert.Equal(0, _secure.RemoveCalls);
        Assert.Single(_toasts.Toasts);
    }

    [Fact]
    public async Task MissingEntryIsNotAnError()
    {
        Assert.Null(await Store().GetAsync("example.com"));
        Assert.Empty(_toasts.Toasts);
    }

    [Fact]
    public void FailedRemovalKeepsHostVisibleForRetry()
    {
        _preferences.Value = "example.com";
        _secure.RemoveError = new CryptographicException("secret-password");
        var store = Store();
        Assert.False(store.Remove("example.com"));
        Assert.Equal(["example.com"], store.Hosts);
        Assert.DoesNotContain("secret-password", Assert.Single(_toasts.Toasts).Text);
    }

    [Fact]
    public void MissingKeyCanBeRemovedFromStaleIndex()
    {
        _preferences.Value = "example.com\nother.example";
        var store = Store();
        Assert.True(store.Remove("example.com"));
        Assert.Equal(["other.example"], store.Hosts);
    }

    [Fact]
    public void UnavailablePreferencesDoNotCrashSettings()
    {
        _preferences.Error = new IOException();
        Assert.Empty(Store().Hosts);
        Assert.Single(_toasts.Toasts);
    }

    private sealed class FakeSecureStorage : ISecureStorage
    {
        public Dictionary<string, string> Values { get; } = [];
        public Exception ReadError, WriteError, RemoveError;
        public int RemoveCalls;
        public Task<string> GetAsync(string key) => ReadError != null
            ? Task.FromException<string>(ReadError) : Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value)
        {
            if (WriteError != null) return Task.FromException(WriteError);
            Values[key] = value;
            return Task.CompletedTask;
        }
        public bool Remove(string key)
        {
            RemoveCalls++;
            if (RemoveError != null) throw RemoveError;
            return Values.Remove(key);
        }
    }

    private sealed class FakePreferences : IPreferences
    {
        public string Value = "";
        public Exception Error;
        public T Get<T>(string key, T defaultValue, string sharedName = null)
        {
            if (Error != null) throw Error;
            return (T)(object)Value;
        }
        public void Set<T>(string key, T value, string sharedName = null)
        {
            if (Error != null) throw Error;
            Value = (string)(object)value;
        }
    }
}
