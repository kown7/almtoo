using AlmToo.Accessor.BrowserGitAccessor.Interface;
using AlmToo.Managers.Workspace.Interface;

namespace AlmToo.Engines.Workspace;

/// <summary>
/// Deterministic, credential-free policy for deciding what a read-only Git-Bug diagnosis permits.
/// This Engine never invokes Git, JavaScript, transport, or repair operations.
/// </summary>
public sealed class GitBugRecoveryEngine
{
    public GitBugRecoveryDecision Evaluate(
        GitOperationResult<GitBugDiagnosisResult>? diagnosis)
    {
        if (diagnosis is null)
        {
            return Invalid("No Git-Bug diagnosis was supplied.");
        }

        if (diagnosis.Succeeded)
        {
            if (diagnosis.Value is null || !GitBugDiagnosisContract.IsValidResult(diagnosis.Value))
            {
                return Invalid("The Git-Bug diagnosis was not valid.");
            }

            return diagnosis.Value.Repairability == GitBugDiagnosisRepairability.Repairable
                ? new(
                    GitBugRecoveryStatus.ReadyToRepair,
                    "The diagnosis identifies a bounded repair candidate; no mutation was performed.",
                    diagnosis.Value,
                    null)
                : new(
                    GitBugRecoveryStatus.NotRepairable,
                    "The diagnosis does not identify a safe repair; no mutation was performed.",
                    diagnosis.Value,
                    null);
        }

        var failureKind = diagnosis.FailureKind ?? GitOperationFailureKind.Unknown;
        return failureKind switch
        {
            GitOperationFailureKind.GitBugDataUnavailable => Failure(
                GitBugRecoveryStatus.DataUnavailable,
                "Git-Bug data is unavailable.",
                failureKind),
            GitOperationFailureKind.GitBugFormatUnsupported => Failure(
                GitBugRecoveryStatus.FormatUnsupported,
                "This Git-Bug data format is not supported.",
                failureKind),
            GitOperationFailureKind.GitBugDataMalformed => Failure(
                GitBugRecoveryStatus.DataMalformed,
                "Git-Bug data could not be read safely.",
                failureKind),
            GitOperationFailureKind.GitBugDiagnosisCancelled => Failure(
                GitBugRecoveryStatus.Cancelled,
                "The Git-Bug diagnosis was canceled safely.",
                failureKind),
            GitOperationFailureKind.GitBugTransportFailure or GitOperationFailureKind.NetworkUnavailable => Failure(
                GitBugRecoveryStatus.TransportFailure,
                "The Git-Bug data source could not be reached safely.",
                failureKind),
            _ => Failure(
                GitBugRecoveryStatus.InvalidResult,
                "The Git-Bug diagnosis could not be evaluated safely.",
                failureKind)
        };
    }

