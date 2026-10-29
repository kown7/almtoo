using AlmToo.Engines.Workspace;
using AlmToo.Managers.Workspace.Interface;
using Xunit;

namespace AlmToo.Tests.Engines.Workspace;

public sealed class ConflictReconciliationEngineTests
{
    private readonly ConflictReconciliationEngine engine = new();

    [Fact]
    public void Classifies_clean_local_remote_converged_and_divergent_paths()
    {
        var result = engine.Evaluate(new WorkspaceReconciliationInput(
        [
            new("/clean.txt", "a", "a", "a"),
            new("/local.txt", "a", "b", "a"),
            new("/remote.txt", "a", "a", "b"),
            new("/converged.txt", "a", "b", "b"),
            new("/divergent.txt", "a", "b", "c")
        ]));

        Assert.Equal(WorkspaceReconciliationStatus.RequiresResolution, result.Status);
        Assert.Equal(WorkspaceReconciliationFailureKind.None, result.FailureKind);
        Assert.Equal(5, result.Changes.Count);
        Assert.Equal(WorkspaceReconciliationChangeKind.Unchanged, result.Changes[0].Kind);
        Assert.Equal(WorkspaceReconciliationChangeKind.LocalOnly, result.Changes[1].Kind);
        Assert.Equal(WorkspaceReconciliationChangeKind.RemoteOnly, result.Changes[2].Kind);
        Assert.Equal(WorkspaceReconciliationChangeKind.Converged, result.Changes[3].Kind);
        Assert.Equal(WorkspaceReconciliationChangeKind.Divergent, result.Changes[4].Kind);
        Assert.Empty(result.Changes[0].AllowedResolutions);
        Assert.Equal(
            [WorkspaceReconciliationResolution.KeepLocal, WorkspaceReconciliationResolution.AcceptRemote],
            result.Changes[4].AllowedResolutions);
        Assert.True(result.RequiresExplicitApply);
        Assert.True(WorkspaceReconciliationContract.IsValidResult(result));
    }

    [Fact]
    public void Allows_only_the_safe_resolution_for_one_sided_changes()
    {
        var result = engine.Evaluate(new WorkspaceReconciliationInput(
        [
            new("/local.txt", "a", "b", "a"),
            new("/remote.txt", "a", "a", "b")
        ]));

        Assert.Equal(WorkspaceReconciliationStatus.Ready, result.Status);
        Assert.Equal(WorkspaceReconciliationFailureKind.None, result.FailureKind);
        Assert.Equal([WorkspaceReconciliationResolution.KeepLocal], result.Changes[0].AllowedResolutions);
        Assert.Equal([WorkspaceReconciliationResolution.AcceptRemote], result.Changes[1].AllowedResolutions);
        Assert.False(result.RequiresExplicitApply);
    }

    [Fact]
    public void Keeps_resolution_empty_for_unchanged_and_converged_paths()
    {
        var result = engine.Evaluate(new WorkspaceReconciliationInput(
        [
            new("/clean.txt", "a", "a", "a"),
            new("/converged.txt", "a", "b", "b")
        ]));

        Assert.All(result.Changes, change => Assert.Empty(change.AllowedResolutions));
        Assert.False(result.RequiresExplicitApply);
        Assert.Equal(WorkspaceReconciliationStatus.Ready, result.Status);
    }

    [Fact]
    public void Treats_unsupported_and_malformed_inputs_as_bounded_failures()
    {
        var unsupported = engine.Evaluate(new WorkspaceReconciliationInput(
        [new("/binary.bin", "a", "b", "c", IsSupported: false)]));
        var malformed = engine.Evaluate(new WorkspaceReconciliationInput(
        [
            new("../escape.txt", "a", "b", "c"),
            new("/duplicate.txt", "a", "b", "c"),
            new("/duplicate.txt", "a", "b", "c"),
            new("/bad-id.txt", " ", "b", "c")
        ]));

        Assert.Equal(WorkspaceReconciliationStatus.Unsupported, unsupported.Status);
        Assert.Equal(WorkspaceReconciliationFailureKind.UnsupportedFormat, unsupported.FailureKind);
        Assert.All(unsupported.Changes, change => Assert.Equal(WorkspaceReconciliationChangeKind.Unsupported, change.Kind));
        Assert.Equal(WorkspaceReconciliationStatus.Failed, malformed.Status);
        Assert.Equal(WorkspaceReconciliationFailureKind.MalformedInput, malformed.FailureKind);
        Assert.Contains(malformed.Changes, change => change.Kind == WorkspaceReconciliationChangeKind.Malformed);
        Assert.DoesNotContain("exception", malformed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(WorkspaceReconciliationContract.IsValidResult(malformed));
    }

    [Fact]
    public void Returns_unavailable_for_missing_comparison_and_never_throws_for_null_collections()
    {
        var unavailable = engine.Evaluate(null);
        var malformedCollection = engine.Evaluate(new WorkspaceReconciliationInput(null!));
        var malformedEntry = engine.Evaluate(new WorkspaceReconciliationInput(
            new[] { (WorkspaceFileComparison)null! }));

        Assert.Equal(WorkspaceReconciliationStatus.Unavailable, unavailable.Status);
        Assert.Equal(WorkspaceReconciliationFailureKind.RemoteUnavailable, unavailable.FailureKind);
        Assert.False(unavailable.RequiresExplicitApply);
        Assert.Equal(WorkspaceReconciliationStatus.Failed, malformedCollection.Status);
        Assert.Equal(WorkspaceReconciliationFailureKind.MalformedInput, malformedCollection.FailureKind);
        Assert.Equal(WorkspaceReconciliationStatus.Failed, malformedEntry.Status);
        Assert.Equal(WorkspaceReconciliationChangeKind.Malformed, Assert.Single(malformedEntry.Changes).Kind);
        Assert.True(WorkspaceReconciliationContract.IsValidResult(unavailable));
        Assert.True(WorkspaceReconciliationContract.IsValidResult(malformedEntry));
    }

    [Fact]
    public void Rejects_oversized_duplicate_and_unsafe_paths_with_bounded_safe_outputs()
    {
        var oversized = engine.Evaluate(new WorkspaceReconciliationInput(
            Enumerable.Range(0, WorkspaceReconciliationContract.MaximumFiles + 1)
                .Select(index => new WorkspaceFileComparison($"/file-{index}.txt", "a", "b", "a"))
                .ToArray()));
        var duplicate = engine.Evaluate(new WorkspaceReconciliationInput(
        [
            new("/duplicate.txt", "a", "b", "a"),
            new("/duplicate.txt", "a", "a", "b")
        ]));
        var unsafePath = engine.Evaluate(new WorkspaceReconciliationInput(
        [new("/folder/\tfile.txt", null, null, "remote")]));

        Assert.Equal(WorkspaceReconciliationFailureKind.MalformedInput, oversized.FailureKind);
        Assert.Empty(oversized.Changes);
        Assert.Equal(WorkspaceReconciliationChangeKind.Malformed, duplicate.Changes[1].Kind);
        Assert.Equal(WorkspaceReconciliationFailureKind.MalformedInput, duplicate.FailureKind);
        Assert.Equal(WorkspaceReconciliationChangeKind.Malformed, Assert.Single(unsafePath.Changes).Kind);
        Assert.Equal("/<invalid-path>", unsafePath.Changes[0].Path);
        Assert.True(WorkspaceReconciliationContract.IsValidResult(duplicate));
        Assert.True(WorkspaceReconciliationContract.IsValidResult(unsafePath));
    }
}
