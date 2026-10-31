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
/// <item>Цепочка, если система её не построила: сначала ещё раз системой Android, добавив «мостовые» сертификаты
/// Let's Encrypt (Resources/Certificates/trusted-bridges.pem) — сайты, не присылающие Root YE/YR, тогда доходят до
/// ISRG Root X1; затем по встроенному набору публичных корней (trusted-roots.pem) — для старых Android и читалок,
/// где нет нужного корня (в Android 7.0 нет ISRG Root X1).</item>
/// </list>
/// Всё остальное — просроченный, самоподписанный, выданный неизвестным центром или на другой сайт — отказ.
/// Не зависит от MAUI: проверяется на ПК (android/CoreSmoke).
/// </remarks>
public static class CertificatePolicy
{
    /// <summary>Имя встроенного набора корней (EmbeddedResource в E2E.Droid.csproj и CoreSmoke.csproj).</summary>
    public const string RootsResource = "E2E.Droid.trusted-roots.pem";

    /// <summary>Имя набора «мостовых» сертификатов Let's Encrypt (EmbeddedResource там же).</summary>
    public const string BridgesResource = "E2E.Droid.trusted-bridges.pem";

    /// <summary>Назначение «TLS-сервер» (serverAuth): корень и промежуточные должны разрешать именно его.</summary>
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    private static readonly Lazy<X509Certificate2Collection> BundledRoots = new(() => LoadPem(RootsResource));

    private static readonly Lazy<X509Certificate2Collection> BundledBridges = new(() => LoadPem(BridgesResource));

    private static volatile string _lastRejection;

    /// <summary>Включено ли «Не проверять сертификаты». Задаётся при запуске приложения (MauiProgram).</summary>
    public static Func<bool> AllowInsecure { get; set; } = () => false;

    /// <summary>Последний отказ — «сайт: причина» — для понятного сообщения об ошибке скачивания.</summary>
    public static string LastRejection => _lastRejection;

    /// <summary>Встроенные корни (для самопроверки в CoreSmoke).</summary>
    public static IReadOnlyList<X509Certificate2> Roots => BundledRoots.Value.ToList();

    /// <summary>Встроенные «мостовые» сертификаты (для самопроверки и диагностики).</summary>
    public static IReadOnlyList<X509Certificate2> Bridges => BundledBridges.Value.ToList();

    /// <summary>
    /// Каждая проверка сертификата одной строкой: сайт, что сказала система, решение, цепочка. Для подробного журнала
    /// (Настройки → Отладка) и диагностики. Вызывается из потоков HttpClient; без подписчиков строка не собирается.
    /// </summary>
    public static event Action<string> Traced;

    /// <summary>Колбэк для BookGetterConfig.ServerCertificateValidator.</summary>
    public static bool Validate(HttpRequestMessage request, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors errors)
    {
        var host = ExpectedHost(request);
        string reason = null;
        bool accepted;
        if (errors == SslPolicyErrors.None)
        {
            accepted = true;
        }
        else if (AllowInsecure())
        {
            accepted = true;
            reason = "принят без проверки: включено «Не проверять сертификаты»";
        }
        else
        {
            reason = Check(host, certificate, chain, errors, out var how);
            accepted = reason == null;
            if (accepted)
            {
                reason = how;
            }
            else
            {
                _lastRejection = $"{host}: {reason}";
            }
        }

        var traced = Traced;
        if (traced != null)
        {
            var address = request?.RequestUri?.IdnHost;
            traced($"{host}{(address != null && address != host ? $" (соединение с {address})" : "")}: " +
                   $"система — {DescribeErrors(errors, chain)}; {(accepted ? "принят" : "ОТКАЗ")}" +
                   $"{(reason != null ? $" ({reason})" : "")}; цепочка: {DescribeChain(certificate, chain)}");
        }

        return accepted;
    }

    /// <summary>Разбор ошибок системной проверки.</summary>
    /// <param name="host">Сайт, на который должен быть выдан сертификат.</param>
    /// <returns>null — сертификат подходит; иначе — причина отказа.</returns>
    public static string Check(string host, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors errors)
        => Check(host, certificate, chain, errors, out _);

    /// <param name="how">Если принят несмотря на ошибки системы — каким путём (для журнала); иначе null.</param>
    public static string Check(string host, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors errors, out string how)
    {
        how = null;
        if (certificate == null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return "сайт не прислал сертификат";
        }

        // Система сверяет имя с адресом соединения; при обходе по IP настоящее имя — в заголовке Host
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            if (string.IsNullOrEmpty(host) || !certificate.MatchesHostname(host))
            {
                return $"сертификат выдан на другой сайт ({certificate.GetNameInfo(X509NameType.DnsName, false)})";
            }

            how = "имя сверено с заголовком Host";
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            var sent = SentCertificates(certificate, chain);
            if (TryChainWithSystemAndBridges(certificate, sent) == null)
            {
                how = "цепочка достроена мостовыми сертификатами Let's Encrypt";
                return null;
            }

            var reason = TryChainWithBundledRoots(certificate, sent);
            if (reason != null)
            {
                return reason;
            }

            how = "цепочка достроена встроенными корнями";
        }

