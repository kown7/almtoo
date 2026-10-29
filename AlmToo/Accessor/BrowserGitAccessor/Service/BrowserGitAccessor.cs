using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using AlmToo.Accessor.BrowserGitAccessor.Interface;
using Microsoft.JSInterop;

namespace AlmToo.Accessor.BrowserGitAccessor.Service;

/// <summary>
/// Cohesive browser-platform Accessor for the active Git workspace and its text files.
/// </summary>
public sealed class BrowserGitAccessor : IBrowserGitAccessor, IGitBugAccessor
{
    private const string ModulePath = "./js/browserGitEngine.js";
    private const string CredentialModulePath = "./js/pushCredentialSession.js";
    private const int MaxEditableTextBytes = 1024 * 1024;
    private const int MaxChangedFilePathLength = 1024;
    private const int MaxGitBugQueryLength = 256;
    private const int MaxGitBugPageSize = 100;
    private const int MaxGitBugTitleLength = 4096;
    private const int MaxGitBugLabelLength = 256;
    private const int MaxGitBugAuthorLength = 512;
    private const int MaxGitBugBodyLength = 1024 * 1024;
    private const int MaxGitBugDetailBytes = 4 * 1024 * 1024;
    private const int MaxGitBugComments = 1000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly IJSRuntime jsRuntime;
    private Task<IJSObjectReference>? moduleTask;
    private Task<IJSObjectReference>? credentialModuleTask;
    private Task<GitOperationResult>? initializationTask;

    public BrowserGitAccessor(IJSRuntime jsRuntime)
    {
        this.jsRuntime = jsRuntime;
    }

    public async ValueTask<GitOperationResult<RepositoryInfo>> CloneOrOpenAsync(
        RepositoryOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null
            || !Uri.TryCreate(request.RepositoryUrl, UriKind.Absolute, out var repository)
            || !IsAllowedRepositoryOrigin(repository)
            || string.IsNullOrWhiteSpace(request.WorkspaceName)
            || request.WorkspaceName.Length > 80
            || request.WorkspaceName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            return new GitOperationResult<RepositoryInfo>("cloneOrOpen", false, "The repository could not be cloned or opened.", Value: default, Diagnostic: "A credential-free HTTPS URL and a normalized workspace name are required.");
        }

