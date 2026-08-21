namespace Wfx.Core;

public enum ApprovalMode
{
    Always,
    Workspace,
    Never
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

        return _mode switch
        {
            ApprovalMode.Workspace when request.Level == ApprovalLevel.WorkspaceWrite => ValueTask.FromResult(true),
            ApprovalMode.Never => ValueTask.FromResult(false),
            _ => _prompt(request, cancellationToken)
        };
    }
}
