using AlmToo.Accessor.BrowserGitAccessor.Interface;

namespace AlmToo.Managers.GitBug;

/// <summary>Coordinates read-only Git-Bug list and detail intent for one active repository.</summary>
public sealed class GitBugWorkspaceManager : IDisposable
{
    private const int MaximumQueryLength = 256;
    private const int MaximumPageSize = 100;
    private readonly IGitBugAccessor accessor;
    private readonly object operationSync = new();
    private CancellationTokenSource? activeOperation;
    private RepositoryIdentity? repositoryIdentity;
    private string repositoryWorkspaceName = string.Empty;
    private long operationGeneration;
    private bool hasListContext;
    private GitBugIssueQuery lastQuery = new(PageSize: 50);
    private bool disposed;

    public GitBugWorkspaceManager(IGitBugAccessor accessor)
    {
        this.accessor = accessor;
    }

    public GitBugWorkspaceState State { get; } = new();
    public event Action? StateChanged;

    /// <summary>Synchronizes the repository lifecycle without depending on another Manager.</summary>
    public void SetRepository(RepositoryInfo? repository)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var identity = repository is null
            ? null
            : new RepositoryIdentity(repository.RepositoryUrl, repository.RootPath);

        CancellationTokenSource? supersededOperation;
        lock (operationSync)
        {
            if (identity == repositoryIdentity)
            {
                return;
            }

            repositoryIdentity = identity;
            repositoryWorkspaceName = repository?.WorkspaceName ?? string.Empty;
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
        if (!State.RepositoryAvailable)
        {
            return Reject("Open a repository before browsing Git-Bug issues.", GitBugWorkspaceStatus.Idle);
        }

        if (!IsValidQuery(query))
        {
            return Reject("Choose a valid issue filter before loading Git-Bug issues.");
        }

        var normalizedQuery = query with { PageSize = Math.Clamp(query.PageSize, 1, MaximumPageSize) };
        lastQuery = normalizedQuery;
        var operation = BeginOperation(cancellationToken, GitBugWorkspaceStatus.LoadingList, () =>
        {
            State.SelectedIssueId = null;
            State.SelectedIssue = null;
        });

        try
        {
            var result = await accessor.ListIssuesAsync(normalizedQuery, operation.Token);
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
        if (!State.RepositoryAvailable)
        {
            return Reject("Open a repository before browsing Git-Bug issues.", GitBugWorkspaceStatus.Idle);
        }

        if (!IsValidIssueId(issueId) || !State.Issues.Any(issue => issue.Id == issueId))
        {
            return Reject("Choose an issue from the current issue list.");
        }

        var operation = BeginOperation(cancellationToken, GitBugWorkspaceStatus.LoadingDetail, () =>
        {
            State.SelectedIssueId = issueId;
            State.SelectedIssue = null;
        });

        try
        {
            var result = await accessor.GetIssueAsync(issueId, operation.Token);
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
            State.WriteOperation = GitBugWriteOperations.IssueEdit;
            State.LastWriteSucceeded = null;
            State.LastRefreshSucceeded = null;
        });
        var request = new GitBugIssueEditRequest(issueId, title!, description!,
            new(repository, issueId, State.SelectedIssue!.Revision, GitBugWriteOperations.IssueEdit),
            new(GitBugWriteContract.SupportedStorageVersion, GitBugWriteContract.Utf8Encoding, true));

        try
        {
            var result = await accessor.EditIssueAsync(request, operation.Token);
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
            State.WriteOperation = GitBugWriteOperations.CommentCreate;
            State.LastWriteSucceeded = null;
            State.LastRefreshSucceeded = null;
        });
        var request = new GitBugCommentCreateRequest(issueId, body!,
            new(repository, issueId, State.SelectedIssue!.Revision, GitBugWriteOperations.CommentCreate),
            new(GitBugWriteContract.SupportedStorageVersion, GitBugWriteContract.Utf8Encoding, true));

        try
        {
            var result = await accessor.CreateCommentAsync(request, operation.Token);
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
        if (!State.RepositoryAvailable || repositoryIdentity is null)
        {
            message = "Open a repository before editing Git-Bug issues.";
            return false;
        }

        if (State.SelectedIssueId is not { } selected || State.SelectedIssue is null || selected != State.SelectedIssue.Summary.Id)
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
        repository = new(repositoryIdentity.RepositoryUrl, repositoryWorkspaceName);
        return true;
    }

