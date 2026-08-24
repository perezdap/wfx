using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wfx.Core;

namespace Wfx.Cli.Tests;

[Collection("Console")]
public sealed partial class ResultJsonTests
{
    [Fact]
    public async Task SessionsJsonEmitsEmptyResultObjectWithoutModelConfiguration()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The sessions command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            var store = new SessionStore(Path.Combine(directory.FullName, "sessions"));
            var exitCode = await CliRunner.RunAsync(
                ["sessions", "--json"],
                httpClient,
                store,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            var result = ParseResultObject(console.Output.ToString());
            Assert.Equal(1, result.GetProperty("schema_version").GetInt32());
            Assert.Equal(JsonValueKind.Array, result.GetProperty("sessions").ValueKind);
            Assert.Empty(result.GetProperty("sessions").EnumerateArray());
            Assert.Empty(console.ErrorText);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task SessionsJsonEmitsSessionFieldsIncludingLastEndpoint()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateCompletedHttpClient();
        try
        {
            var store = new SessionStore(Path.Combine(directory.FullName, "sessions"));
            var userProfile = Path.Combine(directory.FullName, "profile-home");
            Directory.CreateDirectory(userProfile);
            using (var turnConsole = new ConsoleCapture())
            {
                var turnExitCode = await CliRunner.RunAsync(
                    [
                        "run",
                        "--provider", "local",
                        "--protocol", "chat_completions",
                        "--base-url", "https://example.test/v1",
                        "--model", "fake-model",
                        "do it"
                    ],
                    httpClient,
                    store,
                    TestContext.Current.CancellationToken,
                    userProfile);
                Assert.Equal(0, turnExitCode);
            }

            var summary = Assert.Single(store.List().Sessions);
            using var console = new ConsoleCapture();
            var exitCode = await CliRunner.RunAsync(
                ["sessions", "--json"],
                httpClient,
                store,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            var result = ParseResultObject(console.Output.ToString());
            Assert.Equal(1, result.GetProperty("schema_version").GetInt32());
            var session = Assert.Single(result.GetProperty("sessions").EnumerateArray());
            Assert.Equal(summary.SessionId, session.GetProperty("id").GetString());
            Assert.Equal(summary.Workspace, session.GetProperty("workspace").GetString());
            Assert.Matches(IsoTimestamp(), session.GetProperty("created_at").GetString());
            Assert.Matches(IsoTimestamp(), session.GetProperty("updated_at").GetString());
            Assert.Equal(summary.SizeBytes, session.GetProperty("size_bytes").GetInt64());
            var endpoint = session.GetProperty("endpoint");
            Assert.Equal(JsonValueKind.Null, endpoint.GetProperty("profile").ValueKind);
            Assert.Equal("local", endpoint.GetProperty("provider").GetString());
            Assert.Equal("chat_completions", endpoint.GetProperty("protocol").GetString());
            Assert.Equal("fake-model", endpoint.GetProperty("model").GetString());
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task SessionsJsonReportsNullEndpointForTurnlessSession()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The sessions command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            var store = new SessionStore(Path.Combine(directory.FullName, "sessions"));
            using (store.Create(Path.Combine(directory.FullName, "workspace")))
            {
            }

            var exitCode = await CliRunner.RunAsync(
                ["sessions", "--json"],
                httpClient,
                store,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            var result = ParseResultObject(console.Output.ToString());
            var session = Assert.Single(result.GetProperty("sessions").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, session.GetProperty("endpoint").ValueKind);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task SessionsJsonWithExtraArgumentIsAUsageError()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The sessions command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            var store = new SessionStore(Path.Combine(directory.FullName, "sessions"));
            var exitCode = await CliRunner.RunAsync(
                ["sessions", "--json", "extra"],
                httpClient,
                store,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Empty(console.Output.ToString());
            Assert.Contains("Unexpected argument", console.ErrorText);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigJsonEmitsEffectiveSettingsSourcesAndRedactedSecret()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The config command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        var userProfile = Path.Combine(directory.FullName, "profile-home");
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        var userConfigPath = Path.GetFullPath(Path.Combine(userProfile, ".wfx", "config.json"));
        File.WriteAllText(userConfigPath, """
            { "model": "user-model", "api_key": "user-secret", "max_iterations": 7 }
            """);
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["config", "--json"],
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken,
                userProfile);

            Assert.Equal(0, exitCode);
            var result = ParseResultObject(console.Output.ToString());
            Assert.Equal(1, result.GetProperty("schema_version").GetInt32());
            var effective = result.GetProperty("effective");
            Assert.Equal("user-model", effective.GetProperty("model").GetString());
            Assert.Equal(7, effective.GetProperty("max_iterations").GetInt32());
            Assert.Equal("[REDACTED]", effective.GetProperty("api_key").GetString());
            Assert.Equal("openai", effective.GetProperty("provider").GetString());
            Assert.Equal("chat_completions", effective.GetProperty("protocol").GetString());
            Assert.Equal("https://api.openai.com/v1", effective.GetProperty("base_url").GetString());
            Assert.Equal(JsonValueKind.Null, effective.GetProperty("profile").ValueKind);
            Assert.Collection(
                result.GetProperty("sources").EnumerateArray(),
                source =>
                {
                    Assert.Equal("defaults", source.GetProperty("layer").GetString());
                    Assert.Equal(JsonValueKind.Null, source.GetProperty("path").ValueKind);
                },
                source =>
                {
                    Assert.Equal("user", source.GetProperty("layer").GetString());
                    Assert.Equal(userConfigPath, source.GetProperty("path").GetString());
                    var keys = source.GetProperty("keys").EnumerateArray()
                        .Select(element => element.GetString())
                        .ToArray();
                    Assert.Equal(new[] { "api_key", "model", "max_iterations" }, keys);
                });
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigJsonExitsTwoOnConfigurationError()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The config command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        var userProfile = Path.Combine(directory.FullName, "profile-home");
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(Path.Combine(userProfile, ".wfx", "config.json"), """{ "model": "m" }""");
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["config", "--json", "--profile", "does-not-exist"],
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken,
                userProfile);

            Assert.Equal(2, exitCode);
            Assert.Empty(console.Output.ToString());
            Assert.Contains("is not defined", console.ErrorText);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ModelsJsonListsConfiguredProfilesWithEndpointAndCredentials()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The models command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        var userProfile = Path.Combine(directory.FullName, "profile-home");
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(Path.Combine(userProfile, ".wfx", "config.json"), """
            {
              "profiles": {
                "cred": { "model": "cred-model", "api_key": "profile-secret" },
                "bare": { "model": "bare-model" },
                "nomodel": { "base_url": "https://no-model.example/v1" }
              }
            }
            """);
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["models", "--json"],
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken,
                userProfile);

            Assert.Equal(0, exitCode);
            var result = ParseResultObject(console.Output.ToString());
            Assert.Equal(1, result.GetProperty("schema_version").GetInt32());
            // Only profiles carrying a model key appear.
            var profiles = result.GetProperty("profiles").EnumerateArray().ToArray();
            Assert.Equal(2, profiles.Length);

            var bare = profiles[0];
            Assert.Equal("bare", bare.GetProperty("name").GetString());
            Assert.Equal("openai", bare.GetProperty("provider").GetString());
            Assert.Equal("chat_completions", bare.GetProperty("protocol").GetString());
            Assert.Equal("bare-model", bare.GetProperty("model").GetString());
            // Ambient credential variables differ per machine, so only the shape is asserted here;
            // exact has_credentials semantics are covered at the configuration seam.
            Assert.True(bare.GetProperty("has_credentials").ValueKind is JsonValueKind.True or JsonValueKind.False);

            var cred = profiles[1];
            Assert.Equal("cred", cred.GetProperty("name").GetString());
            Assert.Equal("cred-model", cred.GetProperty("model").GetString());
            Assert.Equal("https://api.openai.com/v1", cred.GetProperty("base_url").GetString());
            Assert.True(cred.GetProperty("has_credentials").GetBoolean());
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ModelsJsonWarnsAboutUnresolvableProfileAndOmitsItFromStdout()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The models command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        var userProfile = Path.Combine(directory.FullName, "profile-home");
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(Path.Combine(userProfile, ".wfx", "config.json"), """
            {
              "profiles": {
                "broken": { "model": "broken-model", "protocol": "anthropic_messages" },
                "ok": { "model": "ok-model" }
              }
            }
            """);
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["models", "--json"],
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken,
                userProfile);

            Assert.Equal(0, exitCode);
            Assert.Contains("broken", console.ErrorText);
            var result = ParseResultObject(console.Output.ToString());
            // The broken profile cannot supply the contract's string protocol/base_url, so it is
            // reported on stderr only; every entry on stdout carries the full shape.
            var profile = Assert.Single(result.GetProperty("profiles").EnumerateArray());
            Assert.Equal("ok", profile.GetProperty("name").GetString());
            Assert.Equal("chat_completions", profile.GetProperty("protocol").GetString());
            Assert.Equal("https://api.openai.com/v1", profile.GetProperty("base_url").GetString());
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ModelsJsonExitsTwoOnConfigurationError()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The models command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        var userProfile = Path.Combine(directory.FullName, "profile-home");
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(Path.Combine(userProfile, ".wfx", "config.json"), """{ "model": "m" }""");
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["models", "--json", "--profile", "does-not-exist"],
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken,
                userProfile);

