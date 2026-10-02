namespace AlmToo.Resource.BrowserGitResource.Data;

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
    Unknown
}

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
