namespace E2E.Droid.Services;

/// <summary>Подпись выбранной SAF-папки; не преобразует URI в путь для доступа к файлам.</summary>
public static class StorageFolderLabel
{
    public static string Format(string authority, string decodedDocumentId, string displayName, string volumeDescription = null)
    {
        var fallback = string.IsNullOrWhiteSpace(displayName) ? "Выбранная папка" : displayName;
        // Только системный провайдер внешнего хранилища использует известный формат «том:путь».
        // У облачных и других провайдеров ID непрозрачен, даже если тоже содержит двоеточие.
        if (!string.Equals(authority, "com.android.externalstorage.documents", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(decodedDocumentId))
            return fallback;

        var separator = decodedDocumentId.IndexOf(':');
        if (separator <= 0) return fallback;

        var volumeId = decodedDocumentId[..separator];
        if (volumeId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            || decodedDocumentId.Any(char.IsControl))
            return fallback;

        // GetTreeDocumentId уже декодировал URI. «%2F» и «+» здесь могут быть частью имени папки.
        // Остальные двоеточия и разделители внутри пути сохраняем без файловой нормализации.
        var path = decodedDocumentId[(separator + 1)..].Trim('/');
        var volume = string.Equals(volumeId, "primary", StringComparison.OrdinalIgnoreCase)
            ? "Внутренняя память"
            : string.IsNullOrWhiteSpace(volumeDescription)
                ? $"Накопитель {volumeId}"
                : $"{volumeDescription.Trim()} ({volumeId})";
        return path.Length == 0 ? volume : $"{volume}/{path}";
    }
}
