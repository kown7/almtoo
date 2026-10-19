using AlmToo.Accessor.BrowserGitAccessor.Interface;

namespace AlmToo.Managers.GitBug;

/// <summary>Coordinates read-only Git-Bug list and detail intent for one active repository.</summary>
public sealed class GitBugWorkspaceManager : IDisposable
{
    private const int MaximumQueryLength = 256;
    private const int MaximumPageSize = 100;
    private readonly IGitBugAccessor accessor;
    private readonly IBrowserGitAccessor? browserGitAccessor;
    private readonly object operationSync = new();
    private CancellationTokenSource? activeOperation;
    private RepositoryIdentity? repositoryIdentity;
    private string repositoryWorkspaceName = string.Empty;
    private long operationGeneration;
    private bool hasListContext;
    private GitBugIssueQuery lastQuery = new(PageSize: 50);
    private bool disposed;

    public GitBugWorkspaceManager(IGitBugAccessor accessor, IBrowserGitAccessor? browserGitAccessor = null)
    {
        this.accessor = accessor;
        this.browserGitAccessor = browserGitAccessor;
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

    /// <summary>Loads a bounded, credential-free review of the browser-local working tree.</summary>
    public async Task<bool> LoadCommitReviewAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!State.RepositoryAvailable)
        {
            return Reject("Open a repository before reviewing a local commit.");
        }

        if (browserGitAccessor is null)
        {
            return Reject("Local Git commit review is unavailable.", GitBugWorkspaceStatus.Unavailable);
        }

        var operation = BeginOperation(cancellationToken, GitBugWorkspaceStatus.LoadingCommitReview, () =>
        {
            State.WriteOperation = GitBugWriteOperations.WriteReview;
            State.LastWriteSucceeded = null;
            State.LastRefreshSucceeded = null;
            State.LastCommit = null;
            State.ProposedCommit = null;
        });

        try
        {
            var result = await browserGitAccessor.GetStatusAsync(operation.Token);
            return Complete(operation, () => operation.Token.IsCancellationRequested
                ? ApplyWriteCancellation()
                : ApplyCommitReviewResult(result));
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            return Complete(operation, ApplyWriteCancellation);
        }
        catch (Exception)
        {
            return Complete(operation, ApplyCommitException);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    /// <summary>Updates proposed commit metadata without performing any repository mutation.</summary>
    public bool SetProposedCommit(string? message, string? authorName, string? authorEmail)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var request = new CommitRequest(message ?? string.Empty, authorName ?? string.Empty, authorEmail ?? string.Empty);
        if (State.IsBusy || !State.CommitReviewLoaded || !BrowserGitCommitContract.IsValidCommitRequest(request))
        {
            State.Message = "Enter a valid commit message, author name, and author email.";
            State.FailureKind = GitOperationFailureKind.GitBugWriteInvalidRequest;
            NotifyChanged();
            return false;
        }

        State.ProposedCommit = request;
        State.Status = GitBugWorkspaceStatus.CommitReviewReady;
        State.Message = "Commit details are ready for explicit confirmation.";
        State.FailureKind = null;
        NotifyChanged();
        return true;
    }

    /// <summary>Creates one browser-local commit only after review and metadata confirmation.</summary>
    public async Task<bool> ConfirmLocalCommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (browserGitAccessor is null)
        {
            return Reject("Local Git commit is unavailable.", GitBugWorkspaceStatus.Unavailable);
        }

        var request = State.ProposedCommit;
        if (State.IsBusy || !State.CommitReviewLoaded || request is null
            || (!State.HasChangedFiles && State.SelectedIssue is null)
            || !BrowserGitCommitContract.IsValidCommitRequest(request))
        {
            return Reject("Review changed files and enter valid commit details before confirming.");
        }

        var operation = BeginOperation(cancellationToken, GitBugWorkspaceStatus.Committing, () =>
        {
            State.WriteOperation = GitBugWriteOperations.WriteCommit;
            State.LastWriteSucceeded = null;
            State.LastRefreshSucceeded = null;
        });

