using Wfx.Providers;

namespace Wfx.Providers.Tests;

public sealed class ModelTransportsTests
{
    [Theory]
    [InlineData("chat_completions", typeof(OpenAiCompatibleProvider))]
    [InlineData("CHAT_COMPLETIONS", typeof(OpenAiCompatibleProvider))]
    [InlineData("responses", typeof(OpenAiResponsesProvider))]
    [InlineData("Responses", typeof(OpenAiResponsesProvider))]
    public void SelectsTheTransportForTheProtocol(string protocol, Type expected)
    {
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        var transport = ModelTransports.Create(protocol, httpClient, Options());

        Assert.IsType(expected, transport);
    }

    [Fact]
    public void RejectsProtocolsWithoutATransport()
    {
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModelTransports.Create("anthropic_messages", httpClient, Options()));

        Assert.Contains("anthropic_messages", exception.Message);
        Assert.Contains("not implemented yet", exception.Message);
    }

    private static OpenAiProviderOptions Options() => new()
    {
        BaseUri = new Uri("https://example.test/v1")
    };
}
