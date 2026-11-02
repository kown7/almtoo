using AlmToo.Managers.Workspace.Interface;
using ManagerGitBugPublicationRefKind = AlmToo.Managers.Workspace.Interface.GitBugPublicationRefKind;

namespace AlmToo.Engines.Workspace;

/// <summary>
/// Deterministic policy for reviewing the exact Git-Bug refs that may be published.
/// The engine has no repository, browser, credential, transport, or filesystem dependency.
/// </summary>
public sealed class GitBugPublicationEngine
{
    private const int MaximumRefs = 100;
    private const int MaximumRefNameLength = 256;
    /// <summary>
    /// Reviews a local Git-Bug ref snapshot against the advertised remote refs.
    /// The returned ref specs are the only records a Manager may pass through to publication.
    /// </summary>
    public GitBugPublicationPolicyResult Evaluate(GitBugPublicationInput? input)
    {
        if (input is null)
        {
            return Reject(
                GitBugPublicationStatus.Unavailable,
                GitBugPublicationFailureKind.InvalidInput,
                "The Git-Bug publication snapshot is unavailable.");
        }

        if (!IsObjectId(input.SourceRevision))
        {
            return Reject(
                GitBugPublicationStatus.Stale,
                GitBugPublicationFailureKind.StaleReview,
                "The Git-Bug publication source revision is stale or malformed.");
        }

        if (input.LocalRefs is null || input.RemoteRefs is null)
        {
            return Reject(
                GitBugPublicationStatus.Malformed,
                GitBugPublicationFailureKind.InvalidInput,
                "The Git-Bug publication snapshot is malformed.");
        }

        if (input.LocalRefs.Count > MaximumRefs
            || input.RemoteRefs.Count > MaximumRefs)
        {
            return Reject(
                GitBugPublicationStatus.Malformed,
                GitBugPublicationFailureKind.BoundExceeded,
                "The Git-Bug publication snapshot exceeds the supported ref bound.");
        }

        var local = new Dictionary<string, GitBugPublicationRefState>(StringComparer.Ordinal);
        var remote = new Dictionary<string, GitBugPublicationRefState>(StringComparer.Ordinal);

        var localValidation = ValidateRefs(input.LocalRefs, local);
        if (localValidation is not null)
        {
            return localValidation;
        }

        var remoteValidation = ValidateRefs(input.RemoteRefs, remote);
        if (remoteValidation is not null)
        {
            return remoteValidation;
        }

        if (local.Values.Any(IsIssue)
            && !local.Values.Any(IsIdentity))
        {
            return Reject(
                GitBugPublicationStatus.Malformed,
                GitBugPublicationFailureKind.MissingIdentitySupport,
                "Issue refs require at least one local identity support ref.");
        }

        if (remote.Keys.Any(refName => !local.ContainsKey(refName)))
        {
            return Reject(
                GitBugPublicationStatus.Stale,
                GitBugPublicationFailureKind.RemoteAhead,
                "The advertised remote contains Git-Bug refs that are absent from the local review source.");
        }

        var refSpecs = new List<GitBugPublicationRefSpec>();
        foreach (var localRef in local.Values.OrderBy(reference => reference.RefName, StringComparer.Ordinal))
        {
            if (!remote.TryGetValue(localRef.RefName, out var remoteRef))
            {
                refSpecs.Add(CreateRefSpec(localRef, remoteObjectId: null));
                continue;
            }

            if (string.Equals(localRef.ObjectId, remoteRef.ObjectId, StringComparison.Ordinal))
            {
                continue;
            }

            refSpecs.Add(CreateRefSpec(localRef, remoteRef.ObjectId));
        }

        if (refSpecs.Count == 0)
        {
            return new GitBugPublicationPolicyResult(
                GitBugPublicationStatus.NoChanges,
                "The local Git-Bug refs already match the advertised remote refs.");
        }

        if (refSpecs.Count > MaximumRefs)
        {
            return Reject(
                GitBugPublicationStatus.Malformed,
                GitBugPublicationFailureKind.BoundExceeded,
                "The Git-Bug publication plan exceeds the supported ref bound.");
        }

        var plan = new GitBugPublicationPlan(
            input.SourceRevision,
            refSpecs,
            refSpecs.Count,
            RequiresExplicitConfirmation: true);

        return new GitBugPublicationPolicyResult(
            GitBugPublicationStatus.Ready,
            "The Git-Bug refs are ready for explicit publication review.",
            plan);
    }

