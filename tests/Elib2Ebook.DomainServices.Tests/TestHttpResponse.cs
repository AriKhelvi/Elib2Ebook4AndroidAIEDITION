using System.Net;

namespace Elib2Ebook.DomainServices.Tests;

internal static class TestHttpResponse
{
    /// <summary>Создаёт ответ тестового HTTP-обработчика; владение передаётся вызвавшему.</summary>
    public static HttpResponseMessage Create(HttpStatusCode status, HttpRequestMessage request = null, HttpContent content = null)
        => new(status) { RequestMessage = request, Content = content };
}
