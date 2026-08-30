using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wfx.Core;

/// <summary>
/// Masks secrets in text. The shape pass uses a fixed table of prefix-anchored matchers:
/// environment-style assignments (<c>API_KEY=</c>, <c>PASSWORD=</c>, <c>DATABASE_URL=</c>, and
/// kin), inline token prefixes (<c>sk-</c>, <c>github_pat_</c>, <c>ghp_</c>, <c>AKIA</c>,
/// <c>Bearer </c>), and basic-auth URLs. Matching is prefix-anchored only; there are no
/// entropy or length heuristics, so a filename like <c>ask-turn-default-auto.txt</c> is never
/// mangled. The explicit pass (<see cref="Redact(string?, IReadOnlyList{string}?)"/>) covers
/// opaque values that match no shape — OAuth tokens and configured header values — prepared
/// through <see cref="PrepareNeedles"/>.
/// <para>
/// The agent loop applies this exactly once, at ingestion, so observers, the model's view,
/// in-memory messages, and any persisted transcript hold identical text. This mechanism is
/// deliberately separate from display-time redaction and from child-process environment
/// scrubbing.
/// </para>
/// </summary>
internal static class SecretRedactor
{
    /// <summary>The marker that replaces a matched secret value.</summary>
    internal const string Redacted = "[REDACTED]";

    /// <summary>Values shorter than this are never treated as secrets, so common words survive.</summary>
    internal const int MinSecretLength = 8;

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

    // An environment assignment that begins a line: optional leading whitespace, an optional
    // shell 'export' prefix (so 'export API_KEY=...' is covered too), a plausible variable
    // name, optional whitespace, then '='. The value runs to the end of the line and any
    // leading whitespace after '=' is preserved. The 'export' prefix is kept in <head>, so a
    // variable literally named 'export' (as in 'export=x') still parses as the key.
    private static readonly Regex EnvAssignmentRegex = new(
        @"(?im)^(?<head>[ \t]*(?:export[ \t]+)?)(?<key>[a-z_][a-z0-9_]*)(?<eq>[ \t]*=)(?<ws>[ \t]*)(?<value>[^\r\n]*)",
        RegexOptions.CultureInvariant);

    // Prefix-anchored inline tokens. Each alternative is anchored at a word boundary; the
    // payload runs to an explicit delimiter (whitespace, comma, or quote) rather than assuming
    // a payload alphabet, so a value like 'sk-abc.def' is redacted whole instead of leaking
    // '.def'. Matching is case-insensitive.
    private static readonly Regex InlineTokenRegex = new(
        @"\bsk-[^\s,""']*|" +
        @"\bgithub_pat_[^\s,""']*|" +
        @"\bghp_[^\s,""']*|" +
        @"\bAKIA[^\s,""']*|" +
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
    internal static string Redact(string? input)
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

    /// <summary>
    /// Returns <paramref name="input"/> with known secret shapes and every explicit secret in
    /// <paramref name="secrets"/> replaced by <see cref="Redacted"/>. The explicit pass is
    /// what covers opaque values — OAuth tokens, configured header values — that match no
    /// known shape.
    /// </summary>
    internal static string Redact(string? input, IReadOnlyList<string>? secrets)
    {
        var result = Redact(input);
        if (result.Length == 0)
        {
            return result;
        }

        foreach (var needle in PrepareNeedles(secrets))
        {
            result = result.Replace(needle, Redacted, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Normalizes explicit secret values into match needles: drops null/short values, adds
    /// the JSON-encoded variant (a secret containing quotes or escapes appears encoded inside
    /// arguments_json), dedups case-insensitively, and sorts longest first so a shorter value
    /// that prefixes a longer one never masks the longer match.
    /// </summary>
    internal static IReadOnlyList<string> PrepareNeedles(IReadOnlyList<string>? secrets)
    {
        if (secrets is null || secrets.Count == 0)
        {
            return [];
        }

        var needles = new List<string>(secrets.Count);
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < MinSecretLength)
            {
                continue;
            }

            AddUnique(needles, secret);
            var encoded = JsonEncodedText.Encode(secret).ToString();
            if (!encoded.Equals(secret, StringComparison.Ordinal))
            {
                AddUnique(needles, encoded);
            }
        }

        needles.Sort(static (left, right) => right.Length.CompareTo(left.Length));
        return needles;
    }

    private static void AddUnique(List<string> needles, string value)
    {
        for (var index = 0; index < needles.Count; index++)
        {
            if (needles[index].Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        needles.Add(value);
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