    private static GitBugPublicationRefSpec CreateRefSpec(
        GitBugPublicationRefState localRef,
        string? remoteObjectId)
    {
        var kind = localRef.RefName.StartsWith("refs/bugs/", StringComparison.Ordinal)
            ? ManagerGitBugPublicationRefKind.Issue
            : ManagerGitBugPublicationRefKind.Identity;

        return new GitBugPublicationRefSpec(
            localRef.RefName,
            localRef.RefName,
            localRef.ObjectId,
            remoteObjectId,
            kind,
            remoteObjectId is null
                ? GitBugPublicationChangeKind.Create
                : GitBugPublicationChangeKind.Update,
            Force: false);
    }

    private static GitBugPublicationPolicyResult? ValidateRefs(
        IReadOnlyList<GitBugPublicationRefState> refs,
        IDictionary<string, GitBugPublicationRefState> destination)
    {
        foreach (var reference in refs)
        {
            if (reference is null)
            {
                return Reject(
                    GitBugPublicationStatus.Malformed,
                    GitBugPublicationFailureKind.InvalidInput,
                    "The Git-Bug publication snapshot contains a null ref.");
            }

            var kind = GetKind(reference.RefName);
            if (kind is null)
            {
                return Reject(
                    GitBugPublicationStatus.Unsupported,
                    GitBugPublicationFailureKind.UnsupportedRef,
                    "The Git-Bug publication snapshot contains an unsupported ref namespace.");
            }

            if (!IsValidGitBugRef(reference, kind.Value))
            {
                return Reject(
                    GitBugPublicationStatus.Malformed,
                    GitBugPublicationFailureKind.InvalidInput,
                    "The Git-Bug publication snapshot contains a malformed ref.");
            }

            if (!destination.TryAdd(reference.RefName, reference))
            {
                return Reject(
                    GitBugPublicationStatus.Malformed,
                    GitBugPublicationFailureKind.InvalidInput,
                    "The Git-Bug publication snapshot contains duplicate refs.");
            }
        }

        return null;
    }

    private static ManagerGitBugPublicationRefKind? GetKind(string? refName)
    {
        if (refName is null)
        {
            return null;
        }

        if (refName.StartsWith("refs/bugs/", StringComparison.Ordinal))
        {
            return ManagerGitBugPublicationRefKind.Issue;
        }

        if (refName.StartsWith("refs/identities/", StringComparison.Ordinal))
        {
            return ManagerGitBugPublicationRefKind.Identity;
        }

        return null;
    }

    private static bool IsIssue(GitBugPublicationRefState reference) =>
        reference.RefName.StartsWith("refs/bugs/", StringComparison.Ordinal);

    private static bool IsIdentity(GitBugPublicationRefState reference) =>
        reference.RefName.StartsWith("refs/identities/", StringComparison.Ordinal);

    private static bool IsValidGitBugRef(
        GitBugPublicationRefState reference,
        ManagerGitBugPublicationRefKind kind) =>
        IsAllowedRefName(reference.RefName)
        && IsObjectId(reference.ObjectId)
        && ((kind == ManagerGitBugPublicationRefKind.Issue
                && reference.RefName.StartsWith("refs/bugs/", StringComparison.Ordinal))
            || (kind == ManagerGitBugPublicationRefKind.Identity
                && reference.RefName.StartsWith("refs/identities/", StringComparison.Ordinal)));

    private static bool IsAllowedRefName(string? value) =>
        value is not null
        && value.Length > 0
        && value.Length <= MaximumRefNameLength
        && value.IndexOfAny(['\\', '\0', '\r', '\n', ' ', '\t', '~', '^', ':', '?', '*', '[']) < 0
        && !value.StartsWith("/", StringComparison.Ordinal)
        && !value.EndsWith("/", StringComparison.Ordinal)
        && !value.Contains("//", StringComparison.Ordinal)
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains("@{", StringComparison.Ordinal);

    private static bool IsObjectId(string? value) =>
        value is not null
        && value.Length is 40 or 64
        && value.All(Uri.IsHexDigit);

    private static GitBugPublicationPolicyResult Reject(
        GitBugPublicationStatus status,
        GitBugPublicationFailureKind failureKind,
        string message) =>
        new(status, message, Plan: null, failureKind);
}
