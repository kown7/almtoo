using AlmToo.Accessor.BrowserGitAccessor.Interface;

namespace AlmToo.Managers.Repositories;

public enum RepositoryWorkspaceOperation
{
    None,
    InitializeCredential,
    OpenRepository,
    LoadDirectory,
    LoadFile,
    SaveFile,
    RefreshStatus,
    Commit,
    InspectPush,
    StoreCredential,
    ForgetCredential,
    Push,
    ReviewSynchronization,
    ApplySynchronization
}

public sealed record RepositoryWorkspaceError(
    RepositoryWorkspaceOperation Operation,
    string Message,
    string? Diagnostic = null,
    GitOperationFailureKind? FailureKind = null,
    bool WasCancelled = false);

public sealed record RepositoryWorkspaceResult(
    RepositoryWorkspaceOperation Operation,
    bool Succeeded,
    string Message,
    GitOperationFailureKind? FailureKind = null);

/// <summary>Presentation input for the create-commit workflow.</summary>
public sealed record RepositoryCommitInput(string Message, string AuthorName, string AuthorEmail);

/// <summary>Manager-owned, credential-free state for one browser repository workspace.</summary>
public sealed class RepositoryWorkspaceState
{
    public RepositoryWorkspaceOperation Operation { get; internal set; }
    public bool IsBusy => Operation != RepositoryWorkspaceOperation.None;
    public RepositoryWorkspaceError? Error { get; internal set; }
    public RepositoryWorkspaceResult? Result { get; internal set; }

    public RepositoryInfo? Repository { get; internal set; }
    public IReadOnlyList<RepositoryFileEntry> Entries { get; internal set; } = Array.Empty<RepositoryFileEntry>();
    public string CurrentPath { get; internal set; } = "/";
    public string? SelectedFilePath { get; internal set; }
    public string? SelectedFileContent { get; internal set; }
    public string EditableFileContent { get; internal set; } = string.Empty;
    public bool HasUnsavedChanges => SelectedFileContent is not null && EditableFileContent != SelectedFileContent;

    public IReadOnlyList<ChangedFile> ChangedFiles { get; internal set; } = Array.Empty<ChangedFile>();
    public CommitInfo? CreatedCommit { get; internal set; }

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
}
