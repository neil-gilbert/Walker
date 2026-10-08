using System.Text.Json;
using System.Text.Json.Serialization;

namespace Walker.Core;

public static class VerificationReportJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
