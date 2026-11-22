using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elib2Ebook.DomainServices.Configs;
using Elib2Ebook.ExternalServices.LibSocial.Getters;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class LibSocialAltchaTests
{
    private static readonly Uri ChallengeUrl = new("https://auth.lib.social/altcha/challenge");
    private static readonly MethodInfo CreatePayloadMethod = typeof(RanobeLibGetter).Assembly
        .GetType("Elib2Ebook.ExternalServices.LibSocial.Getters.LibSocialAltcha", throwOnError: true)!
        .GetMethod("CreatePayloadAsync", BindingFlags.Static | BindingFlags.Public)!;

    [Theory]
    [InlineData("SHA-256", 0, 0)]
    [InlineData("SHA-256", 23, 23)]
    [InlineData("SHA-256", 5, 9)]
    [InlineData("SHA-256", 0, 1_000_000)]
    [InlineData("SHA-1", 7, 7)]
    [InlineData("SHA-512", 3, 3)]
    public async Task CreatesVerifiablePayloadIncludingZeroAndUpperBoundary(string algorithm, int number, int maxNumber)
    {
        const string salt = "private-соль-";
        const string signature = "private-signature+/=";
        var challenge = Challenge(algorithm, salt, number, maxNumber, signature);
        var logger = new AfAuditSupport.AuditLogger();
        var transport = new AfAuditSupport.Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(ChallengeUrl, request.RequestUri);
            return AfAuditSupport.Json(challenge);
        });
        using var config = AfAuditSupport.Config(transport, logger: logger);

        var encoded = await CreatePayload(config);
        using var payload = JsonDocument.Parse(Convert.FromBase64String(encoded));
        var root = payload.RootElement;

        Assert.Equal(5, root.EnumerateObject().Count());
        Assert.Equal(algorithm, root.GetProperty("algorithm").GetString());
        Assert.Equal(salt, root.GetProperty("salt").GetString());
        Assert.Equal(signature, root.GetProperty("signature").GetString());
        Assert.Equal(number, root.GetProperty("number").GetInt32());
        Assert.Equal(Hash(algorithm, salt, number), root.GetProperty("challenge").GetString());
        Assert.Equal(1, transport.Calls);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task MissingMaximumUsesV1Default()
    {
        var challenge = Challenge("SHA-256", "salt-", 42, 42);
        challenge.Remove("maxnumber");
        using var config = Config(challenge);

        using var payload = JsonDocument.Parse(Convert.FromBase64String(await CreatePayload(config)));

        Assert.Equal(42, payload.RootElement.GetProperty("number").GetInt32());
    }

    [Theory]
    [InlineData("maxnumber", -1)]
    [InlineData("maxnumber", 1_000_001)]
    [InlineData("maxnumber", 2_147_483_648L)]
    [InlineData("maxnumber", "100")]
    [InlineData("maxnumber", null)]
    [InlineData("challenge", "private-not-hex")]
    [InlineData("challenge", "0011")]
    [InlineData("salt", 123)]
    [InlineData("salt", "")]
    [InlineData("signature", null)]
    [InlineData("signature", "")]
    public async Task RejectsInvalidFieldsWithoutEchoingResponse(string field, object value)
    {
        var challenge = Challenge("SHA-256", "private-salt", 0, 0);
        challenge[field] = value;
        using var config = Config(challenge);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreatePayload(config));

        Assert.Contains("ALTCHA", error.Message);
        Assert.DoesNotContain("private-", error.ToString());
    }

    [Theory]
    [InlineData("algorithm")]
    [InlineData("challenge")]
    [InlineData("salt")]
    [InlineData("signature")]
    public async Task RejectsMissingRequiredFields(string field)
    {
        var challenge = Challenge("SHA-256", "private-salt", 0, 0);
        challenge.Remove(field);
        using var config = Config(challenge);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreatePayload(config));
    }

    [Theory]
    [InlineData("salt", 1025)]
    [InlineData("signature", 2049)]
    public async Task RejectsOversizedFields(string field, int length)
    {
        var challenge = Challenge("SHA-256", "salt", 0, 0);
        challenge[field] = new string('x', length);
        using var config = Config(challenge);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreatePayload(config));
    }

    [Fact]
    public async Task RejectsUnsupportedAlgorithmWithoutTryingAnotherVersion()
    {
        var challenge = Challenge("SHA-256", "private-salt", 0, 0);
        challenge["algorithm"] = "private-unknown";
        using var config = Config(challenge);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreatePayload(config));

        Assert.Contains("неподдерживаемый алгоритм", error.Message);
        Assert.DoesNotContain("private-", error.ToString());
    }

    [Theory]
    [InlineData("{private-malformed-json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    public async Task MalformedResponseDoesNotExposeJsonExceptionOrBody(string body)
    {
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreatePayload(config));

        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-", error.ToString());
    }

    [Fact]
    public async Task DoesNotSearchPastMaximum()
    {
        using var config = Config(Challenge("SHA-256", "private-salt", 3, 2));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreatePayload(config));

        Assert.Contains("Не удалось решить", error.Message);
        Assert.DoesNotContain("private-", error.ToString());
    }

    [Fact]
    public async Task PreservesHttpFailureStatusWithoutParsingOrEchoingBody()
    {
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("private-error-body")
        }));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => CreatePayload(config));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.DoesNotContain("private-", error.ToString());
    }

    [Fact]
    public async Task StopsUnadvertisedBodyAtSizeLimitInsteadOfBufferingItAll()
    {
        var stream = new RecordingStream(new byte[100_000]);
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamingContent(stream)
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreatePayload(config));

        Assert.Equal(16 * 1024 + 1, stream.TotalRead);
    }

    [Fact]
    public async Task RejectsAdvertisedOversizedBodyBeforeReading()
    {
        var stream = new RecordingStream(new byte[100_000]);
        var content = new StreamingContent(stream);
        content.Headers.ContentLength = 100_000;
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreatePayload(config));

        Assert.Equal(0, stream.TotalRead);
    }

    [Fact]
    public async Task AlreadyCancelledDoesNotSendRequest()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new AfAuditSupport.Handler(_ => AfAuditSupport.Json(Challenge("SHA-256", "salt", 0, 0)));
        using var config = AfAuditSupport.Config(transport);
        config.CancellationToken = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreatePayload(config));

        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task CancellationDuringReadDoesNotProceedToSolver()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new RecordingStream(JsonSerializer.SerializeToUtf8Bytes(Challenge("SHA-256", "salt", 0, 0)), cancellation.Cancel);
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamingContent(stream)
        }));
        config.CancellationToken = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreatePayload(config));
    }

    [Fact]
    public async Task BodyWithoutEofTimesOutAfterHeadersInsteadOfWaitingForever()
    {
        var stream = new WaitingStream();
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamingContent(stream)
        }));
        config.Client.Timeout = TimeSpan.FromMilliseconds(500);

        var error = await Assert.ThrowsAsync<TimeoutException>(() => CreatePayload(config).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(stream.ReadStarted);
        Assert.Contains("ALTCHA", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task UserCancellationWhileWaitingForBodyIsNotConvertedToTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new WaitingStream(cancellation.Cancel);
        using var config = AfAuditSupport.Config(new AfAuditSupport.Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamingContent(stream)
        }));
        config.CancellationToken = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreatePayload(config));

        Assert.True(stream.ReadStarted);
    }

    [Theory]
    [InlineData("http://auth.lib.social/altcha/challenge")]
    [InlineData("https://auth.lib.social:444/altcha/challenge")]
    [InlineData("https://auth.lib.social.evil.test/altcha/challenge")]
    [InlineData("https://private-user@auth.lib.social/altcha/challenge")]
    [InlineData("/altcha/challenge")]
    public async Task RejectsUntrustedChallengeAddressBeforeRequest(string address)
    {
        var transport = new AfAuditSupport.Handler(_ => throw new InvalidOperationException("Не должно быть запроса"));
        using var config = AfAuditSupport.Config(transport);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreatePayload(config, new Uri(address, UriKind.RelativeOrAbsolute)));

        Assert.Equal(0, transport.Calls);
        Assert.DoesNotContain("private-", error.ToString());
    }

    private static Task<string> CreatePayload(BookGetterConfig config, Uri challengeUrl = null) =>
        (Task<string>)CreatePayloadMethod.Invoke(null, [config, challengeUrl ?? ChallengeUrl])!;

    private static BookGetterConfig Config(object challenge) =>
        AfAuditSupport.Config(new AfAuditSupport.Handler(_ => AfAuditSupport.Json(challenge)));

    private static Dictionary<string, object> Challenge(string algorithm, string salt, int number, int maxNumber,
        string signature = "private-signature") => new()
    {
        ["algorithm"] = algorithm,
        ["challenge"] = Hash(algorithm, salt, number),
        ["salt"] = salt,
        ["signature"] = signature,
        ["maxnumber"] = maxNumber
    };

    private static string Hash(string algorithm, string salt, int number)
    {
        var input = Encoding.UTF8.GetBytes(salt + number.ToString(CultureInfo.InvariantCulture));
        var hash = algorithm switch
        {
            "SHA-1" => SHA1.HashData(input),
            "SHA-256" => SHA256.HashData(input),
            "SHA-512" => SHA512.HashData(input),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class StreamingContent(Stream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult(stream);
        protected override Task SerializeToStreamAsync(Stream target, TransportContext context) =>
            throw new InvalidOperationException("Ответ не должен буферизоваться перед проверкой размера.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                stream.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingStream(byte[] bytes, Action onRead = null) : MemoryStream(bytes, writable: false)
    {
        public int TotalRead { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            TotalRead += read;
            onRead?.Invoke();
            return read;
        }
    }

    private sealed class WaitingStream(Action onRead = null) : MemoryStream
    {
        public bool ReadStarted { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted = true;
            onRead?.Invoke();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
