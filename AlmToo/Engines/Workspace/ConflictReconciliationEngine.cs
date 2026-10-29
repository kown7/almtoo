using AlmToo.Managers.Workspace.Interface;

namespace AlmToo.Engines.Workspace;

/// <summary>
/// Deterministic policy for classifying local-versus-remote file changes.
/// It has no browser, transport, or Manager lifecycle dependency.
/// </summary>
public sealed class ConflictReconciliationEngine
{
    private const string InvalidPath = "/<invalid-path>";

    public WorkspaceReconciliationResult Evaluate(WorkspaceReconciliationInput? input)
    {
        if (input is null)
        {
            return new WorkspaceReconciliationResult(
                WorkspaceReconciliationStatus.Unavailable,
                Array.Empty<WorkspaceReconciliationChange>(),
                "The remote comparison is unavailable.",
                RequiresExplicitApply: false,
                FailureKind: WorkspaceReconciliationFailureKind.RemoteUnavailable);
        }

        if (input.Files is null)
        {
            return InvalidResult(
                "Reconciliation input is malformed.",
                WorkspaceReconciliationFailureKind.MalformedInput);
        }

        if (input.Files.Count > WorkspaceReconciliationContract.MaximumFiles)
        {
            return InvalidResult(
                "The reconciliation input exceeds the supported file limit.",
                WorkspaceReconciliationFailureKind.MalformedInput);
        }

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var changes = new List<WorkspaceReconciliationChange>(input.Files.Count);

        foreach (var file in input.Files)
        {
            changes.Add(EvaluateFile(file, seenPaths));
        }

        if (changes.Any(change => change.Kind == WorkspaceReconciliationChangeKind.Malformed))
        {
            return new WorkspaceReconciliationResult(
                WorkspaceReconciliationStatus.Failed,
                changes,
                "Some reconciliation inputs are malformed.",
                RequiresExplicitApply: false,
                FailureKind: WorkspaceReconciliationFailureKind.MalformedInput);
        }

        if (changes.Any(change => change.Kind == WorkspaceReconciliationChangeKind.Unsupported))
        {
            return new WorkspaceReconciliationResult(
                WorkspaceReconciliationStatus.Unsupported,
                changes,
                "Some repository changes use an unsupported format.",
                RequiresExplicitApply: false,
                FailureKind: WorkspaceReconciliationFailureKind.UnsupportedFormat);
        }

        var requiresResolution = changes.Any(change => change.Kind == WorkspaceReconciliationChangeKind.Divergent);
        return new WorkspaceReconciliationResult(
            requiresResolution ? WorkspaceReconciliationStatus.RequiresResolution : WorkspaceReconciliationStatus.Ready,
            changes,
            requiresResolution
                ? "Review the divergent files before applying reconciliation."
                : "Remote changes are ready for review.",
            RequiresExplicitApply: requiresResolution,
            FailureKind: WorkspaceReconciliationFailureKind.None);
    }

    /// <summary>
    /// Validates one explicit selection against the exact review that produced it and returns a
    /// deterministic, path-ordered plan. This method has no I/O and must run before an Accessor call.
    /// </summary>
    public WorkspaceReconciliationApplyPlanResult BuildApplyPlan(
        WorkspaceReconciliationResult? review,
        WorkspaceReconciliationApplyRequest? request)
    {
        if (!WorkspaceReconciliationContract.IsValidResult(review)
            || !WorkspaceReconciliationContract.IsValidApplyRequest(request))
        {
            return Rejected(
                "The reconciliation apply request is malformed.",
                WorkspaceReconciliationFailureKind.MalformedInput);
        }

        if (review!.ApplyMetadata is null
            || review.ApplyMetadata.BoundedChangeCount != review.Changes.Count)
        {
            return Rejected(
                "The reconciliation review is missing valid apply metadata.",
                WorkspaceReconciliationFailureKind.StaleReview);
        }

        if (!string.Equals(review.ApplyMetadata.ReviewId, request!.ReviewId, StringComparison.Ordinal)
            || !string.Equals(review.ApplyMetadata.SourceRevision, request.SourceRevision, StringComparison.Ordinal))
        {
            return Rejected(
                "The reconciliation review no longer matches the selected source.",
                WorkspaceReconciliationFailureKind.StaleReview);
        }

        if (review.FailureKind is not null
            && review.FailureKind != WorkspaceReconciliationFailureKind.None)
        {
            return Rejected(
                "The reconciliation review is not applicable for apply.",
                review.FailureKind.Value);
        }

        if (review.Status is not WorkspaceReconciliationStatus.Ready
            and not WorkspaceReconciliationStatus.RequiresResolution)
        {
            return Rejected(
                "The reconciliation review is not ready for apply.",
                WorkspaceReconciliationFailureKind.ApplyRejected);
        }

        if (review.Changes.Any(change => !HasExpectedResolutions(change)))
        {
            return Rejected(
                "The reconciliation review contains contradictory resolution choices.",
                WorkspaceReconciliationFailureKind.StaleReview);
        }

        var applicable = review.Changes
            .Where(change => change.AllowedResolutions.Count > 0)
            .ToArray();
        var applicableByPath = applicable.ToDictionary(change => change.Path, StringComparer.Ordinal);
        var selectedByPath = request.Selections.ToDictionary(selection => selection.Path, StringComparer.Ordinal);

        if (selectedByPath.Count != applicableByPath.Count
            || applicableByPath.Keys.Any(path => !selectedByPath.ContainsKey(path)))
        {
            return Rejected(
                "Select one supported resolution for every applicable change.",
                WorkspaceReconciliationFailureKind.ApplyRejected);
        }

        foreach (var selection in selectedByPath.Values)
        {
            var change = applicableByPath[selection.Path];
            if (!change.AllowedResolutions.Contains(selection.Resolution))
            {
                return Rejected(
                    "One selected reconciliation resolution is not supported.",
                    WorkspaceReconciliationFailureKind.ApplyRejected);
            }
        }

        var preconditionsByPath = request.RemotePreconditions
            .ToDictionary(precondition => precondition.Path, StringComparer.Ordinal);
        if (preconditionsByPath.Count != applicableByPath.Count
            || applicableByPath.Keys.Any(path => !preconditionsByPath.ContainsKey(path)))
        {
            return Rejected(
                "Remote preconditions must cover every applicable change.",
                WorkspaceReconciliationFailureKind.ApplyRejected);
        }

        if ((review.RequiresExplicitApply || request.Selections.Count > 0)
            && !request.ExplicitlyConfirmed)
        {
            return Rejected(
                "Explicit confirmation is required before applying reconciliation.",
                WorkspaceReconciliationFailureKind.ApplyRejected);
        }

        var plan = new WorkspaceReconciliationApplyPlan(
            review.ApplyMetadata with { ExplicitlyConfirmed = request.ExplicitlyConfirmed },
            selectedByPath.Values.OrderBy(selection => selection.Path, StringComparer.Ordinal).ToArray(),
            preconditionsByPath.Values.OrderBy(precondition => precondition.Path, StringComparer.Ordinal).ToArray());

        return new WorkspaceReconciliationApplyPlanResult(
            Succeeded: true,
            "The reviewed reconciliation apply plan is ready.",
            WorkspaceReconciliationFailureKind.None,
            plan);
    }

