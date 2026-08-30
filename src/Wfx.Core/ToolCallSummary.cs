using System.Globalization;
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

    public const int MinSecretLength = SecretRedactor.MinSecretLength;

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
        var arguments = DescribeArguments(argumentsJson, maxArgumentLength, NormalizeSecrets(secrets));
        return arguments.Length == 0 ? toolName : $"{toolName}({arguments})";
    }

    /// <summary>
    /// Collapses free-form text onto a single truncated line.
    /// </summary>
    public static string DescribeText(
        string? text,
        int maxLength = DefaultMaxArgumentLength,
        IReadOnlyList<string>? secrets = null) =>
        Truncate(Collapse(Redact(text, NormalizeSecrets(secrets))), maxLength);

    /// <summary>
    /// Replaces known secret values without collapsing or truncating, for debug output.
    /// </summary>
    public static string RedactSecrets(string? text, IReadOnlyList<string>? secrets) =>
        Redact(text, NormalizeSecrets(secrets));

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
            return DescribeRawArguments(argumentsJson, maxArgumentLength, secrets);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return DescribeRawArguments(argumentsJson, maxArgumentLength, secrets);
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

    private static string DescribeRawArguments(
        string argumentsJson,
        int maxArgumentLength,
        IReadOnlyList<string> secrets)
    {
        var prepared = RedactSecretNamedJsonFields(DecodeJsonEscapes(argumentsJson));
        return Truncate(Collapse(Redact(prepared, secrets)), maxArgumentLength);
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

    private static IReadOnlyList<string> NormalizeSecrets(IReadOnlyList<string>? secrets) =>
        SecretRedactor.PrepareNeedles(secrets);

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
            if (!string.IsNullOrEmpty(value))
            {
                (extra ??= []).Add(value);
            }
        }

        return extra is null ? secrets : SecretRedactor.PrepareNeedles([.. secrets, .. extra]);
    }

    private static string Redact(string? value, IReadOnlyList<string> secrets)
    {
        if (string.IsNullOrEmpty(value) || secrets.Count == 0)
        {
            return value ?? string.Empty;
        }

        foreach (var secret in secrets)
        {
            value = value.Replace(secret, Redacted, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private static string DecodeJsonEscapes(string value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                builder.Append(value[index]);
                continue;
            }

            var next = value[index + 1];
            switch (next)
            {
                case 'u' when index + 5 < value.Length &&
                    int.TryParse(value.AsSpan(index + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    builder.Append((char)code);
                    index += 5;
                    break;
                case 'n':
                    builder.Append('\n');
                    index++;
                    break;
                case 'r':
                    builder.Append('\r');
                    index++;
                    break;
                case 't':
                    builder.Append('\t');
                    index++;
                    break;
                case '"':
                case '\\':
                case '/':
                    builder.Append(next);
                    index++;
                    break;
                default:
                    builder.Append(value[index]);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string RedactSecretNamedJsonFields(string json)
    {
        var builder = new StringBuilder(json.Length);
        var index = 0;
        while (index < json.Length)
        {
            if (json[index] != '"' || !TryReadJsonString(json, index, out var name, out var afterName))
            {
                builder.Append(json[index]);
                index++;
                continue;
            }

            var colon = SkipWhite(json, afterName);
            if (colon >= json.Length || json[colon] != ':' || !IsSecretPropertyName(name))
            {
                builder.Append(json, index, afterName - index);
                index = afterName;
                continue;
            }

            builder.Append(json, index, colon + 1 - index);
            builder.Append(' ');
            builder.Append(Redacted);
            var valueStart = SkipWhite(json, colon + 1);
            if (valueStart < json.Length && json[valueStart] == '"' &&
                TryReadJsonString(json, valueStart, out _, out var afterValue))
            {
                index = afterValue;
                continue;
            }

            index = json.Length;
        }

        return builder.ToString();
    }

    private static bool TryReadJsonString(string json, int start, out string value, out int after)
    {
        value = string.Empty;
        after = start;
        if (start >= json.Length || json[start] != '"')
        {
            return false;
        }

        var builder = new StringBuilder();
        var escaped = false;
        for (var index = start + 1; index < json.Length; index++)
        {
            var character = json[index];
            if (escaped)
            {
                builder.Append(character);
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                value = builder.ToString();
                after = index + 1;
                return true;
            }

            builder.Append(character);
        }

        return false;
    }

    private static int SkipWhite(string json, int index)
    {
        while (index < json.Length && char.IsWhiteSpace(json[index]))
        {
            index++;
        }

        return index;
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
