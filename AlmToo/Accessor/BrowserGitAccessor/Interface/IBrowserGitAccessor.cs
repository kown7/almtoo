using AlmToo.Resource.BrowserGitResource.Data;

namespace AlmToo.Accessor.BrowserGitAccessor.Interface;

/// <summary>Isolates the browser JavaScript and remote Git platform boundary.</summary>
public interface IBrowserGitAccessor : IAsyncDisposable
{
    ValueTask<GitOperationResult<RepositoryInfo>> CloneOrOpenAsync(RepositoryOpenRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<IReadOnlyList<ChangedFile>>> GetStatusAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<CommitInfo>> CommitAsync(CommitRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<PushReview>> InspectPushAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<bool>> HasCredentialAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<bool>> StoreCredentialAsync(string credential, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<bool>> ForgetCredentialAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<PushResult>> PushAsync(PushRequest request, CancellationToken cancellationToken = default);
}
