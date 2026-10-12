namespace AlmToo.Accessor.BrowserGitAccessor.Interface;

public record GitOperationResult(
    string Operation,
    bool Succeeded,
    string Message,
    string? Diagnostic = null,
    GitOperationFailureKind? FailureKind = null);

public record GitOperationResult<T>(
    string Operation,
    bool Succeeded,
    string Message,
    T? Value = default,
    string? Diagnostic = null,
    GitOperationFailureKind? FailureKind = null);

public enum GitOperationFailureKind
{
    CredentialRejected,
    RemoteAhead,
    NetworkUnavailable,
    UnsupportedRef,
    GitBugDataUnavailable,
    GitBugFormatUnsupported,
    GitBugDataMalformed,
    GitBugWriteInvalidRequest,
    GitBugWritePreconditionFailed,
    GitBugWriteConflict,
    GitBugWriteUnsupportedFormat,
    GitBugWriteUnavailable,
    GitBugWriteSerializationFailed,
    GitBugWriteUnknown,
    Unknown
}

/// <summary>Stable operation names for the Git-Bug write boundary.</summary>
public static class GitBugWriteOperations
{
    public const string IssueEdit = "gitBug.issue.edit";
    public const string CommentCreate = "gitBug.comment.create";
    public const string WriteReview = "gitBug.write.review";
    public const string WriteCommit = "gitBug.write.commit";
}

public enum GitBugWriteStage
{
    Review,
    Prepare,
    Commit
}

/// <summary>Credential-free identity for the active repository/workspace.</summary>
public sealed record GitBugRepositoryIdentity(string RepositoryUrl, string WorkspaceName);

/// <summary>Immutable review preconditions; a changed revision requires a new review.</summary>
public sealed record GitBugWritePreconditions(
    GitBugRepositoryIdentity Repository,
    GitBugIssueId IssueId,
    string ExpectedRevision,
    string ReviewedOperation);

/// <summary>Canonical serialization facts selected by the Accessor/Resource boundary.</summary>
public sealed record GitBugSerializationMetadata(
    string StorageVersion,
    string Encoding,
    bool Deterministic);

public sealed record GitBugIssueEditRequest(
    GitBugIssueId IssueId,
    string Title,
    string Description,
    GitBugWritePreconditions Preconditions,
    GitBugSerializationMetadata Serialization);

public sealed record GitBugCommentCreateRequest(
    GitBugIssueId IssueId,
    string Body,
    GitBugWritePreconditions Preconditions,
    GitBugSerializationMetadata Serialization);

public sealed record GitBugWriteReview(
    string ReviewId,
    GitBugWritePreconditions Preconditions,
    GitBugSerializationMetadata Serialization);

public sealed record GitBugIssueEditResult(
    GitBugIssueId IssueId,
    string Revision,
    string ReviewId);

public sealed record GitBugCommentCreateResult(
    GitBugIssueId IssueId,
    string CommentId,
    string Revision,
    string ReviewId);

public sealed record GitBugWriteReceipt(
    string Operation,
    string ReviewId,
    string Revision,
    int BoundedItemCount,
    bool Committed);

/// <summary>Pure contract checks shared by Accessor implementations and contract tests.</summary>
public static class GitBugWriteContract
{
    public const string SupportedStorageVersion = "v0.11.0";
    public const string Utf8Encoding = "utf-8";
    public const int MaximumTitleLength = 4096;
    public const int MaximumBodyLength = 1024 * 1024;
    public const int MaximumRevisionLength = 128;
    public const int MaximumReviewIdLength = 128;

    public static bool IsValidOperation(string? operation) => operation is
        GitBugWriteOperations.IssueEdit or
        GitBugWriteOperations.CommentCreate or
        GitBugWriteOperations.WriteReview or
        GitBugWriteOperations.WriteCommit;

    public static bool IsValidFailure(GitOperationFailureKind kind) => kind is
        GitOperationFailureKind.GitBugWriteInvalidRequest or
        GitOperationFailureKind.GitBugWritePreconditionFailed or
        GitOperationFailureKind.GitBugWriteConflict or
        GitOperationFailureKind.GitBugWriteUnsupportedFormat or
        GitOperationFailureKind.GitBugWriteUnavailable or
        GitOperationFailureKind.GitBugWriteSerializationFailed or
        GitOperationFailureKind.GitBugWriteUnknown;

    public static bool IsValidSerialization(GitBugSerializationMetadata? metadata) =>
        metadata is not null
        && metadata.StorageVersion == SupportedStorageVersion
        && metadata.Encoding == Utf8Encoding
        && metadata.Deterministic;

    public static bool IsValidPreconditions(GitBugWritePreconditions? preconditions) =>
        preconditions is not null
        && preconditions.Repository is not null
        && Uri.TryCreate(preconditions.Repository.RepositoryUrl, UriKind.Absolute, out var repository)
        && repository.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrWhiteSpace(repository.UserInfo) 
        && !string.IsNullOrWhiteSpace(preconditions.Repository.WorkspaceName)
        && preconditions.IssueId.Value is { Length: 64 } issueId && issueId.All(Uri.IsHexDigit)
        && IsBounded(preconditions.ExpectedRevision, MaximumRevisionLength)
        && IsValidOperation(preconditions.ReviewedOperation);

