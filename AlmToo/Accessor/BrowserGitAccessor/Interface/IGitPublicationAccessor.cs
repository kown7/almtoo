namespace AlmToo.Accessor.BrowserGitAccessor.Interface;

/// <summary>
/// Focused publication facet of the Browser Git Accessor. It protects the reviewed-ref
/// validation and browser/remote publication seam while retaining the legacy branch push DTOs.
/// </summary>
public interface IGitPublicationAccessor
{
    /// <summary>Inspects the current branch and Git-Bug refs without applying them.</summary>
    ValueTask<GitOperationResult<GitPublicationReview>> InspectPublicationAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<GitOperationResult<GitPublicationReview>>(
            new NotSupportedException("This publication facet does not support ref-aware publication."));

    /// <summary>Applies only the explicitly accepted, preconditioned ref set.</summary>
    ValueTask<GitOperationResult<GitPublicationReceipt>> PublishAsync(
        GitPublicationRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<GitOperationResult<GitPublicationReceipt>>(
            new NotSupportedException("This publication facet does not support ref-aware publication."));

    /// <summary>Inspects the current branch publication without applying it.</summary>
    ValueTask<GitOperationResult<PushReview>> InspectPushAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies an explicitly accepted branch publication request.</summary>
    ValueTask<GitOperationResult<PushResult>> PushAsync(PushRequest request, CancellationToken cancellationToken = default);
}