    private async Task<bool> RefreshAfterWriteAsync(OperationContext operation, GitBugIssueId issueId)
    {
        var detail = await accessor.GetIssueAsync(issueId, operation.Token);
        var list = hasListContext ? await accessor.ListIssuesAsync(lastQuery, operation.Token) : null;
        return Complete(operation, () =>
        {
            if (operation.Token.IsCancellationRequested)
            {
                return ApplyWriteCancellation();
            }

            State.Status = GitBugWorkspaceStatus.RefreshingAfterWrite;
            if (!detail.Succeeded || detail.Value is null || detail.Value.Summary.Id != issueId)
            {
                State.LastWriteSucceeded = true;
                State.LastRefreshSucceeded = false;
                ApplyFailure(detail.FailureKind ?? GitOperationFailureKind.GitBugDataMalformed, false);
                return false;
            }

            if (list is not null && (!list.Succeeded || list.Value is null))
            {
                State.LastWriteSucceeded = true;
                State.LastRefreshSucceeded = false;
                ApplyFailure(list.FailureKind, true);
                return false;
            }

            if (list?.Value is { } page)
            {
                State.Issues = page.Issues;
                State.TotalCount = page.TotalCount;
                State.NextCursor = page.NextCursor;
                hasListContext = true;
            }
            State.SelectedIssueId = issueId;
            State.SelectedIssue = detail.Value;
                State.Status = GitBugWorkspaceStatus.DetailReady;
            State.Message = "Local Git-Bug changes saved. No commit or push was performed.";
            State.FailureKind = null;
            State.LastWriteSucceeded = true;
            State.LastRefreshSucceeded = true;
            return true;
        });
    }

    private bool ApplyWriteFailure(GitOperationFailureKind? kind)
    {
        State.LastWriteSucceeded = false;
        State.LastRefreshSucceeded = null;
        State.FailureKind = kind ?? GitOperationFailureKind.GitBugWriteUnknown;
        State.Status = State.FailureKind switch
        {
            GitOperationFailureKind.GitBugWriteUnsupportedFormat => GitBugWorkspaceStatus.Unsupported,
            GitOperationFailureKind.GitBugWriteUnavailable => GitBugWorkspaceStatus.Unavailable,
            _ => GitBugWorkspaceStatus.Failed
        };
        State.Message = State.FailureKind switch
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
        State.LastWriteSucceeded = false;
        State.Status = GitBugWorkspaceStatus.Failed;
        State.Message = "The local Git-Bug change was cancelled.";
        State.FailureKind = GitOperationFailureKind.Unknown;
        State.LastRefreshSucceeded = false;
        return false;
    }

    private bool ApplyWriteException()
    {
        State.LastWriteSucceeded = false;
        State.Status = GitBugWorkspaceStatus.Failed;
        State.Message = "The local Git-Bug change could not be saved safely.";
        State.FailureKind = GitOperationFailureKind.GitBugWriteUnknown;
        State.LastRefreshSucceeded = false;
        return false;
    }

    public void ReturnToIssueList()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CancellationTokenSource? supersededOperation;
        lock (operationSync)
        {
            supersededOperation = InvalidateActiveOperation();
            State.SelectedIssueId = null;
            State.SelectedIssue = null;
            State.Message = null;
            State.FailureKind = null;
            State.WriteOperation = null;
            State.LastWriteSucceeded = null;
            State.LastRefreshSucceeded = null;
            State.Status = State.RepositoryAvailable && hasListContext
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
        lock (operationSync)
        {
            supersededOperation = InvalidateActiveOperation();
            ResetState(State.RepositoryAvailable);
        }

        CancelSafely(supersededOperation);
        NotifyChanged();
    }

