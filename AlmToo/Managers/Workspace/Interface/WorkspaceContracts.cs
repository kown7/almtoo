using AlmToo.Accessor.BrowserGitAccessor.Interface;

namespace AlmToo.Managers.Workspace;

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

public sealed record WorkspaceError(
    WorkspaceOperation Operation,
    string Message,
    string? Diagnostic = null,
    GitOperationFailureKind? FailureKind = null,
    bool WasCancelled = false);

public sealed record WorkspaceResult(
    WorkspaceOperation Operation,
    bool Succeeded,
    string Message,
    GitOperationFailureKind? FailureKind = null);

public sealed record WorkspaceCommitInput(
    string Message,
    string AuthorName,
    string AuthorEmail);
