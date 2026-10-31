// Подменяем только границу с Android: тесты исполняют настоящий ActivityResults.cs.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Android.Content
{
    public sealed class Intent;
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
