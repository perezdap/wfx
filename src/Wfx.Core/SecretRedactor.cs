using System.Text.RegularExpressions;

namespace Wfx.Core;

/// <summary>
/// Masks known secret shapes in text using a fixed table of prefix-anchored matchers:
/// environment-style assignments (<c>API_KEY=</c>, <c>PASSWORD=</c>, <c>DATABASE_URL=</c>, and
/// kin), inline token prefixes (<c>sk-</c>, <c>github_pat_</c>, <c>ghp_</c>, <c>AKIA</c>,
/// <c>Bearer </c>), and basic-auth URLs. Matching is prefix-anchored only; there are no
/// entropy or length heuristics, so a filename like <c>ask-turn-default-auto.txt</c> is never
/// mangled.
/// <para>
/// The agent loop applies this exactly once, at tool-result ingestion, so the model's view,
/// in-memory messages, and any persisted transcript hold identical text. This mechanism is
/// deliberately separate from display-time redaction and from child-process environment
/// scrubbing.
/// </para>
/// </summary>
public static class SecretRedactor
{
    /// <summary>The marker that replaces a matched secret value.</summary>
    public const string Redacted = "[REDACTED]";

    private static readonly HashSet<string> SecretEnvKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "api_key",
        "apikey",
        "api_token",
        "access_token",
        "auth_token",
        "refresh_token",
        "token",
        "password",
        "passwd",
        "secret",
        "secret_key",
        "secret_token",
        "client_secret",
        "private_key",
        "database_url",
        "db_url",
        "connection_string",
        "mongodb_uri",
        "mongo_uri",
        "redis_url",
        "aws_access_key_id",
        "aws_secret_access_key",
        "ssh_private_key",
        "ssh_key",
        "authorization",
        "credential",
        "credentials"
    };

    private static readonly string[] SecretEnvSuffixes =
    [
        "_key",
        "_token",
        "_secret",
        "_password",
        "_passwd",
        "_dsn",
        "_credential",
        "_credentials"
    ];

    // An environment assignment that begins a line: optional leading whitespace, a plausible
    // variable name, optional whitespace, then '='. The value runs to the end of the line and
    // any leading whitespace after '=' is preserved.
    private static readonly Regex EnvAssignmentRegex = new(
        @"(?im)^(?<head>[ \t]*)(?<key>[a-z_][a-z0-9_]*)(?<eq>[ \t]*=)(?<ws>[ \t]*)(?<value>[^\r\n]*)",
        RegexOptions.CultureInvariant);

    // Prefix-anchored inline tokens. Each alternative is anchored at a word boundary; the
    // trailing character class stops at punctuation and whitespace so no extra text is eaten.
    // Token payloads are base62/alphanumeric, so matching is case-insensitive.
    private static readonly Regex InlineTokenRegex = new(
        @"\bsk-[a-z0-9_\-]*|" +
        @"\bgithub_pat_[a-z0-9_\-]*|" +
        @"\bghp_[a-z0-9_\-]*|" +
        @"\bAKIA[a-z0-9_\-]*|" +
        @"\bBearer\s+[^\s,""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A basic-auth URL with explicit credentials: <-scheme-//><user>:<password>@<host>. The
    // '://' fragment anchors the match; credentials are required (a bare user@ is left alone).
    private static readonly Regex BasicAuthUrlRegex = new(
        @"\b(?<scheme>[a-z][a-z0-9+.\-]*://)[^/\s:@]+:[^/\s@]*@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns <paramref name="input"/> with known secret shapes replaced by
    /// <see cref="Redacted"/>. A <see langword="null"/> input yields <see cref="string.Empty"/>.
    /// </summary>
    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var result = EnvAssignmentRegex.Replace(input, EnvAssignmentEvaluator);
        result = InlineTokenRegex.Replace(result, Redacted);
        result = BasicAuthUrlRegex.Replace(result, BasicAuthUrlEvaluator);
        return result;
    }

    private static string EnvAssignmentEvaluator(Match match)
    {
        var key = match.Groups["key"].Value;
        if (!IsSecretEnvKey(key))
        {
            return match.Value;
        }

        return $"{match.Groups["head"].Value}{key}{match.Groups["eq"].Value}" +
            $"{match.Groups["ws"].Value}{Redacted}";
    }

    private static string BasicAuthUrlEvaluator(Match match) =>
        match.Groups["scheme"].Value + Redacted + "@";

    private static bool IsSecretEnvKey(string key)
    {
        if (SecretEnvKeys.Contains(key))
        {
            return true;
        }

        for (var index = 0; index < SecretEnvSuffixes.Length; index++)
        {
            if (key.EndsWith(SecretEnvSuffixes[index], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
