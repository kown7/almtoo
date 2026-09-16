namespace AlmToo.Services.Git;

/// <summary>
/// Normalized outcome for browser-local Git operations. The user-facing message is safe
/// to show in the UI; diagnostics are intended for local troubleshooting only.
/// </summary>
public record GitOperationResult(
    string Operation,
    bool Succeeded,
    string Message,
    string? Diagnostic = null)
{
    public static GitOperationResult Success(string operation, string message) =>
        new(operation, true, message);

    public static GitOperationResult Failure(string operation, string message, string? diagnostic = null) =>
        new(operation, false, message, diagnostic);
}

public record GitOperationResult<T>(
    string Operation,
    bool Succeeded,
    string Message,
    T? Value = default,
    string? Diagnostic = null)
{
    public static GitOperationResult<T> Success(string operation, string message, T value) =>
        new(operation, true, message, value);

    public static GitOperationResult<T> Failure(string operation, string message, string? diagnostic = null) =>
        new(operation, false, message, default, diagnostic);
}

public record RepositoryOpenRequest(
    string RepositoryUrl,
    string WorkspaceName);

public record RepositoryInfo(
    string RepositoryUrl,
    string WorkspaceName,
    string RootPath,
    bool WasCloned);

public record RepositoryFileEntry(
    string Path,
    string Name,
    RepositoryFileKind Kind,
    long? SizeBytes = null,
    bool IsEditableText = false);

public enum RepositoryFileKind
{
    File,
    Directory
}

public record TextFileContent(
    string Path,
    string Content,
    string? Encoding = "utf-8",
    long? SizeBytes = null);

public record ChangedFile(
    string Path,
    GitChangeKind ChangeKind);

public enum GitChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Untracked,
    Unknown
}

public record CommitRequest(
    string Message,
    string AuthorName,
    string AuthorEmail);

public record CommitInfo(
    string CommitId,
    string Message);

/// <summary>
/// Immutable snapshot of the active repository state presented before a push.
/// </summary>
public record PushReview(
    string RepositoryUrl,
    string Branch,
    string OutgoingCommitId,
    string DestinationRef);

/// <summary>
/// Token-free request containing the exact push state previously reviewed by the user.
/// Credentials are supplied separately to the service and are never retained by this model.
/// </summary>
public record PushRequest(
    string RepositoryUrl,
    string Branch,
    string OutgoingCommitId,
    string DestinationRef);

/// <summary>
/// Successful remote push identity returned to callers.
/// </summary>
public record PushResult(
    string RepositoryUrl,
    string Branch,
    string DestinationRef,
    string PushedCommitId);
