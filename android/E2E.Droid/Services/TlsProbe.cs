// ============================================================================
// E2E для Android — проверка одного адреса для диагностики: DNS, TLS по политике приложения, ответ сервера
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace E2E.Droid.Services;

/// <summary>Что показала проверка адреса (см. <see cref="TlsProbe.ProbeAsync"/>).</summary>
public sealed class TlsProbeResult
{
    /// <summary>Сервер ответил (любой HTTP-статус) и сертификат принят политикой.</summary>
    public bool Ok { get; internal set; }

    /// <summary>Рукопожатие дошло до проверки сертификата.</summary>
    public bool TlsSeen { get; internal set; }

    /// <summary>Сертификат отклонён политикой (а не сеть подвела).</summary>
    public bool TlsRejected { get; internal set; }

    /// <summary>Система цепочку не построила, а встроенные корни — построили.</summary>
    public bool UsedBundledRoots { get; internal set; }

    public SslPolicyErrors SystemErrors { get; internal set; }

    /// <summary>Итог одной строкой: «HTTP 200 …» или причина неудачи.</summary>
    public string Outcome { get; internal set; }

    /// <summary>Сертификат сайта и присланные им промежуточные — для имитации старого Android.</summary>
    public X509Certificate2 Leaf { get; internal set; }

    public X509Certificate2Collection Sent { get; } = [];

    /// <summary>Строки для журнала: итог, DNS, TLS.</summary>
    public List<string> Lines { get; } = [];
}

/// <summary>
/// Проверка адреса так же, как его увидит ядро: HttpClient с прокси из настроек (или без прокси вовсе) и с той же
/// политикой сертификатов (CertificatePolicy), только без «Не проверять сертификаты». Не зависит от MAUI.
/// </summary>
public static class TlsProbe
{
    /// <param name="url">Адрес; для обхода по IP — https://IP/.</param>
    /// <param name="hostHeader">Настоящее имя сайта в заголовке Host (обход по IP) или null.</param>
    /// <param name="proxy">Прокси из настроек (http://, socks5://…) или пусто.</param>
    public static async Task<TlsProbeResult> ProbeAsync(Uri url, string hostHeader, string proxy, TimeSpan timeout, CancellationToken ct)
    {
        var result = new TlsProbeResult();
        var expected = hostHeader ?? url.IdnHost;
        string dns = null, system = null, chainText = null, verdict = null;

        if (!IPAddress.TryParse(url.IdnHost, out _))
        {
            var dnsTime = Stopwatch.StartNew();
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(url.IdnHost, ct);
                dns = $"{string.Join(", ", addresses.Take(3))}{(addresses.Length > 3 ? ", …" : "")} ({dnsTime.ElapsedMilliseconds} мс)";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                dns = $"ошибка ({ex.GetBaseException().Message})";
            }
        }

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            // Как у ядра (BookGetterConfig): без прокси из настроек — вообще без прокси, системный не берётся
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
            {
                result.TlsSeen = true;
                result.SystemErrors = errors;
                system = CertificatePolicy.DescribeErrors(errors, chain);
                chainText = CertificatePolicy.DescribeChain(certificate, chain);
                if (certificate != null)
                {
                    result.Leaf = X509CertificateLoader.LoadCertificate(certificate.RawData);
                }

                if (chain != null)
                {
                    foreach (var sent in chain.ChainPolicy.ExtraStore)
                    {
                        result.Sent.Add(X509CertificateLoader.LoadCertificate(sent.RawData));
                    }
                }

                verdict = errors == SslPolicyErrors.None ? null : CertificatePolicy.Check(expected, certificate, chain, errors);
                return verdict == null;
            }
        };

        if (!string.IsNullOrWhiteSpace(proxy) && Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var proxyUri))
        {
            handler.Proxy = new WebProxy(proxyUri);
            handler.UseProxy = true;
        }

        using var client = new HttpClient(handler) { Timeout = timeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (hostHeader != null)
        {
            request.Headers.Host = hostHeader;
        }

        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Linux; Android) E2E-diagnostics");

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            result.Ok = true;
            result.Outcome = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}, HTTP/{response.Version}, {sw.ElapsedMilliseconds} мс" +
                             (response.Headers.Location != null ? $", перенаправляет на {response.Headers.Location}" : "") +
                             (response.Headers.Server.Count > 0 ? $", сервер {response.Headers.Server}" : "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.TlsRejected = result.TlsSeen && verdict != null;
            result.Outcome = result.TlsRejected
                ? $"ОТКАЗ ПО СЕРТИФИКАТУ: {verdict}"
                : $"не открылся за {sw.ElapsedMilliseconds} мс: {Describe(ex)}";
        }

        result.UsedBundledRoots = result.Ok && result.SystemErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors);
        result.Lines.Add($"[{(result.Ok ? "ok" : "FAIL")}] {url.IdnHost}{(hostHeader != null ? $" (Host: {hostHeader})" : "")} — {result.Outcome}");
        if (dns != null)
        {
            result.Lines.Add($"      DNS: {dns}");
        }

        result.Lines.Add(result.TlsSeen
            ? $"      TLS: система — {system}; " +
              (verdict != null ? $"отклонён: {verdict}"
                  : result.SystemErrors == SslPolicyErrors.None ? "принят"
                  : result.UsedBundledRoots ? "принят: цепочку достроили встроенные корни"
                  : "принят: имя сверено с Host") +
              $"; цепочка: {chainText}"
            : "      TLS: до проверки сертификата не дошло");
        return result;
    }

    /// <summary>
    /// Как на старом Android, где нет ни одного нужного корня: строим цепочку без доверенных корней (система
    /// ответит ошибкой) и отдаём политике. null — политика цепочку достроила встроенными корнями; иначе — причина.
    /// </summary>
    public static (string SystemView, string Verdict) CheckWithoutSystemRoots(X509Certificate2 leaf, X509Certificate2Collection sent, string host)
    {
        using var bare = new X509Chain();
        bare.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        bare.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        bare.ChainPolicy.ExtraStore.AddRange(sent);
        bare.Build(leaf);
        var view = CertificatePolicy.DescribeErrors(SslPolicyErrors.RemoteCertificateChainErrors, bare);
        return (view, CertificatePolicy.Check(host, leaf, bare, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    /// <summary>Подставной (самоподписанный) сертификат на имя сайта. null — политика его ПРИНЯЛА (так быть не должно).</summary>
    public static string CheckForgedRejected(string host)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        using var forged = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(forged);
        return CertificatePolicy.Check(host, forged, chain, SslPolicyErrors.RemoteCertificateChainErrors);
    }

    /// <summary>Исключение со всеми вложенными: тип и сообщение каждого.</summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null && parts.Count < 5; e = e.InnerException)
        {
            parts.Add($"{e.GetType().Name}: {e.Message}");
        }

        return string.Join(" → ", parts);
    }
}