    public static bool IsValidIssueEdit(GitBugIssueEditRequest? request) =>
        request is not null
        && IsValidPreconditions(request.Preconditions)
        && request.Preconditions.IssueId == request.IssueId
        && IsValidSerialization(request.Serialization)
        && IsBounded(request.IssueId.Value, 64)
        && IsBounded(request.Title, MaximumTitleLength)
        && IsBounded(request.Description, MaximumBodyLength, allowEmpty: true)
        && request.Preconditions.ReviewedOperation == GitBugWriteOperations.IssueEdit;

    public static bool IsValidComment(GitBugCommentCreateRequest? request) =>
        request is not null
        && IsValidPreconditions(request.Preconditions)
        && request.Preconditions.IssueId == request.IssueId
        && IsValidSerialization(request.Serialization)
        && IsBounded(request.Body, MaximumBodyLength, allowEmpty: false)
        && request.Preconditions.ReviewedOperation == GitBugWriteOperations.CommentCreate;

    private static bool IsBounded(string? value, int maximum, bool allowEmpty = false) =>
        value is not null
        && (allowEmpty || value.Length > 0)
        && value.Length <= maximum
        && value.IndexOfAny(['\0', '\r', '\n']) < 0;
}

public readonly record struct GitBugIssueId(string Value);

public sealed record GitBugIssueQuery(
    GitBugIssueState? State = null,
    string? SearchText = null,
    string? Cursor = null,
    int PageSize = 50);

public enum GitBugIssueState
{
    Open,
    Closed
}

public sealed record GitBugIssuePage(
    IReadOnlyList<GitBugIssueSummary> Issues,
    string? NextCursor,
    int TotalCount);

public sealed record GitBugIssueSummary(
    GitBugIssueId Id,
    string Title,
    GitBugIssueState State,
    IReadOnlyList<string>? Labels,
    string? AuthorDisplayName);

public sealed record GitBugIssueDetail(
    GitBugIssueSummary Summary,
    string? Description,
    IReadOnlyList<GitBugComment> Comments);

public sealed record GitBugComment(
    string AuthorDisplayName,
    string Body);

public record RepositoryOpenRequest(string RepositoryUrl, string WorkspaceName);
public record RepositoryInfo(string RepositoryUrl, string WorkspaceName, string RootPath, bool WasCloned);

public enum FileFilterKind { DirectoryEntries, TextContent }
public record FilterFilesRequest(string Path, FileFilterKind Kind);
public record FilterFilesResult(string Path, IReadOnlyList<RepositoryFileEntry> Entries, TextFileContent? TextFile = null);
public record UpdateFilesRequest(IReadOnlyList<TextFileUpdate> Updates);
public record TextFileUpdate(string Path, string Content);
public record UpdateFilesResult(IReadOnlyList<string> UpdatedPaths);

public record RepositoryFileEntry(
    string Path,
    string Name,
    RepositoryFileKind Kind,
    long? SizeBytes = null,
    bool IsEditableText = false);
public enum RepositoryFileKind { File, Directory }

public record TextFileContent(
    string Path,
    string Content,
    string? Encoding = "utf-8",
    long? SizeBytes = null);

public record ChangedFile(string Path, GitChangeKind ChangeKind);
public enum GitChangeKind { Added, Modified, Deleted, Renamed, Untracked, Unknown }

public record CommitRequest(string Message, string AuthorName, string AuthorEmail);
public record CommitInfo(string CommitId, string Message);

public record PushReview(
    string RepositoryUrl,
    string Branch,
    string OutgoingCommitId,
    string DestinationRef);

public record PushRequest(
    string RepositoryUrl,
    string Branch,
    string OutgoingCommitId,
    string DestinationRef);

public record PushResult(
    string RepositoryUrl,
    string Branch,
    string DestinationRef,
    string PushedCommitId);

/// <summary>Describes whether synchronization should only inspect or may apply an accepted review.</summary>
public enum SynchronizationIntent
{
    Fetch,
    Review,
    Apply
}

/// <summary>Stable, user-safe classification of the synchronization decision.</summary>
public enum SynchronizationDecisionState
{
    Current,
    Ahead,
    Divergent,
    ReadyToApply,
    Applied,
    Cancelled,
    Failed
}

/// <summary>Stable categories that may cross the Resource-to-Manager boundary without Git diagnostics.</summary>
public enum SynchronizationFailureCategory
{
    UnsavedEditorChanges,
    UncommittedWorkingTree,
    CredentialRejected,
    NetworkUnavailable,
    UnsupportedRef,
    DivergentHistory,
    Cancelled,
    InvalidRepository,
    Unknown
}

public record SynchronizationRequest(
    string RepositoryUrl,
    string Branch,
    SynchronizationIntent Intent);

/// <summary>Remote commit information used for review; it contains no credentials or transport diagnostics.</summary>
public record IncomingCommitMetadata(
    string CommitId,
    string ParentCommitId,
    string Message,
    string AuthorName,
    DateTimeOffset AuthoredAt);

public record ChangedFileSummary(
    string Path,
    GitChangeKind ChangeKind);

public record SynchronizationReview(
    string RepositoryUrl,
    string Branch,
    string LocalCommitId,
    IncomingCommitMetadata? IncomingCommit,
    IReadOnlyList<ChangedFileSummary> ChangedFiles,
    SynchronizationDecisionState Decision);

/// <summary>A safe message and stable category; raw Accessor diagnostics remain outside this contract.</summary>
public record SynchronizationFailure(
    SynchronizationFailureCategory Category,
    string Message);
