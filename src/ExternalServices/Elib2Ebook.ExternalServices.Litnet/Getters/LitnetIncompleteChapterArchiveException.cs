namespace Elib2Ebook.ExternalServices.Litnet.Getters;

/// <summary>Состав неполного архива без текста глав или данных авторизации.</summary>
internal sealed class LitnetIncompleteChapterArchiveException : InvalidOperationException
{
    public int RequestedCount { get; }
    public int ReceivedCount => RequestedCount - MissingChapterIds.Count;
    public IReadOnlyList<long> MissingChapterIds { get; }

    public LitnetIncompleteChapterArchiveException(int requestedCount, IReadOnlyCollection<long> missingChapterIds)
        : base($"Litnet не вернул {missingChapterIds.Count} из {requestedCount} запрошенных глав. Неполная книга не будет сохранена.")
    {
        RequestedCount = requestedCount;
        MissingChapterIds = Array.AsReadOnly(missingChapterIds.ToArray());
    }
}
