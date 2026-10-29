using AlmToo.Accessor.BrowserGitAccessor.Interface;
using AlmToo.Engines.Workspace;
using AlmToo.Managers.Workspace.Interface;

namespace AlmToo.Managers.Workspace;

/// <summary>Coordinates credential-free remote reconciliation review without applying changes.</summary>
public sealed partial class WorkspaceManager
{
    private const string DefaultRemoteRef = "refs/remotes/origin/main";
    private readonly ConflictReconciliationEngine reconciliationEngine = new();
    private long reconciliationGeneration;
    private WorkspaceReconciliationResult? lastValidReconciliationReview;
    private WorkspaceReconciliationApplyRequest? selectedReconciliationApply;

    /// <summary>Inspects the current local status against a non-mutating remote-tracking snapshot.</summary>
    public Task<bool> ReviewReconciliationAsync(
        string remoteRef = DefaultRemoteRef,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            WorkspaceOperation.ReviewReconciliation,
            token => ReviewReconciliationCoreAsync(remoteRef, token),
            cancellationToken);

    public Task<bool> ReviewReconciliationAsync(CancellationToken cancellationToken) =>
        ReviewReconciliationAsync(DefaultRemoteRef, cancellationToken);

    /// <summary>Stores one supported resolution selection for the exact current review without applying it.</summary>
    public bool SelectReconciliationResolutions(WorkspaceReconciliationApplyRequest request)
    {
        var review = State.ReconciliationRetryable && lastValidReconciliationReview is not null
            ? lastValidReconciliationReview
            : State.Reconciliation;
        if (review is null || request is null)
        {
            return RejectReconciliationSelection("Review the current reconciliation before selecting resolutions.");
        }

        var confirmedRequest = request with { ExplicitlyConfirmed = true };
        var validation = reconciliationEngine.BuildApplyPlan(review, confirmedRequest);
        if (!validation.Succeeded || validation.Plan is null)
        {
            return RejectReconciliationSelection(GetApplyRejectionMessage(validation.FailureKind));
        }

        selectedReconciliationApply = request with { ExplicitlyConfirmed = false };
        State.ReconciliationSelection = selectedReconciliationApply;
        State.ReconciliationApply = review.ApplyMetadata with { ExplicitlyConfirmed = false };
        State.ReconciliationRetryable = false;
        State.Error = null;
        NotifyChanged();
        return true;
    }

    /// <summary>Compatibility singular form for the bounded resolution-selection entrypoint.</summary>
    public bool SelectReconciliationResolution(WorkspaceReconciliationApplyRequest request) =>
        SelectReconciliationResolutions(request);

    /// <summary>Confirms or withdraws the selected reconciliation without performing a write.</summary>
    public void ConfirmReconciliationApply(bool confirmed)
    {
        if (selectedReconciliationApply is null || State.Reconciliation is null)
        {
            return;
        }

        selectedReconciliationApply = selectedReconciliationApply with { ExplicitlyConfirmed = confirmed };
        State.ReconciliationSelection = selectedReconciliationApply;
        State.ReconciliationApply = State.Reconciliation.ApplyMetadata with { ExplicitlyConfirmed = confirmed };
        State.Error = null;
        NotifyChanged();
    }

    /// <summary>Applies exactly the selected and explicitly confirmed current reconciliation review.</summary>
    public Task<bool> ApplyReviewedReconciliationAsync(CancellationToken cancellationToken = default) =>
        RunAsync(
            WorkspaceOperation.ApplyReconciliation,
            token => ApplyReconciliationCoreAsync(selectedReconciliationApply, token),
            cancellationToken);

    /// <summary>Applies the selected reviewed request after explicit confirmation.</summary>
    public Task<bool> ApplyReconciliationAsync(CancellationToken cancellationToken = default) =>
        ApplyReviewedReconciliationAsync(cancellationToken);

    /// <summary>Applies a caller-provided reviewed request after Manager-side identity and confirmation checks.</summary>
    public Task<bool> ApplyReconciliationAsync(
        WorkspaceReconciliationApplyRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            WorkspaceOperation.ApplyReconciliation,
            token => ApplyReconciliationCoreAsync(request, token),
            cancellationToken);

