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

    [Theory]
    [InlineData(WorkspaceReconciliationResolution.KeepLocal)]
    [InlineData(WorkspaceReconciliationResolution.AcceptRemote)]
    public void Builds_a_deterministic_plan_for_each_supported_divergent_resolution(
        WorkspaceReconciliationResolution resolution)
    {
        var review = CreateReview(
            new WorkspaceFileComparison("/z.txt", "base-z", "local-z", "remote-z"),
            new WorkspaceFileComparison("/a.txt", "base-a", "local-a", "remote-a"));
        var request = new WorkspaceReconciliationApplyRequest(
            "review-1",
            "source-1",
            [
                new("/z.txt", resolution),
                new("/a.txt", resolution)
            ],
            [
                new("/z.txt", "remote-z"),
                new("/a.txt", "remote-a")
            ],
            ExplicitlyConfirmed: true);

        var result = engine.BuildApplyPlan(review, request);

        Assert.True(result.Succeeded);
        Assert.Equal(WorkspaceReconciliationFailureKind.None, result.FailureKind);
        var plan = Assert.IsType<WorkspaceReconciliationApplyPlan>(result.Plan);
        Assert.Equal(["/a.txt", "/z.txt"], plan.Selections.Select(selection => selection.Path));
        Assert.Equal(["/a.txt", "/z.txt"], plan.RemotePreconditions.Select(precondition => precondition.Path));
        Assert.All(plan.Selections, selection => Assert.Equal(resolution, selection.Resolution));
        Assert.True(plan.Review.ExplicitlyConfirmed);
        Assert.True(WorkspaceReconciliationContract.IsValidApplyPlan(plan));
        Assert.True(WorkspaceReconciliationContract.IsValidApplyPlanResult(result));
    }

    [Fact]
    public void Builds_a_plan_for_one_sided_changes_only_with_the_engine_supported_choices()
    {
        var review = CreateReview(
            new WorkspaceFileComparison("/local.txt", "base", "local", "base"),
            new WorkspaceFileComparison("/remote.txt", "base", "base", "remote"));
        var request = new WorkspaceReconciliationApplyRequest(
            "review-1",
            "source-1",
            [
                new("/remote.txt", WorkspaceReconciliationResolution.AcceptRemote),
                new("/local.txt", WorkspaceReconciliationResolution.KeepLocal)
            ],
            [new("/remote.txt", "remote"), new("/local.txt", "base")],
            ExplicitlyConfirmed: true);

        var result = engine.BuildApplyPlan(review, request);

        Assert.True(result.Succeeded);
        Assert.Equal(
            [WorkspaceReconciliationResolution.KeepLocal, WorkspaceReconciliationResolution.AcceptRemote],
            Assert.IsType<WorkspaceReconciliationApplyPlan>(result.Plan).Selections
                .Select(selection => selection.Resolution));
    }

    [Fact]
    public void Rejects_missing_confirmation_and_review_replays_without_an_accessor_plan()
    {
        var review = CreateReview(new WorkspaceFileComparison("/conflict.txt", "base", "local", "remote"));
        var request = new WorkspaceReconciliationApplyRequest(
            "review-1",
            "source-1",
            [new("/conflict.txt", WorkspaceReconciliationResolution.KeepLocal)],
            [new("/conflict.txt", "remote")],
            ExplicitlyConfirmed: false);

        var missingConfirmation = engine.BuildApplyPlan(review, request);
        var staleReview = engine.BuildApplyPlan(review, request with { ExplicitlyConfirmed = true, ReviewId = "review-2" });
        var staleSource = engine.BuildApplyPlan(review, request with { ExplicitlyConfirmed = true, SourceRevision = "source-2" });

        Assert.False(missingConfirmation.Succeeded);
        Assert.Equal(WorkspaceReconciliationFailureKind.ApplyRejected, missingConfirmation.FailureKind);
        Assert.False(staleReview.Succeeded);
        Assert.Equal(WorkspaceReconciliationFailureKind.StaleReview, staleReview.FailureKind);
        Assert.False(staleSource.Succeeded);
        Assert.Equal(WorkspaceReconciliationFailureKind.StaleReview, staleSource.FailureKind);
        Assert.Null(missingConfirmation.Plan);
        Assert.All(new[] { missingConfirmation, staleReview, staleSource }, result =>
        {
            Assert.True(WorkspaceReconciliationContract.IsValidApplyPlanResult(result));
            Assert.DoesNotContain("token", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("diagnostic", result.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Rejects_unknown_or_non_applicable_choices_and_missing_remote_preconditions()
    {
        var review = CreateReview(
            new WorkspaceFileComparison("/local.txt", "base", "local", "base"),
            new WorkspaceFileComparison("/unchanged.txt", "base", "base", "base"));
        var unknown = new WorkspaceReconciliationApplyRequest(
            "review-1",
            "source-1",
            [new("/local.txt", (WorkspaceReconciliationResolution)99)],
            [new("/local.txt", "base")],
            ExplicitlyConfirmed: true);
        var nonApplicable = unknown with
        {
            Selections = [new("/unchanged.txt", WorkspaceReconciliationResolution.KeepLocal)],
            RemotePreconditions = [new("/unchanged.txt", "base")]
        };
        var missingPrecondition = unknown with
        {
            Selections = [new("/local.txt", WorkspaceReconciliationResolution.KeepLocal)],
            RemotePreconditions = []
        };

        var unknownResult = engine.BuildApplyPlan(review, unknown);
        var nonApplicableResult = engine.BuildApplyPlan(review, nonApplicable);
        var missingPreconditionResult = engine.BuildApplyPlan(review, missingPrecondition);

        Assert.Equal(WorkspaceReconciliationFailureKind.MalformedInput, unknownResult.FailureKind);
        Assert.Equal(WorkspaceReconciliationFailureKind.ApplyRejected, nonApplicableResult.FailureKind);
        Assert.Equal(WorkspaceReconciliationFailureKind.ApplyRejected, missingPreconditionResult.FailureKind);
        Assert.All(new[] { unknownResult, nonApplicableResult, missingPreconditionResult }, result =>
            Assert.False(result.Succeeded));
    }

    [Fact]
    public void Rejects_reviews_without_metadata_or_failed_review_states()
    {
        var evaluated = engine.Evaluate(new WorkspaceReconciliationInput(
            [new WorkspaceFileComparison("/remote.txt", "base", "base", "remote")]));
        var request = new WorkspaceReconciliationApplyRequest(
            "review-1",
            "source-1",
            [new("/remote.txt", WorkspaceReconciliationResolution.AcceptRemote)],
            [new("/remote.txt", "remote")],
            ExplicitlyConfirmed: true);

        var missingMetadata = engine.BuildApplyPlan(evaluated, request);
        var failedReview = engine.BuildApplyPlan(
            evaluated with
            {
                ApplyMetadata = new WorkspaceReconciliationApplyMetadata("review-1", "source-1", 1, DateTimeOffset.UtcNow),
                FailureKind = WorkspaceReconciliationFailureKind.RemoteUnavailable,
                Status = WorkspaceReconciliationStatus.Unavailable
            },
            request);

        Assert.Equal(WorkspaceReconciliationFailureKind.StaleReview, missingMetadata.FailureKind);
        Assert.Equal(WorkspaceReconciliationFailureKind.RemoteUnavailable, failedReview.FailureKind);
        Assert.All(new[] { missingMetadata, failedReview }, result => Assert.False(result.Succeeded));
    }

    private WorkspaceReconciliationResult CreateReview(params WorkspaceFileComparison[] files)
    {
        var evaluated = engine.Evaluate(new WorkspaceReconciliationInput(files));
        return evaluated with
        {
            ApplyMetadata = new WorkspaceReconciliationApplyMetadata(
                "review-1",
                "source-1",
                evaluated.Changes.Count,
                DateTimeOffset.UtcNow)
        };
    }
}
