using Elib2Ebook.ExternalServices.Litnet;

namespace E2E.Droid.Services;

/// <summary>Встроенное окно сайта: приложение видит только адрес возврата, не поля входа.</summary>
public sealed class AndroidLitnetAuthorizationBrowser : ILitnetAuthorizationBrowser
{
    public Task<Uri> AuthorizeAsync(LitnetAuthorizationRequest request, CancellationToken ct)
        => MainThread.InvokeOnMainThreadAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var root = Application.Current?.Windows.FirstOrDefault()?.Page
                ?? throw new InvalidOperationException("Не удалось открыть окно входа Litnet.");
            var page = new LitnetLoginPage(request);
            using var cancellation = ct.Register(() => MainThread.BeginInvokeOnMainThread(page.Cancel));
            try
            {
                await root.Navigation.PushModalAsync(page);
                return await page.Result;
            }
            finally
            {
                if (root.Navigation.ModalStack.LastOrDefault() == page)
                    await root.Navigation.PopModalAsync();
                page.ReleaseWebView();
            }
        });

    private sealed class LitnetLoginPage : ContentPage
    {
        private readonly LitnetAuthorizationRequest _request;
        private readonly TaskCompletionSource<Uri> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly WebView _web;
        private readonly Label _host;
        private readonly Label _notice;

        public Task<Uri> Result => _completion.Task;

        public LitnetLoginPage(LitnetAuthorizationRequest request)
        {
            _request = request;
            Title = "Вход в Litnet";
            BackgroundColor = Colors.White;
            _host = new Label { Text = "https://id.litnet.com", TextColor = Colors.Black, FontSize = 16, FontAttributes = FontAttributes.Bold };
            _notice = new Label { Text = "Введите данные на сайте Litnet. Пароль не сохраняется в приложении.", TextColor = Colors.DarkSlateGray, FontSize = 13 };
            var cancel = new Button { Text = "Отмена" };
            cancel.Clicked += (_, _) => Cancel();
            var header = new Grid
            {
                Padding = new Thickness(12, 8),
                ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Star }, new ColumnDefinition { Width = GridLength.Auto } },
                RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto } }
            };
            header.Add(_host, 0, 0);
            header.Add(cancel, 1, 0);
            header.Add(_notice, 0, 1);
            Grid.SetColumnSpan(_notice, 2);
            _web = new WebView();
            _web.Navigating += OnNavigating;
            _web.Navigated += OnNavigated;
            _web.HandlerChanged += (_, _) =>
            {
                if (_web.Handler?.PlatformView is global::Android.Webkit.WebView native)
                {
                    native.Settings.AllowFileAccess = false;
                    native.Settings.AllowContentAccess = false;
                    native.Settings.MixedContentMode = global::Android.Webkit.MixedContentHandling.NeverAllow;
                    native.Settings.JavaScriptCanOpenWindowsAutomatically = false;
                    native.Settings.SetSupportMultipleWindows(false);
                    native.Settings.DomStorageEnabled = true;
                }
            };
            var layout = new Grid
            {
                RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Star } }
            };
            layout.Add(header, 0, 0);
            layout.Add(_web, 0, 1);
            Content = layout;
            _web.Source = request.AuthorizationUri.AbsoluteUri;
        }

        private void OnNavigating(object sender, WebNavigatingEventArgs args)
        {
            if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || uri.UserInfo.Length != 0)
            {
                args.Cancel = true;
                _notice.Text = "Открываются только защищённые HTTPS-страницы. Используйте вход на сайте Litnet.";
                return;
            }
            _host.Text = "https://" + uri.IdnHost + (uri.IsDefaultPort ? "" : ":" + uri.Port);
            if (uri.IdnHost.Equals("id.litnet.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath == "/auth/l-audio/callback")
            {
                args.Cancel = true;
                try
                {
                    LitnetOAuth.ValidateCallback(_request, uri);
                    _completion.TrySetResult(uri);
                }
                catch
                {
                    _completion.TrySetException(new InvalidOperationException("Litnet не подтвердил вход. Начните вход заново."));
                }
            }
        }

        private void OnNavigated(object sender, WebNavigatedEventArgs args)
        {
            if (args.Result != WebNavigationResult.Success && !_completion.Task.IsCompleted)
                _notice.Text = "Страница входа не загрузилась. Проверьте подключение и откройте вход заново.";
        }

        public void Cancel() => _completion.TrySetCanceled();
        protected override bool OnBackButtonPressed() { Cancel(); return true; }
        protected override void OnDisappearing() { base.OnDisappearing(); Cancel(); }

        public void ReleaseWebView()
        {
            _web.Navigating -= OnNavigating;
            _web.Navigated -= OnNavigated;
            _web.Handler?.DisconnectHandler();
        }
    }
}
