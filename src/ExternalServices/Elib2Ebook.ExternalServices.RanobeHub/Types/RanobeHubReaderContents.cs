using System.Text.Json.Serialization;

namespace Elib2Ebook.ExternalServices.RanobeHub.Types;

internal sealed class RanobeHubReaderContents
{
    [JsonPropertyName("items")]
    public RanobeHubReaderChapter[] Items { get; set; }

    [JsonPropertyName("total")]
    public int? Total { get; set; }

    [JsonPropertyName("nextOffset")]
    public int? NextOffset { get; set; }
}

internal sealed class RanobeHubReaderChapter
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("bookId")]
    public long BookId { get; set; }

    [JsonPropertyName("volume")]
    public decimal Volume { get; set; }

    [JsonPropertyName("number")]
    public decimal Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("isDraft")]
    public bool IsDraft { get; set; }

    [JsonPropertyName("scheduledFor")]
    public string ScheduledFor { get; set; }
}
