using AlmToo.Accessor.BrowserGitAccessor.Interface;

namespace AlmToo.Managers.Repositories;

/// <summary>Coordinates one browser-local repository workspace and exposes credential-free UI state.</summary>
public sealed class RepositoryWorkspaceManager : IDisposable
{
    private readonly IBrowserGitAccessor gitAccessor;
    private readonly IBrowserFileAccessor fileAccessor;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private CancellationTokenSource? activeOperation;
    private bool disposed;

    public RepositoryWorkspaceManager(IBrowserGitAccessor gitAccessor, IBrowserFileAccessor fileAccessor)
    {
        this.gitAccessor = gitAccessor;
        this.fileAccessor = fileAccessor;
    }

    public RepositoryWorkspaceState State { get; } = new();
    public event Action? StateChanged;

    public Task<bool> InitializeCredentialStateAsync(CancellationToken cancellationToken = default) =>
        RunAsync(RepositoryWorkspaceOperation.InitializeCredential, async token =>
        {
            var result = await gitAccessor.HasCredentialAsync(token);
            if (!result.Succeeded)
            {
                State.HasCredential = false;
                State.CredentialReplacementRequired = true;
                return Fail(result, "Tab credential storage is unavailable. Enter a token after storage becomes available.");
            }

            State.HasCredential = result.Value;
            State.CredentialReplacementRequired = false;
            return Succeed(result.Value ? "A token is available for an explicit push in this tab." : "No token is stored for this tab.");
        }, cancellationToken, exposeAccessorException: false);

    public Task<bool> OpenRepositoryAsync(string repositoryUrl, CancellationToken cancellationToken = default) =>
        RunAsync(RepositoryWorkspaceOperation.OpenRepository, async token =>
        {
            var trimmedUrl = repositoryUrl?.Trim() ?? string.Empty;
            if (trimmedUrl.Length == 0)
            {
                ResetWorkspace();
                return Fail("Enter a public repository URL before opening a workspace.");
            }

            ResetWorkspace();
            var open = await gitAccessor.CloneOrOpenAsync(
                new RepositoryOpenRequest(trimmedUrl, CreateWorkspaceName(trimmedUrl)), token);
            if (!open.Succeeded || open.Value is null)
            {
                return Fail(open, "The repository could not be opened. Check that the URL is public and supported.");
            }

            State.Repository = open.Value;
            if (!await LoadDirectoryCoreAsync("/", token))
            {
                return false;
            }

            if (!await RefreshStatusCoreAsync(token))
            {
                return false;
            }

            return Succeed(open.Message);
        }, cancellationToken);

    public Task<bool> LoadDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        RunAsync(RepositoryWorkspaceOperation.LoadDirectory, token => LoadDirectoryCoreAsync(path, token), cancellationToken);

    public Task<bool> SelectFileAsync(string path, CancellationToken cancellationToken = default) =>
        RunAsync(RepositoryWorkspaceOperation.LoadFile, async token =>
        {
            if (!RequireRepository())
            {
                return false;
            }

            var selectedEntry = State.Entries.FirstOrDefault(entry => string.Equals(entry.Path, path, StringComparison.Ordinal));
            if (selectedEntry is null)
            {
                return Fail("The selected file is no longer visible in the current repository listing.");
            }

            if (selectedEntry.Kind != RepositoryFileKind.File || !selectedEntry.IsEditableText)
            {
                return Fail("This file is binary, oversized, or otherwise unsupported for text preview.");
            }

            State.SelectedFilePath = path;
            State.SelectedFileContent = null;
            State.EditableFileContent = string.Empty;
            NotifyChanged();

            var result = await fileAccessor.FilterFilesAsync(new FilterFilesRequest(path, FileFilterKind.TextContent), token);
            if (!result.Succeeded || result.Value?.TextFile is null)
            {
                return Fail(result, "The selected file could not be loaded as text. Choose another file or reopen the repository.");
            }

            State.SelectedFilePath = result.Value.TextFile.Path;
            State.SelectedFileContent = result.Value.TextFile.Content;
            State.EditableFileContent = result.Value.TextFile.Content;
            return Succeed(result.Message);
        }, cancellationToken);

    public void EditSelectedFile(string content)
    {
        if (State.SelectedFileContent is null || State.IsBusy)
        {
            return;
        }

        State.EditableFileContent = content ?? string.Empty;
        State.Error = null;
        NotifyChanged();
    }