        try
        {
            var result = await browserGitAccessor.CommitAsync(request, operation.Token);
            if (operation.Token.IsCancellationRequested)
            {
                return Complete(operation, ApplyWriteCancellation);
            }

            if (!result.Succeeded || result.Value is null)
            {
                return Complete(operation, () => ApplyCommitFailure(result.FailureKind ?? GitOperationFailureKind.GitBugWriteUnknown));
            }

            var status = await browserGitAccessor.GetStatusAsync(operation.Token);
            return Complete(operation, () => operation.Token.IsCancellationRequested
                ? ApplyWriteCancellation()
                : ApplyCommitResult(result.Value, status));
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            return Complete(operation, ApplyWriteCancellation);
        }
        catch (Exception)
        {
            return Complete(operation, ApplyCommitException);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private bool ApplyCommitReviewResult(GitOperationResult<IReadOnlyList<ChangedFile>> result)
    {
        if (!result.Succeeded || result.Value is null || !BrowserGitCommitContract.IsValidChangedFiles(result.Value))
        {
            return ApplyCommitFailure(result.FailureKind ?? GitOperationFailureKind.GitBugDataMalformed);
        }

        if (result.Value.Count > 0 || State.ChangedFiles.Count == 0)
        {
            State.ChangedFiles = result.Value;
        }
        State.Status = GitBugWorkspaceStatus.CommitReviewReady;
        State.Message = State.ChangedFiles.Count == 0
            ? "No changed files are available for a local commit."
            : "Review the changed files and commit details before confirming.";
        State.FailureKind = null;
        return true;
    }

    private bool ApplyCommitResult(CommitInfo commit, GitOperationResult<IReadOnlyList<ChangedFile>> status)
    {
        if (!IsValidCommitInfo(commit) || !status.Succeeded || status.Value is null
            || !BrowserGitCommitContract.IsValidChangedFiles(status.Value))
        {
            State.LastWriteSucceeded = true;
            State.LastRefreshSucceeded = false;
            return ApplyCommitFailure(status.FailureKind ?? GitOperationFailureKind.GitBugDataMalformed, preserveWrite: true);
        }

        State.ChangedFiles = status.Value;
        State.LastCommit = commit with { ChangedFileCount = Math.Min(commit.ChangedFileCount, BrowserGitCommitContract.MaximumChangedFiles) };
        State.Status = GitBugWorkspaceStatus.CommitSucceeded;
        State.Message = "Local commit created. No push was performed.";
        State.FailureKind = null;
        State.LastWriteSucceeded = true;
        State.LastRefreshSucceeded = true;
        return true;
    }

    private bool ApplyCommitFailure(GitOperationFailureKind kind, bool preserveWrite = false)
    {
        State.FailureKind = kind;
        State.Status = kind switch
        {
            GitOperationFailureKind.GitBugWriteUnsupportedFormat => GitBugWorkspaceStatus.Unsupported,
            GitOperationFailureKind.GitBugWriteUnavailable => GitBugWorkspaceStatus.Unavailable,
            _ => GitBugWorkspaceStatus.Failed
        };
        State.Message = kind switch
        {
            GitOperationFailureKind.GitBugWriteInvalidRequest => "The commit details are invalid.",
            GitOperationFailureKind.GitBugWriteConflict or GitOperationFailureKind.GitBugWritePreconditionFailed => "The working tree changed; review the files again.",
            GitOperationFailureKind.GitBugWriteUnsupportedFormat => "Local commits are not supported for this repository format.",
            GitOperationFailureKind.GitBugWriteUnavailable => "Local commit creation is unavailable for this repository.",
            _ => "The local commit could not be created safely."
        };
        if (!preserveWrite)
        {
            State.LastWriteSucceeded = false;
            State.LastRefreshSucceeded = null;
        }
        return false;
    }

    private bool ApplyCommitException()
    {
        State.LastWriteSucceeded = false;
        State.LastRefreshSucceeded = null;
        return ApplyCommitFailure(GitOperationFailureKind.GitBugWriteUnknown);
    }

    private static bool IsValidCommitInfo(CommitInfo commit) =>
        !string.IsNullOrWhiteSpace(commit.CommitId)
        && commit.CommitId.Length <= BrowserGitCommitContract.MaximumCommitIdLength
        && commit.CommitId.All(Uri.IsHexDigit)
        && commit.ChangedFileCount >= 0
        && commit.ChangedFileCount <= BrowserGitCommitContract.MaximumChangedFiles;

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
        if (title is null || body is null
            || (operation == GitBugWriteOperations.IssueEdit && title.Length == 0)
            || (!allowEmptyBody && body.Length == 0)
            || titleHasInvalidCharacters
            || bodyHasInvalidCharacters)
        {
            message = "Enter valid Git-Bug text without control characters.";
            return false;
        }

        if (operation == GitBugWriteOperations.IssueEdit && (title.Length > GitBugWriteContract.MaximumTitleLength
            || body.Length > GitBugWriteContract.MaximumBodyLength))
        {
            message = "The Git-Bug issue title or description is too long.";
            return false;
        }

        if (operation == GitBugWriteOperations.CommentCreate && body.Length > GitBugWriteContract.MaximumBodyLength)
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
            State.ChangedFiles = new[]
            {
                new ChangedFile($"/git-bug/refs/bugs/{issueId.Value}", GitChangeKind.Modified)
            };
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

    /// <summary>Loads the current remote target and credential presence without mutating the repository.</summary>
    public Task<bool> InspectPushAsync(CancellationToken cancellationToken = default) =>
        LoadPushReviewAsync(cancellationToken);

    public async Task<bool> LoadPushReviewAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!State.RepositoryAvailable || browserGitAccessor is null)
        {
            return Reject("Open a repository before reviewing a push.", browserGitAccessor is null
                ? GitBugWorkspaceStatus.Unavailable : GitBugWorkspaceStatus.Idle);
        }

