namespace AlmToo.Managers.Workspace.Interface;

/// <summary>Supported Git-Bug ref namespaces that may cross the publication boundary.</summary>
public enum GitBugPublicationRefKind
{
    Issue,
    Identity
}

/// <summary>How one reviewed Git-Bug ref changes on the remote.</summary>
public enum GitBugPublicationChangeKind
{
    Create,
    Update
}

/// <summary>Safe policy outcome for Git-Bug publication inspection.</summary>
public enum GitBugPublicationStatus
{
    Ready,
    NoChanges,
    Unavailable,
    Unsupported,
    Malformed,
    Stale
}

/// <summary>Bounded policy failure categories for Git-Bug ref publication.</summary>
public enum GitBugPublicationFailureKind
{
    InvalidInput,
    UnsupportedRef,
    MissingIdentitySupport,
    RemoteAhead,
    StaleReview,
    BoundExceeded,
    ForceUpdateRequired
}

/// <summary>One local or remote Git-Bug ref observation.</summary>
public sealed record GitBugPublicationRefState(
    string RefName,
    string ObjectId);

/// <summary>One exact Git-Bug refspec that the Accessor is allowed to publish.</summary>
public sealed record GitBugPublicationRefSpec(
    string SourceRef,
    string DestinationRef,
    string LocalObjectId,
    string? ExpectedRemoteObjectId,
    GitBugPublicationRefKind Kind,
    GitBugPublicationChangeKind ChangeKind,
    bool Force);

/// <summary>Deterministic, reviewed Git-Bug publication plan.</summary>
public sealed record GitBugPublicationPlan(
    string SourceRevision,
    IReadOnlyList<GitBugPublicationRefSpec> RefSpecs,
    int BoundedRefCount,
    bool RequiresExplicitConfirmation = true);

/// <summary>Input snapshot for deterministic Git-Bug publication policy.</summary>
public sealed record GitBugPublicationInput(
    string SourceRevision,
    IReadOnlyList<GitBugPublicationRefState> LocalRefs,
    IReadOnlyList<GitBugPublicationRefState> RemoteRefs);

/// <summary>Client-safe result of Git-Bug publication inspection.</summary>
public sealed record GitBugPublicationPolicyResult(
    GitBugPublicationStatus Status,
    string Message,
    GitBugPublicationPlan? Plan = null,
    GitBugPublicationFailureKind? FailureKind = null);
