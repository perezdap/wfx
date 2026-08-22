using Wfx.Core;

namespace Wfx.Providers;

/// <summary>
/// Maps a configured protocol to the transport that speaks it. Configuration
/// rejects unknown protocols first; this guard covers hosts that compose a
/// transport directly.
/// </summary>
public static class ModelTransports
{
    public static IModelProvider Create(string protocol, HttpClient httpClient, OpenAiProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (protocol.Equals("responses", StringComparison.OrdinalIgnoreCase))
        {
            return new OpenAiResponsesProvider(httpClient, options);
        }

        if (protocol.Equals("chat_completions", StringComparison.OrdinalIgnoreCase))
        {
            return new OpenAiCompatibleProvider(httpClient, options);
        }

        throw new InvalidOperationException($"Protocol '{protocol}' is not implemented yet.");
    }
}
