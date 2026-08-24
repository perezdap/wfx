namespace Wfx.Core;

public enum ApprovalMode
{
    Always,
    Workspace,
    Never,
    AllowAll
}

public static class ApprovalPolicy
{
    public static bool CanPrompt(ApprovalMode mode) => mode switch
    {
        ApprovalMode.Always or ApprovalMode.Workspace => true,
        ApprovalMode.Never or ApprovalMode.AllowAll => false,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown approval mode.")
    };
}

public sealed record ApprovalRequest(
    string ToolName,
    string ArgumentsJson,
    ApprovalLevel Level,
    string Summary);

public interface IApprovalService
{
    ValueTask<bool> ApproveAsync(ApprovalRequest request, CancellationToken cancellationToken = default);
}

public sealed class PolicyApprovalService : IApprovalService
{
    private readonly ApprovalMode _mode;
    private readonly Func<ApprovalRequest, CancellationToken, ValueTask<bool>> _prompt;

    public PolicyApprovalService(
        ApprovalMode mode,
        Func<ApprovalRequest, CancellationToken, ValueTask<bool>> prompt)
    {
        _mode = mode;
        _prompt = prompt;
    }

    public ValueTask<bool> ApproveAsync(ApprovalRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Level == ApprovalLevel.ReadOnly)
        {
            return ValueTask.FromResult(true);
        }

        if (!ApprovalPolicy.CanPrompt(_mode))
        {
            return ValueTask.FromResult(_mode == ApprovalMode.AllowAll);
        }

        return request.Level switch
        {
            ApprovalLevel.WorkspaceWrite when _mode == ApprovalMode.Workspace => ValueTask.FromResult(true),
            _ => _prompt(request, cancellationToken)
        };
    }
}
