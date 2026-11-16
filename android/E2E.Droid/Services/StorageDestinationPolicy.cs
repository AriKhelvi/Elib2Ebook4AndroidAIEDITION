namespace E2E.Droid.Services;

/// <summary>Выбранное пользователем назначение нельзя молча заменять папкой по умолчанию.</summary>
internal static class StorageDestinationPolicy
{
    /// <returns>Исходный URI выбранной папки; null — пользователь вообще не выбирал свою папку.</returns>
    public static string ResolveForWrite(string selectedUri, string destinationLabel, Func<string, bool> hasWritePermission)
    {
        if (string.IsNullOrEmpty(selectedUri)) return null;

        if (Uri.TryCreate(selectedUri, UriKind.Absolute, out var uri) &&
            uri.Scheme == "content" && !string.IsNullOrEmpty(uri.Host) && hasWritePermission(selectedUri))
            return selectedUri;

        var label = string.IsNullOrWhiteSpace(destinationLabel) ? "выбранной папке" : $"папке «{destinationLabel}»";
        throw new InvalidOperationException(
            $"Нет сохранённого доступа к {label}. Выберите папку заново в настройках или явно нажмите «В Загрузки». " +
            "Место сохранения не изменено.");
    }

    /// <summary>Общий grant сохраняется, пока его использует хотя бы одно назначение.</summary>
    public static bool ShouldReleasePermission(string previousUri, string nextUri, string otherDestinationUri)
        => !string.IsNullOrEmpty(previousUri) &&
           !string.Equals(previousUri, nextUri, StringComparison.Ordinal) &&
           !string.Equals(previousUri, otherDestinationUri, StringComparison.Ordinal);
}