        return null;
    }

    /// <summary>Что сказала системная проверка: «без ошибок» или флаги и статусы цепочки.</summary>
    public static string DescribeErrors(SslPolicyErrors errors, X509Chain chain)
    {
        if (errors == SslPolicyErrors.None)
        {
            return "без ошибок";
        }

        var statuses = chain?.ChainStatus.Select(s => s.Status.ToString()).Distinct().ToList() ?? [];
        return statuses.Count == 0 ? errors.ToString() : $"{errors} [{string.Join(", ", statuses)}]";
    }

    /// <summary>Цепочка, как её построила система: «сайт ← промежуточный ← корень», плюс сколько сертификатов прислал сайт.</summary>
    public static string DescribeChain(X509Certificate2 certificate, X509Chain chain)
    {
        if (certificate == null)
        {
            return "нет сертификата";
        }

        IEnumerable<string> names = chain?.ChainElements.Count > 0
            ? chain.ChainElements.Cast<X509ChainElement>().Select(e => e.Certificate.GetNameInfo(X509NameType.SimpleName, false))
            : [certificate.GetNameInfo(X509NameType.SimpleName, false)];
        var sent = chain?.ChainPolicy.ExtraStore.Count ?? 0;
        return $"{string.Join(" ← ", names)} (сайт прислал {sent}, действует до {certificate.NotAfter:yyyy-MM-dd})";
    }

    /// <summary>Имя сайта, к которому запрос: заголовок Host (без порта), иначе адрес запроса.</summary>
    public static string ExpectedHost(HttpRequestMessage request)
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
    /// Ещё раз системой Android (её корни), добавив к присланному сайтом «мостовые» сертификаты Let's Encrypt.
    /// Так проверяет и браузер, когда сам докачивает недостающее звено. Доверие — только к корням системы.
    /// </summary>
    /// <param name="sent">Промежуточные, которые прислал сайт (и которые система уже нашла).</param>
    /// <returns>null — цепочка доверенная; иначе — причина отказа.</returns>
    public static string TryChainWithSystemAndBridges(X509Certificate2 certificate, X509Certificate2Collection sent)
    {
        try
        {
            using var chain = new X509Chain();
            var policy = chain.ChainPolicy;
            // Как у системной проверки TLS в .NET (CheckCertificateRevocationList = false): без запросов к CRL/OCSP.
            // И без докачки звеньев по AIA: никакой сети внутри рукопожатия, а Android её всё равно не делает
            policy.RevocationMode = X509RevocationMode.NoCheck;
            policy.DisableCertificateDownloads = true;
            policy.ApplicationPolicy.Add(new Oid(ServerAuthOid));
            policy.ExtraStore.AddRange(sent);
            policy.ExtraStore.AddRange(BundledBridges.Value);
            return chain.Build(certificate) ? null : Describe(chain.ChainStatus);
        }
        catch (Exception ex)
        {
            return $"не удалось проверить цепочку сертификата ({ex.Message})";
        }
    }

    /// <summary>
    /// Только по встроенным корням (старые Android без нужного корня). Промежуточные — присланные сайтом и мосты.
    /// На Android .NET не умеет строить цепочку с ПУСТЫМ набором своих корней — у нас он не пустой.
    /// </summary>
    /// <returns>null — цепочка доверенная; иначе — причина отказа.</returns>
    public static string TryChainWithBundledRoots(X509Certificate2 certificate, X509Certificate2Collection sent)
    {
        try
        {
            using var chain = new X509Chain();
            var policy = chain.ChainPolicy;
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.AddRange(BundledRoots.Value);
            policy.RevocationMode = X509RevocationMode.NoCheck;
            policy.DisableCertificateDownloads = true;
            policy.ApplicationPolicy.Add(new Oid(ServerAuthOid));
            policy.ExtraStore.AddRange(sent);
            policy.ExtraStore.AddRange(BundledBridges.Value);
            return chain.Build(certificate) ? null : Describe(chain.ChainStatus);
        }
        catch (Exception ex)
        {
            // Например, платформа не умеет свои корни доверия (PlatformNotSupportedException) — значит, отказ
            return $"не удалось проверить цепочку сертификата ({ex.Message})";
        }
    }

    /// <summary>Что прислал сайт (ExtraStore системной цепочки) и что система уже нашла — кроме самого сертификата сайта.</summary>
    private static X509Certificate2Collection SentCertificates(X509Certificate2 certificate, X509Chain systemChain)
    {
        var result = new X509Certificate2Collection();
        if (systemChain == null)
        {
            return result;
        }

        result.AddRange(systemChain.ChainPolicy.ExtraStore);
        foreach (var element in systemChain.ChainElements)
        {
            if (!element.Certificate.RawDataMemory.Span.SequenceEqual(certificate.RawDataMemory.Span))
            {
                result.Add(element.Certificate);
            }
        }

        return result;
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

    private static X509Certificate2Collection LoadPem(string resource)
    {
        var certificates = new X509Certificate2Collection();
        using var stream = typeof(CertificatePolicy).Assembly.GetManifestResourceStream(resource);
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            // Комментарии между блоками PEM ImportFromPem пропускает
            certificates.ImportFromPem(reader.ReadToEnd());
        }

        return certificates;
    }
}
