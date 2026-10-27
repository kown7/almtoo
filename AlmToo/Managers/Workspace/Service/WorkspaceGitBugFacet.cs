using AlmToo.Accessor.BrowserGitAccessor.Interface;
using AlmToo.Managers.Workspace.Interface;

namespace AlmToo.Managers.Workspace;

/// <summary>Coordinates read-only Git-Bug list and detail intent for one active repository.</summary>
public sealed partial class WorkspaceManager
{
    private const int MaximumQueryLength = 256;
    private const int MaximumPageSize = 100;
    private readonly object gitBugOperationSync = new();
    private CancellationTokenSource? gitBugActiveOperation;
    private RepositoryIdentity? gitBugRepositoryIdentity;
    private string gitBugRepositoryWorkspaceName = string.Empty;
    private long gitBugOperationGeneration;
    private bool gitBugHasListContext;
    private GitBugIssueQuery gitBugLastQuery = new(PageSize: 50);

    /// <summary>Synchronizes the repository lifecycle without depending on another Manager.</summary>
    public void SetRepository(RepositoryInfo? repository)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var identity = repository is null
            ? null
            : new RepositoryIdentity(repository.RepositoryUrl, repository.RootPath);

        CancellationTokenSource? supersededOperation;
        lock (gitBugOperationSync)
        {
            if (identity == gitBugRepositoryIdentity)
            {
                return;
            }

            gitBugRepositoryIdentity = identity;
            gitBugRepositoryWorkspaceName = repository?.WorkspaceName ?? string.Empty;
            supersededOperation = InvalidateActiveOperation();
            ResetState(identity is not null);
        }