            Assert.Equal(2, exitCode);
            Assert.Empty(console.Output.ToString());
            Assert.Contains("is not defined", console.ErrorText);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public void ConfigResultOmitsUnsetSecrets()
    {
        // The unset half of the set-vs-unset redaction pair. It runs at the emitter seam
        // because the command seam reads the real process environment, so no CLI invocation
        // can deterministically present an unset credential on a machine with ambient
        // OPENAI_API_KEY-style variables.
        var settings = new WfxSettings(
            "openai",
            "chat_completions",
            new Uri("https://api.openai.com/v1"),
            null,
            "example-model",
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(300),
            24,
            ApprovalMode.Always);

        var result = EmitResult(writer => JsonResultWriters.WriteConfigResult(writer, settings));

        var effective = result.GetProperty("effective");
        Assert.False(effective.TryGetProperty("api_key", out _));
        Assert.False(effective.TryGetProperty("headers", out _));
    }

    [Fact]
    public void ConfigResultRedactsSecretHeaders()
    {
        var settings = new WfxSettings(
            "openai",
            "chat_completions",
            new Uri("https://api.openai.com/v1"),
            "secret-key",
            "example-model",
            new Dictionary<string, string> { ["X-Auth"] = "header-secret" },
            TimeSpan.FromSeconds(300),
            24,
            ApprovalMode.Always);

        var result = EmitResult(writer => JsonResultWriters.WriteConfigResult(writer, settings));

        var effective = result.GetProperty("effective");
        Assert.Equal("[REDACTED]", effective.GetProperty("api_key").GetString());
        Assert.Equal("[REDACTED]", effective.GetProperty("headers").GetProperty("X-Auth").GetString());
    }

