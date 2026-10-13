using AlmToo.Accessor.BrowserGitAccessor.Interface;

namespace AlmToo.Managers.GitBug;

public enum GitBugWorkspaceStatus
{
    Idle,
    LoadingList,
    ListReady,
    LoadingDetail,
    DetailReady,
    EditingIssue,
    AddingComment,
    RefreshingAfterWrite,
    LoadingCommitReview,
    CommitReviewReady,
    Committing,
    CommitSucceeded,
    Unavailable,
    Unsupported,
    Failed
}

/// <summary>Passive, user-safe state for browsing Git-Bug issues and reviewing a local commit.</summary>
public sealed class GitBugWorkspaceState
{
    public GitBugWorkspaceStatus Status { get; internal set; }
    public bool RepositoryAvailable { get; internal set; }
    public bool IsBusy => Status is GitBugWorkspaceStatus.LoadingList
        or GitBugWorkspaceStatus.LoadingDetail
        or GitBugWorkspaceStatus.EditingIssue
        or GitBugWorkspaceStatus.AddingComment
        or GitBugWorkspaceStatus.RefreshingAfterWrite
        or GitBugWorkspaceStatus.LoadingCommitReview
        or GitBugWorkspaceStatus.Committing;
    public bool IsWriteBusy => Status is GitBugWorkspaceStatus.EditingIssue
        or GitBugWorkspaceStatus.AddingComment
        or GitBugWorkspaceStatus.RefreshingAfterWrite
        or GitBugWorkspaceStatus.LoadingCommitReview
        or GitBugWorkspaceStatus.Committing;
    public string? WriteOperation { get; internal set; }
    public bool? LastWriteSucceeded { get; internal set; }
    public bool? LastRefreshSucceeded { get; internal set; }
    public IReadOnlyList<GitBugIssueSummary> Issues { get; internal set; } = Array.Empty<GitBugIssueSummary>();
    public int TotalCount { get; internal set; }
    public string? NextCursor { get; internal set; }
    public GitBugIssueId? SelectedIssueId { get; internal set; }
    public GitBugIssueDetail? SelectedIssue { get; internal set; }
    public IReadOnlyList<ChangedFile> ChangedFiles { get; internal set; } = Array.Empty<ChangedFile>();
    public CommitRequest? ProposedCommit { get; internal set; }
    public CommitInfo? LastCommit { get; internal set; }
    public bool CommitReviewLoaded => Status is GitBugWorkspaceStatus.CommitReviewReady
        or GitBugWorkspaceStatus.Committing
        or GitBugWorkspaceStatus.CommitSucceeded;
    public bool CanConfirmCommit => CommitReviewLoaded && ProposedCommit is not null && !IsBusy;
    public bool HasChangedFiles => ChangedFiles.Count > 0;
    public string? Message { get; internal set; }
    public GitOperationFailureKind? FailureKind { get; internal set; }
    public bool CanRetry => Status == GitBugWorkspaceStatus.Failed;
}
