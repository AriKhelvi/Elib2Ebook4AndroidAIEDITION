using E2E.Droid.Services;

namespace ServiceTests;

public sealed class TlsProbeTests
{
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