    private static WorkspaceReconciliationApplyPlanResult Rejected(
        string message,
        WorkspaceReconciliationFailureKind failureKind) =>
        new(
            Succeeded: false,
            message,
            failureKind,
            Plan: null);

    private static bool HasExpectedResolutions(WorkspaceReconciliationChange change) =>
        change.AllowedResolutions.SequenceEqual(
            change.Kind switch
            {
                WorkspaceReconciliationChangeKind.LocalOnly or
                    WorkspaceReconciliationChangeKind.ModifiedLocally =>
                    [WorkspaceReconciliationResolution.KeepLocal],
                WorkspaceReconciliationChangeKind.RemoteOnly or
                    WorkspaceReconciliationChangeKind.ModifiedRemotely =>
                    [WorkspaceReconciliationResolution.AcceptRemote],
                WorkspaceReconciliationChangeKind.Divergent =>
                    [WorkspaceReconciliationResolution.KeepLocal, WorkspaceReconciliationResolution.AcceptRemote],
                _ => Array.Empty<WorkspaceReconciliationResolution>()
            });

    private static WorkspaceReconciliationChange EvaluateFile(
        WorkspaceFileComparison? file,
        ISet<string> seenPaths)
    {
        if (file is null || !WorkspaceReconciliationContract.IsValidComparison(file))
        {
            return Malformed(SafePath(file?.Path));
        }

        if (!seenPaths.Add(file.Path))
        {
            return Malformed(file.Path);
        }

        if (!file.IsSupported)
        {
            return new WorkspaceReconciliationChange(
                file.Path,
                WorkspaceReconciliationChangeKind.Unsupported,
                Array.Empty<WorkspaceReconciliationResolution>());
        }

        var kind = Classify(file.BaseObjectId, file.LocalObjectId, file.RemoteObjectId);
        var resolutions = kind switch
        {
            WorkspaceReconciliationChangeKind.LocalOnly or
                WorkspaceReconciliationChangeKind.ModifiedLocally =>
                [WorkspaceReconciliationResolution.KeepLocal],
            WorkspaceReconciliationChangeKind.RemoteOnly or
                WorkspaceReconciliationChangeKind.ModifiedRemotely =>
                [WorkspaceReconciliationResolution.AcceptRemote],
            WorkspaceReconciliationChangeKind.Divergent =>
                [WorkspaceReconciliationResolution.KeepLocal, WorkspaceReconciliationResolution.AcceptRemote],
            _ => Array.Empty<WorkspaceReconciliationResolution>()
        };

        return new WorkspaceReconciliationChange(file.Path, kind, resolutions);
    }

    private static WorkspaceReconciliationChangeKind Classify(
        string? baseObjectId,
        string? localObjectId,
        string? remoteObjectId)
    {
        if (baseObjectId == localObjectId && baseObjectId == remoteObjectId)
        {
            return WorkspaceReconciliationChangeKind.Unchanged;
        }

        if (baseObjectId == localObjectId)
        {
            return WorkspaceReconciliationChangeKind.RemoteOnly;
        }

        if (baseObjectId == remoteObjectId)
        {
            return WorkspaceReconciliationChangeKind.LocalOnly;
        }

        if (localObjectId == remoteObjectId)
        {
            return WorkspaceReconciliationChangeKind.Converged;
        }

        return WorkspaceReconciliationChangeKind.Divergent;
    }

    private static WorkspaceReconciliationChange Malformed(string path) =>
        new(
            path,
            WorkspaceReconciliationChangeKind.Malformed,
            Array.Empty<WorkspaceReconciliationResolution>());

    private static WorkspaceReconciliationResult InvalidResult(
        string message,
        WorkspaceReconciliationFailureKind failureKind) =>
        new(
            WorkspaceReconciliationStatus.Failed,
            Array.Empty<WorkspaceReconciliationChange>(),
            message,
            RequiresExplicitApply: false,
            FailureKind: failureKind);

    private static string SafePath(string? path)
    {
        if (path is null)
        {
            return InvalidPath;
        }

        return WorkspaceReconciliationContract.IsValidComparison(
            new WorkspaceFileComparison(path, null, null, null))
            ? path
            : InvalidPath;
    }
}