        var operation = BeginOperation(cancellationToken, GitBugWorkspaceStatus.LoadingPushReview, () =>
        {
            State.PushReview = null;
            State.PushConfirmed = false;
            State.PushFailureKind = null;
            State.WriteOperation = GitBugWriteOperations.WriteReview;
        });

        try
        {
            var credential = await browserGitAccessor.HasCredentialAsync(operation.Token);
            var review = await browserGitAccessor.InspectPushAsync(operation.Token);
            return Complete(operation, () => operation.Token.IsCancellationRequested
                ? ApplyPushCancellation()
                : ApplyPushReviewResult(credential, review));
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            return Complete(operation, ApplyPushCancellation);
        }
        catch (Exception)
        {
            return Complete(operation, ApplyPushException);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    /// <summary>Accepts a credential only as an input to tab-scoped storage; the secret never enters state.</summary>
    public Task<bool> StoreCredentialAsync(string? credential, CancellationToken cancellationToken = default) =>
        StorePushCredentialAsync(credential, cancellationToken);

    public async Task<bool> StorePushCredentialAsync(string? credential, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (browserGitAccessor is null)
        {
            return Reject("Push credentials are unavailable in this workspace.", GitBugWorkspaceStatus.Unavailable);
        }

        var credentialForAttempt = credential?.Trim() ?? string.Empty;
        if (credentialForAttempt.Length == 0)
        {
            State.HasCredential = false;
            State.CredentialReplacementRequired = true;
            State.Message = "Enter a credential before retrying the push.";
            State.PushFailureKind = GitOperationFailureKind.CredentialRejected;
            NotifyChanged();
            return false;
        }

        try
        {
            var result = await browserGitAccessor.StoreCredentialAsync(credentialForAttempt, cancellationToken);
            State.HasCredential = result.Succeeded && result.Value is true;
            State.CredentialReplacementRequired = !State.HasCredential;
            State.PushFailureKind = State.HasCredential ? null : GitOperationFailureKind.CredentialRejected;
            State.Message = State.HasCredential
                ? "Credential saved for this tab. Review the push again before confirming."
                : "The credential could not be saved. Enter a replacement credential to continue.";
            if (State.HasCredential)
            {
                State.PushConfirmed = false;
            }
            NotifyChanged();
            return State.HasCredential;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            State.HasCredential = false;
            State.CredentialReplacementRequired = true;
            State.Message = "Credential replacement was cancelled.";
            State.PushFailureKind = GitOperationFailureKind.Unknown;
            NotifyChanged();
            return false;
        }
        catch (Exception)
        {
            State.HasCredential = false;
            State.CredentialReplacementRequired = true;
            State.Message = "The credential could not be saved safely.";
            State.PushFailureKind = GitOperationFailureKind.Unknown;
            NotifyChanged();
            return false;
        }
        finally
        {
            credentialForAttempt = string.Empty;
        }
    }

    public void ConfirmPushReview(bool confirmed)
    {
        State.PushConfirmed = confirmed && State.PushReview is not null && !State.IsBusy;
        State.Message = State.PushConfirmed
            ? "Push confirmed. Submit again to perform the reviewed push."
            : "Push confirmation was cleared.";
        NotifyChanged();
    }

    public Task<bool> RetryPushAsync(CancellationToken cancellationToken = default) =>
        State.PushReview is { } review && State.PushConfirmed
            ? PushReviewedCommitAsync(review, cancellationToken)
            : Task.FromResult(Reject("Review and explicitly confirm the current push before retrying."));

    public Task<bool> ReplacePushCredentialAsync(string? credential, CancellationToken cancellationToken = default) =>
        StorePushCredentialAsync(credential, cancellationToken);

    public async Task<bool> PushReviewedCommitAsync(PushReview reviewedPush, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (browserGitAccessor is null)
        {
            return Reject("Local push is unavailable.", GitBugWorkspaceStatus.Unavailable);
        }

        if (reviewedPush is null || State.PushReview is null || !State.PushConfirmed || !State.HasCredential)
        {
            return Reject("Review and confirm the exact push destination and provide a credential before pushing.");
        }
        if (reviewedPush != State.PushReview)
        {
            InvalidatePushReview();
            return Reject("The submitted push review does not match the current review. Review the push again.");
        }

        var operation = BeginOperation(cancellationToken, GitBugWorkspaceStatus.Pushing, () =>
        {
            State.WriteOperation = "git.push";
            State.LastWriteSucceeded = null;
            State.LastRefreshSucceeded = null;
        });

        try
        {
            var latest = await browserGitAccessor.InspectPushAsync(operation.Token);
            if (!latest.Succeeded || latest.Value is null)
            {
                return Complete(operation, () => ApplyPushFailure(latest.FailureKind));
            }
            if (latest.Value != reviewedPush)
            {
                return Complete(operation, () =>
                {
                    InvalidatePushReview();
                    State.Message = "The push coordinates changed after review. Review the current destination before pushing.";
                    State.PushFailureKind = GitOperationFailureKind.RemoteAhead;
                    return false;
                });
            }

            var result = await browserGitAccessor.PushAsync(
                new PushRequest(reviewedPush.RepositoryUrl, reviewedPush.Branch, reviewedPush.OutgoingCommitId, reviewedPush.DestinationRef),
                operation.Token);
            if (!result.Succeeded || result.Value is null)
            {
                return Complete(operation, () => ApplyPushFailure(result.FailureKind));
            }

            await browserGitAccessor.ForgetCredentialAsync(operation.Token);
            var status = await browserGitAccessor.GetStatusAsync(operation.Token);
            return Complete(operation, () => operation.Token.IsCancellationRequested
                ? ApplyPushCancellation()
                : ApplyPushSuccess(result.Value, status));
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            return Complete(operation, ApplyPushCancellation);
        }
        catch (Exception)
        {
            return Complete(operation, ApplyPushException);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private bool ApplyPushReviewResult(GitOperationResult<bool> credential, GitOperationResult<PushReview> review)
    {
        State.HasCredential = credential.Succeeded && credential.Value is true;
        State.CredentialReplacementRequired = !State.HasCredential;
        if (!review.Succeeded || review.Value is null)
        {
            return ApplyPushFailure(review.FailureKind);
        }
        State.PushReview = review.Value;
        State.PushConfirmed = false;
        State.Status = GitBugWorkspaceStatus.PushReviewReady;
        State.Message = State.HasCredential
            ? "Review the destination and explicitly confirm the push."
            : "No credential is available for this tab. Enter one before confirming the push.";
        State.PushFailureKind = null;
        return true;
    }

    private bool ApplyPushSuccess(PushResult result, GitOperationResult<IReadOnlyList<ChangedFile>> status)
    {
        if (!status.Succeeded || status.Value is null || !BrowserGitCommitContract.IsValidChangedFiles(status.Value))
        {
            return ApplyPushFailure(status.FailureKind ?? GitOperationFailureKind.GitBugDataMalformed);
        }
        State.ChangedFiles = status.Value;
        State.PushReview = null;
        State.PushConfirmed = false;
        State.Status = GitBugWorkspaceStatus.PushSucceeded;
        State.Message = $"Pushed commit {result.PushedCommitId} and refreshed local status.";
        State.PushFailureKind = null;
        State.LastWriteSucceeded = true;
        State.LastRefreshSucceeded = true;
        return true;
    }

    private bool ApplyPushFailure(GitOperationFailureKind? kind)
    {
        State.PushFailureKind = NormalizePushFailureKind(kind);
        State.PushConfirmed = false;
        State.LastWriteSucceeded = false;
        State.LastRefreshSucceeded = null;
        if (State.PushFailureKind == GitOperationFailureKind.CredentialRejected)
        {
            State.HasCredential = false;
            State.CredentialReplacementRequired = true;
        }
        State.Status = GitBugWorkspaceStatus.Failed;
        State.Message = State.PushFailureKind switch
        {
            GitOperationFailureKind.CredentialRejected => "The remote rejected the credential. Replace it, review the push again, and retry.",
            GitOperationFailureKind.RemoteAhead => "The remote branch is ahead. Reconcile it, then review and retry the push.",
            GitOperationFailureKind.NetworkUnavailable => "The remote could not be reached. Resolve connectivity, then retry the reviewed push.",
            GitOperationFailureKind.UnsupportedRef => "The checked-out ref is unsupported. Select a local branch and review again.",
            _ => "The push could not be completed safely. Review the workspace before retrying."
        };
        return false;
    }

    private bool ApplyPushCancellation()
    {
        State.PushConfirmed = false;
        State.Status = GitBugWorkspaceStatus.Failed;
        State.Message = "The push was cancelled; no success was recorded.";
        State.PushFailureKind = GitOperationFailureKind.Unknown;
        State.LastWriteSucceeded = false;
        State.LastRefreshSucceeded = false;
        return false;
    }

    private bool ApplyPushException() => ApplyPushFailure(GitOperationFailureKind.Unknown);

    private void InvalidatePushReview()
    {
        State.PushReview = null;
        State.PushConfirmed = false;
    }

    private static GitOperationFailureKind NormalizePushFailureKind(GitOperationFailureKind? kind) => kind switch
    {
        GitOperationFailureKind.CredentialRejected => GitOperationFailureKind.CredentialRejected,
        GitOperationFailureKind.RemoteAhead => GitOperationFailureKind.RemoteAhead,
        GitOperationFailureKind.NetworkUnavailable => GitOperationFailureKind.NetworkUnavailable,
        GitOperationFailureKind.UnsupportedRef => GitOperationFailureKind.UnsupportedRef,
        _ => GitOperationFailureKind.Unknown
    };

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
        State.ChangedFiles = Array.Empty<ChangedFile>();
        State.ProposedCommit = null;
        State.LastCommit = null;
        State.Message = null;
        State.FailureKind = null;
        State.WriteOperation = null;
        State.LastWriteSucceeded = null;
        State.LastRefreshSucceeded = null;
        State.PushReview = null;
        State.PushConfirmed = false;
        State.HasCredential = false;
        State.CredentialReplacementRequired = false;
        State.PushFailureKind = null;
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
