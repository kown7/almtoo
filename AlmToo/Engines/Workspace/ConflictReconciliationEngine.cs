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
