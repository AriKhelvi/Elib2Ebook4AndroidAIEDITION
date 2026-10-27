// ============================================================================
// E2E для Android — интерфейсы платформенных функций (реализации — в Platforms/Android)
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

namespace E2E.Droid.Services;

/// <summary>Работа с общим хранилищем Android: сохранение, открытие, «Поделиться», удаление.</summary>
public interface IBookStorage
{
    /// <summary>Куда сейчас сохраняются книги — для подписи в интерфейсе.</summary>
    string DestinationLabel { get; }

    /// <summary>Копирует дерево файлов из рабочей папки задания в папку сохранения.</summary>
    Task<IReadOnlyList<SavedFile>> SaveTreeAsync(string sourceDir, Func<SavedFile, SavedFile> decorate);

    /// <summary>
    /// Разрешение на запись, если оно нужно (Android 7–9 пишут в «Загрузки» по пути). false — пользователь отказал.
    /// </summary>
    Task<bool> EnsureWriteAccessAsync();

    /// <summary>Системный выбор папки (SAF). false — пользователь отменил.</summary>
    /// <exception cref="InvalidOperationException">На устройстве нет системного выбора папки.</exception>
    Task<bool> PickFolderAsync();

    void ResetFolder();

    bool Exists(SavedFile file);

    /// <summary>Удаляет файл из хранилища. false — файл уже недоступен (например, после переустановки).</summary>
    bool Delete(SavedFile file);

    /// <summary>Открывает файл в приложении-читалке. false — подходящего приложения нет.</summary>
    bool Open(SavedFile file);

    void Share(IReadOnlyCollection<SavedFile> files);
}

/// <summary>Системные функции: уведомления, фоновая работа, системные панели.</summary>
public interface IPlatformServices
{
    Task RequestNotificationPermissionAsync();

    void StartBackgroundWork(string text);

    void UpdateBackgroundWork(string text);

    void StopBackgroundWork();

    void NotifyFinished(string title, string text);

    /// <summary>Подстраивает цвет фона под системными панелями и цвет их значков под тему интерфейса.</summary>
    void ApplyTheme(bool dark, string background);

    bool IsInForeground { get; }

    /// <summary>Есть ли у системы палитра из обоев (Material You, Android 12+).</summary>
    bool SupportsDynamicColor { get; }

    /// <summary>Палитра Material You для светлой или тёмной темы: CSS-переменная → #RRGGBB; null — палитры нет.</summary>
    IReadOnlyDictionary<string, string> GetDynamicColorScheme(bool dark);

    /// <summary>
    /// Уменьшенная копия обложки для вкладки «Книги» (JPEG ~240 px по высоте) в личной папке приложения.
    /// </summary>
    /// <returns>Путь к файлу или null, если картинку не удалось прочитать.</returns>
    string SaveThumbnail(byte[] image, string name);

    /// <summary>Версия приложения (versionName): 0.2.0 или 0.2.1-dev.5.</summary>
    string AppVersion { get; }

    /// <summary>Номер сборки (versionCode).</summary>
    string BuildNumber { get; }
}
