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
