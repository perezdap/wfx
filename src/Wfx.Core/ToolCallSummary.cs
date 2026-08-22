using System.Text;
using System.Text.Json;

namespace Wfx.Core;

/// <summary>
/// Renders a tool call as a single compact line so a human can see what a tool is about to do.
/// Secret-named properties and known secret values are replaced with <c>[REDACTED]</c>.
/// </summary>
public static class ToolCallSummary
{
    public const int DefaultMaxArgumentLength = 160;

    private const string Redacted = "[REDACTED]";

    private const int MinValueLength = 96;
    private const string Ellipsis = "…";

    private static readonly HashSet<string> SecretPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "api_key",
        "api-key",
        "apikey",
        "token",
        "access_token",
        "refresh_token",
        "secret",
        "password",
        "authorization",
        "credential",
        "credentials"
    };

    private static readonly string[] SecretPropertySuffixes =
    [
        "_api_key",
        "_token",
        "_secret",
        "_password",
        "-api-key",
        "-token",
        "-secret",
        "-password"
    ];

    public static string Describe(
        string toolName,
        string? argumentsJson,
        int maxArgumentLength = DefaultMaxArgumentLength,
        IReadOnlyList<string>? secrets = null)
    {
        var normalized = NormalizeSecrets(secrets);
        var arguments = DescribeArguments(argumentsJson, maxArgumentLength, normalized);
        return arguments.Length == 0 ? toolName : $"{toolName}({arguments})";
    }

    /// <summary>
    /// Collapses free-form text onto a single truncated line.
    /// </summary>
    public static string DescribeText(string? text, int maxLength = DefaultMaxArgumentLength) =>
        Truncate(Collapse(text), maxLength);

    private static string DescribeArguments(
        string? argumentsJson,
        int maxArgumentLength,
        IReadOnlyList<string> secrets)
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
            return DescribeText(Redact(argumentsJson, secrets), maxArgumentLength);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return DescribeText(Redact(argumentsJson, secrets), maxArgumentLength);
            }

            var maxValueLength = Math.Max(MinValueLength, maxArgumentLength * 3 / 4);
            var redactionSecrets = CollectJsonSecrets(document.RootElement, secrets);
            var builder = new StringBuilder();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = IsSecretPropertyName(property.Name)
                    ? Redacted
                    : DescribeValue(property.Value, maxValueLength, redactionSecrets);
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

    private static string? DescribeValue(JsonElement value, int maxValueLength, IReadOnlyList<string> secrets) =>
        value.ValueKind switch
        {
            JsonValueKind.String => NonEmpty(DescribeText(Redact(value.GetString(), secrets), maxValueLength)),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => $"[{value.GetArrayLength()} items]",
            JsonValueKind.Object => "{…}",
            _ => null
        };

    private static string? NonEmpty(string value) => value.Length == 0 ? null : value;

    private static IReadOnlyList<string> NormalizeSecrets(IReadOnlyList<string>? secrets)
    {
        if (secrets is null || secrets.Count == 0)
        {
            return [];
        }

        var normalized = new List<string>(secrets.Count);
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret) || normalized.Contains(secret, StringComparer.Ordinal))
            {
                continue;
            }

            normalized.Add(secret);
        }

        normalized.Sort(static (left, right) => right.Length.CompareTo(left.Length));
        return normalized;
    }

    private static IReadOnlyList<string> CollectJsonSecrets(JsonElement root, IReadOnlyList<string> secrets)
    {
        List<string>? extra = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!IsSecretPropertyName(property.Name) || property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = property.Value.GetString();
            if (string.IsNullOrEmpty(value) || ContainsOrdinal(secrets, value) ||
                (extra is not null && ContainsOrdinal(extra, value)))
            {
                continue;
            }

            extra ??= [.. secrets];
            extra.Add(value);
        }

        if (extra is null)
        {
            return secrets;
        }

        extra.Sort(static (left, right) => right.Length.CompareTo(left.Length));
        return extra;
    }

    private static bool ContainsOrdinal(IReadOnlyList<string> values, string candidate)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].Equals(candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Redact(string? value, IReadOnlyList<string> secrets)
    {
        if (string.IsNullOrEmpty(value) || secrets.Count == 0)
        {
            return value ?? string.Empty;
        }

        foreach (var secret in secrets)
        {
            value = value.Replace(secret, Redacted, StringComparison.Ordinal);
        }

        return value;
    }

    private static bool IsSecretPropertyName(string name)
    {
        if (SecretPropertyNames.Contains(name))
        {
            return true;
        }

        foreach (var suffix in SecretPropertySuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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
