using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("API_KEY=sk-abc123def", "API_KEY=[REDACTED]")]
    [InlineData("PASSWORD=hunter2", "PASSWORD=[REDACTED]")]
    [InlineData("DATABASE_URL=postgres://user:s3cret@db:5432/app", "DATABASE_URL=[REDACTED]")]
    [InlineData("  ACCESS_TOKEN = abc", "  ACCESS_TOKEN = [REDACTED]")]
    [InlineData("FOO_BAR_KEY=value", "FOO_BAR_KEY=[REDACTED]")]
    public void RedactsEnvironmentStyleAssignments(string input, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(input));

    [Theory]
    [InlineData("OPENAI_API_KEY=sk-11aa22bb33", "OPENAI_API_KEY=[REDACTED]")]
    [InlineData("sk-11aa22bb33", "[REDACTED]")]
    [InlineData("github_pat_11AAbb22CC", "[REDACTED]")]
    [InlineData("ghp_abcDEF123", "[REDACTED]")]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "[REDACTED]")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9",
        "Authorization: [REDACTED]")]
    public void RedactsInlineTokenPrefixes(string input, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(input));

    [Theory]
    [InlineData("postgres://user:pass@db:5432/app", "postgres://[REDACTED]@db:5432/app")]
    [InlineData("https://admin:secret@example.com/path", "https://[REDACTED]@example.com/path")]
    public void RedactsBasicAuthUrls(string input, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(input));

    [Theory]
    [InlineData("ask-turn-default-auto.txt")]
    [InlineData("PRINTED=value and nothing secret")]
    [InlineData("https://example.com/path?token=none")]
    public void LeavesPrefixAnchoredNonMatchesUntouched(string input) =>
        Assert.Equal(input, SecretRedactor.Redact(input));

    [Fact]
    public void RedactsAllShapesInOnePass()
    {
        const string input = """
            OPENAI_API_KEY=sk-11aa22bb33
            token=ghp_abcDEF123
            url: https://admin:pass@example.com/x
            """;

        var result = SecretRedactor.Redact(input);

        Assert.Contains("OPENAI_API_KEY=[REDACTED]", result);
        Assert.Contains("token=[REDACTED]", result);
        Assert.Contains("https://[REDACTED]@example.com/x", result);
        Assert.DoesNotContain("sk-11aa22bb33", result);
        Assert.DoesNotContain("ghp_abcDEF123", result);
        Assert.DoesNotContain("admin:pass", result);
    }
}
