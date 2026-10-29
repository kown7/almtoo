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
