namespace Wfx.Core;

public enum ModelSwitchRequestKind
{
    Picker,
    FreeForm
}

public sealed record ModelSwitchRequest
{
    private ModelSwitchRequest(ModelSwitchRequestKind kind, string target)
    {
        Kind = kind;
        Target = target;
    }

    public ModelSwitchRequestKind Kind { get; }

    public string Target { get; }

    public static ModelSwitchRequest Picker(string selection) => new(ModelSwitchRequestKind.Picker, selection);

    public static ModelSwitchRequest FreeForm(string model) => new(ModelSwitchRequestKind.FreeForm, model);
}

public sealed record ModelSwitchResult(WfxSettings? Settings, bool TransportChanged, string? Error)
{
    public bool Succeeded => Settings is not null;

    public IReadOnlyList<ModelMessage> MapConversation(IReadOnlyList<ModelMessage> conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return TransportChanged ? ProviderItemDowngrade.Strip(conversation) : conversation;
    }
}

/// <summary>
/// Removes endpoint-bound provider items while preserving portable conversation content.
/// </summary>
public static class ProviderItemDowngrade
{
    public static IReadOnlyList<ModelMessage> Strip(IReadOnlyList<ModelMessage> conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var mapped = new List<ModelMessage>(conversation.Count);
        foreach (var message in conversation)
        {
            var portable = message with { ProviderItemsJson = null };
            if (message.ProviderItemsJson is not null &&
                portable.Role is ModelRole.Assistant &&
                string.IsNullOrEmpty(portable.Content) &&
                portable.ToolCalls is not { Count: > 0 })
            {
                continue;
            }

            mapped.Add(portable);
        }

        return mapped;
    }
}

public static class ModelSwitchResolver
{
    public static ModelSwitchResult Resolve(WfxSettings current, ModelSwitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Kind is ModelSwitchRequestKind.FreeForm)
        {
            if (string.IsNullOrWhiteSpace(request.Target))
            {
                return Failure("A model ID is required.");
            }

            return new ModelSwitchResult(current with { Model = request.Target.Trim() }, false, null);
        }

        if (!int.TryParse(request.Target, out var selection) || selection < 1 || selection > current.ConfiguredModels.Count)
        {
            return Failure($"Unknown model selection '{request.Target}'.");
        }

        var configuredModel = current.ConfiguredModels[selection - 1];
        if (selection > current.ConfiguredModelResolutions.Count)
        {
            return Failure($"Configured model '{configuredModel.Profile}' could not be resolved.");
        }

        var resolution = current.ConfiguredModelResolutions[selection - 1];
        if (resolution.Settings is null)
        {
            return Failure(resolution.Error ?? $"Configured model '{configuredModel.Profile}' could not be resolved.");
        }

        var target = resolution.Settings with
        {
            Model = configuredModel.Model,
            Profile = configuredModel.Profile,
            MaxIterations = current.MaxIterations,
            Approval = current.Approval,
            ConfiguredModels = current.ConfiguredModels,
            ConfiguredModelResolutions = current.ConfiguredModelResolutions
        };
        return new ModelSwitchResult(target, !SameTransport(current, target), null);
    }

    private static ModelSwitchResult Failure(string error) => new(null, false, error);

    private static bool SameTransport(WfxSettings left, WfxSettings right) =>
        left.Provider.Equals(right.Provider, StringComparison.OrdinalIgnoreCase) &&
        left.Protocol.Equals(right.Protocol, StringComparison.OrdinalIgnoreCase) &&
        left.BaseUri == right.BaseUri &&
        string.Equals(left.ApiKey, right.ApiKey, StringComparison.Ordinal) &&
        left.Timeout == right.Timeout &&
        SameHeaders(left.Headers, right.Headers);

    private static bool SameHeaders(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value) || !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
