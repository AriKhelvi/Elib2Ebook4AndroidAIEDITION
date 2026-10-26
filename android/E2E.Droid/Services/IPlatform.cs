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

    /// <summary>Системный выбор папки (SAF). false — пользователь отменил.</summary>
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

    /// <summary>Подстраивает цвет фона под системными панелями и цвет их значков под тему веб-интерфейса.</summary>
    void ApplyTheme(bool dark);

    bool IsInForeground { get; }

    string AppVersion { get; }
}
