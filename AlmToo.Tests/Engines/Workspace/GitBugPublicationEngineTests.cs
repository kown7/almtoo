using AlmToo.Engines.Workspace;
using AlmToo.Managers.Workspace.Interface;
using Xunit;

namespace AlmToo.Tests.Engines.Workspace;

public sealed class GitBugPublicationEngineTests
{
    private const string SourceRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly GitBugPublicationEngine engine = new();

    [Fact]
    public void Evaluate_orders_exact_issue_and_identity_refspecs_deterministically()
    {
        var result = engine.Evaluate(new GitBugPublicationInput(
            SourceRevision,
            [
                Ref("refs/identities/alice", 'c'),
                Ref("refs/bugs/issue-z", 'b'),
                Ref("refs/bugs/issue-a", 'a')
            ],
            []));

        Assert.Equal(GitBugPublicationStatus.Ready, result.Status);
        Assert.Null(result.FailureKind);
        Assert.NotNull(result.Plan);
        Assert.Equal(SourceRevision, result.Plan.SourceRevision);
        Assert.True(result.Plan.RequiresExplicitConfirmation);
        Assert.Equal(
            ["refs/bugs/issue-a", "refs/bugs/issue-z", "refs/identities/alice"],
            result.Plan.RefSpecs.Select(spec => spec.SourceRef));
        Assert.Equal(
            [GitBugPublicationRefKind.Issue, GitBugPublicationRefKind.Issue, GitBugPublicationRefKind.Identity],
            result.Plan.RefSpecs.Select(spec => spec.Kind));
        Assert.All(result.Plan.RefSpecs, spec =>
        {
            Assert.Equal(spec.SourceRef, spec.DestinationRef);
            Assert.Equal(GitBugPublicationChangeKind.Create, spec.ChangeKind);
            Assert.Null(spec.ExpectedRemoteObjectId);
            Assert.False(spec.Force);
        });
    }

    [Fact]
    public void Evaluate_classifies_create_update_and_noop_without_dropping_identity_support()
    {
        var local = new[]
        {
            Ref("refs/bugs/issue-1", 'a'),
            Ref("refs/identities/alice", 'b')
        };
        var remote = new[]
        {
            Ref("refs/bugs/issue-1", 'c'),
            Ref("refs/identities/alice", 'b')
        };

        var update = engine.Evaluate(new GitBugPublicationInput(SourceRevision, local, remote));

        Assert.Equal(GitBugPublicationStatus.Ready, update.Status);
        Assert.Equal(2, update.Plan!.RefSpecs.Count);
        var updateSpec = update.Plan.RefSpecs.Single(spec => spec.SourceRef == "refs/bugs/issue-1");
        Assert.Equal(GitBugPublicationChangeKind.Update, updateSpec.ChangeKind);
        Assert.Equal(new string('c', 40), updateSpec.ExpectedRemoteObjectId);
        Assert.False(updateSpec.Force);
        var identitySpec = update.Plan.RefSpecs.Single(spec => spec.SourceRef == "refs/identities/alice");
        Assert.Equal(GitBugPublicationChangeKind.Update, identitySpec.ChangeKind);
        Assert.Equal(new string('b', 40), identitySpec.ExpectedRemoteObjectId);
        Assert.Equal(2, update.Plan.BoundedRefCount);

        var noop = engine.Evaluate(new GitBugPublicationInput(SourceRevision, local, local));

        Assert.Equal(GitBugPublicationStatus.NoChanges, noop.Status);
        Assert.Null(noop.Plan);
        Assert.Null(noop.FailureKind);
    }

    [Fact]
    public void Evaluate_rejects_unrelated_ref_namespaces_before_planning()
    {
        var result = engine.Evaluate(new GitBugPublicationInput(
            SourceRevision,
            [Ref("refs/heads/main", 'a')],
            []));

        Assert.Equal(GitBugPublicationStatus.Unsupported, result.Status);
        Assert.Equal(GitBugPublicationFailureKind.UnsupportedRef, result.FailureKind);
        Assert.Null(result.Plan);
    }

