using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wfx.Tools;

internal static class ToolJson
{
    public static string RequiredString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"'{name}' must be a non-empty string.");
        }

        return value.GetString()!;
    }

    public static string RequiredStringAllowEmpty(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"'{name}' must be a string.");
        }

        return value.GetString()!;
    }

    public static string String(JsonElement root, string name, string defaultValue) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? defaultValue
            : defaultValue;

    public static bool Boolean(JsonElement root, string name, bool defaultValue) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    public static int Integer(JsonElement root, string name, int defaultValue, int min, int max)
    {
        var result = root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : defaultValue;
        return Math.Clamp(result, min, max);
    }

    public static IReadOnlyList<string> Strings(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return [];
        }

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"'{name}' must be an array of strings.");
        }

        var items = new List<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"'{name}' must be an array of strings.");
            }

            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                items.Add(text);
            }
        }

        return items;
    }

    public static JsonObject ObjectSchema(
        IEnumerable<(string Name, JsonObject Schema, bool Required)> properties)
    {
        var propertyNode = new JsonObject();
        var requiredNode = new JsonArray();
        foreach (var property in properties)
        {
            propertyNode[property.Name] = property.Schema;
            if (property.Required)
            {
                requiredNode.Add((JsonNode?)property.Name);
            }
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = propertyNode,
            ["required"] = requiredNode,
            ["additionalProperties"] = false
        };
    }

    public static JsonObject StringSchema(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description
    };

    public static JsonObject BooleanSchema(string description) => new()
    {
        ["type"] = "boolean",
        ["description"] = description
    };

    public static JsonObject IntegerSchema(string description, int minimum, int maximum) => new()
    {
        ["type"] = "integer",
        ["description"] = description,
        ["minimum"] = minimum,
        ["maximum"] = maximum
    };

    public static JsonObject StringArraySchema(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new JsonObject { ["type"] = "string" }
    };
}