    [Fact]
    public async Task HelpDocumentsJsonOutputAndSchemas()
    {
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "Help must not call a model endpoint.");
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            ["--help"],
            httpClient,
            new TestSessionStore(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var output = console.Output.ToString();
        Assert.Contains("--json", output);
        Assert.Contains("result object", output);
        Assert.Contains("not an event stream", output);
        Assert.Contains("docs/schemas", output);
    }

    [Fact]
    public async Task ConfigJsonExitsTwoOnMalformedConfigFile()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The config command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        var userProfile = Path.Combine(directory.FullName, "profile-home");
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(Path.Combine(userProfile, ".wfx", "config.json"), "{ not valid json");
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["config", "--json"],
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken,
                userProfile);

            Assert.Equal(2, exitCode);
            Assert.Empty(console.Output.ToString());
            Assert.Contains("not valid JSON", console.ErrorText);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Theory]
    [InlineData("wfx-sessions-result.v1.json", "urn:wfx:sessions-result:v1")]
    [InlineData("wfx-config-result.v1.json", "urn:wfx:config-result:v1")]
    [InlineData("wfx-models-result.v1.json", "urn:wfx:models-result:v1")]
    public void ResultSchemasMarkEveryFieldVisibility(string fileName, string schemaId)
    {
        var root = LoadSchema(fileName);

        Assert.Equal(schemaId, root.GetProperty("$id").GetString());
        Assert.Equal(1, root.GetProperty("properties").GetProperty("schema_version").GetProperty("const").GetInt32());
        AssertFieldsMarked(root);
    }

    [Fact]
    public async Task SessionsJsonValidatesAgainstPublishedSchema()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The sessions command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            var store = new SessionStore(Path.Combine(directory.FullName, "sessions"));
            using (store.Create(Path.Combine(directory.FullName, "workspace")))
            {
            }

            var exitCode = await CliRunner.RunAsync(
                ["sessions", "--json"],
                httpClient,
                store,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            AssertValidates(console.Output.ToString(), "wfx-sessions-result.v1.json");
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigJsonValidatesAgainstPublishedSchema()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The config command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        var userProfile = Path.Combine(directory.FullName, "profile-home");
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(Path.Combine(userProfile, ".wfx", "config.json"), """
            { "model": "user-model", "api_key": "user-secret" }
            """);
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["config", "--json"],
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken,
                userProfile);

            Assert.Equal(0, exitCode);
            AssertValidates(console.Output.ToString(), "wfx-config-result.v1.json");
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ModelsJsonValidatesAgainstPublishedSchema()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The models command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        var userProfile = Path.Combine(directory.FullName, "profile-home");
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(Path.Combine(userProfile, ".wfx", "config.json"), """
            { "profiles": { "deep": { "model": "deep-model", "api_key": "secret" } } }
            """);
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["models", "--json"],
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken,
                userProfile);

            Assert.Equal(0, exitCode);
            AssertValidates(console.Output.ToString(), "wfx-models-result.v1.json");
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ModelsJsonEmitsEmptyProfilesWhenNoneConfigured()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The models command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        var userProfile = Path.Combine(directory.FullName, "profile-home");
        Directory.CreateDirectory(userProfile);
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["models", "--json"],
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken,
                userProfile);

            Assert.Equal(0, exitCode);
            var result = ParseResultObject(console.Output.ToString());
            Assert.Empty(result.GetProperty("profiles").EnumerateArray());
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigJsonEmitsDefaultsWhenNoConfigFilesExist()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The config command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        var userProfile = Path.Combine(directory.FullName, "profile-home");
        Directory.CreateDirectory(userProfile);
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["config", "--json"],
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken,
                userProfile);

            Assert.Equal(0, exitCode);
            var result = ParseResultObject(console.Output.ToString());
            var effective = result.GetProperty("effective");
            Assert.Equal("openai", effective.GetProperty("provider").GetString());
            Assert.Equal(24, effective.GetProperty("max_iterations").GetInt32());
            // Ambient provider credential variables differ per machine, so only file layers are
            // asserted absent; the defaults layer must always anchor the provenance.
            var sources = result.GetProperty("sources").EnumerateArray().ToArray();
            Assert.Equal("defaults", sources[0].GetProperty("layer").GetString());
            Assert.DoesNotContain(
                sources,
                source => source.GetProperty("layer").GetString() is "user" or "project" or "cli");
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static void AssertValidates(string output, string schemaFileName)
    {
        var result = ParseResultObject(output);
        var schema = LoadSchema(schemaFileName);
        var errors = JsonSchemaValidator.Validate(result, schema);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static JsonElement LoadSchema(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "docs", "schemas", fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static void AssertFieldsMarked(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("properties", out var properties) &&
                    properties.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in properties.EnumerateObject())
                    {
                        Assert.True(
                            property.Value.TryGetProperty("x-wfx-visibility", out var visibility) &&
                            visibility.GetString() is "public" or "internal",
                            $"Schema property '{property.Name}' is not marked public or internal.");
                    }
                }

                foreach (var child in element.EnumerateObject())
                {
                    AssertFieldsMarked(child.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertFieldsMarked(item);
                }

                break;
        }
    }

    private static JsonElement EmitResult(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement ParseResultObject(string output)
    {
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")]
    private static partial Regex IsoTimestamp();
}
