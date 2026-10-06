namespace AlmToo.Accessor.BrowserGitAccessor.Interface;

/// <summary>
/// Protects the external Git-Bug storage and browser-platform boundary, with a substitution seam for fixture-backed and future remote implementations.
/// </summary>
public interface IGitBugAccessor
{
    ValueTask<GitOperationResult<GitBugIssuePage>> ListIssuesAsync(
        GitBugIssueQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<GitOperationResult<GitBugIssueDetail>> GetIssueAsync(
        GitBugIssueId issueId,
        CancellationToken cancellationToken = default);
}
