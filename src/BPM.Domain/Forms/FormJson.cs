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
        Converters = { new JsonStringEnumConverter(), new FormRuleJsonConverter() },
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

// Hand-written converter for FormRule only (everything else in the schema still goes through the
// default reflection-based (de)serializer). Needed because an unrecognized `type` string would
// otherwise throw JsonException and, via FormJson.TryDeserialize's catch-all, corrupt/discard the
// *entire* schema — one rule from a future phase this backend doesn't understand yet must not be
// able to take an otherwise-valid form down with it. Mirrors the frontend Form Designer's own
// `raw`-field passthrough for unsupported field types (formSchemaModel.ts), applied here for rules
// for the first time.
internal sealed class FormRuleJsonConverter : JsonConverter<FormRule>
{
    // Shape used for the "understood" path only — a plain DTO, not FormRule itself, so delegating
    // to JsonSerializer for it can't recurse back into this converter.
    private sealed record KnownRuleShape(string Id, string Target, FormRuleType Type, FormCondition? Condition, string? Formula);

    public override FormRule? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var typeStr = root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString()
            : null;

        var isKnown = typeStr is not null
            && Enum.GetNames<FormRuleType>().Any(n => string.Equals(n, typeStr, StringComparison.OrdinalIgnoreCase))
            && !string.Equals(typeStr, nameof(FormRuleType.Unknown), StringComparison.OrdinalIgnoreCase);

        if (isKnown)
        {
            var known = JsonSerializer.Deserialize<KnownRuleShape>(root.GetRawText(), options)
                ?? throw new JsonException("Could not parse form rule.");
            return new FormRule(known.Id, known.Target, known.Type, known.Condition, known.Formula);
        }

        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString()! : Guid.NewGuid().ToString();
        var target = root.TryGetProperty("target", out var targetEl) && targetEl.ValueKind == JsonValueKind.String ? targetEl.GetString()! : string.Empty;
        return new FormRule(id, target, FormRuleType.Unknown, RawJson: root.Clone());
    }

    public override void Write(Utf8JsonWriter writer, FormRule value, JsonSerializerOptions options)
    {
        if (value.Type == FormRuleType.Unknown && value.RawJson is JsonElement raw)
        {
            raw.WriteTo(writer);
            return;
        }

        var known = new KnownRuleShape(value.Id, value.Target, value.Type, value.Condition, value.Formula);
        JsonSerializer.Serialize(writer, known, options);
    }
}
