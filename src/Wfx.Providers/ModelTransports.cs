using Wfx.Core;

namespace Wfx.Providers;

/// <summary>
/// Maps a configured protocol to the transport that speaks it.
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

        throw new InvalidOperationException($"Protocol '{protocol}' has no model transport.");
    }
}
