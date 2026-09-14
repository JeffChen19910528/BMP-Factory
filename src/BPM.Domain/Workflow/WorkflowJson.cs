using System.Text.Json;
using System.Text.Json.Serialization;

namespace BPM.Domain.Workflow;

// Shared JSON contract for ProcessVersion.DefinitionJson (Skill.md §11's example uses camelCase
// property names and PascalCase enum member names, e.g. "type": "Start").
public static class WorkflowJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(WorkflowDefinition definition) =>
        JsonSerializer.Serialize(definition, Options);

    public static WorkflowDefinition? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowDefinition>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
