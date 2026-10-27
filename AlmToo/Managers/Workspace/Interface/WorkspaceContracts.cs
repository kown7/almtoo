using AlmToo.Accessor.BrowserGitAccessor.Interface;

namespace AlmToo.Managers.Workspace.Interface;

/// <summary>Application-safe Git-Bug lifecycle states exposed by the WorkspaceManager.</summary>
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
    Unavailable,
    Unsupported,
    Failed
}

/// <summary>Stable application use-case identifiers. These names are safe for Client state and diagnostics.</summary>
public enum WorkspaceOperation
{
    None,
    InitializeCredential,
    OpenRepository,
    LoadDirectory,
    LoadFile,
    SaveFile,
    RefreshStatus,
    LoadBranchHistory,
    SwitchBranch,
    ReviewCommit,
    Commit,
    ReviewPublication,
    PublishPublication,
    InspectPush,
    StoreCredential,
    ForgetCredential,
    Push,
    ReviewSynchronization,
    ApplySynchronization,
    LoadGitBugIssues,
    OpenGitBugIssue,
    EditGitBugIssue,
    AddGitBugComment
}

/// <summary>
/// A Client-safe failed or cancelled Manager outcome. Accessor diagnostics are intentionally not part of this contract.
/// </summary>
public sealed record WorkspaceError(
    WorkspaceOperation Operation,
    string Message,
    GitOperationFailureKind? FailureKind = null,
    bool WasCancelled = false);

/// <summary>A Client-safe outcome for a complete WorkspaceManager use-case.</summary>
public sealed record WorkspaceResult(
    WorkspaceOperation Operation,
    bool Succeeded,
    string Message,
    GitOperationFailureKind? FailureKind = null);

/// <summary>Bounded application input for the reviewed local commit use-case.</summary>
public sealed record WorkspaceCommitInput(
    string Message,
    string AuthorName,
    string AuthorEmail);
