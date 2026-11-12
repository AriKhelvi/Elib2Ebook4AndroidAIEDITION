using System.Net;
using System.Reflection;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.DomainServices.Misc.TempFolder;
using Microsoft.Extensions.Logging;

namespace Elib2Ebook.DomainServices.Tests.Getters;

internal static class AfAuditSupport
{
    public static BookGetterConfig Config(HttpMessageHandler handler, Options options = null, ILogger logger = null)
        => new(options ?? new Options(), new HttpClient(handler), new CookieContainer(), TempFolderFactory.Create(null), logger ?? new AuditLogger());

    public static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(value)) };

    public static Task<T> Invoke<T>(object target, string name, params object[] args)
        => (Task<T>)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;

    public sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = send(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    public sealed class AuditLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (level >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