    public Task<bool> SaveSelectedFileAsync(CancellationToken cancellationToken = default) =>
        RunAsync(RepositoryWorkspaceOperation.SaveFile, async token =>
        {
            if (!RequireRepository() || string.IsNullOrWhiteSpace(State.SelectedFilePath) || State.SelectedFileContent is null)
            {
                return Fail("Choose a text file before saving changes.");
            }

            var savedContent = State.EditableFileContent;
            var result = await fileAccessor.UpdateFilesAsync(
                new UpdateFilesRequest([new TextFileUpdate(State.SelectedFilePath, savedContent)]), token);
            if (!result.Succeeded || result.Value is null)
            {
                return Fail(result, "The selected file could not be saved. Reopen the repository and try again.");
            }

            State.SelectedFileContent = savedContent;
            if (!await RefreshStatusCoreAsync(token))
            {
                return false;
            }

            return Succeed(result.Message);
        }, cancellationToken);

    public Task<bool> RefreshStatusAsync(CancellationToken cancellationToken = default) =>
        RunAsync(RepositoryWorkspaceOperation.RefreshStatus, RefreshStatusCoreAsync, cancellationToken);

    public Task<bool> CommitAsync(CommitRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(RepositoryWorkspaceOperation.Commit, async token =>
        {
            if (!RequireRepository())
            {
                return false;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Message) || string.IsNullOrWhiteSpace(request.AuthorName) || string.IsNullOrWhiteSpace(request.AuthorEmail))
            {
                return Fail("Commit message, author name, and author email are required.");
            }

            var result = await gitAccessor.CommitAsync(request, token);
            if (!result.Succeeded || result.Value is null)
            {
                State.CreatedCommit = null;
                return Fail(result, "The local commit could not be created. Review the changed files and try again.");
            }

            State.CreatedCommit = result.Value;
            InvalidatePushReview();
            if (!await RefreshStatusCoreAsync(token))
            {
                return false;
            }

            return Succeed(result.Message);
        }, cancellationToken);

    public Task<bool> InspectPushAsync(CancellationToken cancellationToken = default) =>
        RunAsync(RepositoryWorkspaceOperation.InspectPush, async token =>
        {
            if (!RequireRepository())
            {
                return false;
            }

            InvalidatePushReview();
            var result = await gitAccessor.InspectPushAsync(token);
            if (!result.Succeeded || result.Value is null)
            {
                State.PushFailureKind = NormalizePushFailureKind(result.FailureKind);
                return Fail(result, GetPushRecoveryGuidance(State.PushFailureKind.Value));
            }

            State.PushReview = result.Value;
            return Succeed("Review the origin, branch, destination ref, and outgoing commit before confirming the push.");
        }, cancellationToken);

    public void ConfirmPushReview(bool confirmed)
    {
        State.PushConfirmed = confirmed && State.PushReview is not null;
        State.Error = null;
        NotifyChanged();
    }

    public Task<bool> StoreCredentialAsync(string credential, CancellationToken cancellationToken = default) =>
        RunAsync(RepositoryWorkspaceOperation.StoreCredential, async token =>
        {
            var credentialForAttempt = credential?.Trim() ?? string.Empty;
            if (credentialForAttempt.Length == 0)
            {
                State.HasCredential = false;
                State.CredentialReplacementRequired = true;
                return Fail("Enter a token before saving it for this tab.");
            }

            try
            {
                var result = await gitAccessor.StoreCredentialAsync(credentialForAttempt, token);
                State.HasCredential = result.Succeeded && result.Value is true;
                State.CredentialReplacementRequired = !State.HasCredential;
                return State.HasCredential
                    ? Succeed("Token saved for this tab.")
                    : Fail("The token could not be saved in this tab. Enter a replacement token to continue.", result.FailureKind);
            }
            finally
            {
                credentialForAttempt = string.Empty;
            }
        }, cancellationToken, exposeAccessorException: false);

    public Task<bool> ForgetCredentialAsync(CancellationToken cancellationToken = default) =>
        RunAsync(RepositoryWorkspaceOperation.ForgetCredential, async token =>
        {
            State.HasCredential = false;
            var result = await gitAccessor.ForgetCredentialAsync(token);
            State.CredentialReplacementRequired = !result.Succeeded || result.Value is not true;
            return !State.CredentialReplacementRequired
                ? Succeed("Token forgotten for this tab.")
                : Fail("The token could not be removed from tab storage. Close this tab before continuing.", result.FailureKind);
        }, cancellationToken, exposeAccessorException: false);

