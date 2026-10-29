using AlmToo.Accessor.BrowserGitAccessor.Interface;

namespace AlmToo.Tests.Managers.Workspace;

internal sealed class NoOpGitBugAccessor : IGitBugAccessor
{
    public ValueTask<GitOperationResult<GitBugIssuePage>> ListIssuesAsync(
        GitBugIssueQuery query,
        CancellationToken cancellationToken = default) =>
        Unsupported<GitBugIssuePage>("list-issues");

    public ValueTask<GitOperationResult<GitBugIssueDetail>> GetIssueAsync(
        GitBugIssueId issueId,
        CancellationToken cancellationToken = default) =>
        Unsupported<GitBugIssueDetail>("get-issue");

    public ValueTask<GitOperationResult<GitBugIssueEditResult>> EditIssueAsync(
        GitBugIssueEditRequest request,
        CancellationToken cancellationToken = default) =>
        Unsupported<GitBugIssueEditResult>("edit-issue");

    public ValueTask<GitOperationResult<GitBugCommentCreateResult>> CreateCommentAsync(
        GitBugCommentCreateRequest request,
        CancellationToken cancellationToken = default) =>
        Unsupported<GitBugCommentCreateResult>("create-comment");

    private static ValueTask<GitOperationResult<T>> Unsupported<T>(string operation) =>
        ValueTask.FromResult(new GitOperationResult<T>(
            operation,
            Succeeded: false,
            Message: "Git-Bug is not used by this test.",
            FailureKind: GitOperationFailureKind.Unknown));
}
