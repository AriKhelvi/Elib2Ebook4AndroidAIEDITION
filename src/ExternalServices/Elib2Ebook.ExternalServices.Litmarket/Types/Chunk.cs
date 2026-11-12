using System.Text.Json.Serialization;

namespace Elib2Ebook.ExternalServices.Litmarket.Types;

internal class Chunk
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("mods")]
    public Mod[] Mods { get; set; }
}
