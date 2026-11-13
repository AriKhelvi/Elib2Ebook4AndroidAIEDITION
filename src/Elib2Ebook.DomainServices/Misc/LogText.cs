using System.Text.RegularExpressions;

namespace Elib2Ebook.DomainServices.Misc;

/// <summary>Убирает данные изображений и реквизиты доступа из журналов и уведомлений.</summary>
public static class LogText
{
    public const int MaxLineLength = 2048;
    public const string Hidden = "[скрыто]";

    private const string SecretNames = @"(?:password|passwd|pwd|pass|secret|client[_-]?secret|api[_-]?key|x-api-key|access[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|auth[_-]?token|x[_-]?(?:csrf|xsrf)[_-]?token|token|sid|session[_-]?(?:id|token|key)|session|credential|signature|sig|md5|login|username|key|auth|csrf(?:[_-]?token)?|cf_clearance|phpsessid|пароль|токен|секрет)";
    private static readonly Regex UserInfo = Pattern(@"(?<scheme>\b[a-z][a-z0-9+.-]*://)[^\s/?#]*@");
    private static readonly Regex TruncatedAuthority = Pattern(@"(?<scheme>\b[a-z][a-z0-9+.-]*://)[^\s/?#]*$");
    private static readonly Regex Header = Pattern(@"(?<prefix>\b(?:proxy-authorization|authorization|set-cookie|cookie)[""']?\s*[:=]\s*)[^\r\n]*(?:\r?\n[ \t]+[^\r\n]*)*");
    private static readonly Regex NamedSecret = Pattern(@"(?<prefix>(?<![\w-])[""']?" + SecretNames + @"[""']?\s*[:=]\s*)(?:""(?:\\.|[^""\\])*(?:""|$)|'(?:\\.|[^'\\])*(?:'|$)|[^,;&}\r\n]+)");
    private static readonly Regex EncodedQuerySecret = Pattern(@"(?<prefix>[?&#](?:amp;)?(?<key>[^=\s&#]+)=)(?<value>[^&#\s""'<>]*)");
    private static readonly Regex StandaloneAuthorization = Pattern(@"\b(?:Bearer|Basic)\s+[a-z0-9+/_.=-]+");
    private static readonly Regex PrivateKey = Pattern(@"-----BEGIN (?:[A-Z]+ )*PRIVATE KEY-----[\s\S]*");
    private static readonly Regex EmptySecret = Pattern(@"(?:\b(?:proxy-authorization|authorization|set-cookie|cookie)|(?<![\w-])[""']?" + SecretNames + @"[""']?)\s*[:=]\s*$");

    private static Regex Pattern(string expression) => new(expression,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        // Сначала ограничиваем вход: даже многомегабайтный data URL не создаст огромную копию.
        var truncated = text.Length > MaxLineLength;
        var prefix = truncated ? text[..MaxLineLength] : text;
        var embedded = prefix.IndexOf("data:", StringComparison.OrdinalIgnoreCase);
        // Base64 допускает пробелы и переносы: убираем весь остаток сообщения.
        if (embedded >= 0) prefix = prefix[..embedded] + "[встроенное изображение]";
        try
        {
            prefix = PrivateKey.Replace(prefix, "[закрытый ключ скрыт]");
            // У очень длинного user:password символ @ мог остаться за границей отрезанного префикса.
            if (truncated) prefix = TruncatedAuthority.Replace(prefix, "${scheme}" + Hidden);
            prefix = UserInfo.Replace(prefix, "${scheme}" + Hidden + "@");
            // Декодируем только имя query-параметра: значение не копируем и не выводим.
            prefix = EncodedQuerySecret.Replace(prefix, match => IsSecretName(match.Groups["key"].Value)
                ? match.Groups["prefix"].Value + Hidden : match.Value);
            prefix = NamedSecret.Replace(prefix, "${prefix}" + Hidden);
            prefix = Header.Replace(prefix, "${prefix}" + Hidden);
            prefix = StandaloneAuthorization.Replace(prefix, Hidden);
        }
        catch (RegexMatchTimeoutException)
        {
            return "[строка скрыта: превышено время безопасной обработки]";
        }
        return truncated || prefix.Length > MaxLineLength ? prefix[..Math.Min(prefix.Length, MaxLineLength - 1)] + "…" : prefix;
    }

    /// <summary>Повторно очищает старые журналы, включая продолжения заголовков и закрытых ключей.</summary>
    public static IEnumerable<string> CleanLines(IEnumerable<string> lines)
    {
        var privateKey = false;
        var continuation = false;
        var nextValue = false;
        foreach (var line in lines)
        {
            var text = line ?? string.Empty;
            if (privateKey || nextValue || continuation && text.Length > 0 && char.IsWhiteSpace(text[0]))
            {
                yield return Hidden;
                nextValue = false;
            }
            else
            {
                yield return Clean(text);
                try
                {
                    continuation = Header.IsMatch(text[..Math.Min(text.Length, MaxLineLength)]);
                    nextValue = EmptySecret.IsMatch(text[..Math.Min(text.Length, MaxLineLength)]);
                }
                catch (RegexMatchTimeoutException) { continuation = nextValue = true; }
            }

            if (text.Contains("-----BEGIN ", StringComparison.Ordinal) && text.Contains("PRIVATE KEY-----", StringComparison.Ordinal)) privateKey = true;
            if (text.Contains("-----END ", StringComparison.Ordinal) && text.Contains("PRIVATE KEY-----", StringComparison.Ordinal)) privateKey = false;
        }
    }

    private static bool IsSecretName(string key)
    {
        key = Uri.UnescapeDataString(key).Replace("_", "").Replace("-", "").ToLowerInvariant();
        return key is "password" or "passwd" or "pwd" or "pass" or "secret" or "clientsecret" or "apikey"
            or "xapikey" or "accesskey" or "accesstoken" or "refreshtoken" or "idtoken" or "token"
            or "sid" or "sessionid" or "session" or "credential" or "signature" or "sig" or "md5"
            or "login" or "username" or "authorization" or "cookie" or "key" or "auth" or "code"
            or "sessiontoken" or "sessionkey" or "csrf" or "csrftoken" or "cfclearance" or "phpsessid"
            or "authtoken" or "xcsrftoken" or "xxsrftoken"
            or "xamzcredential" or "xamzsignature" or "xamzsecuritytoken";
    }
}
