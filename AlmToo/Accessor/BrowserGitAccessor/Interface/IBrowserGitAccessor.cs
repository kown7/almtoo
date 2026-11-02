namespace AlmToo.Accessor.BrowserGitAccessor.Interface;

/// <summary>Isolates the browser JavaScript, local file, remote Git, publication, and VCS metadata platform boundary.</summary>
public interface IBrowserGitAccessor : IAsyncDisposable
{
    ValueTask<GitOperationResult<RepositoryInfo>> CloneOrOpenAsync(RepositoryOpenRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<IReadOnlyList<ChangedFile>>> GetStatusAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<BranchSnapshot>> GetBranchesAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<RemoteSnapshot>> GetRemoteSnapshotAsync(RemoteSnapshotRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult> CheckoutBranchAsync(CheckoutBranchRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<CommitHistory>> GetHistoryAsync(BranchHistoryRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<CommitInfo>> CommitAsync(CommitRequest request, CancellationToken cancellationToken = default);

    ValueTask<GitOperationResult<GitPublicationReview>> InspectPublicationAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<GitPublicationReceipt>> PublishAsync(GitPublicationRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<PushReview>> InspectPushAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<PushResult>> PushAsync(PushRequest request, CancellationToken cancellationToken = default);

    ValueTask<GitOperationResult<FilterFilesResult>> FilterFilesAsync(FilterFilesRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<UpdateFilesResult>> UpdateFilesAsync(UpdateFilesRequest request, CancellationToken cancellationToken = default);

    ValueTask<GitOperationResult<bool>> HasCredentialAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<bool>> StoreCredentialAsync(string credential, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<bool>> ForgetCredentialAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<SynchronizationReview>> SynchronizeAsync(SynchronizationRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<ReviewedReconciliationApplyReceipt>> ApplyReviewedReconciliationAsync(ReviewedReconciliationApplyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Passively inspects remote Git-Bug refs without mutation; concrete Accessors override this boundary operation.</summary>
    ValueTask<GitOperationResult<GitBugRemoteInspection>> InspectGitBugRemoteAsync(
        GitBugRemoteInspectionRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            BrowserGitPublicationContract.IsValidInspectionRequest(request)
                ? new GitOperationResult<GitBugRemoteInspection>(
                    GitBugPublicationOperations.InspectRemote,
                    false,
                    "Remote Git-Bug inspection is unavailable.",
                    FailureKind: GitOperationFailureKind.GitBugPublicationUnsupported)
                : new GitOperationResult<GitBugRemoteInspection>(
                    GitBugPublicationOperations.InspectRemote,
                    false,
                    "The Git-Bug inspection request is invalid.",
                    FailureKind: GitOperationFailureKind.GitBugPublicationInvalidRequest));

    /// <summary>Publishes only a validated reviewed Git-Bug request; concrete Accessors override this boundary operation.</summary>
    ValueTask<GitOperationResult<GitBugPublicationReceipt>> PublishGitBugAsync(
        GitBugPublicationRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            BrowserGitPublicationContract.IsValidGitBugRequest(request)
                ? new GitOperationResult<GitBugPublicationReceipt>(
                    GitBugPublicationOperations.Publish,
                    false,
                    "Git-Bug publication is unavailable.",
                    FailureKind: GitOperationFailureKind.GitBugPublicationUnsupported)
                : new GitOperationResult<GitBugPublicationReceipt>(
                    GitBugPublicationOperations.Publish,
                    false,
                    "The Git-Bug publication request is invalid.",
                    FailureKind: GitOperationFailureKind.GitBugPublicationInvalidRequest));
}