    [Theory]
    [InlineData("refs/bugs/issue-1", "not-an-object-id")]
    [InlineData("refs/bugs/issue\n1", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("refs/identities/alice", "gggggggggggggggggggggggggggggggggggggggg")]
    public void Evaluate_rejects_malformed_ref_names_and_object_ids(string refName, string objectId)
    {
        var result = engine.Evaluate(new GitBugPublicationInput(
            SourceRevision,
            [new GitBugPublicationRefState(refName, objectId)],
            []));

        Assert.Equal(GitBugPublicationStatus.Malformed, result.Status);
        Assert.Equal(GitBugPublicationFailureKind.InvalidInput, result.FailureKind);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Evaluate_requires_identity_support_when_an_issue_is_published()
    {
        var result = engine.Evaluate(new GitBugPublicationInput(
            SourceRevision,
            [Ref("refs/bugs/issue-1", 'a')],
            []));

        Assert.Equal(GitBugPublicationStatus.Malformed, result.Status);
        Assert.Equal(GitBugPublicationFailureKind.MissingIdentitySupport, result.FailureKind);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Evaluate_rejects_remote_ahead_refs_instead_of_planning_deletion_or_force()
    {
        var result = engine.Evaluate(new GitBugPublicationInput(
            SourceRevision,
            [Ref("refs/identities/alice", 'a')],
            [Ref("refs/identities/alice", 'a'), Ref("refs/bugs/remote-only", 'b')]));

        Assert.Equal(GitBugPublicationStatus.Stale, result.Status);
        Assert.Equal(GitBugPublicationFailureKind.RemoteAhead, result.FailureKind);
        Assert.Null(result.Plan);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-source-revision")]
    public void Evaluate_rejects_stale_or_missing_source_revision(string? sourceRevision)
    {
        var result = engine.Evaluate(new GitBugPublicationInput(
            sourceRevision!,
            [Ref("refs/identities/alice", 'a')],
            []));

        Assert.Equal(GitBugPublicationStatus.Stale, result.Status);
        Assert.Equal(GitBugPublicationFailureKind.StaleReview, result.FailureKind);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Evaluate_enforces_bounded_local_and_remote_snapshots()
    {
        var tooManyLocalRefs = Enumerable.Range(0, 101)
            .Select(index => Ref($"refs/identities/identity-{index:D3}", 'a'))
            .ToArray();
        var tooManyRemoteRefs = tooManyLocalRefs;

        var localResult = engine.Evaluate(new GitBugPublicationInput(SourceRevision, tooManyLocalRefs, []));
        var remoteResult = engine.Evaluate(new GitBugPublicationInput(SourceRevision, [tooManyLocalRefs[0]], tooManyRemoteRefs));

        Assert.Equal(GitBugPublicationFailureKind.BoundExceeded, localResult.FailureKind);
        Assert.Equal(GitBugPublicationFailureKind.BoundExceeded, remoteResult.FailureKind);
        Assert.Equal(GitBugPublicationStatus.Malformed, localResult.Status);
        Assert.Equal(GitBugPublicationStatus.Malformed, remoteResult.Status);
    }

    [Fact]
    public void Evaluate_denies_force_updates_and_emits_only_exact_non_force_specs()
    {
        var result = engine.Evaluate(new GitBugPublicationInput(
            SourceRevision,
            [Ref("refs/identities/alice", 'b')],
            [Ref("refs/identities/alice", 'a')]));

        Assert.Equal(GitBugPublicationStatus.Ready, result.Status);
        var spec = Assert.Single(result.Plan!.RefSpecs);
        Assert.Equal(GitBugPublicationChangeKind.Update, spec.ChangeKind);
        Assert.False(spec.Force);
        Assert.NotEqual(GitBugPublicationFailureKind.ForceUpdateRequired, result.FailureKind);
    }

    [Fact]
    public void Evaluate_keeps_identity_support_in_a_changed_issue_plan_when_identity_is_unchanged()
    {
        var result = engine.Evaluate(new GitBugPublicationInput(
            SourceRevision,
            [
                Ref("refs/bugs/issue-1", 'b'),
                Ref("refs/identities/alice", 'a')
            ],
            [
                Ref("refs/bugs/issue-1", 'a'),
                Ref("refs/identities/alice", 'a')
            ]));

        Assert.Equal(GitBugPublicationStatus.Ready, result.Status);
        Assert.Equal(
            ["refs/bugs/issue-1", "refs/identities/alice"],
            result.Plan!.RefSpecs.Select(spec => spec.SourceRef));
        var identitySpec = result.Plan.RefSpecs.Single(spec => spec.Kind == GitBugPublicationRefKind.Identity);
        Assert.Equal(GitBugPublicationChangeKind.Update, identitySpec.ChangeKind);
        Assert.Equal(identitySpec.LocalObjectId, identitySpec.ExpectedRemoteObjectId);
    }

    private static GitBugPublicationRefState Ref(string refName, char objectIdPrefix) =>
        new(refName, new string(objectIdPrefix, 40));
}
