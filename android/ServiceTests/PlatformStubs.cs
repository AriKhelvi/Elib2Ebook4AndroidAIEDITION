// Подменяем только границу с Android: тесты исполняют настоящий ActivityResults.cs.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Android.Content
{
    public sealed class Intent;
}

// Контракты методов MAUI, используемых CredentialStore. Сам Keystore проверяется на устройстве.
namespace Microsoft.Maui.Storage
{
    public interface ISecureStorage
    {
        Task<string> GetAsync(string key);
        Task SetAsync(string key, string value);
        bool Remove(string key);
    }

    public interface IPreferences
    {
        T Get<T>(string key, T defaultValue, string sharedName = null);
        void Set<T>(string key, T value, string sharedName = null);
    }
}

namespace Android.App
{
    public enum Result { Canceled, Ok }

    public sealed class Activity
    {
        public Action<Android.Content.Intent, int> Start { get; set; }
        public void StartActivityForResult(Android.Content.Intent intent, int code) => Start(intent, code);
    }
}

namespace Microsoft.Maui.ApplicationModel
{
    public static class Platform
    {
        public static Android.App.Activity CurrentActivity { get; set; }
    }

    public static class MainThread
    {
        public static Task InvokeOnMainThreadAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