    public void CancelCurrentOperation()
    {
        CancellationTokenSource? operation;
        lock (operationSync)
        {
            operation = activeOperation;
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
        lock (operationSync)
        {
            supersededOperation = InvalidateActiveOperation();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            activeOperation = cancellation;
            operation = new(operationGeneration, cancellation);
            prepareState();
            State.Status = loadingStatus;
            State.Message = null;
            State.FailureKind = null;
        }

        CancelSafely(supersededOperation);
        NotifyChanged();
        return operation;
    }

    private bool Complete(OperationContext operation, Func<bool> apply)
    {
        bool result;
        lock (operationSync)
        {
            if (operation.Generation != operationGeneration || activeOperation != operation.Cancellation)
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
        lock (operationSync)
        {
            if (activeOperation == operation.Cancellation)
            {
                activeOperation = null;
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

        State.Issues = result.Value.Issues;
        State.TotalCount = result.Value.TotalCount;
        State.NextCursor = result.Value.NextCursor;
        hasListContext = true;
        State.Status = GitBugWorkspaceStatus.ListReady;
        State.Message = null;
        State.FailureKind = null;
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

        State.SelectedIssueId = result.Value.Summary.Id;
        State.SelectedIssue = result.Value;
        State.Status = GitBugWorkspaceStatus.DetailReady;
        State.Message = null;
        State.FailureKind = null;
        return true;
    }

    private bool ApplyFailure(GitOperationFailureKind? failureKind, bool listOperation)
    {
        State.FailureKind = failureKind ?? GitOperationFailureKind.Unknown;
        switch (State.FailureKind)
        {
            case GitOperationFailureKind.GitBugDataUnavailable:
                State.Status = GitBugWorkspaceStatus.Unavailable;
                State.Message = "This repository has no supported Git-Bug issue data.";
                if (listOperation)
                {
                    State.Issues = Array.Empty<GitBugIssueSummary>();
                    State.TotalCount = 0;
                    State.NextCursor = null;
                    hasListContext = false;
                }
                break;
            case GitOperationFailureKind.GitBugFormatUnsupported:
                State.Status = GitBugWorkspaceStatus.Unsupported;
                State.Message = "This Git-Bug data version is not supported yet.";
                break;
            case GitOperationFailureKind.GitBugDataMalformed:
                State.Status = GitBugWorkspaceStatus.Failed;
                State.Message = "Git-Bug issue data could not be read safely.";
                break;
            default:
                State.Status = GitBugWorkspaceStatus.Failed;
                State.Message = "Git-Bug issues could not be loaded.";
                State.FailureKind = GitOperationFailureKind.Unknown;
                break;
        }

        return false;
    }

    private bool ApplyCancellation()
    {
        State.Status = GitBugWorkspaceStatus.Failed;
        State.Message = "The Git-Bug issue operation was cancelled.";
        State.FailureKind = GitOperationFailureKind.Unknown;
        return false;
    }

    private bool ApplySafeException()
    {
        State.Status = GitBugWorkspaceStatus.Failed;
        State.Message = "Git-Bug issues could not be loaded safely.";
        State.FailureKind = GitOperationFailureKind.Unknown;
        return false;
    }

    private bool Reject(string message, GitBugWorkspaceStatus status = GitBugWorkspaceStatus.Failed)
    {
        CancellationTokenSource? supersededOperation;
        lock (operationSync)
        {
            supersededOperation = InvalidateActiveOperation();
            State.Status = status;
            State.Message = message;
            State.FailureKind = GitOperationFailureKind.Unknown;
            State.LastWriteSucceeded = null;
            State.LastRefreshSucceeded = null;
        }

        CancelSafely(supersededOperation);
        NotifyChanged();
        return false;
    }

    private CancellationTokenSource? InvalidateActiveOperation()
    {
        var operation = activeOperation;
        activeOperation = null;
        operationGeneration++;
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
        State.RepositoryAvailable = repositoryAvailable;
        State.Status = GitBugWorkspaceStatus.Idle;
        State.Issues = Array.Empty<GitBugIssueSummary>();
        State.TotalCount = 0;
        State.NextCursor = null;
        hasListContext = false;
        State.SelectedIssueId = null;
        State.SelectedIssue = null;
        State.Message = null;
        State.FailureKind = null;
        State.WriteOperation = null;
        State.LastWriteSucceeded = null;
        State.LastRefreshSucceeded = null;
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

    private void NotifyChanged() => StateChanged?.Invoke();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancellationTokenSource? active;
        lock (operationSync)
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