    /// <summary>
    /// Builds a deterministic, source-bound repair preview. The optional observed revision is the
    /// caller's current ref observation and is deliberately compared before any Accessor mutation.
    /// </summary>
    public GitBugRepairPreviewResult BuildRepairPlan(
        GitBugRecoveryDecision? decision,
        string? observedSourceRevision = null)
    {
        if (decision is null
            || decision.Diagnosis is null
            || decision.Status == GitBugRecoveryStatus.InvalidResult
            || !GitBugDiagnosisContract.IsValidResult(decision.Diagnosis))
        {
            return RejectPreview(
                GitBugRepairPreviewStatus.Malformed,
                GitBugRepairRejectionReason.InvalidDiagnosis,
                "The Git-Bug repair diagnosis is not valid.");
        }

        var diagnosis = decision.Diagnosis;
        if (observedSourceRevision is not null
            && !string.Equals(observedSourceRevision, diagnosis.SourceRevision, StringComparison.Ordinal))
        {
            return RejectPreview(
                GitBugRepairPreviewStatus.Stale,
                GitBugRepairRejectionReason.StaleSourceRevision,
                "The Git-Bug repair source revision is stale.");
        }

        if (decision.Status == GitBugRecoveryStatus.NotRepairable
            || diagnosis.Repairability != GitBugDiagnosisRepairability.Repairable)
        {
            return RejectPreview(
                GitBugRepairPreviewStatus.NotRepairable,
                GitBugRepairRejectionReason.NotRepairable,
                "The diagnosis does not identify a safe Git-Bug repair.");
        }

        if (decision.Status != GitBugRecoveryStatus.ReadyToRepair)
        {
            return decision.Status switch
            {
                GitBugRecoveryStatus.FormatUnsupported => RejectPreview(
                    GitBugRepairPreviewStatus.Unsupported,
                    GitBugRepairRejectionReason.UnsupportedInvariant,
                    "The Git-Bug format is not supported for repair."),
                GitBugRecoveryStatus.DataMalformed => RejectPreview(
                    GitBugRepairPreviewStatus.Malformed,
                    GitBugRepairRejectionReason.InvalidDiagnosis,
                    "The Git-Bug diagnosis is malformed."),
                _ => RejectPreview(
                    GitBugRepairPreviewStatus.Stale,
                    GitBugRepairRejectionReason.InvalidDiagnosis,
                    "The Git-Bug repair diagnosis is not available for planning.")
            };
        }

        if (diagnosis.LatestValidParent is null)
        {
            return RejectPreview(
                GitBugRepairPreviewStatus.Malformed,
                GitBugRepairRejectionReason.MissingValidParent,
                "The repair diagnosis does not identify a valid parent.");
        }

        if (!GitBugRepairContract.IsRepairableInvariant(diagnosis.InvariantCode))
        {
            var ambiguous = diagnosis.InvariantCode == GitBugDiagnosisInvariantCode.AmbiguousHistory;
            return RejectPreview(
                ambiguous ? GitBugRepairPreviewStatus.Ambiguous : GitBugRepairPreviewStatus.Unsupported,
                ambiguous
                    ? GitBugRepairRejectionReason.AmbiguousHistory
                    : GitBugRepairRejectionReason.UnsupportedInvariant,
                ambiguous
                    ? "The Git-Bug history is ambiguous and cannot be repaired deterministically."
                    : "The Git-Bug invariant is not supported for deterministic repair.");
        }

        var operation = GitBugRepairContract.OperationFor(diagnosis.InvariantCode);
        var strategy = GitBugRepairContract.EditClockStrategyFor(diagnosis.InvariantCode);
        var backupRef = GitBugRepairContract.CreateBackupRef(
            diagnosis.SourceRevision,
            diagnosis.AffectedRef);
        if (operation is null || strategy is null)
        {
            return RejectPreview(
                GitBugRepairPreviewStatus.Unsupported,
                GitBugRepairRejectionReason.UnsupportedInvariant,
                "The Git-Bug repair operation is not supported.");
        }

        if (backupRef is null)
        {
            return RejectPreview(
                GitBugRepairPreviewStatus.Malformed,
                GitBugRepairRejectionReason.BackupRefNotAllowed,
                "The Git-Bug repair backup ref is not allowed.");
        }

        var plan = new GitBugRepairPlan(
            diagnosis.SourceRevision,
            diagnosis.AffectedRef,
            backupRef,
            ExpectedOldObjectId: diagnosis.SourceRevision,
            ExpectedNewObjectId: diagnosis.LatestValidParent,
            new GitBugRepairMutation(
                operation,
                new GitBugRepairEditClockChange(
                    strategy,
                    GitBugRepairContract.EditClockDeltaFor(diagnosis.InvariantCode))),
            RequiresExplicitConfirmation: true);

        return new GitBugRepairPreviewResult(
            GitBugRepairPreviewStatus.Ready,
            "The Git-Bug repair preview is ready for explicit confirmation.",
            plan);
    }

    private static GitBugRepairPreviewResult RejectPreview(
        GitBugRepairPreviewStatus status,
        GitBugRepairRejectionReason reason,
        string message) =>
        new(
            status,
            message,
            Plan: null,
            Idempotency: GitBugRepairIdempotencyState.NotApplied,
            RejectionReason: reason);

    private static GitBugRecoveryDecision Invalid(string message) =>
        new(GitBugRecoveryStatus.InvalidResult, message, null, GitOperationFailureKind.Unknown);

    private static GitBugRecoveryDecision Failure(
        GitBugRecoveryStatus status,
        string message,
        GitOperationFailureKind failureKind) =>
        new(status, message, null, failureKind);
}

public enum GitBugRecoveryStatus
{
    ReadyToRepair,
    NotRepairable,
    DataUnavailable,
    FormatUnsupported,
    DataMalformed,
    Cancelled,
    TransportFailure,
    InvalidResult
}

/// <summary>Safe policy output for a diagnosis; it contains no transport or credential diagnostics.</summary>
public sealed record GitBugRecoveryDecision(
    GitBugRecoveryStatus Status,
    string Message,
    GitBugDiagnosisResult? Diagnosis,
    GitOperationFailureKind? FailureKind);
