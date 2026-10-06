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
    private long operationGeneration;
    private bool hasListContext;
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
            supersededOperation = InvalidateActiveOperation();
            ResetState(identity is not null);
        }

        CancelSafely(supersededOperation);
        NotifyChanged();
    }

    /// <summary>Loads the default bounded issue list for Client callers.</summary>
    public Task<bool> LoadIssuesAsync(CancellationToken cancellationToken = default) =>
        LoadIssuesAsync(new GitBugIssueQuery(PageSize: 50), cancellationToken);

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
    public Task<bool> OpenIssueAsync(string issueId, CancellationToken cancellationToken = default) =>
        OpenIssueAsync(new GitBugIssueId(issueId), cancellationToken);

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
    }

    private static bool IsValidQuery(GitBugIssueQuery? query) =>
        query is not null
        && (!query.State.HasValue || Enum.IsDefined(query.State.Value))
        && IsBoundedQueryText(query.SearchText)
        && IsBoundedQueryText(query.Cursor);

    private static bool IsBoundedQueryText(string? value) =>
        value is null || value.Length is > 0 and <= MaximumQueryLength && value.IndexOfAny(['\0', '\r']) < 0;

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
