using AlmToo.Accessor.BrowserGitAccessor.Interface;

namespace AlmToo.Managers.GitBug;

public enum GitBugWorkspaceStatus
{
    Idle,
    LoadingList,
    ListReady,
    LoadingDetail,
    DetailReady,
    Unavailable,
    Unsupported,
    Failed
}

/// <summary>Passive, user-safe state for browsing Git-Bug issues in the active repository.</summary>
public sealed class GitBugWorkspaceState
{
    public GitBugWorkspaceStatus Status { get; internal set; }
    public bool RepositoryAvailable { get; internal set; }
    public bool IsBusy => Status is GitBugWorkspaceStatus.LoadingList or GitBugWorkspaceStatus.LoadingDetail;
    public IReadOnlyList<GitBugIssueSummary> Issues { get; internal set; } = Array.Empty<GitBugIssueSummary>();
    public int TotalCount { get; internal set; }
    public string? NextCursor { get; internal set; }
    public GitBugIssueId? SelectedIssueId { get; internal set; }
    public GitBugIssueDetail? SelectedIssue { get; internal set; }
    public string? Message { get; internal set; }
    public GitOperationFailureKind? FailureKind { get; internal set; }
    public bool CanRetry => Status == GitBugWorkspaceStatus.Failed;
}
