using System.Text.Json;

namespace Wfx.Cli.Tests;

/// <summary>
/// A deliberately small JSON Schema validator covering exactly the constructs the published
/// docs/schemas/ result schemas use (type, required, properties, items, $ref into $defs,
/// const, enum). Keeps the tests dependency-free and Native AOT-friendly, matching the
/// validation approach used for the event stream schema.
/// </summary>
internal static class JsonSchemaValidator
{
    public static IReadOnlyList<string> Validate(JsonElement instance, JsonElement schemaRoot)
    {
        var errors = new List<string>();
        Validate(instance, schemaRoot, schemaRoot, "$", errors);
        return errors;
    }

    private static void Validate(
        JsonElement instance,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var target = ResolveReference(reference.GetString()!, rootSchema);
            Validate(instance, target, rootSchema, path, errors);
            return;
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(instance, constant))
        {
            errors.Add($"{path}: expected const {constant}, got {instance}.");
        }

        if (schema.TryGetProperty("enum", out var choices) &&
            !choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(instance, choice)))
        {
            errors.Add($"{path}: value {instance} is not one of the enum choices.");
        }

        if (schema.TryGetProperty("type", out var type) && !MatchesType(instance, type))
        {
            errors.Add($"{path}: value {instance} does not match type {type}.");
            return;
        }

        if (instance.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray())
                {
                    if (!instance.TryGetProperty(name.GetString()!, out _))
                    {
                        errors.Add($"{path}: missing required property '{name.GetString()}'.");
                    }
                }
            }

            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (instance.TryGetProperty(property.Name, out var value))
                    {
                        Validate(value, property.Value, rootSchema, $"{path}.{property.Name}", errors);
                    }
                }
            }
        }

        if (instance.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in instance.EnumerateArray())
            {
                Validate(item, items, rootSchema, $"{path}[{index++}]", errors);
            }
        }
    }

    private static bool MatchesType(JsonElement instance, JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.Array)
        {
            return type.EnumerateArray().Any(choice => MatchesSingleType(instance, choice.GetString()!));
        }

        return MatchesSingleType(instance, type.GetString()!);
    }

    private static bool MatchesSingleType(JsonElement instance, string type) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "integer" => instance.ValueKind == JsonValueKind.Number &&
            instance.TryGetInt64(out _),
        "number" => instance.ValueKind == JsonValueKind.Number,
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static JsonElement ResolveReference(string reference, JsonElement rootSchema)
    {
        var target = rootSchema;
        foreach (var segment in reference.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "#")
            {
                continue;
            }

            target = target.GetProperty(segment);
        }

        return target;
    }
}
