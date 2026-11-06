using AlmToo.Accessor.BrowserGitAccessor.Interface;

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
