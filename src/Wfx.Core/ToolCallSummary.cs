using System.Text;
using System.Text.Json;

namespace Wfx.Core;

/// <summary>
/// Renders a tool call as a single compact line so a human can see what a tool is about to do.
/// </summary>
public static class ToolCallSummary
{
    public const int DefaultMaxArgumentLength = 160;

    private const int MinValueLength = 96;
    private const string Ellipsis = "…";

    public static string Describe(string toolName, string? argumentsJson, int maxArgumentLength = DefaultMaxArgumentLength)
    {
        var arguments = DescribeArguments(argumentsJson, maxArgumentLength);
        return arguments.Length == 0 ? toolName : $"{toolName}({arguments})";
    }

    /// <summary>
    /// Collapses free-form text onto a single truncated line.
    /// </summary>
    public static string DescribeText(string? text, int maxLength = DefaultMaxArgumentLength) =>
        Truncate(Collapse(text), maxLength);

    private static string DescribeArguments(string? argumentsJson, int maxArgumentLength)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return string.Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return DescribeText(argumentsJson, maxArgumentLength);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return DescribeText(argumentsJson, maxArgumentLength);
            }

            var maxValueLength = Math.Max(MinValueLength, maxArgumentLength * 3 / 4);
            var builder = new StringBuilder();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = DescribeValue(property.Value, maxValueLength);
                if (value is null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(property.Name).Append(": ").Append(value);
                if (builder.Length > maxArgumentLength)
                {
                    break;
                }
            }

            return Truncate(builder.ToString(), maxArgumentLength);
        }
    }

    private static string? DescribeValue(JsonElement value, int maxValueLength) => value.ValueKind switch
    {
        JsonValueKind.String => NonEmpty(DescribeText(value.GetString(), maxValueLength)),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => $"[{value.GetArrayLength()} items]",
        JsonValueKind.Object => "{…}",
        _ => null
    };

    private static string? NonEmpty(string value) => value.Length == 0 ? null : value;

    private static string Collapse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength] + Ellipsis;
    }
}
