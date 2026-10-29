using AlmToo.Accessor.BrowserGitAccessor.Interface;

namespace AlmToo.Managers.Workspace.Interface;

/// <summary>Passive state owned by the application Manager for the complete workspace use-case boundary.</summary>
public sealed record WorkspaceState
{
    public WorkspaceOperation Operation { get; internal set; }
    public bool IsBusy => Operation != WorkspaceOperation.None;
    public WorkspaceError? Error { get; internal set; }
    public WorkspaceResult? Result { get; internal set; }

    public RepositoryInfo? Repository { get; internal set; }
    public bool RepositoryAvailable => Repository is not null;
    public IReadOnlyList<RepositoryFileEntry> Entries { get; internal set; } = Array.Empty<RepositoryFileEntry>();
    public string CurrentPath { get; internal set; } = "/";
    public string? SelectedFilePath { get; internal set; }
    public string? SelectedFileContent { get; internal set; }
    public string EditableFileContent { get; internal set; } = string.Empty;
    public bool HasUnsavedChanges => SelectedFileContent is not null && EditableFileContent != SelectedFileContent;

    public IReadOnlyList<ChangedFile> ChangedFiles { get; internal set; } = Array.Empty<ChangedFile>();
    public bool HasChangedFiles => ChangedFiles.Count > 0;
    public GitPublicationReview? PublicationReview { get; internal set; }
    public IReadOnlyList<GitRefUpdate> ChangedRefs => PublicationReview?.Updates ?? Array.Empty<GitRefUpdate>();
    public bool HasChangedRefs => ChangedRefs.Count > 0;
    public bool PublicationReviewLoaded => PublicationReview is not null;
    public bool PublicationConfirmed { get; internal set; }
    public GitPublicationReceipt? PublicationReceipt { get; internal set; }
    public GitOperationFailureKind? PublicationFailureKind { get; internal set; }
    public bool CanPublishPublication => PublicationReviewLoaded && PublicationConfirmed && !IsBusy;
    public bool CommitReviewLoaded { get; internal set; }
    public WorkspaceCommitInput? ProposedCommit { get; internal set; }
    public CommitInfo? CreatedCommit { get; internal set; }
    public bool CanConfirmCommit => CommitReviewLoaded && ProposedCommit is not null && !IsBusy;
    public bool? LastWriteSucceeded { get; internal set; }
    public bool? LastRefreshSucceeded { get; internal set; }

    public CurrentBranch? CurrentBranch { get; internal set; }
    public IReadOnlyList<BranchChoice> AvailableBranches { get; internal set; } = Array.Empty<BranchChoice>();
    public CommitHistory? RecentHistory { get; internal set; }
    public bool IsBranchHistoryLoading { get; internal set; }
    public bool IsBranchSwitching { get; internal set; }
    public bool BranchHistoryWasCancelled { get; internal set; }
    public BranchHistoryFailure? BranchHistoryFailure { get; internal set; }
    public string? BranchHistoryFailureCode => BranchHistoryFailure?.Category switch
    {
        BranchHistoryFailureCategory.RepositoryUnavailable => "repository-unavailable",
        BranchHistoryFailureCategory.DataMalformed => "data-malformed",
        BranchHistoryFailureCategory.InvalidRequest => "invalid-request",
        BranchHistoryFailureCategory.UnsupportedReference => "unsupported-reference",
        BranchHistoryFailureCategory.ResultLimitExceeded => "result-limit-exceeded",
        BranchHistoryFailureCategory.Unknown => "unknown",
        _ => null
    };

    public PushReview? PushReview { get; internal set; }
    public bool PushConfirmed { get; internal set; }
    public bool HasCredential { get; internal set; }
    public bool CredentialReplacementRequired { get; internal set; }
    public GitOperationFailureKind? PushFailureKind { get; internal set; }
    public SynchronizationReview? SynchronizationReview { get; internal set; }
    public SynchronizationDecisionState? SynchronizationDecision { get; internal set; }
    public SynchronizationFailureCategory? SynchronizationFailureCategory { get; internal set; }
    public string? SynchronizationFailureCode => SynchronizationFailureCategory switch
    {
        AlmToo.Accessor.BrowserGitAccessor.Interface.SynchronizationFailureCategory.UnsavedEditorChanges => "unsaved-editor-changes",
        AlmToo.Accessor.BrowserGitAccessor.Interface.SynchronizationFailureCategory.UncommittedWorkingTree => "uncommitted-working-tree",
        AlmToo.Accessor.BrowserGitAccessor.Interface.SynchronizationFailureCategory.CredentialRejected => "credential-rejected",
        AlmToo.Accessor.BrowserGitAccessor.Interface.SynchronizationFailureCategory.NetworkUnavailable => "network-unavailable",
        AlmToo.Accessor.BrowserGitAccessor.Interface.SynchronizationFailureCategory.UnsupportedRef => "unsupported-ref",
        AlmToo.Accessor.BrowserGitAccessor.Interface.SynchronizationFailureCategory.DivergentHistory => "divergent-history",
        AlmToo.Accessor.BrowserGitAccessor.Interface.SynchronizationFailureCategory.Cancelled => "cancelled",
        AlmToo.Accessor.BrowserGitAccessor.Interface.SynchronizationFailureCategory.InvalidRepository => "invalid-repository",
        AlmToo.Accessor.BrowserGitAccessor.Interface.SynchronizationFailureCategory.Unknown => "unknown",
        _ => null
    };
    public string? PushFailureCategory => PushFailureKind switch
    {
        GitOperationFailureKind.CredentialRejected => "credential-rejected",
        GitOperationFailureKind.RemoteAhead => "remote-ahead",
        GitOperationFailureKind.NetworkUnavailable => "network-unavailable",
        GitOperationFailureKind.UnsupportedRef => "unsupported-ref",
        GitOperationFailureKind.Unknown => "unknown",
        _ => null
    };
    public GitBugState GitBug { get; } = new();
}

/// <summary>Passive feature state for the Git-Bug use-case section of <see cref="WorkspaceState"/>.</summary>
public sealed record GitBugState
{
    public GitBugWorkspaceStatus Status { get; internal set; }
    public bool RepositoryAvailable { get; internal set; }
    public bool IsBusy => Status is GitBugWorkspaceStatus.LoadingList
        or GitBugWorkspaceStatus.LoadingDetail
        or GitBugWorkspaceStatus.EditingIssue
        or GitBugWorkspaceStatus.AddingComment
        or GitBugWorkspaceStatus.RefreshingAfterWrite;
    public bool IsWriteBusy => Status is GitBugWorkspaceStatus.EditingIssue
        or GitBugWorkspaceStatus.AddingComment
        or GitBugWorkspaceStatus.RefreshingAfterWrite;
    public string? WriteOperation { get; internal set; }
    public bool? LastWriteSucceeded { get; internal set; }
    public bool? LastRefreshSucceeded { get; internal set; }
    public IReadOnlyList<GitBugIssueSummary> Issues { get; internal set; } = Array.Empty<GitBugIssueSummary>();
    public int TotalCount { get; internal set; }
    public string? NextCursor { get; internal set; }
    public GitBugIssueId? SelectedIssueId { get; internal set; }
    public GitBugIssueDetail? SelectedIssue { get; internal set; }
    public string? Message { get; internal set; }
    public GitOperationFailureKind? FailureKind { get; internal set; }
    public bool CanRetry => Status == GitBugWorkspaceStatus.Failed;
}
