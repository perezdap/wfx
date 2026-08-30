using System.Collections;

namespace Wfx.Cli;

/// <summary>
/// The redaction set for console output and approval prompts: provider secrets, fixed at
/// startup, concatenated with the MCP host's secret set, which is live — a token minted by a
/// mid-run OAuth refresh is redacted as soon as it exists. Enumeration reads the live side
/// each time, so consumers always see the current set.
/// </summary>
internal sealed class RedactionSecretList(
    IReadOnlyList<string> providerSecrets,
    IReadOnlyList<string> mcpSecrets) : IReadOnlyList<string>
{
    public int Count => providerSecrets.Count + mcpSecrets.Count;

    public string this[int index] =>
        index < providerSecrets.Count ? providerSecrets[index] : mcpSecrets[index - providerSecrets.Count];

    public IEnumerator<string> GetEnumerator() => providerSecrets.Concat(mcpSecrets).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