    public Task<bool> PushReviewedCommitAsync(PushReview reviewedPush, CancellationToken cancellationToken = default) =>
        RunAsync(RepositoryWorkspaceOperation.Push, async token =>
        {
            if (!RequireRepository())
            {
                return false;
            }

            if (reviewedPush is null || State.PushReview is null || !State.PushConfirmed || !State.HasCredential)
            {
                return Fail("Review and confirm the exact push destination and provide a token before pushing.");
            }

            if (reviewedPush != State.PushReview)
            {
                InvalidatePushReview();
                return Fail("The submitted push review does not match the currently reviewed coordinates. Review the push again.");
            }

            var latest = await gitAccessor.InspectPushAsync(token);
            if (!latest.Succeeded || latest.Value is null)
            {
                InvalidatePushReview();
                State.PushFailureKind = NormalizePushFailureKind(latest.FailureKind);
                return Fail(latest, GetPushRecoveryGuidance(State.PushFailureKind.Value));
            }

            if (latest.Value != reviewedPush)
            {
                InvalidatePushReview();
                return Fail("The push coordinates changed after review. Review the current destination and commit before pushing.");
            }

            var request = new PushRequest(reviewedPush.RepositoryUrl, reviewedPush.Branch, reviewedPush.OutgoingCommitId, reviewedPush.DestinationRef);
            var result = await gitAccessor.PushAsync(request, token);
            State.PushConfirmed = false;
            if (!result.Succeeded || result.Value is null)
            {
                State.PushFailureKind = NormalizePushFailureKind(result.FailureKind);
                if (State.PushFailureKind == GitOperationFailureKind.CredentialRejected)
                {
                    State.HasCredential = false;
                    State.CredentialReplacementRequired = true;
                }
                return Fail(result, GetPushRecoveryGuidance(State.PushFailureKind.Value));
            }

            State.PushFailureKind = null;
            State.PushReview = null;
            if (!await RefreshStatusCoreAsync(token))
            {
                return false;
            }

            return Succeed($"Pushed commit {result.Value.PushedCommitId} to the reviewed branch.");
        }, cancellationToken);

    public void CancelCurrentOperation() => activeOperation?.Cancel();

    private async Task<bool> LoadDirectoryCoreAsync(string path, CancellationToken token)
    {
        if (!RequireRepository())
        {
            return false;
        }

        var normalizedPath = NormalizeRepositoryPath(path);
        State.Entries = Array.Empty<RepositoryFileEntry>();
        ClearSelection();
        NotifyChanged();

        var result = await fileAccessor.FilterFilesAsync(new FilterFilesRequest(normalizedPath, FileFilterKind.DirectoryEntries), token);
        if (!result.Succeeded || result.Value is null)
        {
            return Fail(result, "Repository files could not be loaded. Reopen the public repository and try again.");
        }

        State.Entries = result.Value.Entries;
        State.CurrentPath = result.Value.Path;
        return Succeed(result.Message);
    }

    private async Task<bool> RefreshStatusCoreAsync(CancellationToken token)
    {
        if (!RequireRepository())
        {
            State.ChangedFiles = Array.Empty<ChangedFile>();
            return false;
        }

        var result = await gitAccessor.GetStatusAsync(token);
        if (!result.Succeeded || result.Value is null)
        {
            State.ChangedFiles = Array.Empty<ChangedFile>();
            return Fail(result, "Changed files could not be loaded. Reopen the repository and try again.");
        }

        State.ChangedFiles = result.Value;
        return Succeed(result.Message);
    }

