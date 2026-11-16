using System.Net;
using E2E.Droid.Services;

namespace ServiceTests;

public sealed class SiteRateLimitScopeTests
{
    [Fact]
    public void AliasesShareLimitButRelatedGetterAndNewRunDoNot()
    {
        using var fixture = RateLimitFixture.Use();
        var scope = new SiteRateLimitScope();
        var error = new InvalidOperationException("private-value", new AggregateException(
            new IOException("Не HTTP"), new HttpRequestException("secret", null, HttpStatusCode.TooManyRequests)));

        Assert.True(scope.Record(RateLimitFixture.Site + "/book/1", error));
        Assert.True(scope.TryGetReason(RateLimitFixture.Mirror + "/book/2?token=private", out var reason));
        Assert.Contains("HTTP 429", reason);
        Assert.DoesNotContain("private", reason);
        Assert.DoesNotContain("secret", reason);
        Assert.False(scope.TryGetReason(RateLimitFixture.Other + "/book/3", out _));
        Assert.False(new SiteRateLimitScope().TryGetReason(RateLimitFixture.Mirror + "/book/2", out _));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public void OtherFailuresDoNotBlockLaterBooks(int status)
    {
        var scope = new SiteRateLimitScope();
        Assert.False(scope.Record("https://www.example.test/a", new HttpRequestException("HTTP 429 в тексте", null, (HttpStatusCode)status)));
        Assert.False(scope.TryGetReason("https://example.test/b", out _));
    }

    [Fact]
    public void UnrecognizedHostsUseNormalizedHostAndIgnorePortAndPath()
    {
        var scope = new SiteRateLimitScope();
        Assert.True(scope.Record("https://www.unknown.test:8443/a", 429));
        Assert.True(scope.TryGetReason("http://UNKNOWN.test/b", out _));
        Assert.False(scope.TryGetReason("https://different.test/a", out _));
    }

    [Theory]
    [InlineData("mangalib.org", "mangalib.me")]
    [InlineData("mangalib.me", "www.mangalib.org")]
    public void MangaLibDomainsShareLimitButSlashLibRemainsIndependent(string first, string second)
    {
        var scope = new SiteRateLimitScope();
        Assert.True(scope.Record($"https://{first}/ru/manga/206--one-piece", 429));
        Assert.True(scope.TryGetReason($"https://{second}/ru/manga/206--one-piece", out _));
        Assert.False(scope.TryGetReason("https://v2.slashlib.me/ru/23435--secret-alliance", out _));
    }
}
