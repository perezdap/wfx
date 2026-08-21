namespace Wfx.Core;

public interface IContextProvider
{
    ValueTask<string?> GetContextAsync(CancellationToken cancellationToken = default);
}

public sealed class CompositeContextProvider : IContextProvider
{
    private readonly IReadOnlyList<IContextProvider> _providers;

    public CompositeContextProvider(IEnumerable<IContextProvider> providers) =>
        _providers = providers.ToArray();

    public async ValueTask<string?> GetContextAsync(CancellationToken cancellationToken = default)
    {
        var sections = new List<string>();
        foreach (var provider in _providers)
        {
            var context = await provider.GetContextAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(context))
            {
                sections.Add(context);
            }
        }

        return sections.Count == 0 ? null : string.Join("\n\n", sections);
    }
}

public sealed class StaticContextProvider(string context) : IContextProvider
{
    public ValueTask<string?> GetContextAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<string?>(context);
}
