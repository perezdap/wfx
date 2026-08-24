using System.Text;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Cli;

internal sealed class NdjsonAgentObserver(TextWriter output) : IAgentObserver
{
    public async ValueTask OnEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            AgentEventJson.Write(writer, agentEvent);
        }

        await output.WriteLineAsync(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length))
            .ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
