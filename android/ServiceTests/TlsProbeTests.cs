using E2E.Droid.Services;

namespace ServiceTests;

public sealed class TlsProbeTests
{
    [Fact]
    public void SummaryKeepsDefaultFailuresSeparateFromAlternativeHttpResponses()
    {
        TlsProbeResult[] results =
        [
            new() { Ok = true, HttpStatusCode = 200 },
            new() { Tls12HttpStatusCode = 200 },
            new() { Tls12HttpStatusCode = 403 },
        ];

        var summary = TlsProbe.DescribeSummary(results);
        Assert.Contains("в штатном режиме: получен HTTP-ответ 1", summary);
        Assert.Contains("без HTTP-ответа и без отказа по сертификату 2", summary);
        Assert.Contains("в отдельной пробе TLS 1.2 получен HTTP-ответ 2, из них HTTP >= 400: 1", summary);
        Assert.False(results[1].Ok);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(204, false)]
    [InlineData(301, false)]
    [InlineData(302, false)]
    [InlineData(400, true)]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(404, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    public void HttpErrorsDoNotTurnSuccessfulTransportIntoTlsFailure(int status, bool blocked)
    {
        var result = new TlsProbeResult { Ok = true, TlsSeen = true, HttpStatusCode = status };

        Assert.Equal(blocked, result.HttpBlocked);
        Assert.True(result.Ok);
        Assert.False(result.TlsRejected);
    }

    [Fact]
    public void MissingHttpResponseIsNotClassifiedAsServerRejection()
    {
        var result = new TlsProbeResult { TlsRejected = true, TlsSeen = true };

        Assert.Null(result.HttpStatusCode);
        Assert.False(result.HttpBlocked);
        Assert.False(result.Ok);
        Assert.True(result.TlsRejected);
    }
}
