using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrowserSync.Core.Protocol;

/// <summary>Shared JSON options so the wire format matches what `JSON.stringify` produces
/// on the extension side: camelCase properties, string enums.</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
