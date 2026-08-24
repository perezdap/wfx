using System.Text.Json;

namespace Wfx.Cli.Tests;

/// <summary>
/// A deliberately small JSON Schema validator covering exactly the constructs the published
/// docs/schemas/ schemas use (type, required, properties, items, $ref into $defs, const,
/// enum, minLength, minimum, oneOf). Keeps the tests dependency-free and Native AOT-friendly.
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

        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            var firstBranchError = string.Empty;
            foreach (var branch in oneOf.EnumerateArray())
            {
                var candidateErrors = new List<string>();
                Validate(instance, branch, rootSchema, path, candidateErrors);
                if (candidateErrors.Count == 0)
                {
                    return;
                }

                if (firstBranchError.Length == 0)
                {
                    firstBranchError = candidateErrors[0];
                }
            }

            errors.Add($"{path}: matches none of the oneOf branches. First branch: {firstBranchError}.");
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

        if (schema.TryGetProperty("minLength", out var minLength) &&
            instance.ValueKind == JsonValueKind.String &&
            instance.GetString()!.Length < minLength.GetInt32())
        {
            errors.Add($"{path}: string shorter than minLength {minLength.GetInt32()}.");
        }

        if (schema.TryGetProperty("minimum", out var minimum) &&
            instance.ValueKind == JsonValueKind.Number &&
            instance.GetDouble() < minimum.GetDouble())
        {
            errors.Add($"{path}: value {instance} is below minimum {minimum.GetDouble()}.");
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
