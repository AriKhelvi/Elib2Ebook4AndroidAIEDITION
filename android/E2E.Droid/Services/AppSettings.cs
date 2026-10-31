// ============================================================================
// E2E для Android — настройки приложения (хранятся в Android Preferences)
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

namespace E2E.Droid.Services;

/// <summary>Настройки приложения (Preferences). Значения по умолчанию — как у консольной версии.</summary>
public sealed class AppSettings
{
    public const string DefaultBookNamePattern = "{Author.Name} - {Book.Title}";

    /// <summary>Android SharedPreferences через MAUI Essentials: простые значения, переживают перезапуск.</summary>
    private static IPreferences P => Preferences.Default;

    /// <summary>Любая настройка изменилась (интерфейс может перерисоваться).</summary>
    public event Action Changed;

    /// <summary>Последние выбранные форматы (--format): epub, fb2, cbz, txt. Запоминаются автоматически.</summary>
    public IReadOnlyList<string> Formats
    {
        get => Split(P.Get(nameof(Formats), "epub"));
        set => Set(nameof(Formats), string.Join(',', value));
    }

    /// <summary>
    /// Текстовый формат по умолчанию (epub, fb2, txt) — для книг и ранобэ. Манга и комиксы распознаются по сайту
    /// и получают CBZ (см. FormatSupport.ForUrl). Если настройку ещё не задавали — берётся прежний выбор
    /// с главного экрана (ключ Formats), когда он был текстовым.
    /// </summary>
    public string TextFormat
    {
        get
        {
            var value = P.Get(nameof(TextFormat), string.Empty);
            if (FormatSupport.TextFormats.Contains(value))
            {
                return value;
            }

            return Formats.FirstOrDefault(FormatSupport.TextFormats.Contains) ?? "epub";
        }
        set => Set(nameof(TextFormat), FormatSupport.TextFormats.Contains(value) ? value : "epub");
    }

    /// <summary>Экспериментально: распознавать сайты с мангой и комиксами и выбирать для них CBZ.</summary>
    public bool AutoFormat
    {
        get => P.Get(nameof(AutoFormat), true);
        set => Set(nameof(AutoFormat), value);
    }

    /// <summary>Таймаут одного запроса в секундах (--timeout, по умолчанию 120).</summary>
    public int Timeout
    {
        get => P.Get(nameof(Timeout), 120);
        set => Set(nameof(Timeout), Math.Clamp(value, 5, 3600));
    }

    /// <summary>Пауза между запросами в секундах (--delay) — для сайтов, которые не любят частые запросы.</summary>
    public int Delay
    {
        get => P.Get(nameof(Delay), 0);
        set => Set(nameof(Delay), Math.Clamp(value, 0, 600));
    }

    /// <summary>Прокси (--proxy): http://, https://, socks4:// или socks5://host:port. Пусто — без прокси.</summary>
    public string Proxy
    {
        get => P.Get(nameof(Proxy), string.Empty);
        set => Set(nameof(Proxy), value?.Trim() ?? string.Empty);
    }

    /// <summary>
    /// «Не проверять сертификаты сайтов» — как ядро на ПК. По умолчанию выключено: сертификаты проверяются
    /// (см. CertificatePolicy). Запасной выход для сайтов с неправильным сертификатом.
    /// </summary>
    public bool AllowInsecureTls
    {
        get => P.Get(nameof(AllowInsecureTls), false);
        set => Set(nameof(AllowInsecureTls), value);
    }

    /// <summary>
    /// «Подробный журнал» (Настройки → Отладка): в журнал скачивания добавляются проверки сертификатов, время этапов,
    /// параметры ядра (без пароля) и сохранённые файлы; журнал хранит больше строк.
    /// </summary>
    public bool DebugLog
    {
        get => P.Get(nameof(DebugLog), false);
        set => Set(nameof(DebugLog), value);
    }

    /// <summary>Книга для пробного скачивания в диагностике (качаются 2 главы); пусто — DiagnosticsRunner.DefaultTestUrl.</summary>
    public string DebugTestUrl
    {
        get => P.Get(nameof(DebugTestUrl), string.Empty);
        set => Set(nameof(DebugTestUrl), value?.Trim() ?? string.Empty);
    }

    /// <summary>Шаблон имени файла (--book-name-pattern), например «{Author.Name} - {Book.Title}».</summary>
    public string BookNamePattern
    {
        get => P.Get(nameof(BookNamePattern), DefaultBookNamePattern);
        set => Set(nameof(BookNamePattern), string.IsNullOrWhiteSpace(value) ? DefaultBookNamePattern : value.Trim());
    }

    /// <summary>Картинки в книге (обложка и иллюстрации). Выключено — как --no-image консольной версии.</summary>
    public bool Images
    {
        get => P.Get(nameof(Images), true);
        set => Set(nameof(Images), value);
    }

    /// <summary>Сохранять обложку отдельным файлом рядом с книгой (--cover).</summary>
    public bool Cover
    {
        get => P.Get(nameof(Cover), false);
        set => Set(nameof(Cover), value);
    }

    /// <summary>Сохранять дополнительные файлы со страницы книги (--additional).</summary>
    public bool Additional
    {
        get => P.Get(nameof(Additional), false);
        set => Set(nameof(Additional), value);
    }

    /// <summary>books, audio, images; пусто — все типы.</summary>
    public IReadOnlyList<string> AdditionalTypes
    {
        get => Split(P.Get(nameof(AdditionalTypes), string.Empty));
        set => Set(nameof(AdditionalTypes), string.Join(',', value));
    }

    /// <summary>Начинать скачивание сразу, как только ссылка пришла через «Поделиться».</summary>
    /// <remarks>
    /// В интерфейсе — «Пропустить диалог скачивания» (Да/Нет). Ключ хранения прежний, чтобы выбор не сбросился.
    /// </remarks>
    public bool AutoStartOnShare
    {
        get => P.Get(nameof(AutoStartOnShare), false);
        set => Set(nameof(AutoStartOnShare), value);
    }

    /// <summary>content://-URI папки, выбранной через системный диалог; пусто — Загрузки/Elib2Ebook.</summary>
    public string SaveFolderUri
    {
        get => P.Get(nameof(SaveFolderUri), string.Empty);
        set => Set(nameof(SaveFolderUri), value ?? string.Empty);
    }

    public string SaveFolderName
    {
        get => P.Get(nameof(SaveFolderName), string.Empty);
        set => Set(nameof(SaveFolderName), value ?? string.Empty);
    }

    private void Set<T>(string key, T value)
    {
        P.Set(key, value);
        Changed?.Invoke();
    }

    private static IReadOnlyList<string> Split(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
