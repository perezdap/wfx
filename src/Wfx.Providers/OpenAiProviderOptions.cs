namespace Wfx.Providers;

public sealed record OpenAiProviderOptions
{
    public required Uri BaseUri { get; init; }

    public string? ApiKey { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class ProviderProtocolException : Exception
{
    public ProviderProtocolException(string message) : base(message)
    {
    }

    public ProviderProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
