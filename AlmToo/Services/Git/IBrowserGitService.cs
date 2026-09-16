namespace AlmToo.Services.Git;

/// <summary>
/// Browser-local Git boundary used by Blazor UI components. Implementations may use
/// JavaScript interop and browser storage, but callers should not depend on those details.
/// </summary>
public interface IBrowserGitService
{
    /// <summary>
    /// Clones a public repository into browser-local storage, or opens the existing
    /// local workspace when it has already been cloned.
    /// </summary>
    ValueTask<GitOperationResult<RepositoryInfo>> CloneOrOpenAsync(
        RepositoryOpenRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists files and directories under a repository path.
    /// </summary>
    ValueTask<GitOperationResult<IReadOnlyList<RepositoryFileEntry>>> ListFilesAsync(
        string path = "/",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an editable plain text file from the browser-local working copy.
    /// </summary>
    ValueTask<GitOperationResult<TextFileContent>> ReadTextFileAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes plain text content to the browser-local working copy.
    /// </summary>
    ValueTask<GitOperationResult> WriteTextFileAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the changed-file status for the browser-local working copy.
    /// </summary>
    ValueTask<GitOperationResult<IReadOnlyList<ChangedFile>>> GetStatusAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a local commit in the browser-local repository. This does not push.
    /// </summary>
    ValueTask<GitOperationResult<CommitInfo>> CommitAsync(
        CommitRequest request,
        CancellationToken cancellationToken = default);
}
