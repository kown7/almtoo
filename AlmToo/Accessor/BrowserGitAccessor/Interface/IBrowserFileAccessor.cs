using AlmToo.Resource.BrowserGitResource.Data;

namespace AlmToo.Accessor.BrowserGitAccessor.Interface;

/// <summary>Isolates browser-workspace file operations behind a capability-specific platform seam.</summary>
public interface IBrowserFileAccessor
{
    ValueTask<GitOperationResult<FilterFilesResult>> FilterFilesAsync(FilterFilesRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<UpdateFilesResult>> UpdateFilesAsync(UpdateFilesRequest request, CancellationToken cancellationToken = default);
}
