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

/// <summary>One explicit, path-scoped resolution selected from a reviewed reconciliation result.</summary>
public sealed record WorkspaceReconciliationResolutionSelection(
    string Path,
    WorkspaceReconciliationResolution Resolution);

/// <summary>Remote object state that must still match immediately before a reviewed apply.</summary>
public sealed record WorkspaceReconciliationRemotePrecondition(
    string Path,
    string? ExpectedRemoteObjectId);

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

/// <summary>Explicit input for turning one exact review into a bounded apply plan.</summary>
public sealed record WorkspaceReconciliationApplyRequest(
    string ReviewId,
    string SourceRevision,
    IReadOnlyList<WorkspaceReconciliationResolutionSelection> Selections,
    IReadOnlyList<WorkspaceReconciliationRemotePrecondition> RemotePreconditions,
    bool ExplicitlyConfirmed);

/// <summary>Deterministic, credential-free plan produced only after a reviewed request passes Engine policy.</summary>
public sealed record WorkspaceReconciliationApplyPlan(
    WorkspaceReconciliationApplyMetadata Review,
    IReadOnlyList<WorkspaceReconciliationResolutionSelection> Selections,
    IReadOnlyList<WorkspaceReconciliationRemotePrecondition> RemotePreconditions);

/// <summary>Bounded result of reviewed apply-plan construction. Failure details never include Accessor diagnostics.</summary>
public sealed record WorkspaceReconciliationApplyPlanResult(
    bool Succeeded,
    string Message,
    WorkspaceReconciliationFailureKind FailureKind,
    WorkspaceReconciliationApplyPlan? Plan = null);

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
    public const int MaximumPreconditions = MaximumFiles;

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

    public static bool IsValidResolutionSelection(WorkspaceReconciliationResolutionSelection? selection) =>
        selection is not null
        && IsSafePath(selection.Path)
        && IsValidResolution(selection.Resolution);

    public static bool IsValidRemotePrecondition(WorkspaceReconciliationRemotePrecondition? precondition) =>
        precondition is not null
        && IsSafePath(precondition.Path)
        && IsSafeObjectId(precondition.ExpectedRemoteObjectId);

    public static bool IsValidApplyMetadata(WorkspaceReconciliationApplyMetadata? metadata) =>
        metadata is not null
        && IsSafeText(metadata.ReviewId, MaximumReviewIdLength)
        && IsSafeText(metadata.SourceRevision, MaximumSourceRevisionLength)
        && metadata.BoundedChangeCount is >= 0 and <= MaximumFiles
        && metadata.ReviewedAt != default;

    public static bool IsValidApplyRequest(WorkspaceReconciliationApplyRequest? request) =>
        request is not null
        && IsSafeText(request.ReviewId, MaximumReviewIdLength)
        && IsSafeText(request.SourceRevision, MaximumSourceRevisionLength)
        && request.Selections is not null
        && request.Selections.Count <= MaximumFiles
        && request.Selections.All(IsValidResolutionSelection)
        && HasUniquePaths(request.Selections.Select(selection => selection.Path))
        && request.RemotePreconditions is not null
        && request.RemotePreconditions.Count <= MaximumPreconditions
        && request.RemotePreconditions.All(IsValidRemotePrecondition)
        && HasUniquePaths(request.RemotePreconditions.Select(precondition => precondition.Path));

    public static bool IsValidApplyPlan(WorkspaceReconciliationApplyPlan? plan) =>
        plan is not null
        && IsValidApplyMetadata(plan.Review)
        && plan.Selections is not null
        && plan.Selections.Count <= MaximumFiles
        && plan.Selections.All(IsValidResolutionSelection)
        && HasUniquePaths(plan.Selections.Select(selection => selection.Path))
        && plan.RemotePreconditions is not null
        && plan.RemotePreconditions.Count <= MaximumPreconditions
        && plan.RemotePreconditions.All(IsValidRemotePrecondition)
        && HasUniquePaths(plan.RemotePreconditions.Select(precondition => precondition.Path));

    public static bool IsValidApplyPlanResult(WorkspaceReconciliationApplyPlanResult? result) =>
        result is not null
        && IsSafeText(result.Message, MaximumMessageLength)
        && IsValidFailure(result.FailureKind)
        && (result.Plan is null || IsValidApplyPlan(result.Plan))
        && (result.Succeeded
            ? result.FailureKind == WorkspaceReconciliationFailureKind.None && result.Plan is not null
            : result.Plan is null && result.FailureKind != WorkspaceReconciliationFailureKind.None);

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
            || path.Any(char.IsControl)
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
