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
    LoadingPushReview,
    PushReviewReady,
    Pushing,
    PushSucceeded,
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
        or GitBugWorkspaceStatus.Committing
        or GitBugWorkspaceStatus.LoadingPushReview
        or GitBugWorkspaceStatus.Pushing;
    public bool IsWriteBusy => Status is GitBugWorkspaceStatus.EditingIssue
        or GitBugWorkspaceStatus.AddingComment
        or GitBugWorkspaceStatus.RefreshingAfterWrite
        or GitBugWorkspaceStatus.LoadingCommitReview
        or GitBugWorkspaceStatus.Committing
        or GitBugWorkspaceStatus.LoadingPushReview
        or GitBugWorkspaceStatus.Pushing;
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

    public PushReview? PushReview { get; internal set; }
    public bool PushConfirmed { get; internal set; }
    public bool HasCredential { get; internal set; }
    public bool CredentialReplacementRequired { get; internal set; }
    public GitOperationFailureKind? PushFailureKind { get; internal set; }
    public bool PushReviewLoaded => Status is GitBugWorkspaceStatus.PushReviewReady
        or GitBugWorkspaceStatus.Pushing
        or GitBugWorkspaceStatus.PushSucceeded;
    public bool CanConfirmPush => PushReviewLoaded && PushReview is not null && HasCredential && !IsBusy;
    public bool CanRetryPush => PushFailureKind is GitOperationFailureKind.CredentialRejected
        or GitOperationFailureKind.RemoteAhead
        or GitOperationFailureKind.NetworkUnavailable
        or GitOperationFailureKind.UnsupportedRef;
    public string? Message { get; internal set; }
    public GitOperationFailureKind? FailureKind { get; internal set; }
    public string? PushFailureCategory => PushFailureKind switch
    {
        GitOperationFailureKind.CredentialRejected => "credential-rejected",
        GitOperationFailureKind.RemoteAhead => "remote-ahead",
        GitOperationFailureKind.NetworkUnavailable => "network-unavailable",
        GitOperationFailureKind.UnsupportedRef => "unsupported-ref",
        GitOperationFailureKind.Unknown => "unknown",
        _ => null
    };
    public bool CanRetry => Status == GitBugWorkspaceStatus.Failed || CanRetryPush;
}
