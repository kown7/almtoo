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

/// <summary>Bounded failure categories that may cross the Manager reconciliation boundary.</summary>
public enum WorkspaceReconciliationFailureKind
{
    None,
    MalformedInput,
    UnsupportedFormat,
    LocalUnavailable,
    RemoteUnavailable,
    StaleReview,
    ApplyRejected,
    Cancelled,
    Unknown
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

/// <summary>Credential-free coordinates that fence an explicit apply to the review that produced it.</summary>
public sealed record WorkspaceReconciliationApplyMetadata(
    string ReviewId,
    string SourceRevision,
    int BoundedChangeCount,
    DateTimeOffset ReviewedAt,
    bool ExplicitlyConfirmed = false);

/// <summary>Client-safe deterministic reconciliation result.</summary>
public sealed record WorkspaceReconciliationResult(
    WorkspaceReconciliationStatus Status,
    IReadOnlyList<WorkspaceReconciliationChange> Changes,
    string Message,
    bool RequiresExplicitApply,
    WorkspaceReconciliationFailureKind? FailureKind = null,
    WorkspaceReconciliationApplyMetadata? ApplyMetadata = null);

/// <summary>Pure bounds and safety checks for Manager-owned reconciliation contracts.</summary>
public static class WorkspaceReconciliationContract
{
    public const int MaximumFiles = 1000;
    public const int MaximumPathLength = 1024;
    public const int MaximumObjectIdLength = 128;
    public const int MaximumMessageLength = 512;
    public const int MaximumReviewIdLength = 128;
    public const int MaximumSourceRevisionLength = 128;
    public const int MaximumResolutions = 2;

    public static bool IsValidInput(WorkspaceReconciliationInput? input) =>
        input is not null
        && input.Files is not null
        && input.Files.Count <= MaximumFiles
        && input.Files.All(IsValidComparison)
        && HasUniquePaths(input.Files.Select(file => file.Path));

    public static bool IsValidComparison(WorkspaceFileComparison? comparison) =>
        comparison is not null
        && IsSafePath(comparison.Path)
        && IsSafeObjectId(comparison.BaseObjectId)
        && IsSafeObjectId(comparison.LocalObjectId)
        && IsSafeObjectId(comparison.RemoteObjectId);

    public static bool IsValidChange(WorkspaceReconciliationChange? change) =>
        change is not null
        && IsSafePath(change.Path)
        && Enum.IsDefined(change.Kind)
        && change.AllowedResolutions is not null
        && change.AllowedResolutions.Count <= MaximumResolutions
        && change.AllowedResolutions.All(IsValidResolution)
        && change.AllowedResolutions.Distinct().Count() == change.AllowedResolutions.Count;

    public static bool IsValidApplyMetadata(WorkspaceReconciliationApplyMetadata? metadata) =>
        metadata is not null
        && IsSafeText(metadata.ReviewId, MaximumReviewIdLength)
        && IsSafeText(metadata.SourceRevision, MaximumSourceRevisionLength)
        && metadata.BoundedChangeCount is >= 0 and <= MaximumFiles
        && metadata.ReviewedAt != default;

    public static bool IsValidResult(WorkspaceReconciliationResult? result) =>
        result is not null
        && Enum.IsDefined(result.Status)
        && result.Changes is not null
        && result.Changes.Count <= MaximumFiles
        && result.Changes.All(IsValidChange)
        && IsSafeText(result.Message, MaximumMessageLength)
        && (result.FailureKind is null || Enum.IsDefined(result.FailureKind.Value))
        && (result.ApplyMetadata is null || IsValidApplyMetadata(result.ApplyMetadata));

    public static bool IsValidFailure(WorkspaceReconciliationFailureKind kind) =>
        Enum.IsDefined(kind);

    private static bool IsValidResolution(WorkspaceReconciliationResolution resolution) =>
        Enum.IsDefined(resolution);

    private static bool HasUniquePaths(IEnumerable<string> paths) =>
        paths.Distinct(StringComparer.Ordinal).Count() == paths.Count();

    private static bool IsSafePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > MaximumPathLength
            || !path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('\\')
            || path.IndexOfAny(['\0', '\r', '\n']) >= 0
            || path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = path.Split('/');
        return segments.Length > 1
            && segments.Skip(1).All(segment => !string.IsNullOrWhiteSpace(segment) && segment is not "." and not "..");
    }

    private static bool IsSafeObjectId(string? objectId) =>
        objectId is null
        || (objectId.Length <= MaximumObjectIdLength
            && !string.IsNullOrWhiteSpace(objectId)
            && objectId.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character)));

    private static bool IsSafeText(string? value, int maximum) =>
        value is not null
        && value.Trim().Length > 0
        && value.Length <= maximum
        && value.IndexOfAny(['\0', '\r', '\n']) < 0;
}
