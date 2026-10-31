// ============================================================================
// E2E для Android — проверка сертификатов сайтов (TLS) для всех запросов ядра
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace E2E.Droid.Services;

/// <summary>
/// Политика проверки сертификатов для ядра (подключается в BookGetterConfig.ServerCertificateValidator).
/// Само ядро принимает любой сертификат — на телефоне это значит, что в чужой Wi-Fi или через недобросовестный
/// прокси можно незаметно перехватить пароль от сайта, cookies и подменить скачиваемое.
/// </summary>
/// <remarks>
/// По порядку:
/// <list type="number">
/// <item>Системная проверка прошла — принимаем.</item>
/// <item>Включено «Не проверять сертификаты» (настройки → Сеть) — принимаем, как ядро на ПК.</item>
/// <item>Имя: сертификат должен быть выдан на сайт, к которому запрос. Если ядро ходит по IP в обход блокировок
/// (author.today, readli) и передаёт настоящее имя в заголовке Host — сверяем с ним.</item>
/// <item>Цепочка: если система не знает корня (старые Android и читалки — в Android 7.0 нет ISRG Root X1 от
/// Let's Encrypt), достраиваем её по встроенному набору публичных корней (Resources/Certificates/trusted-roots.pem).</item>
/// </list>
/// Всё остальное — просроченный, самоподписанный, выданный неизвестным центром или на другой сайт — отказ.
/// Не зависит от MAUI: проверяется на ПК (android/CoreSmoke).
/// </remarks>
public static class CertificatePolicy
{
    /// <summary>Имя встроенного набора корней (EmbeddedResource в E2E.Droid.csproj и CoreSmoke.csproj).</summary>
    public const string RootsResource = "E2E.Droid.trusted-roots.pem";

    /// <summary>Назначение «TLS-сервер» (serverAuth): корень и промежуточные должны разрешать именно его.</summary>
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    private static readonly Lazy<X509Certificate2Collection> BundledRoots = new(LoadBundledRoots);

    private static volatile string _lastRejection;

    /// <summary>Включено ли «Не проверять сертификаты». Задаётся при запуске приложения (MauiProgram).</summary>
    public static Func<bool> AllowInsecure { get; set; } = () => false;

    /// <summary>Последний отказ — «сайт: причина» — для понятного сообщения об ошибке скачивания.</summary>
    public static string LastRejection => _lastRejection;

    /// <summary>Встроенные корни (для самопроверки в CoreSmoke).</summary>
    public static IReadOnlyList<X509Certificate2> Roots => BundledRoots.Value.ToList();

    /// <summary>Колбэк для BookGetterConfig.ServerCertificateValidator.</summary>
    public static bool Validate(HttpRequestMessage request, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None || AllowInsecure())
        {
            return true;
        }

        var host = ExpectedHost(request);
        var reason = Check(host, certificate, chain, errors);
        if (reason == null)
        {
            return true;
        }

        _lastRejection = $"{host}: {reason}";
        return false;
    }

    /// <summary>Разбор ошибок системной проверки.</summary>
    /// <param name="host">Сайт, на который должен быть выдан сертификат.</param>
    /// <returns>null — сертификат подходит; иначе — причина отказа.</returns>
    public static string Check(string host, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors errors)
    {
        if (certificate == null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return "сайт не прислал сертификат";
        }

        // Система сверяет имя с адресом соединения; при обходе по IP настоящее имя — в заголовке Host
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
            && (string.IsNullOrEmpty(host) || !certificate.MatchesHostname(host)))
        {
            return $"сертификат выдан на другой сайт ({certificate.GetNameInfo(X509NameType.DnsName, false)})";
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            return CheckChainWithBundledRoots(certificate, chain);
        }

        return null;
    }

    /// <summary>Имя сайта, к которому запрос: заголовок Host (без порта), иначе адрес запроса.</summary>
    private static string ExpectedHost(HttpRequestMessage request)
    {
        var header = request?.Headers.Host;
        if (string.IsNullOrEmpty(header))
        {
            return request?.RequestUri?.IdnHost;
        }

        // [::1]:443 → ::1; example.com:8443 → example.com
        if (header.StartsWith('['))
        {
            var end = header.IndexOf(']');
            return end > 0 ? header[1..end] : header;
        }

        var colon = header.IndexOf(':');
        return colon > 0 && colon == header.LastIndexOf(':') ? header[..colon] : header;
    }

    /// <summary>
    /// Система не смогла построить доверенную цепочку — пробуем со встроенными корнями. Промежуточные сертификаты
    /// берём из того, что прислал сайт (ExtraStore системной цепочки) и что система уже нашла.
    /// </summary>
    /// <returns>null — цепочка доверенная; иначе — причина отказа.</returns>
    private static string CheckChainWithBundledRoots(X509Certificate2 certificate, X509Chain systemChain)
    {
        try
        {
            using var chain = new X509Chain();
            var policy = chain.ChainPolicy;
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.AddRange(BundledRoots.Value);
            // Как у системной проверки TLS в .NET (CheckCertificateRevocationList = false): без запросов к CRL/OCSP
            policy.RevocationMode = X509RevocationMode.NoCheck;
            policy.ApplicationPolicy.Add(new Oid(ServerAuthOid));

            if (systemChain != null)
            {
                policy.ExtraStore.AddRange(systemChain.ChainPolicy.ExtraStore);
                foreach (var element in systemChain.ChainElements)
                {
                    if (!element.Certificate.RawDataMemory.Span.SequenceEqual(certificate.RawDataMemory.Span))
                    {
                        policy.ExtraStore.Add(element.Certificate);
                    }
                }
            }

            return chain.Build(certificate) ? null : Describe(chain.ChainStatus);
        }
        catch (Exception ex)
        {
            // Например, на платформе нет своих корней доверия (PlatformNotSupportedException) — значит, отказ
            return $"не удалось проверить цепочку сертификата ({ex.Message})";
        }
    }

    private static string Describe(X509ChainStatus[] statuses)
    {
        var flags = statuses.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
        if (flags.HasFlag(X509ChainStatusFlags.NotTimeValid))
        {
            return "сертификат просрочен или ещё не действует (проверьте дату и время на устройстве)";
        }

        if (flags.HasFlag(X509ChainStatusFlags.Revoked))
        {
            return "сертификат отозван";
        }

        if (flags.HasFlag(X509ChainStatusFlags.NotSignatureValid))
        {
            return "подпись сертификата неверна";
        }

        if (flags.HasFlag(X509ChainStatusFlags.NotValidForUsage))
        {
            return "сертификат не предназначен для сайтов";
        }

        if (flags.HasFlag(X509ChainStatusFlags.UntrustedRoot) || flags.HasFlag(X509ChainStatusFlags.PartialChain))
        {
            return "сертификат выдан неизвестным центром сертификации — возможна подмена в сети";
        }

        return $"цепочка сертификата не проверяется ({flags})";
    }

    private static X509Certificate2Collection LoadBundledRoots()
    {
        var roots = new X509Certificate2Collection();
        using var stream = typeof(CertificatePolicy).Assembly.GetManifestResourceStream(RootsResource);
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            // Комментарии между блоками PEM ImportFromPem пропускает
            roots.ImportFromPem(reader.ReadToEnd());
        }

        return roots;
    }
}
