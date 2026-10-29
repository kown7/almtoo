using AlmToo.Accessor.BrowserGitAccessor.Interface;

namespace AlmToo.Managers.Workspace.Interface;

/// <summary>Application-safe Git-Bug lifecycle states exposed by the application Manager.</summary>
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
    ReviewReconciliation,
    ApplyReconciliation,
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

/// <summary>A Client-safe outcome for a complete workspace use-case.</summary>
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

/// <summary>Client-safe lifecycle states for remote-change reconciliation.</summary>
public enum WorkspaceReconciliationStatus
{
    Idle,
    Inspecting,
    Ready,
    RequiresResolution,
    Applying,
    Applied,
    Cancelled,
    Unavailable,
    Unsupported,
    Failed
}

/// <summary>Deterministic classification of a path in a local-versus-remote comparison.</summary>
public enum WorkspaceReconciliationChangeKind
{
    Unchanged,
    LocalOnly,
    RemoteOnly,
    ModifiedLocally,
    ModifiedRemotely,
    Converged,
    Divergent,
    Unsupported,
    Malformed
}

/// <summary>Bounded choices the Client may present for a supported reconciliation conflict.</summary>
public enum WorkspaceReconciliationResolution
{
    KeepLocal,
    AcceptRemote
}

/// <summary>One safe, path-scoped comparison input for deterministic reconciliation policy.</summary>
public sealed record WorkspaceFileComparison(
    string Path,
    string? BaseObjectId,
    string? LocalObjectId,
    string? RemoteObjectId,
    bool IsSupported = true);

/// <summary>Typed input to the deterministic reconciliation policy; it contains no transport details.</summary>
public sealed record WorkspaceReconciliationInput(
    IReadOnlyList<WorkspaceFileComparison> Files);

/// <summary>One path-scoped reconciliation classification exposed to Manager state and tests.</summary>
public sealed record WorkspaceReconciliationChange(
    string Path,
    WorkspaceReconciliationChangeKind Kind,
    IReadOnlyList<WorkspaceReconciliationResolution> AllowedResolutions);

/// <summary>Client-safe deterministic reconciliation result.</summary>
public sealed record WorkspaceReconciliationResult(
    WorkspaceReconciliationStatus Status,
    IReadOnlyList<WorkspaceReconciliationChange> Changes,
    string Message,
    bool RequiresExplicitApply);