    private async Task<bool> RunAsync(
        RepositoryWorkspaceOperation operation,
        Func<CancellationToken, Task<bool>> action,
        CancellationToken cancellationToken,
        bool exposeAccessorException = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!await operationGate.WaitAsync(0, CancellationToken.None))
        {
            State.Error = new(operation, "Another repository operation is already in progress.");
            State.Result = new(operation, false, State.Error.Message);
            NotifyChanged();
            return false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        activeOperation = linkedCancellation;
        State.Operation = operation;
        State.Error = null;
        State.Result = null;
        NotifyChanged();

        try
        {
            return await action(linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            State.Error = new(operation, "The repository operation was cancelled.", WasCancelled: true);
            State.Result = new(operation, false, State.Error.Message);
            return false;
        }
        catch (Exception exception)
        {
            var diagnostic = exposeAccessorException ? exception.GetType().Name : null;
            State.Error = new(operation, "The repository operation could not be completed safely.", diagnostic, GitOperationFailureKind.Unknown);
            State.Result = new(operation, false, State.Error.Message, State.Error.FailureKind);
            return false;
        }
        finally
        {
            activeOperation = null;
            State.Operation = RepositoryWorkspaceOperation.None;
            operationGate.Release();
            NotifyChanged();
        }
    }

    private bool RequireRepository() => State.Repository is not null || Fail("Open a repository before continuing.");

    private bool Succeed(string message)
    {
        State.Error = null;
        State.Result = new(State.Operation, true, message);
        NotifyChanged();
        return true;
    }

    private bool Fail(string message, GitOperationFailureKind? failureKind = null)
    {
        State.Error = new(State.Operation, message, FailureKind: failureKind);
        State.Result = new(State.Operation, false, message, failureKind);
        NotifyChanged();
        return false;
    }

    private bool Fail<T>(GitOperationResult<T> result, string fallbackMessage)
    {
        var message = string.IsNullOrWhiteSpace(result.Message) ? fallbackMessage : result.Message;
        State.Error = new(State.Operation, message, result.Diagnostic, result.FailureKind);
        State.Result = new(State.Operation, false, message, result.FailureKind);
        NotifyChanged();
        return false;
    }

    private void ResetWorkspace()
    {
        State.Repository = null;
        State.Entries = Array.Empty<RepositoryFileEntry>();
        State.CurrentPath = "/";
        State.ChangedFiles = Array.Empty<ChangedFile>();
        State.CreatedCommit = null;
        ClearSelection();
        InvalidatePushReview();
    }

    private void ClearSelection()
    {
        State.SelectedFilePath = null;
        State.SelectedFileContent = null;
        State.EditableFileContent = string.Empty;
    }

    private void InvalidatePushReview()
    {
        State.PushReview = null;
        State.PushConfirmed = false;
        State.PushFailureKind = null;
    }

    private void NotifyChanged() => StateChanged?.Invoke();

    private static string CreateWorkspaceName(string repositoryUrl)
    {
        var name = repositoryUrl.TrimEnd('/').Split('/').LastOrDefault() ?? "repository";
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        var safeCharacters = name.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        var workspaceName = new string(safeCharacters).Trim('-').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(workspaceName) ? "repository" : workspaceName;
    }

    private static string NormalizeRepositoryPath(string path) =>
        string.IsNullOrWhiteSpace(path) || path == "." ? "/" : path.StartsWith('/') ? path : $"/{path}";

    private static GitOperationFailureKind NormalizePushFailureKind(GitOperationFailureKind? failureKind) => failureKind switch
    {
        GitOperationFailureKind.CredentialRejected => GitOperationFailureKind.CredentialRejected,
        GitOperationFailureKind.RemoteAhead => GitOperationFailureKind.RemoteAhead,
        GitOperationFailureKind.NetworkUnavailable => GitOperationFailureKind.NetworkUnavailable,
        GitOperationFailureKind.UnsupportedRef => GitOperationFailureKind.UnsupportedRef,
        _ => GitOperationFailureKind.Unknown
    };

    private static string GetPushRecoveryGuidance(GitOperationFailureKind failureKind) => failureKind switch
    {
        GitOperationFailureKind.CredentialRejected => "The remote rejected the credential or repository permission. Enter a replacement token, review the destination again, and retry manually.",
        GitOperationFailureKind.RemoteAhead => "The remote branch is ahead. Reconcile the remote commits, then review and retry manually.",
        GitOperationFailureKind.NetworkUnavailable => "The browser could not reach the remote. Resolve the network issue, then retry this reviewed push manually.",
        GitOperationFailureKind.UnsupportedRef => "The checked-out ref is not a supported local branch. Check out a local branch and review again.",
        _ => "The push could not be completed safely. Inspect the workspace before retrying manually."
    };

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        activeOperation?.Cancel();
    }
}
