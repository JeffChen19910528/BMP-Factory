using System.Text.Json;
using System.Text.Json.Serialization;

namespace BPM.Domain.Forms;

// Shared JSON contract for FormVersion.SchemaJson, matching WorkflowJson's conventions exactly
// (camelCase property names, enum member names as strings) so both schemas read the same way.
public static class FormJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(FormSchema schema) => JsonSerializer.Serialize(schema, Options);

    public static FormSchema? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<FormSchema>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