    public Task<bool> ApplyReviewedAsync(CancellationToken cancellationToken = default) =>
        ApplyReviewedReconciliationAsync(cancellationToken);

    private async Task<bool> ApplyReconciliationCoreAsync(
        WorkspaceReconciliationApplyRequest? requested,
        CancellationToken token)
    {
        var generation = Interlocked.Increment(ref reconciliationGeneration);
        if (!RequireRepository())
        {
            return false;
        }

        var review = State.ReconciliationRetryable && lastValidReconciliationReview is not null
            ? lastValidReconciliationReview
            : State.Reconciliation;
        var request = requested ?? selectedReconciliationApply;
        if (review is null || request is null)
        {
            return FailReconciliationApply(
                WorkspaceReconciliationFailureKind.ApplyRejected,
                "Review the current reconciliation and select a resolution before applying.",
                retryable: false);
        }

        if (!request.ExplicitlyConfirmed)
        {
            return FailReconciliationApply(
                WorkspaceReconciliationFailureKind.ApplyRejected,
                "Explicitly confirm the reviewed reconciliation before applying.",
                retryable: true);
        }

        var planResult = reconciliationEngine.BuildApplyPlan(review, request);
        if (!planResult.Succeeded || planResult.Plan is null)
        {
            return FailReconciliationApply(
                planResult.FailureKind,
                GetApplyRejectionMessage(planResult.FailureKind),
                retryable: planResult.FailureKind is not WorkspaceReconciliationFailureKind.StaleReview);
        }

        var plan = planResult.Plan;
        if (!IsCurrentReconciliation(generation))
        {
            return false;
        }

        selectedReconciliationApply = request;
        State.ReconciliationSelection = request;
        State.ReconciliationApply = plan.Review;
        State.ReconciliationRetryable = false;
        State.Reconciliation = review with
        {
            Status = WorkspaceReconciliationStatus.Applying,
            Message = "Applying the explicitly confirmed reconciliation.",
            RequiresExplicitApply = false,
            FailureKind = WorkspaceReconciliationFailureKind.None,
            ApplyMetadata = plan.Review
        };
        NotifyChanged();

        try
        {
            token.ThrowIfCancellationRequested();
            var result = await gitAccessor.ApplyReviewedReconciliationAsync(ToAccessorRequest(plan), token);
            if (!IsCurrentReconciliation(generation))
            {
                if (token.IsCancellationRequested)
                {
                    return FailReconciliationApply(
                        WorkspaceReconciliationFailureKind.Cancelled,
                        "The reconciliation apply was cancelled; the reviewed plan is available to retry.",
                        retryable: true);
                }

                return false;
            }

            token.ThrowIfCancellationRequested();
            if (!result.Succeeded || result.Value is null)
            {
                return FailReconciliationApply(
                    MapApplyFailure(result.FailureKind),
                    GetApplyFailureMessage(result.FailureKind),
                    retryable: result.FailureKind != GitOperationFailureKind.ReconciliationStaleRemote);
            }

            var receipt = result.Value;
            if (!BrowserGitReconciliationContract.IsValidReceipt(receipt)
                || !string.Equals(receipt.ReviewId, plan.Review.ReviewId, StringComparison.Ordinal)
                || !string.Equals(receipt.SourceRevision, plan.Review.SourceRevision, StringComparison.Ordinal)
                || receipt.BoundedAppliedPathCount != plan.Selections.Count
                || !receipt.AppliedPaths.ToHashSet(StringComparer.Ordinal).SetEquals(plan.Selections.Select(selection => selection.Path)))
            {
                return FailReconciliationApply(
                    WorkspaceReconciliationFailureKind.Unknown,
                    "The reconciliation write result was not safe to publish. Retry the review.",
                    retryable: true);
            }

            lastValidReconciliationReview = null;
            selectedReconciliationApply = null;
            State.ReconciliationSelection = null;
            State.ReconciliationRetryable = false;
            State.ReconciliationApply = plan.Review;
            State.Reconciliation = review with
            {
                Status = WorkspaceReconciliationStatus.Applied,
                Message = "The reviewed reconciliation was applied safely.",
                RequiresExplicitApply = false,
                FailureKind = WorkspaceReconciliationFailureKind.None,
                ApplyMetadata = plan.Review
            };
            return Succeed("The reviewed reconciliation was applied safely.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (IsCurrentReconciliation(generation))
            {
                FailReconciliationApply(
                    WorkspaceReconciliationFailureKind.Cancelled,
                    "The reconciliation apply was cancelled; the reviewed plan is available to retry.",
                    retryable: true);
            }

            throw;
        }
        catch (Exception)
        {
            if (!IsCurrentReconciliation(generation))
            {
                return false;
            }

            return FailReconciliationApply(
                WorkspaceReconciliationFailureKind.Unknown,
                "The reconciliation could not be applied safely; the reviewed plan is available to retry.",
                retryable: true);
        }
    }

    private bool RejectReconciliationSelection(string message)
    {
        State.Error = new(
            WorkspaceOperation.ApplyReconciliation,
            message,
            GitOperationFailureKind.ReconciliationInvalidRequest);
        State.Result = new(
            WorkspaceOperation.ApplyReconciliation,
            false,
            message,
            GitOperationFailureKind.ReconciliationInvalidRequest);
        NotifyChanged();
        return false;
    }

    private bool FailReconciliationApply(
        WorkspaceReconciliationFailureKind failureKind,
        string message,
        bool retryable)
    {
        var review = lastValidReconciliationReview ?? State.Reconciliation;
        if (failureKind is WorkspaceReconciliationFailureKind.StaleReview or WorkspaceReconciliationFailureKind.StaleRemote)
        {
            lastValidReconciliationReview = null;
            selectedReconciliationApply = null;
            State.ReconciliationSelection = null;
            State.ReconciliationApply = null;
            State.ReconciliationRetryable = false;
        }
        else
        {
            lastValidReconciliationReview = review;
            State.ReconciliationRetryable = retryable;
        }

        State.Reconciliation = review is null
            ? new WorkspaceReconciliationResult(
                WorkspaceReconciliationStatus.Failed,
                Array.Empty<WorkspaceReconciliationChange>(),
                message,
                RequiresExplicitApply: false,
                FailureKind: failureKind)
            : review with
            {
                Status = failureKind == WorkspaceReconciliationFailureKind.Cancelled
                    ? WorkspaceReconciliationStatus.Cancelled
                    : WorkspaceReconciliationStatus.Failed,
                Message = message,
                RequiresExplicitApply = false,
                FailureKind = failureKind
            };
        return Fail(message, ToReconciliationOperationFailure(failureKind));
    }

    private static ReviewedReconciliationApplyRequest ToAccessorRequest(WorkspaceReconciliationApplyPlan plan) =>
        new(
            plan.Review.ReviewId,
            plan.Review.SourceRevision,
            plan.Selections.Count,
            plan.Selections
                .Select(selection => new ReviewedReconciliationSelection(
                    selection.Path,
                    selection.Resolution.ToString()))
                .ToArray(),
            plan.RemotePreconditions
                .Select(precondition => new ReviewedReconciliationRemotePrecondition(
                    precondition.Path,
                    precondition.ExpectedRemoteObjectId))
                .ToArray());

    private static WorkspaceReconciliationFailureKind MapApplyFailure(GitOperationFailureKind? failureKind) => failureKind switch
    {
        GitOperationFailureKind.ReconciliationStaleRemote => WorkspaceReconciliationFailureKind.StaleRemote,
        GitOperationFailureKind.ReconciliationUnsupportedResolution => WorkspaceReconciliationFailureKind.ApplyRejected,
        GitOperationFailureKind.ReconciliationInvalidRequest => WorkspaceReconciliationFailureKind.ApplyRejected,
        GitOperationFailureKind.ReconciliationWriteFailed => WorkspaceReconciliationFailureKind.WriteFailed,
        GitOperationFailureKind.ReconciliationCancelled => WorkspaceReconciliationFailureKind.Cancelled,
        GitOperationFailureKind.ReconciliationDataMalformed => WorkspaceReconciliationFailureKind.MalformedInput,
        GitOperationFailureKind.ReconciliationDataUnavailable => WorkspaceReconciliationFailureKind.RemoteUnavailable,
        _ => WorkspaceReconciliationFailureKind.Unknown
    };

    private static GitOperationFailureKind ToReconciliationOperationFailure(WorkspaceReconciliationFailureKind failureKind) => failureKind switch
    {
        WorkspaceReconciliationFailureKind.StaleReview or WorkspaceReconciliationFailureKind.StaleRemote => GitOperationFailureKind.ReconciliationStaleRemote,
        WorkspaceReconciliationFailureKind.ApplyRejected => GitOperationFailureKind.ReconciliationInvalidRequest,
        WorkspaceReconciliationFailureKind.WriteFailed => GitOperationFailureKind.ReconciliationWriteFailed,
        WorkspaceReconciliationFailureKind.Cancelled => GitOperationFailureKind.ReconciliationCancelled,
        WorkspaceReconciliationFailureKind.MalformedInput => GitOperationFailureKind.ReconciliationDataMalformed,
        WorkspaceReconciliationFailureKind.RemoteUnavailable => GitOperationFailureKind.ReconciliationDataUnavailable,
        _ => GitOperationFailureKind.ReconciliationUnknown
    };

    private static string GetApplyRejectionMessage(WorkspaceReconciliationFailureKind failureKind) => failureKind switch
    {
        WorkspaceReconciliationFailureKind.StaleReview => "The reconciliation review is stale. Review the current remote state again.",
        WorkspaceReconciliationFailureKind.StaleRemote => "The remote changed after review. Review the current remote state again.",
        WorkspaceReconciliationFailureKind.ApplyRejected => "Select one supported resolution for every reviewed change and explicitly confirm apply.",
        WorkspaceReconciliationFailureKind.MalformedInput => "The selected reconciliation was malformed. Review the current remote state again.",
        _ => "The reviewed reconciliation is not ready to apply. Review the current remote state again."
    };

    private static string GetApplyFailureMessage(GitOperationFailureKind? failureKind) => failureKind switch
    {
        GitOperationFailureKind.ReconciliationStaleRemote => "The remote changed after review. Review the current remote state again before retrying.",
        GitOperationFailureKind.ReconciliationUnsupportedResolution => "The selected reconciliation resolution is no longer supported. Review the current remote state again.",
        GitOperationFailureKind.ReconciliationWriteFailed => "The reconciliation write failed safely; the reviewed plan is available to retry.",
        GitOperationFailureKind.ReconciliationCancelled => "The reconciliation apply was cancelled; the reviewed plan is available to retry.",
        GitOperationFailureKind.ReconciliationDataUnavailable => "The remote reconciliation write could not be reached. The reviewed plan is available to retry.",
        GitOperationFailureKind.ReconciliationDataMalformed => "The reconciliation write response was malformed. Review the current remote state again.",
        _ => "The reconciliation could not be applied safely; the reviewed plan is available to retry."
    };

    private async Task<bool> ReviewReconciliationCoreAsync(string remoteRef, CancellationToken token)
    {
        var generation = Interlocked.Increment(ref reconciliationGeneration);
        if (State.Repository is null)
        {
            return FailReconciliation(
                WorkspaceReconciliationFailureKind.LocalUnavailable,
                "Open a repository before reviewing remote reconciliation.",
                GitOperationFailureKind.RemoteSnapshotInvalidRequest);
        }

        lastValidReconciliationReview = null;
        selectedReconciliationApply = null;
        State.ReconciliationSelection = null;
        State.ReconciliationRetryable = false;
        State.Reconciliation = new WorkspaceReconciliationResult(
            WorkspaceReconciliationStatus.Inspecting,
            Array.Empty<WorkspaceReconciliationChange>(),
            "Inspecting the local workspace and remote comparison.",
            RequiresExplicitApply: false,
            FailureKind: WorkspaceReconciliationFailureKind.None);
        State.ReconciliationApply = null;
        NotifyChanged();

        try
        {
            var status = await gitAccessor.GetStatusAsync(token);
            if (!IsCurrentReconciliation(generation))
            {
                return false;
            }

            if (!status.Succeeded || status.Value is null)
            {
                return FailReconciliation(
                    MapFailure(status.FailureKind, localFailure: true),
                    "The local workspace status could not be inspected safely. Retry the review.",
                    NormalizeReconciliationOperationFailure(status.FailureKind, localFailure: true));
            }

            if (!BrowserGitCommitContract.IsValidChangedFiles(status.Value)
                || status.Value.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != status.Value.Count)
            {
                return FailReconciliation(
                    WorkspaceReconciliationFailureKind.MalformedInput,
                    "The local workspace status was malformed. Reopen the repository and retry the review.",
                    GitOperationFailureKind.RemoteSnapshotDataMalformed);
            }

            State.ChangedFiles = status.Value;
            var request = new RemoteSnapshotRequest(remoteRef?.Trim() ?? string.Empty);
            if (!BrowserGitRemoteSnapshotContract.IsValidRequest(request))
            {
                return FailReconciliation(
                    WorkspaceReconciliationFailureKind.UnsupportedFormat,
                    "The remote comparison ref is unsupported. Select a supported ref and retry.",
                    GitOperationFailureKind.RemoteSnapshotInvalidRequest);
            }

            var snapshotResult = await gitAccessor.GetRemoteSnapshotAsync(request, token);
            if (!IsCurrentReconciliation(generation))
            {
                return false;
            }

            if (!snapshotResult.Succeeded || snapshotResult.Value is null)
            {
                var failureKind = MapFailure(snapshotResult.FailureKind, localFailure: false);
                return FailReconciliation(
                    failureKind,
                    GetReconciliationFailureMessage(failureKind),
                    NormalizeReconciliationOperationFailure(snapshotResult.FailureKind, localFailure: false));
            }

            if (!IsValidSnapshot(snapshotResult.Value))
            {
                return FailReconciliation(
                    WorkspaceReconciliationFailureKind.MalformedInput,
                    "The remote comparison data was malformed. Retry the review.",
                    GitOperationFailureKind.RemoteSnapshotDataMalformed);
            }

            var input = NormalizeInput(snapshotResult.Value, status.Value);
            if (!WorkspaceReconciliationContract.IsValidInput(input))
            {
                return FailReconciliation(
                    WorkspaceReconciliationFailureKind.MalformedInput,
                    "The reconciliation comparison could not be represented safely. Retry the review.",
                    GitOperationFailureKind.RemoteSnapshotDataMalformed);
            }

            var evaluated = reconciliationEngine.Evaluate(input);
            if (!WorkspaceReconciliationContract.IsValidResult(evaluated))
            {
                return FailReconciliation(
                    WorkspaceReconciliationFailureKind.MalformedInput,
                    "The reconciliation review produced an unsafe result. Retry the review.",
                    GitOperationFailureKind.RemoteSnapshotDataMalformed);
            }

            var metadata = new WorkspaceReconciliationApplyMetadata(
                CreateReviewId(generation, snapshotResult.Value.SourceRevision),
                snapshotResult.Value.SourceRevision,
                evaluated.Changes.Count,
                DateTimeOffset.UtcNow);
            var reviewed = evaluated with
            {
                ApplyMetadata = metadata
            };

            if (!IsCurrentReconciliation(generation))
            {
                return false;
            }

            lastValidReconciliationReview = reviewed;
            selectedReconciliationApply = null;
            State.ReconciliationSelection = null;
            State.ReconciliationRetryable = false;
            State.Reconciliation = reviewed;
            State.ReconciliationApply = metadata;
            return Succeed(GetReviewMessage(reviewed));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (IsCurrentReconciliation(generation))
            {
                FailReconciliation(
                    WorkspaceReconciliationFailureKind.Cancelled,
                    "The reconciliation review was cancelled.",
                    GitOperationFailureKind.RemoteSnapshotCancelled);
            }

            throw;
        }
        catch (Exception)
        {
            if (!IsCurrentReconciliation(generation))
            {
                return false;
            }

            return FailReconciliation(
                WorkspaceReconciliationFailureKind.Unknown,
                "The reconciliation review could not be completed safely. Inspect the workspace before retrying.",
                GitOperationFailureKind.RemoteSnapshotUnknown);
        }
    }

    private bool IsCurrentReconciliation(long generation) =>
        generation == Volatile.Read(ref reconciliationGeneration);

    private bool FailReconciliation(
        WorkspaceReconciliationFailureKind failureKind,
        string message,
        GitOperationFailureKind operationFailureKind)
    {
        var status = failureKind switch
        {
            WorkspaceReconciliationFailureKind.Cancelled => WorkspaceReconciliationStatus.Cancelled,
            WorkspaceReconciliationFailureKind.RemoteUnavailable => WorkspaceReconciliationStatus.Unavailable,
            WorkspaceReconciliationFailureKind.UnsupportedFormat => WorkspaceReconciliationStatus.Unsupported,
            _ => WorkspaceReconciliationStatus.Failed
        };
        State.Reconciliation = new WorkspaceReconciliationResult(
            status,
            Array.Empty<WorkspaceReconciliationChange>(),
            message,
            RequiresExplicitApply: false,
            FailureKind: failureKind);
        lastValidReconciliationReview = null;
        selectedReconciliationApply = null;
        State.ReconciliationSelection = null;
        State.ReconciliationRetryable = false;
        State.ReconciliationApply = null;
        var failed = Fail(message, operationFailureKind);
        NotifyChanged();
        return failed;
    }

    private static bool IsValidSnapshot(RemoteSnapshot snapshot) =>
        snapshot.Ref is not null
        && snapshot.Commit is not null
        && snapshot.Blobs is not null
        && BrowserGitRemoteSnapshotContract.IsValidSnapshot(snapshot);

    private WorkspaceReconciliationInput NormalizeInput(
        RemoteSnapshot snapshot,
        IReadOnlyList<ChangedFile> localChanges)
    {
        var remoteByPath = snapshot.Blobs.ToDictionary(blob => blob.Path, StringComparer.Ordinal);
        var localByPath = localChanges.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var paths = remoteByPath.Keys
            .Concat(localByPath.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal);
        var localHead = State.RecentHistory?.Entries.FirstOrDefault()?.CommitId;
        var comparisons = new List<WorkspaceFileComparison>();

        foreach (var path in paths)
        {
            remoteByPath.TryGetValue(path, out var remote);
            if (!localByPath.TryGetValue(path, out var local))
            {
                var unchanged = string.Equals(localHead, snapshot.Commit.CommitId, StringComparison.Ordinal);
                var basedOnLocalHead = string.Equals(localHead, snapshot.Commit.ParentCommitId, StringComparison.Ordinal);
                comparisons.Add(unchanged
                    ? new WorkspaceFileComparison(path, remote?.ObjectId, remote?.ObjectId, remote?.ObjectId)
                    : basedOnLocalHead
                        ? new WorkspaceFileComparison(path, snapshot.Commit.ParentCommitId, snapshot.Commit.ParentCommitId, remote?.ObjectId)
                        : new WorkspaceFileComparison(path, null, null, remote?.ObjectId));
                continue;
            }

            var localObjectId = CreateLocalComparisonId(path);
            if (local.ChangeKind is GitChangeKind.Added or GitChangeKind.Untracked or GitChangeKind.Deleted)
            {
                comparisons.Add(new WorkspaceFileComparison(path, null, localObjectId, remote?.ObjectId));
                continue;
            }

            if (local.ChangeKind is GitChangeKind.Modified or GitChangeKind.Renamed)
            {
                comparisons.Add(new WorkspaceFileComparison(
                    path,
                    string.Equals(localHead, snapshot.Commit.CommitId, StringComparison.Ordinal)
                        ? remote?.ObjectId
                        : CreateBaseComparisonId(path),
                    localObjectId,
                    remote?.ObjectId));
                continue;
            }

            comparisons.Add(new WorkspaceFileComparison(
                path,
                null,
                null,
                remote?.ObjectId,
                IsSupported: false));
        }

        return new WorkspaceReconciliationInput(comparisons);
    }

    private static string CreateLocalComparisonId(string path) =>
        $"local:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))}";

    private static string CreateBaseComparisonId(string path) =>
        $"base:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))}";

    private static string CreateReviewId(long generation, string sourceRevision) =>
        $"reconciliation-{generation}-{sourceRevision}";

    private static WorkspaceReconciliationFailureKind MapFailure(
        GitOperationFailureKind? failureKind,
        bool localFailure) => failureKind switch
        {
            GitOperationFailureKind.RemoteSnapshotUnsupportedReference => WorkspaceReconciliationFailureKind.UnsupportedFormat,
            GitOperationFailureKind.RemoteSnapshotInvalidRequest => WorkspaceReconciliationFailureKind.UnsupportedFormat,
            GitOperationFailureKind.RemoteSnapshotDataMalformed => WorkspaceReconciliationFailureKind.MalformedInput,
            GitOperationFailureKind.RemoteSnapshotLimitExceeded => WorkspaceReconciliationFailureKind.MalformedInput,
            GitOperationFailureKind.RemoteSnapshotCancelled => WorkspaceReconciliationFailureKind.Cancelled,
            GitOperationFailureKind.RemoteSnapshotDataUnavailable => WorkspaceReconciliationFailureKind.RemoteUnavailable,
            GitOperationFailureKind.NetworkUnavailable => WorkspaceReconciliationFailureKind.RemoteUnavailable,
            GitOperationFailureKind.RemoteAhead => WorkspaceReconciliationFailureKind.RemoteUnavailable,
            GitOperationFailureKind.BranchHistoryDataMalformed when localFailure => WorkspaceReconciliationFailureKind.MalformedInput,
            _ when localFailure => WorkspaceReconciliationFailureKind.LocalUnavailable,
            _ => WorkspaceReconciliationFailureKind.RemoteUnavailable
        };

    private static GitOperationFailureKind NormalizeReconciliationOperationFailure(
        GitOperationFailureKind? failureKind,
        bool localFailure) => failureKind switch
        {
            GitOperationFailureKind.RemoteSnapshotUnsupportedReference => GitOperationFailureKind.RemoteSnapshotUnsupportedReference,
            GitOperationFailureKind.RemoteSnapshotInvalidRequest => GitOperationFailureKind.RemoteSnapshotInvalidRequest,
            GitOperationFailureKind.RemoteSnapshotDataMalformed => GitOperationFailureKind.RemoteSnapshotDataMalformed,
            GitOperationFailureKind.RemoteSnapshotLimitExceeded => GitOperationFailureKind.RemoteSnapshotLimitExceeded,
            GitOperationFailureKind.RemoteSnapshotCancelled => GitOperationFailureKind.RemoteSnapshotCancelled,
            GitOperationFailureKind.RemoteSnapshotDataUnavailable => GitOperationFailureKind.RemoteSnapshotDataUnavailable,
            GitOperationFailureKind.NetworkUnavailable => GitOperationFailureKind.NetworkUnavailable,
            GitOperationFailureKind.RemoteAhead => GitOperationFailureKind.RemoteAhead,
            _ when localFailure => GitOperationFailureKind.Unknown,
            _ => GitOperationFailureKind.RemoteSnapshotUnknown
        };

    private static string GetReconciliationFailureMessage(WorkspaceReconciliationFailureKind failureKind) => failureKind switch
    {
        WorkspaceReconciliationFailureKind.UnsupportedFormat => "The remote comparison ref is unsupported. Select a supported ref and retry.",
        WorkspaceReconciliationFailureKind.MalformedInput => "The remote comparison data was malformed. Retry the review.",
        WorkspaceReconciliationFailureKind.Cancelled => "The reconciliation review was cancelled.",
        WorkspaceReconciliationFailureKind.RemoteUnavailable => "The remote comparison snapshot is unavailable. Retry the review.",
        WorkspaceReconciliationFailureKind.LocalUnavailable => "The local workspace status is unavailable. Reopen the repository and retry the review.",
        _ => "The reconciliation review could not be completed safely. Inspect the workspace before retrying."
    };

    private static string GetReviewMessage(WorkspaceReconciliationResult result) => result.Status switch
    {
        WorkspaceReconciliationStatus.RequiresResolution => "Review the divergent files before applying reconciliation.",
        WorkspaceReconciliationStatus.Ready when result.Changes.Count == 0 => "The workspace is clean and has no remote file changes to reconcile.",
        _ => "The remote comparison is ready for review."
    };
}
