using System.Text.Json;
using AlmToo.Accessor.BrowserGitAccessor.Interface;
using Xunit;

namespace AlmToo.Tests.Accessor.BrowserGitAccessor;

public sealed class GitBugPublicationAccessorContractTests
{
    private const string RepositoryUrl = "https://github.com/example/project.git";
    private const string Branch = "main";
    private const string ReviewId = "review-gitbug-001";
    private const string SourceRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BugObjectId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Publication_request_preserves_the_exact_review_records_without_a_second_refspec_model()
    {
        var updates = new[]
        {
            new GitRefUpdate(
                "refs/bugs/issue-1",
                GitRefNamespace.GitBug,
                GitRefUpdateKind.Create,
                BugObjectId)
        };
        var preconditions = new[]
        {
            new GitRemotePrecondition("refs/bugs/issue-1", null)
        };
        var request = new GitBugPublicationRequest(
            ReviewId,
            RepositoryUrl,
            Branch,
            SourceRevision,
            updates,
            preconditions);

        Assert.True(BrowserGitPublicationContract.IsValidGitBugRequest(request));
        Assert.Same(updates, request.Updates);
        Assert.Same(preconditions, request.RemotePreconditions);
        Assert.Equal(updates[0], request.Updates[0]);
        Assert.Equal(preconditions[0], request.RemotePreconditions[0]);
        Assert.DoesNotContain("RefSpec", typeof(GitBugPublicationRequest).GetProperties()
            .Select(property => property.Name), StringComparer.Ordinal);
    }

    [Fact]
    public void Publication_request_rejects_unreviewed_or_tampered_inputs()
    {
        var validUpdate = new GitRefUpdate(
            "refs/bugs/issue-1",
            GitRefNamespace.GitBug,
            GitRefUpdateKind.Update,
            BugObjectId,
            SourceRevision);
        var validPrecondition = new GitRemotePrecondition(validUpdate.RefName, SourceRevision);

        Assert.False(BrowserGitPublicationContract.IsValidGitBugRequest(new GitBugPublicationRequest(
            "review with whitespace",
            RepositoryUrl,
            Branch,
            SourceRevision,
            [validUpdate],
            [validPrecondition])));
        Assert.False(BrowserGitPublicationContract.IsValidGitBugRequest(new GitBugPublicationRequest(
            ReviewId,
            RepositoryUrl,
            Branch,
            SourceRevision,
            [validUpdate with { RefName = "refs/heads/main", Namespace = GitRefNamespace.Branch }],
            [validPrecondition])));
        Assert.False(BrowserGitPublicationContract.IsValidGitBugRequest(new GitBugPublicationRequest(
            ReviewId,
            RepositoryUrl,
            Branch,
            SourceRevision,
            [validUpdate with { Kind = GitRefUpdateKind.Delete }],
            [validPrecondition])));
        Assert.False(BrowserGitPublicationContract.IsValidGitBugRequest(new GitBugPublicationRequest(
            ReviewId,
            RepositoryUrl,
            Branch,
            SourceRevision,
            [validUpdate],
            [new GitRemotePrecondition(validUpdate.RefName, BugObjectId)])));
        Assert.False(BrowserGitPublicationContract.IsValidGitBugRequest(new GitBugPublicationRequest(
            ReviewId,
            "https://user:secret@example.test/project.git",
            Branch,
            SourceRevision,
            [validUpdate],
            [validPrecondition])));
    }

    [Fact]
    public void Remote_inspection_is_passive_bounded_and_credential_metadata_is_non_secret()
    {
        var request = new GitBugRemoteInspectionRequest(RepositoryUrl, Branch);
        var inspection = new GitBugRemoteInspection(
            RepositoryUrl,
            Branch,
            [new GitBugRemoteRef("refs/bugs/issue-1", GitRefNamespace.GitBug, BugObjectId)],
            new GitBugCredentialUsage(Available: true, Used: false));

        Assert.True(BrowserGitPublicationContract.IsValidInspectionRequest(request));
        Assert.True(BrowserGitPublicationContract.IsValidInspection(inspection));
        Assert.True(inspection.Credential.Available);
        Assert.False(inspection.Credential.Used);
        Assert.False(BrowserGitPublicationContract.IsValidCredentialUsage(
            new GitBugCredentialUsage(Available: false, Used: true)));
        Assert.DoesNotContain("token", JsonSerializer.Serialize(inspection), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(inspection), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Operation_and_failure_categories_map_to_typed_safe_outcomes()
    {
        Assert.True(BrowserGitPublicationContract.IsValidOperation(GitBugPublicationOperations.InspectRemote));
        Assert.True(BrowserGitPublicationContract.IsValidOperation(GitBugPublicationOperations.Publish));
        Assert.False(BrowserGitPublicationContract.IsValidOperation("git.push.unreviewed"));

        Assert.Equal(
            GitOperationFailureKind.GitBugPublicationCredentialUnavailable,
            BrowserGitPublicationContract.ToOperationFailure(GitBugPublicationFailureCategory.CredentialUnavailable));
        Assert.Equal(
            GitOperationFailureKind.NetworkUnavailable,
            BrowserGitPublicationContract.ToOperationFailure(GitBugPublicationFailureCategory.NetworkUnavailable));
        Assert.Equal(
            GitOperationFailureKind.GitBugPublicationUnsupported,
            BrowserGitPublicationContract.ToOperationFailure(GitBugPublicationFailureCategory.Unsupported));
        Assert.Equal(
            GitOperationFailureKind.GitBugPublicationRemoteRejected,
            BrowserGitPublicationContract.ToOperationFailure(GitBugPublicationFailureCategory.RemoteRejected));
        Assert.Equal(
            GitOperationFailureKind.GitBugPublicationStaleRemote,
            BrowserGitPublicationContract.ToOperationFailure(GitBugPublicationFailureCategory.StaleRemote));
        Assert.Equal(
            GitOperationFailureKind.GitBugPublicationCancelled,
            BrowserGitPublicationContract.ToOperationFailure(GitBugPublicationFailureCategory.Cancelled));

        var cancelled = new GitOperationResult<GitBugPublicationReceipt>(
            GitBugPublicationOperations.Publish,
            false,
            "Publication was cancelled.",
            FailureKind: GitOperationFailureKind.GitBugPublicationCancelled);
        Assert.True(BrowserGitPublicationContract.IsSafeOutcome(
            cancelled,
            GitBugPublicationOperations.Publish,
            BrowserGitPublicationContract.IsValidReceipt));
    }

    [Fact]
    public void Safe_outcome_exposes_no_diagnostic_or_credential_material()
    {
        var receipt = new GitBugPublicationReceipt(
            ReviewId,
            RepositoryUrl,
            Branch,
            SourceRevision,
            [new GitRefUpdate(
                "refs/bugs/issue-1",
                GitRefNamespace.GitBug,
                GitRefUpdateKind.Create,
                BugObjectId)],
            new GitBugCredentialUsage(Available: true, Used: true),
            BoundedUpdateCount: 1);
        var success = new GitOperationResult<GitBugPublicationReceipt>(
            GitBugPublicationOperations.Publish,
            true,
            "Git-Bug publication succeeded.",
            receipt);

        Assert.True(BrowserGitPublicationContract.IsSafeOutcome(
            success,
            GitBugPublicationOperations.Publish,
            BrowserGitPublicationContract.IsValidReceipt));
        Assert.Null(success.Diagnostic);
        var serialized = JsonSerializer.Serialize(success);
        Assert.DoesNotContain("fixture-token", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("password", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", serialized, StringComparison.OrdinalIgnoreCase);
    }
}