        CancelSafely(supersededOperation);
        NotifyChanged();
    }

    /// <summary>Loads the default bounded issue list for Client callers.</summary>
    public Task<bool> LoadIssuesAsync() =>
        LoadIssuesAsync(new GitBugIssueQuery(PageSize: 50));

    public async Task<bool> LoadIssuesAsync(
        GitBugIssueQuery query,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!State.GitBug.RepositoryAvailable)
        {
            return Reject("Open a repository before browsing Git-Bug issues.", GitBugWorkspaceStatus.Idle);
        }

        if (!IsValidQuery(query))
        {
            return Reject("Choose a valid issue filter before loading Git-Bug issues.");
        }

        var normalizedQuery = query with { PageSize = Math.Clamp(query.PageSize, 1, MaximumPageSize) };
        gitBugLastQuery = normalizedQuery;
        var operation = BeginOperation(cancellationToken, GitBugWorkspaceStatus.LoadingList, () =>
        {
            State.GitBug.SelectedIssueId = null;
            State.GitBug.SelectedIssue = null;
        });

        try
        {
            var result = await gitBugAccessor.ListIssuesAsync(normalizedQuery, operation.Token);
            return Complete(operation, () => operation.Token.IsCancellationRequested
                ? ApplyCancellation()
                : ApplyListResult(result));
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            return Complete(operation, ApplyCancellation);
        }
        catch (Exception)
        {
            return Complete(operation, ApplySafeException);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    /// <summary>Opens a current-list issue from a Client-safe string identifier.</summary>
    public Task<bool> OpenIssueAsync(string issueId) =>
        OpenIssueAsync(new GitBugIssueId(issueId));

    public async Task<bool> OpenIssueAsync(
        GitBugIssueId issueId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!State.GitBug.RepositoryAvailable)
        {
            return Reject("Open a repository before browsing Git-Bug issues.", GitBugWorkspaceStatus.Idle);
        }

        if (!IsValidIssueId(issueId) || !State.GitBug.Issues.Any(issue => issue.Id == issueId))
        {
            return Reject("Choose an issue from the current issue list.");
        }

        var operation = BeginOperation(cancellationToken, GitBugWorkspaceStatus.LoadingDetail, () =>
        {
            State.GitBug.SelectedIssueId = issueId;
            State.GitBug.SelectedIssue = null;
        });

        try
        {
            var result = await gitBugAccessor.GetIssueAsync(issueId, operation.Token);
            return Complete(operation, () => operation.Token.IsCancellationRequested
                ? ApplyCancellation()
                : ApplyDetailResult(issueId, result));
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            return Complete(operation, ApplyCancellation);
        }
        catch (Exception)
        {
            return Complete(operation, ApplySafeException);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    /// <summary>Edits the selected issue locally after validating the Client-provided fields.</summary>
    public async Task<bool> EditSelectedIssueAsync(
        string? title,
        string? description,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!TryGetWriteContext(GitBugWriteOperations.IssueEdit, title, description, allowEmptyBody: true,
                out var issueId, out var repository, out var message))
        {
            return Reject(message!);
        }

        var operation = BeginOperation(cancellationToken, GitBugWorkspaceStatus.EditingIssue, () =>
        {
            State.GitBug.WriteOperation = GitBugWriteOperations.IssueEdit;
            State.GitBug.LastWriteSucceeded = null;
            State.GitBug.LastRefreshSucceeded = null;
        });
        var request = new GitBugIssueEditRequest(issueId, title!, description!,
            new(repository, issueId, State.GitBug.SelectedIssue!.Revision, GitBugWriteOperations.IssueEdit),
            new(GitBugWriteContract.SupportedStorageVersion, GitBugWriteContract.Utf8Encoding, true));

        try
        {
            var result = await gitBugAccessor.EditIssueAsync(request, operation.Token);
            if (!result.Succeeded || result.Value is null)
            {
                return Complete(operation, () => ApplyWriteFailure(result.FailureKind));
            }

            return await RefreshAfterWriteAsync(operation, issueId);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            return Complete(operation, ApplyWriteCancellation);
        }
        catch (Exception)
        {
            return Complete(operation, ApplyWriteException);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    /// <summary>Adds a local Git-Bug comment to the selected issue.</summary>
    public async Task<bool> AddCommentToSelectedIssueAsync(
        string? body,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!TryGetWriteContext(GitBugWriteOperations.CommentCreate, body, body, allowEmptyBody: false,
                out var issueId, out var repository, out var message))
        {
            return Reject(message!);
        }

        var operation = BeginOperation(cancellationToken, GitBugWorkspaceStatus.AddingComment, () =>
        {
            State.GitBug.WriteOperation = GitBugWriteOperations.CommentCreate;
            State.GitBug.LastWriteSucceeded = null;
            State.GitBug.LastRefreshSucceeded = null;
        });
        var request = new GitBugCommentCreateRequest(issueId, body!,
            new(repository, issueId, State.GitBug.SelectedIssue!.Revision, GitBugWriteOperations.CommentCreate),
            new(GitBugWriteContract.SupportedStorageVersion, GitBugWriteContract.Utf8Encoding, true));

        try
        {
            var result = await gitBugAccessor.CreateCommentAsync(request, operation.Token);
            if (!result.Succeeded || result.Value is null)
            {
                return Complete(operation, () => ApplyWriteFailure(result.FailureKind));
            }

            return await RefreshAfterWriteAsync(operation, issueId);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            return Complete(operation, ApplyWriteCancellation);
        }
        catch (Exception)
        {
            return Complete(operation, ApplyWriteException);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private bool TryGetWriteContext(string operation, string? first, string? second, bool allowEmptyBody,
        out GitBugIssueId issueId, out GitBugRepositoryIdentity repository, out string? message)
    {
        issueId = default;
        repository = default!;
        message = null;
        if (!State.GitBug.RepositoryAvailable || gitBugRepositoryIdentity is null)
        {
            message = "Open a repository before editing Git-Bug issues.";
            return false;
        }

        if (State.GitBug.SelectedIssueId is not { } selected || State.GitBug.SelectedIssue is null || selected != State.GitBug.SelectedIssue.Summary.Id)
        {
            message = "Choose an issue before changing Git-Bug data.";
            return false;
        }

        if (!IsValidIssueId(selected))
        {
            message = "Choose a valid issue before changing Git-Bug data.";
            return false;
        }

        var title = operation == GitBugWriteOperations.IssueEdit ? first : null;
        var body = second;
        var titleHasInvalidCharacters = title is not null
            && HasInvalidGitBugTextCharacters(title, allowLineFeeds: false);
        var bodyHasInvalidCharacters = body is not null
            && HasInvalidGitBugTextCharacters(body, allowLineFeeds: true);
        if (operation == GitBugWriteOperations.IssueEdit
            && (title is null || body is null || title.Length == 0
                || titleHasInvalidCharacters || bodyHasInvalidCharacters))
        {
            message = "Enter valid Git-Bug text without control characters.";
            return false;
        }

        if (operation == GitBugWriteOperations.CommentCreate
            && (body is null || (!allowEmptyBody && body.Length == 0) || bodyHasInvalidCharacters))
        {
            message = "Enter valid Git-Bug text without control characters.";
            return false;
        }

        if (operation == GitBugWriteOperations.IssueEdit && (title!.Length > GitBugWriteContract.MaximumTitleLength
            || body!.Length > GitBugWriteContract.MaximumBodyLength))
        {
            message = "The Git-Bug issue title or description is too long.";
            return false;
        }

        if (operation == GitBugWriteOperations.CommentCreate && body!.Length > GitBugWriteContract.MaximumBodyLength)
        {
            message = "The Git-Bug comment is too long.";
            return false;
        }

        issueId = selected;
        repository = new(gitBugRepositoryIdentity.RepositoryUrl, gitBugRepositoryWorkspaceName);
        return true;
    }

    private async Task<bool> RefreshAfterWriteAsync(OperationContext operation, GitBugIssueId issueId)
    {
        var detail = await gitBugAccessor.GetIssueAsync(issueId, operation.Token);
        var list = gitBugHasListContext ? await gitBugAccessor.ListIssuesAsync(gitBugLastQuery, operation.Token) : null;
        return Complete(operation, () =>
        {
            if (operation.Token.IsCancellationRequested)
            {
                return ApplyWriteCancellation();
            }

            State.GitBug.Status = GitBugWorkspaceStatus.RefreshingAfterWrite;
            if (!detail.Succeeded || detail.Value is null || detail.Value.Summary.Id != issueId)
            {
                State.GitBug.LastWriteSucceeded = true;
                State.GitBug.LastRefreshSucceeded = false;
                ApplyFailure(detail.FailureKind ?? GitOperationFailureKind.GitBugDataMalformed, false);
                return false;
            }

            if (list is not null && (!list.Succeeded || list.Value is null))
            {
                State.GitBug.LastWriteSucceeded = true;
                State.GitBug.LastRefreshSucceeded = false;
                ApplyFailure(list.FailureKind, true);
                return false;
            }

            if (list?.Value is { } page)
            {
                State.GitBug.Issues = page.Issues;
                State.GitBug.TotalCount = page.TotalCount;
                State.GitBug.NextCursor = page.NextCursor;
                gitBugHasListContext = true;
            }
            State.GitBug.SelectedIssueId = issueId;
            State.GitBug.SelectedIssue = detail.Value;
                State.GitBug.Status = GitBugWorkspaceStatus.DetailReady;
            State.GitBug.Message = "Local Git-Bug changes saved. No commit or push was performed.";
            State.GitBug.FailureKind = null;
            State.GitBug.LastWriteSucceeded = true;
            State.GitBug.LastRefreshSucceeded = true;
            return true;
        });
    }

    private bool ApplyWriteFailure(GitOperationFailureKind? kind)
    {
        State.GitBug.LastWriteSucceeded = false;
        State.GitBug.LastRefreshSucceeded = null;
        State.GitBug.FailureKind = kind ?? GitOperationFailureKind.GitBugWriteUnknown;
        State.GitBug.Status = State.GitBug.FailureKind switch
        {
            GitOperationFailureKind.GitBugWriteUnsupportedFormat => GitBugWorkspaceStatus.Unsupported,
            GitOperationFailureKind.GitBugWriteUnavailable => GitBugWorkspaceStatus.Unavailable,
            _ => GitBugWorkspaceStatus.Failed
        };
        State.GitBug.Message = State.GitBug.FailureKind switch
        {
            GitOperationFailureKind.GitBugWriteUnsupportedFormat => "Git-Bug editing is not supported for this data version.",
            GitOperationFailureKind.GitBugWriteUnavailable => "Git-Bug editing is unavailable for this repository.",
            GitOperationFailureKind.GitBugWriteConflict or GitOperationFailureKind.GitBugWritePreconditionFailed => "The issue changed; reload it and review the edit again.",
            _ => "The local Git-Bug change could not be saved safely."
        };
        return false;
    }

    private bool ApplyWriteCancellation()
    {
        State.GitBug.LastWriteSucceeded = false;
        State.GitBug.Status = GitBugWorkspaceStatus.Failed;
        State.GitBug.Message = "The local Git-Bug change was cancelled.";
        State.GitBug.FailureKind = GitOperationFailureKind.Unknown;
        State.GitBug.LastRefreshSucceeded = false;
        return false;
    }

    private bool ApplyWriteException()
    {
        State.GitBug.LastWriteSucceeded = false;
        State.GitBug.Status = GitBugWorkspaceStatus.Failed;
        State.GitBug.Message = "The local Git-Bug change could not be saved safely.";
        State.GitBug.FailureKind = GitOperationFailureKind.GitBugWriteUnknown;
        State.GitBug.LastRefreshSucceeded = false;
        return false;
    }

    public void ReturnToIssueList()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CancellationTokenSource? supersededOperation;
        lock (gitBugOperationSync)
        {
            supersededOperation = InvalidateActiveOperation();
            State.GitBug.SelectedIssueId = null;
            State.GitBug.SelectedIssue = null;
            State.GitBug.Message = null;
            State.GitBug.FailureKind = null;
            State.GitBug.WriteOperation = null;
            State.GitBug.LastWriteSucceeded = null;
            State.GitBug.LastRefreshSucceeded = null;
            State.GitBug.Status = State.GitBug.RepositoryAvailable && gitBugHasListContext
                ? GitBugWorkspaceStatus.ListReady
                : GitBugWorkspaceStatus.Idle;
        }

        CancelSafely(supersededOperation);
        NotifyChanged();
    }

    public void ClearIssueState()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CancellationTokenSource? supersededOperation;
        lock (gitBugOperationSync)
        {
            supersededOperation = InvalidateActiveOperation();
            ResetState(State.GitBug.RepositoryAvailable);
        }

        CancelSafely(supersededOperation);
        NotifyChanged();
    }

    private void CancelGitBugOperation()
    {
        CancellationTokenSource? operation;
        lock (gitBugOperationSync)
        {
            operation = gitBugActiveOperation;
        }

        CancelSafely(operation);
    }

    private OperationContext BeginOperation(
        CancellationToken cancellationToken,
        GitBugWorkspaceStatus loadingStatus,
        Action prepareState)
    {
        OperationContext operation;
        CancellationTokenSource? supersededOperation;
        lock (gitBugOperationSync)
        {
            supersededOperation = InvalidateActiveOperation();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            gitBugActiveOperation = cancellation;
            operation = new(gitBugOperationGeneration, cancellation);
            prepareState();
            State.GitBug.Status = loadingStatus;
            State.GitBug.Message = null;
            State.GitBug.FailureKind = null;
        }

        CancelSafely(supersededOperation);
        NotifyChanged();
        return operation;
    }

    private bool Complete(OperationContext operation, Func<bool> apply)
    {
        bool result;
        lock (gitBugOperationSync)
        {
            if (operation.Generation != gitBugOperationGeneration || gitBugActiveOperation != operation.Cancellation)
            {
                return false;
            }

            result = apply();
        }

        NotifyChanged();
        return result;
    }

    private void EndOperation(OperationContext operation)
    {
        lock (gitBugOperationSync)
        {
            if (gitBugActiveOperation == operation.Cancellation)
            {
                gitBugActiveOperation = null;
            }
        }

        operation.Cancellation.Dispose();
    }

    private bool ApplyListResult(GitOperationResult<GitBugIssuePage> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            return ApplyFailure(result.FailureKind, listOperation: true);
        }

        State.GitBug.Issues = result.Value.Issues;
        State.GitBug.TotalCount = result.Value.TotalCount;
        State.GitBug.NextCursor = result.Value.NextCursor;
        gitBugHasListContext = true;
        State.GitBug.Status = GitBugWorkspaceStatus.ListReady;
        State.GitBug.Message = null;
        State.GitBug.FailureKind = null;
        return true;
    }

    private bool ApplyDetailResult(GitBugIssueId requestedIssueId, GitOperationResult<GitBugIssueDetail> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            return ApplyFailure(result.FailureKind, listOperation: false);
        }

        if (result.Value.Summary.Id != requestedIssueId)
        {
            return ApplyFailure(GitOperationFailureKind.GitBugDataMalformed, listOperation: false);
        }

        State.GitBug.SelectedIssueId = result.Value.Summary.Id;
        State.GitBug.SelectedIssue = result.Value;
        State.GitBug.Status = GitBugWorkspaceStatus.DetailReady;
        State.GitBug.Message = null;
        State.GitBug.FailureKind = null;
        return true;
    }

    private bool ApplyFailure(GitOperationFailureKind? failureKind, bool listOperation)
    {
        State.GitBug.FailureKind = failureKind ?? GitOperationFailureKind.Unknown;
        switch (State.GitBug.FailureKind)
        {
            case GitOperationFailureKind.GitBugDataUnavailable:
                State.GitBug.Status = GitBugWorkspaceStatus.Unavailable;
                State.GitBug.Message = "This repository has no supported Git-Bug issue data.";
                if (listOperation)
                {
                    State.GitBug.Issues = Array.Empty<GitBugIssueSummary>();
                    State.GitBug.TotalCount = 0;
                    State.GitBug.NextCursor = null;
                    gitBugHasListContext = false;
                }
                break;
            case GitOperationFailureKind.GitBugFormatUnsupported:
                State.GitBug.Status = GitBugWorkspaceStatus.Unsupported;
                State.GitBug.Message = "This Git-Bug data version is not supported yet.";
                break;
            case GitOperationFailureKind.GitBugDataMalformed:
                State.GitBug.Status = GitBugWorkspaceStatus.Failed;
                State.GitBug.Message = "Git-Bug issue data could not be read safely.";
                break;
            default:
                State.GitBug.Status = GitBugWorkspaceStatus.Failed;
                State.GitBug.Message = "Git-Bug issues could not be loaded.";
                State.GitBug.FailureKind = GitOperationFailureKind.Unknown;
                break;
        }

        return false;
    }

    private bool ApplyCancellation()
    {
        State.GitBug.Status = GitBugWorkspaceStatus.Failed;
        State.GitBug.Message = "The Git-Bug issue operation was cancelled.";
        State.GitBug.FailureKind = GitOperationFailureKind.Unknown;
        return false;
    }

    private bool ApplySafeException()
    {
        State.GitBug.Status = GitBugWorkspaceStatus.Failed;
        State.GitBug.Message = "Git-Bug issues could not be loaded safely.";
        State.GitBug.FailureKind = GitOperationFailureKind.Unknown;
        return false;
    }

    private bool Reject(string message, GitBugWorkspaceStatus status = GitBugWorkspaceStatus.Failed)
    {
        CancellationTokenSource? supersededOperation;
        lock (gitBugOperationSync)
        {
            supersededOperation = InvalidateActiveOperation();
            State.GitBug.Status = status;
            State.GitBug.Message = message;
            State.GitBug.FailureKind = GitOperationFailureKind.Unknown;
            State.GitBug.LastWriteSucceeded = null;
            State.GitBug.LastRefreshSucceeded = null;
        }

        CancelSafely(supersededOperation);
        NotifyChanged();
        return false;
    }

    private CancellationTokenSource? InvalidateActiveOperation()
    {
        var operation = gitBugActiveOperation;
        gitBugActiveOperation = null;
        gitBugOperationGeneration++;
        return operation;
    }

    private static void CancelSafely(CancellationTokenSource? operation)
    {
        try
        {
            operation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The superseded operation completed and disposed its token concurrently.
        }
    }

    private void ResetState(bool repositoryAvailable)
    {
        State.GitBug.RepositoryAvailable = repositoryAvailable;
        State.GitBug.Status = GitBugWorkspaceStatus.Idle;
        State.GitBug.Issues = Array.Empty<GitBugIssueSummary>();
        State.GitBug.TotalCount = 0;
        State.GitBug.NextCursor = null;
        gitBugHasListContext = false;
        State.GitBug.SelectedIssueId = null;
        State.GitBug.SelectedIssue = null;
        State.GitBug.Message = null;
        State.GitBug.FailureKind = null;
        State.GitBug.WriteOperation = null;
        State.GitBug.LastWriteSucceeded = null;
        State.GitBug.LastRefreshSucceeded = null;
    }

    private static bool IsValidQuery(GitBugIssueQuery? query) =>
        query is not null
        && (!query.State.HasValue || Enum.IsDefined(query.State.Value))
        && IsBoundedQueryText(query.SearchText)
        && IsBoundedQueryText(query.Cursor);

    private static bool IsBoundedQueryText(string? value) =>
        value is null || value.Length is > 0 and <= MaximumQueryLength && value.IndexOfAny(['\0', '\r']) < 0;

    private static bool HasInvalidGitBugTextCharacters(string value, bool allowLineFeeds) =>
        value.IndexOf('\0') >= 0
        || value.IndexOf('\r') >= 0
        || (!allowLineFeeds && value.IndexOf('\n') >= 0);

    private static bool IsValidIssueId(GitBugIssueId issueId) =>
        issueId.Value is { Length: 64 } && issueId.Value.All(Uri.IsHexDigit);


    private void DisposeGitBugResources()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancellationTokenSource? active;
        lock (gitBugOperationSync)
        {
            active = InvalidateActiveOperation();
        }

        CancelSafely(active);
    }

    private sealed record RepositoryIdentity(string RepositoryUrl, string RootPath);
    private sealed record OperationContext(long Generation, CancellationTokenSource Cancellation)
    {
        public CancellationToken Token => Cancellation.Token;
    }
}
