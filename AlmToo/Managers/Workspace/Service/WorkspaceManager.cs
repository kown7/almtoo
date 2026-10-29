using AlmToo.Accessor.BrowserGitAccessor.Interface;
using AlmToo.Managers.Workspace.Interface;

namespace AlmToo.Managers.Workspace;

/// <summary>Coordinates one browser-local repository workspace and exposes credential-free UI state.</summary>
public sealed partial class WorkspaceManager : IDisposable
{
    private readonly IBrowserGitAccessor gitAccessor;
    private readonly IGitBugAccessor gitBugAccessor;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private CancellationTokenSource? activeOperation;
    private bool disposed;

    public WorkspaceManager(IBrowserGitAccessor gitAccessor, IGitBugAccessor gitBugAccessor)
    {
        this.gitAccessor = gitAccessor;
        this.gitBugAccessor = gitBugAccessor;
    }

    public WorkspaceState State { get; } = new();
    public event Action? StateChanged;

    public Task<bool> InitializeCredentialStateAsync(CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.InitializeCredential, async token =>
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
        }, cancellationToken);

    public Task<bool> OpenRepositoryAsync(string repositoryUrl, CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.OpenRepository, async token =>
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
            SetRepository(open.Value);
            if (!await LoadDirectoryCoreAsync("/", token))
            {
                return false;
            }

            if (!await RefreshStatusCoreAsync(token))
            {
                return false;
            }

            if (!await LoadBranchHistoryCoreAsync(token))
            {
                return false;
            }

            return Succeed(open.Message);
        }, cancellationToken);

    public Task<bool> LoadBranchHistoryAsync(CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.LoadBranchHistory, LoadBranchHistoryCoreAsync, cancellationToken);

    public Task<bool> SwitchBranchAsync(string branchName, CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.SwitchBranch, async token =>
        {
            if (!RequireRepository())
            {
                return false;
            }

            var request = new CheckoutBranchRequest(branchName?.Trim() ?? string.Empty);
            if (!BrowserGitBranchHistoryContract.IsValidCheckoutRequest(request))
            {
                return FailBranchSwitch(
                    GitOperationFailureKind.BranchSwitchInvalidRequest,
                    "Select a valid local branch before switching branches.");
            }

            if (State.HasUnsavedChanges)
            {
                return FailBranchSwitch(
                    GitOperationFailureKind.BranchSwitchDirtyWorktree,
                    "Save the edited file before switching branches.");
            }

            State.IsBranchSwitching = true;
            State.BranchHistoryWasCancelled = false;
            NotifyChanged();
            try
            {
                // This read must be fresh and must be the only Accessor mutation before checkout.
                var status = await gitAccessor.GetStatusAsync(token);
                if (!status.Succeeded || status.Value is null)
                {
                    return FailBranchSwitch(status, "The worktree status could not be verified; no branch switch was attempted.");
                }

                State.ChangedFiles = status.Value;
                NotifyChanged();
                if (State.ChangedFiles.Count > 0)
                {
                    return FailBranchSwitch(
                        GitOperationFailureKind.BranchSwitchDirtyWorktree,
                        "Commit or discard the changed files before switching branches.");
                }

                token.ThrowIfCancellationRequested();
                var checkout = await gitAccessor.CheckoutBranchAsync(request, token);
                if (!checkout.Succeeded)
                {
                    return FailBranchSwitch(checkout, "The branch could not be switched safely. Select another local branch and try again.");
                }

                token.ThrowIfCancellationRequested();
                if (!await LoadDirectoryCoreAsync("/", token))
                {
                    return FailBranchSwitchFromCurrentError("The switched branch directory could not be refreshed safely.");
                }

                token.ThrowIfCancellationRequested();
                if (!await RefreshStatusCoreAsync(token))
                {
                    return FailBranchSwitchFromCurrentError("The switched branch status could not be refreshed safely.");
                }

                token.ThrowIfCancellationRequested();
                if (!await LoadBranchHistoryCoreAsync(token))
                {
                    return false;
                }

                token.ThrowIfCancellationRequested();
                return Succeed($"Switched to branch {request.BranchName} and refreshed the workspace.");
            }
            finally
            {
                State.IsBranchSwitching = false;
                NotifyChanged();
            }
        }, cancellationToken);

    public Task<bool> LoadDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.LoadDirectory, token => LoadDirectoryCoreAsync(path, token), cancellationToken);

    public Task<bool> SelectFileAsync(string path, CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.LoadFile, async token =>
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

            var result = await gitAccessor.FilterFilesAsync(new FilterFilesRequest(path, FileFilterKind.TextContent), token);
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
        RunAsync(WorkspaceOperation.SaveFile, async token =>
        {
            if (!RequireRepository() || string.IsNullOrWhiteSpace(State.SelectedFilePath) || State.SelectedFileContent is null)
            {
                return Fail("Choose a text file before saving changes.");
            }

            var savedContent = State.EditableFileContent;
            var result = await gitAccessor.UpdateFilesAsync(
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
        RunAsync(WorkspaceOperation.RefreshStatus, RefreshStatusCoreAsync, cancellationToken);

    public Task<bool> ReviewSynchronizationAsync(CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.ReviewSynchronization, async token =>
        {
            if (!RequireRepository())
            {
                return false;
            }

            State.SynchronizationReview = null;
            State.SynchronizationDecision = null;
            State.SynchronizationFailureCategory = null;
            var repository = State.Repository!;
            var result = await gitAccessor.SynchronizeAsync(
                new SynchronizationRequest(repository.RepositoryUrl, "main", SynchronizationIntent.Review), token);
            if (!result.Succeeded || result.Value is null)
            {
                return FailSynchronization(result);
            }

            State.SynchronizationReview = result.Value;
            State.SynchronizationDecision = result.Value.Decision;
            return Succeed(GetSynchronizationReviewMessage(result.Value.Decision));
        }, cancellationToken);

    public Task<bool> ApplySynchronizationAsync(SynchronizationReview reviewedSynchronization, CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.ApplySynchronization, async token =>
        {
            if (!RequireRepository())
            {
                return false;
            }

            if (!await RefreshStatusCoreAsync(token))
            {
                return false;
            }

            if (State.HasUnsavedChanges)
            {
                return FailSynchronization(new SynchronizationFailure(
                    SynchronizationFailureCategory.UnsavedEditorChanges,
                    "Save the edited file before applying repository synchronization."));
            }

            if (State.ChangedFiles.Count > 0)
            {
                return FailSynchronization(new SynchronizationFailure(
                    SynchronizationFailureCategory.UncommittedWorkingTree,
                    "Commit or discard the changed files before applying repository synchronization."));
            }

            if (reviewedSynchronization is null || State.SynchronizationReview is null ||
                reviewedSynchronization != State.SynchronizationReview ||
                State.SynchronizationReview.Decision != SynchronizationDecisionState.ReadyToApply)
            {
                return FailSynchronization(new SynchronizationFailure(
                    SynchronizationFailureCategory.Unknown,
                    "Review the current incoming commits and explicitly accept the ready synchronization before applying it."));
            }

            var repository = State.Repository!;
            var result = await gitAccessor.SynchronizeAsync(
                new SynchronizationRequest(repository.RepositoryUrl, reviewedSynchronization.Branch, SynchronizationIntent.Apply), token);
            if (!result.Succeeded || result.Value is null)
            {
                return FailSynchronization(result);
            }

            State.SynchronizationReview = result.Value;
            State.SynchronizationDecision = result.Value.Decision;
            if (!await RefreshStatusCoreAsync(token))
            {
                return false;
            }

            return Succeed("Repository synchronization was applied and workspace status was refreshed.");
        }, cancellationToken);

    public Task<bool> ReviewCommitAsync(CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.ReviewCommit, async token =>
        {
            if (!RequireRepository())
            {
                return false;
            }

            State.CommitReviewLoaded = false;
            State.ProposedCommit = null;
            State.CreatedCommit = null;
            if (!await RefreshStatusCoreAsync(token))
            {
                return false;
            }

            if (!await RefreshPublicationReviewCoreAsync(token))
            {
                return false;
            }

            State.CommitReviewLoaded = true;
            State.LastRefreshSucceeded = true;
            return Succeed(State.ChangedFiles.Count == 0 && !State.HasChangedRefs
                ? "No changed files or refs are currently available for publication."
                : "Review the changed files and refs before confirming publication.");
        }, cancellationToken);

    /// <summary>Loads the reviewed branch and Git-Bug ref set without applying it.</summary>
    public Task<bool> ReviewPublicationAsync(CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.ReviewPublication, async token =>
        {
            if (!RequireRepository())
            {
                return false;
            }

            if (!await RefreshPublicationReviewCoreAsync(token))
            {
                return false;
            }

            return Succeed(State.HasChangedRefs
                ? "Review the changed refs before publishing."
                : "No changed refs are currently available for publication.");
        }, cancellationToken);

    /// <summary>Compatibility alias for callers that use the Accessor operation name.</summary>
    public Task<bool> InspectPublicationAsync(CancellationToken cancellationToken = default) =>
        ReviewPublicationAsync(cancellationToken);

    public void ConfirmPublicationReview(bool confirmed)
    {
        State.PublicationConfirmed = confirmed && State.PublicationReview is not null;
        State.Error = null;
        NotifyChanged();
    }

    public Task<bool> PublishReviewedPublicationAsync(CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.PublishPublication, async token =>
        {
            if (State.PublicationReview is not { } review)
            {
                return Fail("Review the changed refs before publishing.");
            }

            return await PublishPublicationCoreAsync(
                new GitPublicationRequest(review.RepositoryUrl, review.Branch, review.Updates, review.RemotePreconditions),
                token);
        }, cancellationToken);

    /// <summary>Publishes exactly the currently reviewed ref set after explicit confirmation.</summary>
    public Task<bool> PublishPublicationAsync(
        GitPublicationRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.PublishPublication,
            token => PublishPublicationCoreAsync(request, token), cancellationToken);

    public Task<bool> PublishReviewedAsync(CancellationToken cancellationToken = default) =>
        PublishReviewedPublicationAsync(cancellationToken);

    public Task<bool> PublishReviewedAsync(
        GitPublicationRequest request,
        CancellationToken cancellationToken = default) =>
        PublishPublicationAsync(request, cancellationToken);

    public void SetProposedCommit(WorkspaceCommitInput? request)
    {
        State.ProposedCommit = request;
        State.Error = null;
        NotifyChanged();
    }

    public Task<bool> ConfirmLocalCommitAsync(CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.Commit, token =>
        {
            if (!State.CommitReviewLoaded || State.ProposedCommit is null)
            {
                return Task.FromResult(Fail("Review changed files and enter valid commit details before confirming."));
            }

            return CommitCoreAsync(State.ProposedCommit, token);
        }, cancellationToken);

    public Task<bool> CommitAsync(WorkspaceCommitInput request, CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.Commit, token => CommitCoreAsync(request, token), cancellationToken);

    public Task<bool> CommitAsync(CommitRequest request, CancellationToken cancellationToken = default) =>
        CommitAsync(new WorkspaceCommitInput(request.Message, request.AuthorName, request.AuthorEmail), cancellationToken);

    private async Task<bool> CommitCoreAsync(WorkspaceCommitInput request, CancellationToken token)
    {
        if (!RequireRepository())
        {
            return false;
        }

        State.LastWriteSucceeded = null;
        if (request is null || string.IsNullOrWhiteSpace(request.Message) || string.IsNullOrWhiteSpace(request.AuthorName) || string.IsNullOrWhiteSpace(request.AuthorEmail))
        {
            State.LastWriteSucceeded = false;
            return Fail("Commit message, author name, and author email are required.");
        }

        var result = await gitAccessor.CommitAsync(
            new CommitRequest(request.Message, request.AuthorName, request.AuthorEmail), token);
        if (!result.Succeeded || result.Value is null)
        {
            State.CreatedCommit = null;
            State.LastWriteSucceeded = false;
            return Fail(result, "The local commit could not be created. Review the changed files and try again.");
        }

        State.CreatedCommit = result.Value;
        State.ProposedCommit = null;
        State.CommitReviewLoaded = false;
        State.LastWriteSucceeded = true;
        InvalidatePushReview();
        if (!await RefreshStatusCoreAsync(token))
        {
            return false;
        }

        return Succeed(result.Message);
    }

    public Task<bool> InspectPushAsync(CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.InspectPush, async token =>
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
        RunAsync(WorkspaceOperation.StoreCredential, async token =>
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
        }, cancellationToken);

    public Task<bool> ForgetCredentialAsync(CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.ForgetCredential, async token =>
        {
            State.HasCredential = false;
            var result = await gitAccessor.ForgetCredentialAsync(token);
            State.CredentialReplacementRequired = !result.Succeeded || result.Value is not true;
            return !State.CredentialReplacementRequired
                ? Succeed("Token forgotten for this tab.")
                : Fail("The token could not be removed from tab storage. Close this tab before continuing.", result.FailureKind);
        }, cancellationToken);

    public Task<bool> PushReviewedCommitAsync(PushReview reviewedPush, CancellationToken cancellationToken = default) =>
        RunAsync(WorkspaceOperation.Push, async token =>
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

    public void CancelCurrentOperation()
    {
        activeOperation?.Cancel();
        CancelGitBugOperation();
    }

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

        var result = await gitAccessor.FilterFilesAsync(new FilterFilesRequest(normalizedPath, FileFilterKind.DirectoryEntries), token);
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
            InvalidatePublicationReview();
            return false;
        }

        var result = await gitAccessor.GetStatusAsync(token);
        if (!result.Succeeded || result.Value is null)
        {
            State.ChangedFiles = Array.Empty<ChangedFile>();
            InvalidatePublicationReview();
            return Fail(result, "Changed files could not be loaded. Reopen the repository and try again.");
        }

        State.ChangedFiles = result.Value;
        InvalidatePublicationReview();
        State.LastRefreshSucceeded = true;
        return Succeed(result.Message);
    }

    private async Task<bool> RefreshPublicationReviewCoreAsync(CancellationToken token)
    {
        InvalidatePublicationReview();

        GitOperationResult<GitPublicationReview> result;
        try
        {
            result = await gitAccessor.InspectPublicationAsync(token);
        }
        catch (NotSupportedException)
        {
            // Legacy test doubles and alternate Accessors may not expose ref-aware publication yet.
            return true;
        }

        if (!result.Succeeded || result.Value is null)
        {
            State.PublicationFailureKind = result.FailureKind;
            return Fail(result, "Changed refs could not be reviewed safely. Retry the repository refresh.");
        }

        if (!BrowserGitPublicationContract.IsValidReview(result.Value))
        {
            State.PublicationFailureKind = GitOperationFailureKind.UnsupportedRef;
            return Fail("The reviewed refs were not safe to publish.", State.PublicationFailureKind);
        }

        State.PublicationReview = result.Value;
        State.PublicationFailureKind = null;
        return true;
    }

    private async Task<bool> PublishPublicationCoreAsync(
        GitPublicationRequest request,
        CancellationToken token)
    {
        if (!State.PublicationConfirmed || State.PublicationReview is not { } review)
        {
            return Fail("Review and explicitly confirm the changed refs before publishing.");
        }

        if (!State.HasCredential)
        {
            State.PublicationFailureKind = GitOperationFailureKind.CredentialRejected;
            State.PushFailureKind = GitOperationFailureKind.CredentialRejected;
            State.CredentialReplacementRequired = true;
            State.LastWriteSucceeded = false;
            return Fail("Enter a token before publishing the reviewed refs.", GitOperationFailureKind.CredentialRejected);
        }

        if (request is null
            || !BrowserGitPublicationContract.IsValidRequest(request)
            || !ReviewsMatch(review, request))
        {
            State.PublicationFailureKind = GitOperationFailureKind.UnsupportedRef;
            return Fail("The publication review is stale or invalid. Review the changed refs again.", State.PublicationFailureKind);
        }

        State.LastWriteSucceeded = null;
        State.PublicationConfirmed = false;
        var result = await gitAccessor.PublishAsync(request, token);
        if (!result.Succeeded || result.Value is null)
        {
            State.PublicationFailureKind = result.FailureKind;
            State.PushFailureKind = NormalizePushFailureKind(result.FailureKind);
            await gitAccessor.ForgetCredentialAsync(token);
            State.HasCredential = false;
            State.CredentialReplacementRequired = true;
            State.PublicationReview = null;
            State.PublicationConfirmed = false;
            State.LastWriteSucceeded = false;
            return Fail(result, GetPublicationFailureGuidance(result.FailureKind));
        }

        var receipt = result.Value;
        var forgotten = await gitAccessor.ForgetCredentialAsync(token);
        State.HasCredential = false;
        State.CredentialReplacementRequired = !forgotten.Succeeded || forgotten.Value is not true;
        State.PublicationReceipt = receipt;
        State.PublicationConfirmed = false;
        State.LastWriteSucceeded = true;
        if (!await RefreshStatusCoreAsync(token))
        {
            return false;
        }

        State.PublicationReceipt = receipt;
        State.PublicationReview = review;
        return Succeed(result.Message);
    }

    private static bool ReviewsMatch(GitPublicationReview review, GitPublicationRequest request) =>
        string.Equals(review.RepositoryUrl, request.RepositoryUrl, StringComparison.Ordinal)
        && string.Equals(review.Branch, request.Branch, StringComparison.Ordinal)
        && review.Updates.SequenceEqual(request.Updates)
        && review.RemotePreconditions.SequenceEqual(request.RemotePreconditions);

    private async Task<bool> LoadBranchHistoryCoreAsync(CancellationToken cancellationToken)
    {
        if (!RequireRepository())
        {
            return false;
        }

        State.IsBranchHistoryLoading = true;
        State.BranchHistoryWasCancelled = false;
        State.BranchHistoryFailure = null;
        NotifyChanged();

        try
        {
            var branches = await gitAccessor.GetBranchesAsync(cancellationToken);
            if (!branches.Succeeded || branches.Value is null)
            {
                return FailBranchHistory(branches, "Branch information is unavailable. Reopen the repository and try again.");
            }

            var history = await gitAccessor.GetHistoryAsync(
                new BranchHistoryRequest(branches.Value.Current?.Name), cancellationToken);
            if (!history.Succeeded || history.Value is null)
            {
                return FailBranchHistory(history, "Recent commit history is unavailable. Reopen the repository and try again.");
            }

            State.CurrentBranch = branches.Value.Current;
            State.AvailableBranches = branches.Value.Choices;
            State.RecentHistory = history.Value;
            return Succeed("Branch and recent history information was refreshed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            State.BranchHistoryWasCancelled = true;
            throw;
        }
        finally
        {
            State.IsBranchHistoryLoading = false;
            NotifyChanged();
        }
    }

    private bool FailBranchHistory<T>(GitOperationResult<T> result, string fallbackMessage)
    {
        var category = result.FailureKind switch
        {
            GitOperationFailureKind.BranchHistoryDataUnavailable => BranchHistoryFailureCategory.RepositoryUnavailable,
            GitOperationFailureKind.BranchHistoryDataMalformed => BranchHistoryFailureCategory.DataMalformed,
            GitOperationFailureKind.BranchHistoryInvalidRequest => BranchHistoryFailureCategory.InvalidRequest,
            GitOperationFailureKind.BranchHistoryUnsupportedReference => BranchHistoryFailureCategory.UnsupportedReference,
            GitOperationFailureKind.BranchHistoryLimitExceeded => BranchHistoryFailureCategory.ResultLimitExceeded,
            _ => BranchHistoryFailureCategory.Unknown
        };

        State.BranchHistoryFailure = new(category, fallbackMessage);
        return Fail(fallbackMessage, result.FailureKind);
    }

    private bool FailBranchSwitch(GitOperationFailureKind failureKind, string fallbackMessage) =>
        Fail(GetBranchSwitchFailureMessage(failureKind, fallbackMessage), failureKind);

    private bool FailBranchSwitch(GitOperationResult result, string fallbackMessage) =>
        FailBranchSwitch(result.FailureKind ?? GitOperationFailureKind.BranchSwitchUnknown, fallbackMessage);

    private bool FailBranchSwitch<T>(GitOperationResult<T> result, string fallbackMessage) =>
        FailBranchSwitch(result.FailureKind ?? GitOperationFailureKind.BranchSwitchUnknown, fallbackMessage);

    private bool FailBranchSwitchFromCurrentError(string fallbackMessage) =>
        FailBranchSwitch(State.Error?.FailureKind ?? GitOperationFailureKind.BranchSwitchUnavailable, fallbackMessage);

    private static string GetBranchSwitchFailureMessage(GitOperationFailureKind failureKind, string fallbackMessage) => failureKind switch
    {
        GitOperationFailureKind.BranchSwitchInvalidRequest => "Select a valid local branch before switching branches.",
        GitOperationFailureKind.BranchSwitchDirtyWorktree => "Commit or discard the changed files before switching branches.",
        GitOperationFailureKind.BranchSwitchUnsupportedReference => "The selected branch is not a supported local branch.",
        GitOperationFailureKind.BranchSwitchUnavailable => "The repository state is unavailable; no branch switch was completed.",
        GitOperationFailureKind.NetworkUnavailable => "The repository state could not be reached; no branch switch was completed.",
        _ => fallbackMessage
    };

    private async Task<bool> RunAsync(
        WorkspaceOperation operation,
        Func<CancellationToken, Task<bool>> action,
        CancellationToken cancellationToken)
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
            if (operation is WorkspaceOperation.ReviewSynchronization or WorkspaceOperation.ApplySynchronization)
            {
                State.SynchronizationFailureCategory = SynchronizationFailureCategory.Cancelled;
                State.SynchronizationDecision = SynchronizationDecisionState.Cancelled;
            }
            State.Error = new(operation, "The repository operation was cancelled.", WasCancelled: true);
            State.Result = new(operation, false, State.Error.Message);
            return false;
        }
        catch (Exception)
        {
            State.Error = new(operation, "The repository operation could not be completed safely.", GitOperationFailureKind.Unknown);
            State.Result = new(operation, false, State.Error.Message, State.Error.FailureKind);
            return false;
        }
        finally
        {
            activeOperation = null;
            State.Operation = WorkspaceOperation.None;
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
        State.Error = new(State.Operation, message, result.FailureKind);
        State.Result = new(State.Operation, false, message, result.FailureKind);
        NotifyChanged();
        return false;
    }

    private bool FailSynchronization(GitOperationResult<SynchronizationReview> result)
    {
        var category = result.FailureKind switch
        {
            GitOperationFailureKind.CredentialRejected => SynchronizationFailureCategory.CredentialRejected,
            GitOperationFailureKind.NetworkUnavailable => SynchronizationFailureCategory.NetworkUnavailable,
            GitOperationFailureKind.UnsupportedRef => SynchronizationFailureCategory.UnsupportedRef,
            GitOperationFailureKind.RemoteAhead => SynchronizationFailureCategory.DivergentHistory,
            _ => SynchronizationFailureCategory.Unknown
        };
        return FailSynchronization(new SynchronizationFailure(category, GetSynchronizationFailureMessage(category)));
    }

    private bool FailSynchronization(SynchronizationFailure failure)
    {
        State.SynchronizationFailureCategory = failure.Category;
        State.SynchronizationDecision = SynchronizationDecisionState.Failed;
        State.Error = new(State.Operation, failure.Message);
        State.Result = new(State.Operation, false, failure.Message);
        NotifyChanged();
        return false;
    }

    private void ResetWorkspace()
    {
        State.SynchronizationReview = null;
        State.SynchronizationDecision = null;
        State.SynchronizationFailureCategory = null;
        State.Repository = null;
        SetRepository(null);
        State.Entries = Array.Empty<RepositoryFileEntry>();
        State.CurrentPath = "/";
        State.ChangedFiles = Array.Empty<ChangedFile>();
        State.CreatedCommit = null;
        State.CurrentBranch = null;
        State.AvailableBranches = Array.Empty<BranchChoice>();
        State.RecentHistory = null;
        State.IsBranchHistoryLoading = false;
        State.IsBranchSwitching = false;
        State.BranchHistoryWasCancelled = false;
        State.BranchHistoryFailure = null;
        ClearSelection();
        InvalidatePushReview();
        InvalidatePublicationReview();
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

    private void InvalidatePublicationReview()
    {
        State.PublicationReview = null;
        State.PublicationConfirmed = false;
        State.PublicationReceipt = null;
        State.PublicationFailureKind = null;
    }

    private static string GetPublicationFailureGuidance(GitOperationFailureKind? failureKind) =>
        GetPushRecoveryGuidance(NormalizePushFailureKind(failureKind));

    private static string GetSynchronizationReviewMessage(SynchronizationDecisionState decision) => decision switch
    {
        SynchronizationDecisionState.Current => "The workspace is already current with the remote branch.",
        SynchronizationDecisionState.Ahead => "The workspace is ahead of the remote branch; no incoming changes are available.",
        SynchronizationDecisionState.Divergent => "The local and remote branches have diverged. Reconcile them before synchronizing.",
        SynchronizationDecisionState.ReadyToApply => "Incoming changes are ready. Review them and explicitly accept synchronization to apply.",
        SynchronizationDecisionState.Applied => "Repository synchronization was applied.",
        _ => "Review the repository synchronization result before continuing."
    };

    private static string GetSynchronizationFailureMessage(SynchronizationFailureCategory category) => category switch
    {
        SynchronizationFailureCategory.CredentialRejected => "The remote rejected the tab credential. Replace it before retrying synchronization.",
        SynchronizationFailureCategory.NetworkUnavailable => "The remote could not be reached. Check connectivity before retrying synchronization.",
        SynchronizationFailureCategory.UnsupportedRef => "The checked-out ref is not supported for synchronization. Select a local branch and retry.",
        SynchronizationFailureCategory.DivergentHistory => "The local and remote histories diverged. Reconcile them before retrying synchronization.",
        _ => "Repository synchronization could not be completed safely. Inspect the workspace before retrying."
    };

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
        DisposeGitBugResources();
        activeOperation?.Cancel();
    }
}