        return await InvokeWithValueAsync<RepositoryInfoDto, RepositoryInfo>(
            "cloneOrOpen",
            "cloneOrOpen",
            value => value.ToRepositoryInfo(),
            cancellationToken,
            request);
    }

    public async ValueTask<GitOperationResult<FilterFilesResult>> FilterFilesAsync(
        FilterFilesRequest request,
        CancellationToken cancellationToken = default)
    {
        const string operation = "filterFiles";
        if (request is null
            || !Enum.IsDefined(request.Kind)
            || !TryNormalizeRepositoryPath(request.Path, request.Kind == FileFilterKind.DirectoryEntries, out var path))
        {
            return new GitOperationResult<FilterFilesResult>(operation, false, "The requested repository path is not valid.", Value: default, Diagnostic: "The path must be normalized and contained in the active checkout.");
        }

        if (request.Kind == FileFilterKind.DirectoryEntries)
        {
            var entries = await InvokeWithValueAsync<List<RepositoryFileEntryDto>, IReadOnlyList<RepositoryFileEntry>>(
                "listFiles", "listFiles", values => values.Select(value => value.ToRepositoryFileEntry()).ToArray(), cancellationToken, path);
            return entries.Succeeded
                ? new GitOperationResult<FilterFilesResult>(operation, true, entries.Message, new(path, entries.Value!))
                : new GitOperationResult<FilterFilesResult>(operation, false, entries.Message, Value: default, Diagnostic: entries.Diagnostic, FailureKind: entries.FailureKind);
        }

        if (!IsSupportedTextPath(path))
        {
            return new GitOperationResult<FilterFilesResult>(operation, false, "The requested file is not supported text.", Value: default, Diagnostic: "Only allowlisted UTF-8 text files can be read.");
        }

        var text = await InvokeWithValueAsync<TextFileContentDto, TextFileContent>(
            "readTextFile", "readTextFile", value => value.ToTextFileContent(), cancellationToken, path);
        return text.Succeeded
            ? new GitOperationResult<FilterFilesResult>(operation, true, text.Message, new(path, Array.Empty<RepositoryFileEntry>(), text.Value))
            : new GitOperationResult<FilterFilesResult>(operation, false, text.Message, Value: default, Diagnostic: text.Diagnostic, FailureKind: text.FailureKind);
    }

    public async ValueTask<GitOperationResult<UpdateFilesResult>> UpdateFilesAsync(
        UpdateFilesRequest request,
        CancellationToken cancellationToken = default)
    {
        const string operation = "updateFiles";
        if (request?.Updates is not { Count: > 0 })
        {
            return new GitOperationResult<UpdateFilesResult>(operation, false, "No file updates were supplied.", Value: default, Diagnostic: "At least one text update is required.");
        }

        var validated = new List<TextFileUpdate>(request.Updates.Count);
        var uniquePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var update in request.Updates)
        {
            if (update is null || update.Content is null
                || !TryNormalizeRepositoryPath(update.Path, allowRoot: false, out var path)
                || !IsSupportedTextPath(path)
                || !TryGetUtf8ByteCount(update.Content, out var byteCount)
                || byteCount > MaxEditableTextBytes
                || !uniquePaths.Add(path))
            {
                return new GitOperationResult<UpdateFilesResult>(operation, false, "The requested file updates are not valid.", Value: default, Diagnostic: "Paths must be unique, contained text-file paths with valid UTF-8 content no larger than 1 MiB.");
            }
            validated.Add(new(path, update.Content));
        }

        foreach (var update in validated)
        {
            var result = await InvokeAsync("writeTextFile", "writeTextFile", cancellationToken, update.Path, update.Content);
            if (!result.Succeeded)
            {
                return new GitOperationResult<UpdateFilesResult>(operation, false, result.Message, Value: default, Diagnostic: result.Diagnostic, FailureKind: result.FailureKind);
            }
        }

        return new GitOperationResult<UpdateFilesResult>(operation, true, "The browser-local files were updated.", new(validated.Select(update => update.Path).ToArray()));
    }

    public async ValueTask<GitOperationResult<BranchSnapshot>> GetBranchesAsync(
        CancellationToken cancellationToken = default)
    {
        const string operation = BrowserGitBranchHistoryOperations.ListBranches;
        var result = await InvokeWithValueAsync<BranchSnapshotDto, BranchSnapshot>(
            operation,
            "getBranches",
            value => value.ToContract(),
            cancellationToken);

        if (!result.Succeeded || result.Value is null)
        {
            return result;
        }

        return BrowserGitBranchHistoryContract.IsValidSnapshot(result.Value)
            ? result
            : new GitOperationResult<BranchSnapshot>(
                operation,
                false,
                "Branch data could not be read safely.",
                Value: default,
                Diagnostic: null,
                FailureKind: GitOperationFailureKind.BranchHistoryDataMalformed);
    }

    public async ValueTask<GitOperationResult<RemoteSnapshot>> GetRemoteSnapshotAsync(
        RemoteSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        const string operation = BrowserGitRemoteSnapshotOperations.Read;
        if (!BrowserGitRemoteSnapshotContract.IsValidRequest(request))
        {
            return new GitOperationResult<RemoteSnapshot>(
                operation,
                false,
                "The remote snapshot request is not valid.",
                Value: default,
                Diagnostic: null,
                FailureKind: GitOperationFailureKind.RemoteSnapshotInvalidRequest);
        }

        var result = await InvokeWithValueAsync<RemoteSnapshotDto, RemoteSnapshot>(
            operation,
            "getRemoteSnapshot",
            value => value.ToContract(),
            cancellationToken,
            request);

        return SanitizeRemoteSnapshotResult(result);
    }

    public async ValueTask<GitOperationResult> CheckoutBranchAsync(
        CheckoutBranchRequest request,
        CancellationToken cancellationToken = default)
    {
        const string operation = BrowserGitBranchHistoryOperations.CheckoutBranch;
        if (!BrowserGitBranchHistoryContract.IsValidCheckoutRequest(request))
        {
            return new GitOperationResult(
                operation,
                false,
                "The branch selection is not valid.",
                FailureKind: GitOperationFailureKind.BranchSwitchInvalidRequest);
        }

        return await InvokeAsync(operation, "checkoutBranch", cancellationToken, request);
    }

    public async ValueTask<GitOperationResult<CommitHistory>> GetHistoryAsync(
        BranchHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        const string operation = BrowserGitBranchHistoryOperations.ReadHistory;
        if (!BrowserGitBranchHistoryContract.IsValidRequest(request))
        {
            return new GitOperationResult<CommitHistory>(
                operation,
                false,
                "The branch history request is not valid.",
                Value: default,
                Diagnostic: null,
                FailureKind: GitOperationFailureKind.BranchHistoryInvalidRequest);
        }

        var result = await InvokeWithValueAsync<CommitHistoryDto, CommitHistory>(
            operation,
            "getHistory",
            value => value.ToContract(),
            cancellationToken,
            request);

        if (!result.Succeeded || result.Value is null)
        {
            return result;
        }

        return BrowserGitBranchHistoryContract.IsValidHistory(result.Value)
            ? result
            : new GitOperationResult<CommitHistory>(
                operation,
                false,
                "Commit history could not be read safely.",
                Value: default,
                Diagnostic: null,
                FailureKind: GitOperationFailureKind.BranchHistoryDataMalformed);
    }

    public async ValueTask<GitOperationResult<IReadOnlyList<ChangedFile>>> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeWithValueAsync<List<ChangedFileDto>, IReadOnlyList<ChangedFile>>(
            "getStatus",
            "getStatus",
            files => files.Select(file => file.ToChangedFile()).ToArray(),
            cancellationToken);
        if (!result.Succeeded || result.Value is null)
        {
            return result;
        }

        if (!BrowserGitCommitContract.IsValidChangedFiles(result.Value))
        {
            return new GitOperationResult<IReadOnlyList<ChangedFile>>(
                "getStatus", false, "Repository status could not be read safely.",
                Value: default, FailureKind: GitOperationFailureKind.GitBugDataMalformed);
        }

        return result;
    }

    public async ValueTask<GitOperationResult<CommitInfo>> CommitAsync(
        CommitRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!BrowserGitCommitContract.IsValidCommitRequest(request))
        {
            return new GitOperationResult<CommitInfo>("commit", false, "A browser-local commit could not be created.", Value: default, Diagnostic: "A non-empty message and valid author metadata are required.");
        }

        return await InvokeWithValueAsync<CommitInfoDto, CommitInfo>(
            "commit",
            "commit",
            value => value.ToCommitInfo(),
            cancellationToken,
            request);
    }

    public async ValueTask<GitOperationResult<PushReview>> InspectPushAsync(
        CancellationToken cancellationToken = default)
    {
        return await InvokeWithValueAsync<PushReviewDto, PushReview>(
            "inspectPush",
            "inspectPush",
            value => value.ToPushReview(),
            cancellationToken);
    }

    public async ValueTask<GitOperationResult<GitPublicationReview>> InspectPublicationAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeWithValueAsync<GitPublicationReviewDto, GitPublicationReview>(
            "inspectPublication",
            "inspectPublication",
            value => value.ToReview(),
            cancellationToken);

        if (!result.Succeeded || result.Value is null || !BrowserGitPublicationContract.IsValidReview(result.Value))
        {
            return new GitOperationResult<GitPublicationReview>(
                "inspectPublication",
                false,
                "The reviewed publication set could not be read safely.",
                Value: default,
                FailureKind: GitOperationFailureKind.GitBugDataMalformed);
        }

        return result;
    }

    public async ValueTask<GitOperationResult<GitPublicationReceipt>> PublishAsync(
        GitPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!BrowserGitPublicationContract.IsValidRequest(request))
        {
            return new GitOperationResult<GitPublicationReceipt>(
                "publish",
                false,
                "The reviewed publication request is invalid.",
                Value: default,
                FailureKind: GitOperationFailureKind.UnsupportedRef);
        }

        var initialized = await EnsureInitializedAsync(cancellationToken);
        if (!initialized.Succeeded)
        {
            return new GitOperationResult<GitPublicationReceipt>(
                "publish",
                false,
                "Browser Git could not be initialized for publication.",
                Value: default,
                FailureKind: GitOperationFailureKind.Unknown);
        }

        var credentialModule = await GetCredentialModuleAsync(cancellationToken);
        var personalAccessToken = await credentialModule.InvokeAsync<string?>("getCredentialForPush", cancellationToken);
        if (string.IsNullOrWhiteSpace(personalAccessToken))
        {
            return new GitOperationResult<GitPublicationReceipt>(
                "publish",
                false,
                "Enter a token before publishing the reviewed refs.",
                Value: default,
                FailureKind: GitOperationFailureKind.CredentialRejected);
        }

        var result = await InvokeWithValueAsync<GitPublicationReceiptDto, GitPublicationReceipt>(
            "publish",
            "publish",
            value => value.ToReceipt(),
            cancellationToken,
            request,
            personalAccessToken);

        return SanitizePublicationResult(result);
    }

    public async ValueTask<GitOperationResult<SynchronizationReview>> SynchronizeAsync(
        SynchronizationRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request is null || !Enum.IsDefined(request.Intent))
            {
                return SanitizedSynchronizationFailure(GitOperationFailureKind.Unknown);
            }

            ValidateSynchronizationCoordinates(request.RepositoryUrl, request.Branch);
            var result = await InvokeWithValueAsync<SynchronizationReviewDto, SynchronizationReview>(
                "synchronize",
                "synchronize",
                value => value.ToSynchronizationReview(),
                cancellationToken,
                request);

            var sanitized = SanitizeSynchronizationResult(result);
            if (sanitized.Succeeded && (sanitized.Value is null
                || !string.Equals(sanitized.Value.RepositoryUrl, request.RepositoryUrl, StringComparison.Ordinal)
                || !string.Equals(sanitized.Value.Branch, request.Branch, StringComparison.Ordinal)))
            {
                return SanitizedSynchronizationFailure(GitOperationFailureKind.Unknown);
            }

            return sanitized;
        }
        catch (OperationCanceledException)
        {
            return SanitizedSynchronizationFailure(GitOperationFailureKind.Unknown);
        }
        catch (Exception)
        {
            return SanitizedSynchronizationFailure(GitOperationFailureKind.Unknown);
        }
    }

    public async ValueTask<GitOperationResult<ReviewedReconciliationApplyReceipt>> ApplyReviewedReconciliationAsync(
        ReviewedReconciliationApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        const string operation = BrowserGitReconciliationOperations.Apply;
        if (!BrowserGitReconciliationContract.IsValidRequest(request))
        {
            return new GitOperationResult<ReviewedReconciliationApplyReceipt>(
                operation,
                false,
                "The reviewed reconciliation request is not valid.",
                Value: default,
                Diagnostic: null,
                FailureKind: GitOperationFailureKind.ReconciliationInvalidRequest);
        }

        try
        {
            var result = await InvokeWithValueAsync<ReviewedReconciliationApplyReceiptDto, ReviewedReconciliationApplyReceipt>(
                operation,
                "applyReviewedReconciliation",
                value => value.ToContract(),
                cancellationToken,
                request);

            if (!result.Succeeded)
            {
                return SanitizeReviewedReconciliationResult(result);
            }

            if (result.Value is null)
            {
                return SanitizeReviewedReconciliationResult(result);
            }

            return BrowserGitReconciliationContract.IsValidReceipt(result.Value)
                && string.Equals(result.Value.ReviewId, request.ReviewId, StringComparison.Ordinal)
                && string.Equals(result.Value.SourceRevision, request.SourceRevision, StringComparison.Ordinal)
                ? result
                : new GitOperationResult<ReviewedReconciliationApplyReceipt>(
                    operation,
                    false,
                    "The reviewed reconciliation result could not be read safely.",
                    Value: default,
                    Diagnostic: null,
                    FailureKind: GitOperationFailureKind.ReconciliationDataMalformed);
        }
        catch (OperationCanceledException)
        {
            return new GitOperationResult<ReviewedReconciliationApplyReceipt>(
                operation,
                false,
                "The reviewed reconciliation apply was canceled safely.",
                Value: default,
                Diagnostic: null,
                FailureKind: GitOperationFailureKind.ReconciliationCancelled);
        }
        catch (Exception)
        {
            return new GitOperationResult<ReviewedReconciliationApplyReceipt>(
                operation,
                false,
                "The reviewed reconciliation apply could not be completed safely.",
                Value: default,
                Diagnostic: null,
                FailureKind: GitOperationFailureKind.ReconciliationUnknown);
        }
    }

    private static GitOperationResult<ReviewedReconciliationApplyReceipt> SanitizeReviewedReconciliationResult(
        GitOperationResult<ReviewedReconciliationApplyReceipt> result)
    {
        if (result.Succeeded && result.Value is not null
            && BrowserGitReconciliationContract.IsValidReceipt(result.Value))
        {
            return result with { Diagnostic = null, FailureKind = null };
        }

        var failureKind = result.FailureKind is { } kind && BrowserGitReconciliationContract.IsValidFailure(kind)
            ? kind
            : result.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                ? GitOperationFailureKind.ReconciliationCancelled
                : GitOperationFailureKind.ReconciliationWriteFailed;
        var message = failureKind switch
        {
            GitOperationFailureKind.ReconciliationInvalidRequest => "The reviewed reconciliation request is not valid.",
            GitOperationFailureKind.ReconciliationStaleRemote => "The reviewed remote state is stale; review it again before applying.",
            GitOperationFailureKind.ReconciliationUnsupportedResolution => "The selected reconciliation resolution is not supported.",
            GitOperationFailureKind.ReconciliationCancelled => "The reviewed reconciliation apply was canceled safely.",
            GitOperationFailureKind.ReconciliationDataMalformed => "The reviewed reconciliation data could not be read safely.",
            GitOperationFailureKind.ReconciliationDataUnavailable => "The reviewed reconciliation data is unavailable.",
            _ => "The reviewed reconciliation could not be applied safely."
        };
        return new(
            BrowserGitReconciliationOperations.Apply,
            false,
            message,
            Value: default,
            Diagnostic: null,
            FailureKind: failureKind);
    }

    public async ValueTask<GitOperationResult<GitBugIssuePage>> ListIssuesAsync(
        GitBugIssueQuery query,
        CancellationToken cancellationToken = default)
    {
        const string operation = "gitBug.listIssues";
        if (!IsValidGitBugQuery(query))
        {
            return SanitizedGitBugFailure<GitBugIssuePage>(operation, GitOperationFailureKind.Unknown);
        }

        var request = new GitBugIssueQueryDto
        {
            State = query.State?.ToString().ToLowerInvariant(),
            SearchText = query.SearchText,
            Cursor = query.Cursor,
            PageSize = query.PageSize
        };

        return await InvokeGitBugWithValueAsync<GitBugIssuePageDto, GitBugIssuePage>(
            operation,
            "listGitBugIssues",
            value => value.ToGitBugIssuePage(),
            cancellationToken,
            request);
    }

    public async ValueTask<GitOperationResult<GitBugIssueDetail>> GetIssueAsync(
        GitBugIssueId issueId,
        CancellationToken cancellationToken = default)
    {
        const string operation = "gitBug.getIssue";
        if (!IsValidGitBugIssueId(issueId.Value))
        {
            return SanitizedGitBugFailure<GitBugIssueDetail>(operation, GitOperationFailureKind.Unknown);
        }

        return await InvokeGitBugWithValueAsync<GitBugIssueDetailDto, GitBugIssueDetail>(
            operation,
            "getGitBugIssue",
            value => value.ToGitBugIssueDetail(),
            cancellationToken,
            issueId.Value);
    }

    public async ValueTask<GitOperationResult<GitBugIssueEditResult>> EditIssueAsync(
        GitBugIssueEditRequest request,
        CancellationToken cancellationToken = default)
    {
        const string operation = GitBugWriteOperations.IssueEdit;
        if (!GitBugWriteContract.IsValidIssueEdit(request))
        {
            return SanitizedGitBugWriteFailure<GitBugIssueEditResult>(operation, GitOperationFailureKind.GitBugWriteInvalidRequest);
        }

        var dto = new GitBugIssueEditRequestDto(request);
        return await InvokeGitBugWithValueAsync<GitBugWriteResultDto, GitBugIssueEditResult>(
            operation,
            "editGitBugIssue",
            value => value.ToIssueEditResult(),
            cancellationToken,
            dto);
    }

    public async ValueTask<GitOperationResult<GitBugCommentCreateResult>> CreateCommentAsync(
        GitBugCommentCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        const string operation = GitBugWriteOperations.CommentCreate;
        if (!GitBugWriteContract.IsValidComment(request))
        {
            return SanitizedGitBugWriteFailure<GitBugCommentCreateResult>(operation, GitOperationFailureKind.GitBugWriteInvalidRequest);
        }

        var dto = new GitBugCommentCreateRequestDto(request);
        return await InvokeGitBugWithValueAsync<GitBugWriteResultDto, GitBugCommentCreateResult>(
            operation,
            "createGitBugComment",
            value => value.ToCommentCreateResult(),
            cancellationToken,
            dto);
    }

    public async ValueTask<GitOperationResult<bool>> HasCredentialAsync(CancellationToken cancellationToken = default)
    {
        const string operation = "hasCredential";
        try
        {
            var module = await GetCredentialModuleAsync(cancellationToken);
            var presence = await module.InvokeAsync<int>("credentialPresence", cancellationToken);
            return presence switch
            {
                0 => new GitOperationResult<bool>(operation, true, CredentialSuccessMessage(operation), false),
                1 => new GitOperationResult<bool>(operation, true, CredentialSuccessMessage(operation), true),
                _ => new GitOperationResult<bool>(operation, false, CredentialFailureMessage(operation), Value: default, Diagnostic: "Tab-scoped credential storage is unavailable.", FailureKind: GitOperationFailureKind.Unknown)
            };
        }
        catch (OperationCanceledException)
        {
            return new GitOperationResult<bool>(operation, false, CredentialFailureMessage(operation), Value: default, Diagnostic: "The credential operation was canceled.", FailureKind: GitOperationFailureKind.Unknown);
        }
        catch (Exception)
        {
            return new GitOperationResult<bool>(operation, false, CredentialFailureMessage(operation), Value: default, Diagnostic: "The tab-scoped credential module could not complete the operation.", FailureKind: GitOperationFailureKind.Unknown);
        }
    }

    public ValueTask<GitOperationResult<bool>> StoreCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return ValueTask.FromResult(new GitOperationResult<bool>("storeCredential", false, "The credential could not be stored.", Value: default, Diagnostic: "A non-empty credential is required."));
        }

        return InvokeCredentialAsync("storeCredential", "storeCredential", credential, falseIsFailure: true, cancellationToken);
    }

    public ValueTask<GitOperationResult<bool>> ForgetCredentialAsync(CancellationToken cancellationToken = default) =>
        InvokeCredentialAsync("forgetCredential", "forgetCredential", null, falseIsFailure: true, cancellationToken);

    public async ValueTask<GitOperationResult<PushResult>> PushAsync(
        PushRequest request,
        CancellationToken cancellationToken = default)
    {
        string? personalAccessToken = null;
        try
        {
            if (request is null)
            {
                return SanitizedPushFailure(GitOperationFailureKind.Unknown);
            }

            ValidatePushCoordinates(request.RepositoryUrl, request.Branch, request.DestinationRef);
            ValidateCommitId(request.OutgoingCommitId, nameof(request.OutgoingCommitId));
            var initialized = await EnsureInitializedAsync(cancellationToken);
            if (!initialized.Succeeded)
            {
                return SanitizedPushFailure(GitOperationFailureKind.Unknown);
            }
            var credentialModule = await GetCredentialModuleAsync(cancellationToken);
            personalAccessToken = await credentialModule.InvokeAsync<string?>("getCredentialForPush", cancellationToken);
            if (string.IsNullOrWhiteSpace(personalAccessToken))
            {
                return SanitizedPushFailure(GitOperationFailureKind.CredentialRejected);
            }

            var result = await InvokeWithValueAsync<PushResultDto, PushResult>(
                "push", "push", value => value.ToPushResult(), cancellationToken, request, personalAccessToken);
            result = SanitizePushFailure(RedactCredential(result, personalAccessToken));

            if (!result.Succeeded && result.FailureKind == GitOperationFailureKind.CredentialRejected)
            {
                await ForgetCredentialAsync(CancellationToken.None);
            }

            if (result.Succeeded && (result.Value is null
                || result.Value.RepositoryUrl != request.RepositoryUrl
                || result.Value.Branch != request.Branch
                || result.Value.DestinationRef != request.DestinationRef
                || result.Value.PushedCommitId != request.OutgoingCommitId))
            {
                return SanitizedPushFailure(GitOperationFailureKind.Unknown);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return SanitizedPushFailure(GitOperationFailureKind.Unknown);
        }
        catch (Exception)
        {
            return SanitizedPushFailure(GitOperationFailureKind.Unknown);
        }
        finally
        {
            personalAccessToken = null;
        }
    }

    private static GitOperationResult<SynchronizationReview> SanitizeSynchronizationResult(
        GitOperationResult<SynchronizationReview> result)
    {
        if (result.Succeeded && result.Value is not null)
        {
            return result with { Diagnostic = null, FailureKind = null };
        }

        return SanitizedSynchronizationFailure(result.FailureKind ?? GitOperationFailureKind.Unknown);
    }

    private static GitOperationResult<SynchronizationReview> SanitizedSynchronizationFailure(
        GitOperationFailureKind failureKind) =>
        new("synchronize", false, "Repository synchronization could not be completed.", Value: default,
            Diagnostic: null, FailureKind: failureKind);

    private static GitOperationResult<PushResult> SanitizePushFailure(
        GitOperationResult<PushResult> result)
    {
        if (result.Succeeded)
        {
            return result with { FailureKind = null };
        }

        return SanitizedPushFailure(result.FailureKind ?? GitOperationFailureKind.Unknown);
    }

    private static GitOperationResult<PushResult> SanitizedPushFailure(
        GitOperationFailureKind failureKind)
    {
        var diagnostic = failureKind switch
        {
            GitOperationFailureKind.CredentialRejected => "The remote rejected the supplied credentials or repository permission.",
            GitOperationFailureKind.RemoteAhead => "The remote branch contains commits that are not in the reviewed local history.",
            GitOperationFailureKind.NetworkUnavailable => "The remote could not be reached from this browser.",
            GitOperationFailureKind.UnsupportedRef => "The checked-out ref is not a supported local branch.",
            _ => "The push failed without exposing remote response details."
        };

        return new GitOperationResult<PushResult>("push", false, "The reviewed commit could not be pushed.", Value: default, Diagnostic: diagnostic, FailureKind: failureKind);
    }

    private static GitOperationResult<PushResult> RedactCredential(
        GitOperationResult<PushResult> result,
        string personalAccessToken)
    {
        static string? Redact(string? value, string secret) => value?.Replace(
            secret,
            "[REDACTED]",
            StringComparison.Ordinal);

        return result with
        {
            Message = Redact(result.Message, personalAccessToken) ?? "The reviewed commit could not be pushed.",
            Diagnostic = Redact(result.Diagnostic, personalAccessToken)
        };
    }

    private static GitOperationResult<GitPublicationReceipt> SanitizePublicationResult(
        GitOperationResult<GitPublicationReceipt> result)
    {
        if (!result.Succeeded)
        {
            return new GitOperationResult<GitPublicationReceipt>(
                "publish",
                false,
                result.Message,
                Value: default,
                Diagnostic: null,
                FailureKind: result.FailureKind ?? GitOperationFailureKind.Unknown);
        }

        if (result.Value is not null && BrowserGitPublicationContract.IsValidReceipt(result.Value))
        {
            return result with { Diagnostic = null, FailureKind = null };
        }

        return new GitOperationResult<GitPublicationReceipt>(
            "publish",
            false,
            "The reviewed publication result could not be read safely.",
            Value: default,
            Diagnostic: null,
            FailureKind: GitOperationFailureKind.GitBugDataMalformed);
    }

    public async ValueTask DisposeAsync()
    {
        if (moduleTask is { IsCompletedSuccessfully: true })
        {
            await (await moduleTask).DisposeAsync();
        }
        if (credentialModuleTask is { IsCompletedSuccessfully: true })
        {
            await (await credentialModuleTask).DisposeAsync();
        }
    }

    private async ValueTask<GitOperationResult<bool>> InvokeCredentialAsync(
        string operation,
        string identifier,
        string? credential,
        bool falseIsFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            var module = await GetCredentialModuleAsync(cancellationToken);
            var value = credential is null
                ? await module.InvokeAsync<bool>(identifier, cancellationToken)
                : await module.InvokeAsync<bool>(identifier, cancellationToken, credential);
            return !value && falseIsFailure
                ? new GitOperationResult<bool>(operation, false, CredentialFailureMessage(operation), Value: default, Diagnostic: "Tab-scoped credential storage was unavailable or rejected the operation.", FailureKind: GitOperationFailureKind.Unknown)
                : new GitOperationResult<bool>(operation, true, CredentialSuccessMessage(operation), value);
        }
        catch (OperationCanceledException)
        {
            return new GitOperationResult<bool>(operation, false, CredentialFailureMessage(operation), Value: default, Diagnostic: "The credential operation was canceled.", FailureKind: GitOperationFailureKind.Unknown);
        }
        catch (Exception)
        {
            return new GitOperationResult<bool>(operation, false, CredentialFailureMessage(operation), Value: default, Diagnostic: "The tab-scoped credential module could not complete the operation.", FailureKind: GitOperationFailureKind.Unknown);
        }
    }

    private static string CredentialSuccessMessage(string operation) => operation switch
    {
        "hasCredential" => "Credential presence was checked.",
        "storeCredential" => "The credential was stored for this browser tab.",
        _ => "The credential was forgotten."
    };

    private static string CredentialFailureMessage(string operation) => operation switch
    {
        "hasCredential" => "Credential presence could not be checked.",
        "storeCredential" => "The credential could not be stored.",
        _ => "The credential could not be forgotten."
    };

    private async Task<GitOperationResult<TValue>> InvokeGitBugWithValueAsync<TInteropValue, TValue>(
        string operation,
        string identifier,
        Func<TInteropValue, TValue> mapValue,
        CancellationToken cancellationToken,
        params object?[] args)
    {
        var result = await InvokeResponseAsync<TInteropValue>(operation, identifier, cancellationToken, args);
        if (!result.Succeeded)
        {
            return SanitizedGitBugFailure<TValue>(operation, ParseGitBugFailureKind(result.FailureKind));
        }

        if (result.Value is null)
        {
            return SanitizedGitBugFailure<TValue>(operation, GitOperationFailureKind.GitBugDataMalformed);
        }

        try
        {
            return new GitOperationResult<TValue>(
                operation,
                true,
                operation == "gitBug.listIssues" ? "Git-Bug issues were loaded." : "The Git-Bug issue was loaded.",
                mapValue(result.Value),
                Diagnostic: null,
                FailureKind: null);
        }
        catch (Exception)
        {
            return SanitizedGitBugFailure<TValue>(operation, GitOperationFailureKind.GitBugDataMalformed);
        }
    }

    private static GitOperationFailureKind ParseGitBugFailureKind(string? failureKind) => failureKind switch
    {
        "gitBugDataUnavailable" => GitOperationFailureKind.GitBugDataUnavailable,
        "gitBugFormatUnsupported" => GitOperationFailureKind.GitBugFormatUnsupported,
        "gitBugDataMalformed" => GitOperationFailureKind.GitBugDataMalformed,
        "gitBugWriteInvalidRequest" => GitOperationFailureKind.GitBugWriteInvalidRequest,
        "gitBugWritePreconditionFailed" => GitOperationFailureKind.GitBugWritePreconditionFailed,
        "gitBugWriteConflict" => GitOperationFailureKind.GitBugWriteConflict,
        "gitBugWriteUnsupportedFormat" => GitOperationFailureKind.GitBugWriteUnsupportedFormat,
        "gitBugWriteUnavailable" => GitOperationFailureKind.GitBugWriteUnavailable,
        "gitBugWriteSerializationFailed" => GitOperationFailureKind.GitBugWriteSerializationFailed,
        "gitBugWriteUnknown" => GitOperationFailureKind.GitBugWriteUnknown,
        _ => GitOperationFailureKind.Unknown
    };

    private static GitOperationResult<TValue> SanitizedGitBugWriteFailure<TValue>(
        string operation,
        GitOperationFailureKind failureKind)
    {
        var message = failureKind switch
        {
            GitOperationFailureKind.GitBugWriteInvalidRequest => "The Git-Bug write request is not valid.",
            GitOperationFailureKind.GitBugWriteUnavailable => "Git-Bug writing is not available yet.",
            _ => "The Git-Bug write could not be completed."
        };

        return new(operation, false, message, Value: default, Diagnostic: null, FailureKind: failureKind);
    }

    private static GitOperationResult<TValue> SanitizedGitBugFailure<TValue>(
        string operation,
        GitOperationFailureKind failureKind)
    {
        var message = failureKind switch
        {
            GitOperationFailureKind.GitBugDataUnavailable => "This repository has no supported Git-Bug issue data.",
            GitOperationFailureKind.GitBugFormatUnsupported => "This Git-Bug data version is not supported yet.",
            GitOperationFailureKind.GitBugDataMalformed => "Git-Bug issue data could not be read safely.",
            _ => "Git-Bug issues could not be loaded."
        };

        return new GitOperationResult<TValue>(
            operation,
            false,
            message,
            Value: default,
            Diagnostic: null,
            FailureKind: failureKind);
    }

    private static GitOperationResult<RemoteSnapshot> SanitizeRemoteSnapshotResult(
        GitOperationResult<RemoteSnapshot> result)
    {
        if (!result.Succeeded)
        {
            var failureKind = result.FailureKind is { } kind && BrowserGitRemoteSnapshotContract.IsValidFailure(kind)
                ? kind
                : result.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                    ? GitOperationFailureKind.RemoteSnapshotCancelled
                    : GitOperationFailureKind.RemoteSnapshotUnknown;
            var message = failureKind switch
            {
                GitOperationFailureKind.RemoteSnapshotInvalidRequest => "The remote snapshot request is not valid.",
                GitOperationFailureKind.RemoteSnapshotUnsupportedReference => "The requested remote ref is not available.",
                GitOperationFailureKind.RemoteSnapshotLimitExceeded => "The remote snapshot is too large to review safely.",
                GitOperationFailureKind.RemoteSnapshotDataMalformed => "The remote snapshot could not be read safely.",
                GitOperationFailureKind.RemoteSnapshotDataUnavailable => "The remote snapshot is unavailable.",
                GitOperationFailureKind.RemoteSnapshotCancelled => "The remote snapshot was canceled.",
                _ => "The remote snapshot could not be acquired."
            };
            return new GitOperationResult<RemoteSnapshot>(
                BrowserGitRemoteSnapshotOperations.Read,
                false,
                message,
                Value: default,
                Diagnostic: null,
                FailureKind: failureKind);
        }

        if (result.Value is not null && BrowserGitRemoteSnapshotContract.IsValidSnapshot(result.Value))
        {
            return result with { Diagnostic = null, FailureKind = null };
        }

        return new GitOperationResult<RemoteSnapshot>(
            BrowserGitRemoteSnapshotOperations.Read,
            false,
            "The remote snapshot could not be read safely.",
            Value: default,
            Diagnostic: null,
            FailureKind: GitOperationFailureKind.RemoteSnapshotDataMalformed);
    }

    private async Task<GitOperationResult<TValue>> InvokeWithValueAsync<TInteropValue, TValue>(
        string operation,
        string identifier,
        Func<TInteropValue, TValue> mapValue,
        CancellationToken cancellationToken,
        params object?[] args)
    {
        var result = await InvokeResponseAsync<TInteropValue>(operation, identifier, cancellationToken, args);

        if (!result.Succeeded)
        {
            return new GitOperationResult<TValue>(result.Operation, false, result.Message, Value: default, Diagnostic: result.Diagnostic, FailureKind: ParseFailureKind(result.FailureKind));
        }

        if (result.Value is null)
        {
            return new GitOperationResult<TValue>(result.Operation, false, result.Message, Value: default, Diagnostic: $"Browser Git operation '{result.Operation}' succeeded without a value.");
        }

        try
        {
            return new GitOperationResult<TValue>(result.Operation, true, result.Message, mapValue(result.Value));
        }
        catch (Exception)
        {
            return new GitOperationResult<TValue>(operation, false, $"Browser Git operation '{operation}' returned an unexpected response.", Value: default, Diagnostic: "The response value did not match the expected contract.");
        }
    }

    private async Task<GitOperationResult> InvokeAsync(
        string operation,
        string identifier,
        CancellationToken cancellationToken,
        params object?[] args)
    {
        var result = await InvokeResponseAsync<object>(operation, identifier, cancellationToken, args);

        return result.Succeeded
            ? new GitOperationResult(result.Operation, true, result.Message)
            : new GitOperationResult(result.Operation, false, result.Message, Diagnostic: result.Diagnostic, FailureKind: ParseFailureKind(result.FailureKind));
    }

    private static GitOperationFailureKind? ParseFailureKind(string? failureKind) => failureKind switch
    {
        "remoteSnapshotDataUnavailable" => GitOperationFailureKind.RemoteSnapshotDataUnavailable,
        "remoteSnapshotDataMalformed" => GitOperationFailureKind.RemoteSnapshotDataMalformed,
        "remoteSnapshotInvalidRequest" => GitOperationFailureKind.RemoteSnapshotInvalidRequest,
        "remoteSnapshotUnsupportedReference" => GitOperationFailureKind.RemoteSnapshotUnsupportedReference,
        "remoteSnapshotLimitExceeded" => GitOperationFailureKind.RemoteSnapshotLimitExceeded,
        "remoteSnapshotCancelled" => GitOperationFailureKind.RemoteSnapshotCancelled,
        "remoteSnapshotUnknown" => GitOperationFailureKind.RemoteSnapshotUnknown,
        "credentialRejected" => GitOperationFailureKind.CredentialRejected,
        "remoteAhead" => GitOperationFailureKind.RemoteAhead,
        "networkUnavailable" => GitOperationFailureKind.NetworkUnavailable,
        "unsupportedRef" => GitOperationFailureKind.UnsupportedRef,
        "gitBugDataUnavailable" => GitOperationFailureKind.GitBugDataUnavailable,
        "gitBugFormatUnsupported" => GitOperationFailureKind.GitBugFormatUnsupported,
        "gitBugDataMalformed" => GitOperationFailureKind.GitBugDataMalformed,
        "gitBugWriteInvalidRequest" => GitOperationFailureKind.GitBugWriteInvalidRequest,
        "gitBugWritePreconditionFailed" => GitOperationFailureKind.GitBugWritePreconditionFailed,
        "gitBugWriteConflict" => GitOperationFailureKind.GitBugWriteConflict,
        "gitBugWriteUnsupportedFormat" => GitOperationFailureKind.GitBugWriteUnsupportedFormat,
        "gitBugWriteUnavailable" => GitOperationFailureKind.GitBugWriteUnavailable,
        "gitBugWriteSerializationFailed" => GitOperationFailureKind.GitBugWriteSerializationFailed,
        "gitBugWriteUnknown" => GitOperationFailureKind.GitBugWriteUnknown,
        "branchSwitchInvalidRequest" => GitOperationFailureKind.BranchSwitchInvalidRequest,
        "branchSwitchDirtyWorktree" => GitOperationFailureKind.BranchSwitchDirtyWorktree,
        "branchSwitchUnsupportedReference" => GitOperationFailureKind.BranchSwitchUnsupportedReference,
        "branchSwitchUnavailable" => GitOperationFailureKind.BranchSwitchUnavailable,
        "branchSwitchUnknown" => GitOperationFailureKind.BranchSwitchUnknown,
        "reconciliationInvalidRequest" => GitOperationFailureKind.ReconciliationInvalidRequest,
        "reconciliationStaleRemote" => GitOperationFailureKind.ReconciliationStaleRemote,
        "reconciliationUnsupportedResolution" => GitOperationFailureKind.ReconciliationUnsupportedResolution,
        "reconciliationWriteFailed" => GitOperationFailureKind.ReconciliationWriteFailed,
        "reconciliationCancelled" => GitOperationFailureKind.ReconciliationCancelled,
        "reconciliationDataMalformed" => GitOperationFailureKind.ReconciliationDataMalformed,
        "reconciliationDataUnavailable" => GitOperationFailureKind.ReconciliationDataUnavailable,
        "reconciliationUnknown" => GitOperationFailureKind.ReconciliationUnknown,
        "unknown" => GitOperationFailureKind.Unknown,
        _ => null
    };

    private async Task<BrowserGitResponse<TValue>> InvokeResponseAsync<TValue>(
        string operation,
        string identifier,
        CancellationToken cancellationToken,
        params object?[] args)
    {
        try
        {
            var initializationResult = await EnsureInitializedAsync(cancellationToken);
            if (!initializationResult.Succeeded)
            {
                return BrowserGitResponse<TValue>.Failure(operation, initializationResult.Message, initializationResult.Diagnostic);
            }

            var module = await GetModuleAsync(cancellationToken);
            var response = await module.InvokeAsync<BrowserGitResponse<TValue>>(
                identifier,
                cancellationToken,
                args);

            if (response is null)
            {
                return BrowserGitResponse<TValue>.Failure(
                    operation,
                    $"Browser Git operation '{operation}' did not return a response.",
                    "The JavaScript operation returned null.");
            }

            if (!string.Equals(response.Operation, operation, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(response.Message))
            {
                return BrowserGitResponse<TValue>.Failure(
                    operation,
                    $"Browser Git operation '{operation}' returned an unexpected response.",
                    "The JavaScript response envelope did not match the expected contract.");
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            return BrowserGitResponse<TValue>.Failure(
                operation,
                $"Browser Git operation '{operation}' was canceled.",
                "The operation was canceled before it returned a result.");
        }
        catch (JSException)
        {
            return BrowserGitResponse<TValue>.Failure(
                operation,
                $"Browser Git operation '{operation}' failed in JavaScript interop.",
                "JavaScript interop failed without exposing exception details.");
        }
        catch (InvalidOperationException)
        {
            return BrowserGitResponse<TValue>.Failure(
                operation,
                $"Browser Git operation '{operation}' could not be invoked.",
                "The JavaScript module was unavailable or could not be invoked.");
        }
        catch (Exception)
        {
            return BrowserGitResponse<TValue>.Failure(
                operation,
                $"Browser Git operation '{operation}' could not be completed.",
                "An unexpected interop boundary failure occurred.");
        }
    }

    private async Task<GitOperationResult> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        initializationTask ??= InitializeAsync(cancellationToken);
        return await initializationTask;
    }

    private async Task<GitOperationResult> InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var module = await GetModuleAsync(cancellationToken);
            var response = await module.InvokeAsync<BrowserGitResponse<object>>(
                "initialize",
                cancellationToken);

            if (response is null)
            {
                return new GitOperationResult("initialize", false, "Browser Git storage could not be initialized.", Diagnostic: "The JavaScript initialize operation did not return a response.");
            }

            return response.Succeeded
                ? new GitOperationResult(response.Operation, true, response.Message)
                : new GitOperationResult(response.Operation, false, response.Message, Diagnostic: response.Diagnostic);
        }
        catch (OperationCanceledException)
        {
            return new GitOperationResult("initialize", false, "Browser Git storage initialization was canceled.", Diagnostic: "The operation was canceled before initialization completed.");
        }
        catch (JSException)
        {
            return new GitOperationResult("initialize", false, "Browser Git storage could not be initialized.", Diagnostic: "JavaScript initialization failed without exposing exception details.");
        }
        catch (InvalidOperationException)
        {
            return new GitOperationResult("initialize", false, "Browser Git storage could not be initialized.", Diagnostic: "The JavaScript module was unavailable or could not be imported.");
        }
        catch (Exception)
        {
            return new GitOperationResult("initialize", false, "Browser Git storage could not be initialized.", Diagnostic: "An unexpected initialization boundary failure occurred.");
        }
    }

    private Task<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        moduleTask ??= jsRuntime.InvokeAsync<IJSObjectReference>(
            "import",
            cancellationToken,
            ModulePath).AsTask();

        return moduleTask;
    }

    private Task<IJSObjectReference> GetCredentialModuleAsync(CancellationToken cancellationToken)
    {
        credentialModuleTask ??= jsRuntime.InvokeAsync<IJSObjectReference>(
            "import",
            cancellationToken,
            CredentialModulePath).AsTask();

        return credentialModuleTask;
    }

    private static bool TryNormalizeRepositoryPath(string? candidate, bool allowRoot, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)
            || !string.Equals(candidate, candidate.Trim(), StringComparison.Ordinal)
            || !candidate.StartsWith('/')
            || candidate.Contains('\\')
            || candidate.Contains('%')
            || candidate.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            return false;
        }

        var value = candidate.StartsWith('/') ? candidate : $"/{candidate}";
        if (value == "/")
        {
            normalized = value;
            return allowRoot;
        }

        if (value.EndsWith('/'))
        {
            return false;
        }

        var segments = value[1..].Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            return false;
        }

        normalized = "/" + string.Join('/', segments);
        return true;
    }

    private static bool IsSupportedTextPath(string path)
    {
        var name = Path.GetFileName(path);
        if (name is "README" or "LICENSE" or "Dockerfile" or "Makefile" or ".gitignore" or ".editorconfig")
        {
            return true;
        }

        return Path.GetExtension(name).ToLowerInvariant() is
            ".txt" or ".md" or ".markdown" or ".json" or ".js" or ".mjs" or ".cjs"
            or ".ts" or ".tsx" or ".jsx" or ".css" or ".scss" or ".html" or ".xml"
            or ".yml" or ".yaml" or ".toml" or ".ini" or ".cfg" or ".cs" or ".razor"
            or ".csproj" or ".sln" or ".sh" or ".py" or ".java" or ".go" or ".rs";
    }

    private static bool TryGetUtf8ByteCount(string content, out int byteCount)
    {
        try
        {
            byteCount = StrictUtf8.GetByteCount(content);
            return true;
        }
        catch (EncoderFallbackException)
        {
            byteCount = 0;
            return false;
        }
    }

    private sealed record BranchSnapshotDto
    {
        [JsonPropertyName("current")]
        public CurrentBranchDto? Current { get; init; }

        [JsonPropertyName("choices")]
        public List<BranchChoiceDto> Choices { get; init; } = [];

        [JsonPropertyName("boundedResultCount")]
        public int BoundedResultCount { get; init; }

        public BranchSnapshot ToContract() => new(
            Current?.ToContract(),
            Choices.Select(choice => choice.ToContract()).ToArray(),
            BoundedResultCount);
    }

    private sealed record CurrentBranchDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("isDetached")]
        public bool IsDetached { get; init; }

        public CurrentBranch ToContract() => new(Name, IsDetached);
    }

    private sealed record BranchChoiceDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("kind")]
        public BranchChoiceKind Kind { get; init; }

        [JsonPropertyName("isCurrent")]
        public bool IsCurrent { get; init; }

        public BranchChoice ToContract() => new(Name, Kind, IsCurrent);
    }

    private sealed record CommitHistoryDto
    {
        [JsonPropertyName("branchName")]
        public string? BranchName { get; init; }

        [JsonPropertyName("entries")]
        public List<CommitHistoryEntryDto> Entries { get; init; } = [];

        [JsonPropertyName("boundedResultCount")]
        public int BoundedResultCount { get; init; }

        public CommitHistory ToContract() => new(
            BranchName,
            Entries.Select(entry => entry.ToContract()).ToArray(),
            BoundedResultCount);
    }

    private sealed record CommitHistoryEntryDto
    {
        [JsonPropertyName("commitId")]
        public string CommitId { get; init; } = string.Empty;

        [JsonPropertyName("parentCommitId")]
        public string? ParentCommitId { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("authorName")]
        public string AuthorName { get; init; } = string.Empty;

        [JsonPropertyName("authoredAt")]
        public DateTimeOffset AuthoredAt { get; init; }

        public CommitHistoryEntry ToContract() => new(
            CommitId,
            ParentCommitId,
            Message,
            AuthorName,
            AuthoredAt);
    }

    private sealed record ReviewedReconciliationApplyReceiptDto
    {
        [JsonPropertyName("reviewId")]
        public string ReviewId { get; init; } = string.Empty;

        [JsonPropertyName("sourceRevision")]
        public string SourceRevision { get; init; } = string.Empty;

        [JsonPropertyName("appliedPaths")]
        public IReadOnlyList<string> AppliedPaths { get; init; } = [];

        [JsonPropertyName("boundedAppliedPathCount")]
        public int BoundedAppliedPathCount { get; init; }

        public ReviewedReconciliationApplyReceipt ToContract() =>
            new(ReviewId, SourceRevision, AppliedPaths, BoundedAppliedPathCount);
    }

    private sealed record BrowserGitResponse<TValue>
    {
        [JsonPropertyName("operation")]
        public string Operation { get; init; } = string.Empty;

        [JsonPropertyName("succeeded")]
        public bool Succeeded { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("value")]
        public TValue? Value { get; init; }

        [JsonPropertyName("diagnostic")]
        public string? Diagnostic { get; init; }

        [JsonPropertyName("failureKind")]
        public string? FailureKind { get; init; }

        public static BrowserGitResponse<TValue> Failure(string operation, string message, string? diagnostic) =>
            new()
            {
                Operation = operation,
                Succeeded = false,
                Message = message,
                Diagnostic = diagnostic
            };
    }

    private sealed record GitBugIssueEditRequestDto
    {
        public GitBugIssueEditRequestDto(GitBugIssueEditRequest request)
        {
            IssueId = request.IssueId.Value;
            Title = request.Title;
            Description = request.Description;
            Preconditions = request.Preconditions;
            Serialization = request.Serialization;
        }

        [JsonPropertyName("issueId")] public string IssueId { get; init; }
        [JsonPropertyName("title")] public string Title { get; init; }
        [JsonPropertyName("description")] public string Description { get; init; }
        [JsonPropertyName("preconditions")] public GitBugWritePreconditions Preconditions { get; init; }
        [JsonPropertyName("serialization")] public GitBugSerializationMetadata Serialization { get; init; }
    }

    private sealed record GitBugCommentCreateRequestDto
    {
        public GitBugCommentCreateRequestDto(GitBugCommentCreateRequest request)
        {
            IssueId = request.IssueId.Value;
            Body = request.Body;
            Preconditions = request.Preconditions;
            Serialization = request.Serialization;
        }

        [JsonPropertyName("issueId")] public string IssueId { get; init; }
        [JsonPropertyName("body")] public string Body { get; init; }
        [JsonPropertyName("preconditions")] public GitBugWritePreconditions Preconditions { get; init; }
        [JsonPropertyName("serialization")] public GitBugSerializationMetadata Serialization { get; init; }
    }

    private sealed record GitBugWriteResultDto
    {
        [JsonPropertyName("issueId")] public string? IssueId { get; init; }
        [JsonPropertyName("revision")] public string? Revision { get; init; }
        [JsonPropertyName("reviewId")] public string? ReviewId { get; init; }
        [JsonPropertyName("commentId")] public string? CommentId { get; init; }

        public GitBugIssueEditResult ToIssueEditResult() =>
            new(new GitBugIssueId(RequireValue(IssueId)), RequireValue(Revision), RequireValue(ReviewId));

        public GitBugCommentCreateResult ToCommentCreateResult() =>
            new(new GitBugIssueId(RequireValue(IssueId)), RequireValue(CommentId), RequireValue(Revision), RequireValue(ReviewId));

        private static string RequireValue(string? value) =>
            !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException("Git-Bug write response was incomplete.");
    }

    private sealed record GitBugIssueQueryDto
    {
        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("searchText")]
        public string? SearchText { get; init; }

        [JsonPropertyName("cursor")]
        public string? Cursor { get; init; }

        [JsonPropertyName("pageSize")]
        public int PageSize { get; init; }
    }

    private sealed record GitBugIssuePageDto
    {
        [JsonPropertyName("issues")]
        public IReadOnlyList<GitBugIssueSummaryDto>? Issues { get; init; }

        [JsonPropertyName("nextCursor")]
        public string? NextCursor { get; init; }

        [JsonPropertyName("totalCount")]
        public int TotalCount { get; init; }

        public GitBugIssuePage ToGitBugIssuePage()
        {
            if (Issues is null
                || Issues.Count > MaxGitBugPageSize
                || TotalCount < Issues.Count
                || TotalCount < 0
                || !IsBoundedGitBugText(NextCursor, MaxGitBugQueryLength, allowNull: true))
            {
                throw new InvalidOperationException("The Git-Bug issue page did not match the expected contract.");
            }

            return new(
                Issues.Select(issue => issue.ToGitBugIssueSummary()).ToArray(),
                NextCursor,
                TotalCount);
        }
    }

    private sealed record GitBugIssueSummaryDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; init; } = string.Empty;

        [JsonPropertyName("labels")]
        public IReadOnlyList<string>? Labels { get; init; }

        [JsonPropertyName("authorDisplayName")]
        public string? AuthorDisplayName { get; init; }

        public GitBugIssueSummary ToGitBugIssueSummary()
        {
            if (!IsValidGitBugIssueId(Id)
                || !IsBoundedGitBugText(Title, MaxGitBugTitleLength)
                || !Enum.TryParse<GitBugIssueState>(State, ignoreCase: true, out var state)
                || !Enum.IsDefined(state)
                || !IsBoundedGitBugText(AuthorDisplayName, MaxGitBugAuthorLength, allowNull: true)
                || (Labels is not null && (Labels.Count > 100
                    || Labels.Any(label => !IsBoundedGitBugText(label, MaxGitBugLabelLength)))))
            {
                throw new InvalidOperationException("The Git-Bug issue summary did not match the expected contract.");
            }

            return new(new(Id), Title, state, Labels?.ToArray(), AuthorDisplayName);
        }
    }

    private sealed record GitBugIssueDetailDto
    {
        [JsonPropertyName("summary")]
        public GitBugIssueSummaryDto? Summary { get; init; }

        [JsonPropertyName("revision")]
        public string? Revision { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("comments")]
        public IReadOnlyList<GitBugCommentDto>? Comments { get; init; }

        public GitBugIssueDetail ToGitBugIssueDetail()
        {
            if (Summary is null
                || string.IsNullOrWhiteSpace(Revision)
                || Revision.Length != 40
                || !Revision.All(Uri.IsHexDigit)
                || Comments is null
                || Comments.Count > MaxGitBugComments
                || !IsBoundedGitBugText(Description, MaxGitBugBodyLength, allowNull: true, allowEmpty: true))
            {
                throw new InvalidOperationException("The Git-Bug issue detail did not match the expected contract.");
            }

            var comments = Comments.Select(comment => comment.ToGitBugComment()).ToArray();
            if (!TryGetUtf8ByteCount(Description ?? string.Empty, out var detailBytes)
                || comments.Any(comment => !TryGetUtf8ByteCount(comment.Body, out _))
                || comments.Aggregate(
                    (long)detailBytes,
                    (total, comment) => total + StrictUtf8.GetByteCount(comment.Body)) > MaxGitBugDetailBytes)
            {
                throw new InvalidOperationException("The Git-Bug issue detail exceeded the safe payload limit.");
            }

            // Git-Bug v4 permits an empty creation message. The typed Accessor
            // contract exposes that optional text as safe, non-null state.
            return new(Summary.ToGitBugIssueSummary(), Revision, Description ?? string.Empty, comments);
        }
    }

    private sealed record GitBugCommentDto
    {
        [JsonPropertyName("authorDisplayName")]
        public string AuthorDisplayName { get; init; } = string.Empty;

        [JsonPropertyName("body")]
        public string Body { get; init; } = string.Empty;

        public GitBugComment ToGitBugComment()
        {
            if (!IsBoundedGitBugText(AuthorDisplayName, MaxGitBugAuthorLength)
                || !IsBoundedGitBugText(Body, MaxGitBugBodyLength))
            {
                throw new InvalidOperationException("The Git-Bug comment did not match the expected contract.");
            }

            return new(AuthorDisplayName, Body);
        }
    }

    private static bool IsValidGitBugQuery(GitBugIssueQuery? query) =>
        query is not null
        && (!query.State.HasValue || Enum.IsDefined(query.State.Value))
        && query.PageSize is >= 1 and <= MaxGitBugPageSize
        && IsBoundedGitBugText(query.SearchText, MaxGitBugQueryLength, allowNull: true)
        && IsBoundedGitBugText(query.Cursor, MaxGitBugQueryLength, allowNull: true);

    private static bool IsValidGitBugIssueId(string? issueId) =>
        issueId is { Length: 64 } && issueId.All(Uri.IsHexDigit);

    private static bool IsBoundedGitBugText(
        string? value,
        int maximumLength,
        bool allowNull = false,
        bool allowEmpty = false) =>
        value is null
            ? allowNull
            : (allowEmpty || value.Length > 0)
                && value.Length <= maximumLength
                && value.IndexOfAny(['\0', '\r']) < 0;

    private sealed record RepositoryInfoDto
    {
        [JsonPropertyName("repositoryUrl")]
        public string RepositoryUrl { get; init; } = string.Empty;

        [JsonPropertyName("workspaceName")]
        public string WorkspaceName { get; init; } = string.Empty;

        [JsonPropertyName("rootPath")]
        public string RootPath { get; init; } = string.Empty;

        [JsonPropertyName("wasCloned")]
        public bool WasCloned { get; init; }

        public RepositoryInfo ToRepositoryInfo() =>
            new(RepositoryUrl, WorkspaceName, RootPath, WasCloned);
    }

    private sealed record RepositoryFileEntryDto
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; init; } = string.Empty;

        [JsonPropertyName("sizeBytes")]
        public long? SizeBytes { get; init; }

        [JsonPropertyName("isEditableText")]
        public bool IsEditableText { get; init; }

        public RepositoryFileEntry ToRepositoryFileEntry() =>
            new(
                Path,
                Name,
                Enum.TryParse<RepositoryFileKind>(Kind, ignoreCase: true, out var kind) ? kind : RepositoryFileKind.File,
                SizeBytes,
                IsEditableText);
    }

    private sealed record TextFileContentDto
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;

        [JsonPropertyName("encoding")]
        public string? Encoding { get; init; }

        [JsonPropertyName("sizeBytes")]
        public long? SizeBytes { get; init; }

        public TextFileContent ToTextFileContent() =>
            new(Path, Content, Encoding, SizeBytes);
    }

    private sealed record ChangedFileDto
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("changeKind")]
        public string ChangeKind { get; init; } = string.Empty;

        public ChangedFile ToChangedFile() =>
            new(
                Path,
                Enum.TryParse<GitChangeKind>(ChangeKind, ignoreCase: true, out var changeKind) ? changeKind : GitChangeKind.Unknown);
    }

    private sealed record CommitInfoDto
    {
        [JsonPropertyName("commitId")]
        public string CommitId { get; init; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("changedFileCount")]
        public int? ChangedFileCount { get; init; }

        public CommitInfo ToCommitInfo() =>
            new(
                CommitId.Length <= BrowserGitCommitContract.MaximumCommitIdLength ? CommitId : string.Empty,
                Message,
                ChangedFileCount is >= 0 and <= BrowserGitCommitContract.MaximumChangedFiles
                    ? ChangedFileCount.Value
                    : 0);
    }

    private sealed record GitPublicationReviewDto
    {
        [JsonPropertyName("repositoryUrl")] public string RepositoryUrl { get; init; } = string.Empty;
        [JsonPropertyName("branch")] public string Branch { get; init; } = string.Empty;
        [JsonPropertyName("updates")] public IReadOnlyList<GitRefUpdateDto> Updates { get; init; } = [];
        [JsonPropertyName("remotePreconditions")] public IReadOnlyList<GitRemotePreconditionDto> RemotePreconditions { get; init; } = [];
        [JsonPropertyName("boundedUpdateCount")] public int BoundedUpdateCount { get; init; }

        public GitPublicationReview ToReview() => new(
            RepositoryUrl,
            Branch,
            Updates.Select(update => update.ToContract()).ToArray(),
            RemotePreconditions.Select(precondition => precondition.ToContract()).ToArray(),
            BoundedUpdateCount);
    }

    private sealed record GitPublicationReceiptDto
    {
        [JsonPropertyName("repositoryUrl")] public string RepositoryUrl { get; init; } = string.Empty;
        [JsonPropertyName("branch")] public string Branch { get; init; } = string.Empty;
        [JsonPropertyName("appliedUpdates")] public IReadOnlyList<GitRefUpdateDto> AppliedUpdates { get; init; } = [];
        [JsonPropertyName("boundedUpdateCount")] public int BoundedUpdateCount { get; init; }

        public GitPublicationReceipt ToReceipt() => new(
            RepositoryUrl,
            Branch,
            AppliedUpdates.Select(update => update.ToContract()).ToArray(),
            BoundedUpdateCount);
    }

    private sealed record GitRefUpdateDto
    {
        [JsonPropertyName("refName")] public string RefName { get; init; } = string.Empty;
        [JsonPropertyName("namespace")] public string Namespace { get; init; } = string.Empty;
        [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;
        [JsonPropertyName("newObjectId")] public string NewObjectId { get; init; } = string.Empty;
        [JsonPropertyName("expectedOldObjectId")] public string? ExpectedOldObjectId { get; init; }

        public GitRefUpdate ToContract()
        {
            if (!Enum.TryParse<GitRefNamespace>(Namespace, true, out var @namespace)
                || !Enum.TryParse<GitRefUpdateKind>(Kind, true, out var kind))
            {
                throw new InvalidOperationException("The publication ref update contract is malformed.");
            }

            return new(RefName, @namespace, kind, NewObjectId, ExpectedOldObjectId);
        }
    }

    private sealed record GitRemotePreconditionDto
    {
        [JsonPropertyName("refName")] public string RefName { get; init; } = string.Empty;
        [JsonPropertyName("expectedObjectId")] public string? ExpectedObjectId { get; init; }

        public GitRemotePrecondition ToContract() => new(RefName, ExpectedObjectId);
    }

    private sealed record PushReviewDto
    {
        [JsonPropertyName("repositoryUrl")]
        public string RepositoryUrl { get; init; } = string.Empty;

        [JsonPropertyName("branch")]
        public string Branch { get; init; } = string.Empty;

        [JsonPropertyName("outgoingCommitId")]
        public string OutgoingCommitId { get; init; } = string.Empty;

        [JsonPropertyName("destinationRef")]
        public string DestinationRef { get; init; } = string.Empty;

        public PushReview ToPushReview()
        {
            ValidatePushCoordinates(RepositoryUrl, Branch, DestinationRef);
            ValidateCommitId(OutgoingCommitId, nameof(OutgoingCommitId));
            return new(RepositoryUrl, Branch, OutgoingCommitId, DestinationRef);
        }
    }

    private sealed record PushResultDto
    {
        [JsonPropertyName("repositoryUrl")]
        public string RepositoryUrl { get; init; } = string.Empty;

        [JsonPropertyName("branch")]
        public string Branch { get; init; } = string.Empty;

        [JsonPropertyName("destinationRef")]
        public string DestinationRef { get; init; } = string.Empty;

        [JsonPropertyName("pushedCommitId")]
        public string PushedCommitId { get; init; } = string.Empty;

        public PushResult ToPushResult()
        {
            ValidatePushCoordinates(RepositoryUrl, Branch, DestinationRef);
            ValidateCommitId(PushedCommitId, nameof(PushedCommitId));
            return new(RepositoryUrl, Branch, DestinationRef, PushedCommitId);
        }
    }

    private sealed record SynchronizationReviewDto
    {
        [JsonPropertyName("repositoryUrl")] public string RepositoryUrl { get; init; } = string.Empty;
        [JsonPropertyName("branch")] public string Branch { get; init; } = string.Empty;
        [JsonPropertyName("localCommitId")] public string LocalCommitId { get; init; } = string.Empty;
        [JsonPropertyName("incomingCommit")] public IncomingCommitMetadata? IncomingCommit { get; init; }
        [JsonPropertyName("changedFiles")] public IReadOnlyList<ChangedFileSummaryDto> ChangedFiles { get; init; } = [];
        [JsonPropertyName("decision")] public string Decision { get; init; } = string.Empty;

        public SynchronizationReview ToSynchronizationReview()
        {
            ValidateSynchronizationCoordinates(RepositoryUrl, Branch);
            ValidateCommitId(LocalCommitId, nameof(LocalCommitId));
            if (!Enum.TryParse<SynchronizationDecisionState>(Decision, true, out var decision)
                || !Enum.IsDefined(decision)
                || decision is SynchronizationDecisionState.Cancelled or SynchronizationDecisionState.Failed)
            {
                throw new InvalidOperationException("The synchronization response did not match the expected review contract.");
            }

            var incoming = IncomingCommit is null ? null : ValidateIncomingCommit(IncomingCommit);
            var changedFiles = ChangedFiles.Select(file => file.ToChangedFileSummary()).ToArray();
            if ((decision is SynchronizationDecisionState.ReadyToApply or SynchronizationDecisionState.Applied) && incoming is null)
            {
                throw new InvalidOperationException("A synchronization ready to apply must include incoming commit metadata.");
            }

            return new(RepositoryUrl, Branch, LocalCommitId, incoming, changedFiles, decision);
        }

        private static IncomingCommitMetadata ValidateIncomingCommit(IncomingCommitMetadata incoming)
        {
            ValidateCommitId(incoming.CommitId, nameof(incoming.CommitId));
            if (incoming.ParentCommitId.Length > 0)
            {
                ValidateCommitId(incoming.ParentCommitId, nameof(incoming.ParentCommitId));
            }

            if (string.IsNullOrWhiteSpace(incoming.AuthorName))
            {
                throw new InvalidOperationException("Incoming commit metadata must identify its author.");
            }

            return incoming;
        }
    }

    private sealed record RemoteSnapshotRefDto
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("objectId")] public string ObjectId { get; init; } = string.Empty;

        public RemoteSnapshotRef ToContract() => new(Name, ObjectId);
    }

    private sealed record RemoteSnapshotCommitDto
    {
        [JsonPropertyName("commitId")] public string CommitId { get; init; } = string.Empty;
        [JsonPropertyName("treeId")] public string TreeId { get; init; } = string.Empty;
        [JsonPropertyName("parentCommitId")] public string? ParentCommitId { get; init; }
        [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
        [JsonPropertyName("authorName")] public string AuthorName { get; init; } = string.Empty;
        [JsonPropertyName("authoredAt")] public DateTimeOffset AuthoredAt { get; init; }

        public RemoteSnapshotCommit ToContract() =>
            new(CommitId, TreeId, ParentCommitId, Message, AuthorName, AuthoredAt);
    }

    private sealed record RemoteSnapshotBlobDto
    {
        [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
        [JsonPropertyName("objectId")] public string ObjectId { get; init; } = string.Empty;
        [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }

        public RemoteSnapshotBlob ToContract() => new(Path, ObjectId, SizeBytes);
    }

    private sealed record RemoteSnapshotDto
    {
        [JsonPropertyName("ref")] public RemoteSnapshotRefDto Ref { get; init; } = new();
        [JsonPropertyName("commit")] public RemoteSnapshotCommitDto Commit { get; init; } = new();
        [JsonPropertyName("blobs")] public IReadOnlyList<RemoteSnapshotBlobDto> Blobs { get; init; } = [];
        [JsonPropertyName("boundedBlobCount")] public int BoundedBlobCount { get; init; }

        public RemoteSnapshot ToContract() =>
            new(Ref.ToContract(), Commit.ToContract(), Blobs.Select(blob => blob.ToContract()).ToArray(), BoundedBlobCount);
    }

    private sealed record ChangedFileSummaryDto
    {
        [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
        [JsonPropertyName("changeKind")] public string ChangeKind { get; init; } = string.Empty;

        public ChangedFileSummary ToChangedFileSummary()
        {
            if (!TryNormalizeRepositoryPath(Path, allowRoot: false, out var path)
                || !Enum.TryParse<GitChangeKind>(ChangeKind, ignoreCase: true, out var changeKind)
                || changeKind is not (GitChangeKind.Added or GitChangeKind.Modified or GitChangeKind.Deleted))
            {
                throw new InvalidOperationException("The synchronization file summary did not match the expected contract.");
            }

            return new(path, changeKind);
        }
    }

    private static void ValidateSynchronizationCoordinates(string repositoryUrl, string branch)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repository)
            || !string.Equals(repository.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(repository.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(repository.UserInfo)
            || !string.IsNullOrEmpty(repository.Query)
            || !string.IsNullOrEmpty(repository.Fragment)
            || repository.Segments.Length != 3
            || string.IsNullOrWhiteSpace(branch)
            || !string.Equals(branch, branch.Trim(), StringComparison.Ordinal)
            || branch.IndexOfAny(['\0', '\r', '\n', ' ', '~', '^', ':', '?', '*', '[', '\\']) >= 0
            || branch.Contains("..", StringComparison.Ordinal)
            || branch.Contains("@{", StringComparison.Ordinal)
            || branch.EndsWith(".", StringComparison.Ordinal)
            || branch.EndsWith("/", StringComparison.Ordinal)
            || branch.Split('/').Any(segment => segment.Length == 0 || segment.StartsWith(".", StringComparison.Ordinal) || segment.EndsWith(".lock", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The synchronization coordinates are not a credential-free GitHub HTTPS origin and supported local branch.");
        }
    }

    private static bool IsLoopbackHost(string host) =>
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static bool IsAllowedRepositoryOrigin(Uri repository) =>
        string.IsNullOrEmpty(repository.UserInfo)
        && string.IsNullOrEmpty(repository.Query)
        && string.IsNullOrEmpty(repository.Fragment)
        && (string.Equals(repository.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(repository.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(repository.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(repository.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                    || IsLoopbackHost(repository.DnsSafeHost)));

    private static void ValidatePushCoordinates(
        string repositoryUrl,
        string branch,
        string destinationRef)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repository)
            || !string.IsNullOrEmpty(repository.UserInfo)
            || !string.IsNullOrEmpty(repository.Query)
            || !string.IsNullOrEmpty(repository.Fragment)
            || (!string.Equals(repository.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !(string.Equals(repository.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(repository.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                        || IsLoopbackHost(repository.DnsSafeHost))))
            || (string.Equals(repository.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(repository.Host, "github.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The push repository is not a credential-free GitHub HTTPS URL or local controlled test remote.");
        }

        if (string.IsNullOrWhiteSpace(branch)
            || !string.Equals(destinationRef, $"refs/heads/{branch}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The push branch and destination ref do not match.");
        }
    }

    private static void ValidateCommitId(string commitId, string propertyName)
    {
        if (commitId.Length != 40 || !commitId.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException($"{propertyName} is not a full commit SHA.");
        }
    }
}
