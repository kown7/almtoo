using System.Net;

namespace AlmToo.Accessor.BrowserGitAccessor.Interface;

public record GitOperationResult(
    string Operation,
    bool Succeeded,
    string Message,
    string? Diagnostic = null,
    GitOperationFailureKind? FailureKind = null);

public record GitOperationResult<T>(
    string Operation,
    bool Succeeded,
    string Message,
    T? Value = default,
    string? Diagnostic = null,
    GitOperationFailureKind? FailureKind = null);

public enum GitOperationFailureKind
{
    CredentialRejected,
    RemoteAhead,
    NetworkUnavailable,
    UnsupportedRef,
    GitBugDataUnavailable,
    GitBugFormatUnsupported,
    GitBugDataMalformed,
    GitBugWriteInvalidRequest,
    GitBugWritePreconditionFailed,
    GitBugWriteConflict,
    GitBugWriteUnsupportedFormat,
    GitBugWriteUnavailable,
    GitBugWriteSerializationFailed,
    GitBugWriteUnknown,
    BranchHistoryDataUnavailable,
    BranchHistoryDataMalformed,
    BranchHistoryInvalidRequest,
    BranchHistoryUnsupportedReference,
    BranchHistoryLimitExceeded,
    BranchHistoryUnknown,
    BranchSwitchInvalidRequest,
    BranchSwitchDirtyWorktree,
    BranchSwitchUnsupportedReference,
    BranchSwitchUnavailable,
    BranchSwitchUnknown,
    Unknown
}

/// <summary>Stable operation names for the Git-Bug write boundary.</summary>
public static class GitBugWriteOperations
{
    public const string IssueEdit = "gitBug.issue.edit";
    public const string CommentCreate = "gitBug.comment.create";
    public const string WriteReview = "gitBug.write.review";
    public const string WriteCommit = "gitBug.write.commit";
}

public enum GitBugWriteStage
{
    Review,
    Prepare,
    Commit
}

/// <summary>Non-sensitive identity for the active repository/workspace.</summary>
public sealed record GitBugRepositoryIdentity(string RepositoryUrl, string WorkspaceName);

/// <summary>Immutable review preconditions; a changed revision requires a new review.</summary>
public sealed record GitBugWritePreconditions(
    GitBugRepositoryIdentity Repository,
    GitBugIssueId IssueId,
    string ExpectedRevision,
    string ReviewedOperation);

/// <summary>Canonical serialization facts selected by the Accessor/Resource boundary.</summary>
public sealed record GitBugSerializationMetadata(
    string StorageVersion,
    string Encoding,
    bool Deterministic);

public sealed record GitBugIssueEditRequest(
    GitBugIssueId IssueId,
    string Title,
    string Description,
    GitBugWritePreconditions Preconditions,
    GitBugSerializationMetadata Serialization);

public sealed record GitBugCommentCreateRequest(
    GitBugIssueId IssueId,
    string Body,
    GitBugWritePreconditions Preconditions,
    GitBugSerializationMetadata Serialization);

public sealed record GitBugWriteReview(
    string ReviewId,
    GitBugWritePreconditions Preconditions,
    GitBugSerializationMetadata Serialization);

public sealed record GitBugIssueEditResult(
    GitBugIssueId IssueId,
    string Revision,
    string ReviewId);

public sealed record GitBugCommentCreateResult(
    GitBugIssueId IssueId,
    string CommentId,
    string Revision,
    string ReviewId);

public sealed record GitBugWriteReceipt(
    string Operation,
    string ReviewId,
    string Revision,
    int BoundedItemCount,
    bool Committed);

/// <summary>Pure contract checks shared by Accessor implementations and contract tests.</summary>
public static class GitBugWriteContract
{
    public const string SupportedStorageVersion = "v0.11.0";
    public const string Utf8Encoding = "utf-8";
    public const int MaximumTitleLength = 4096;
    public const int MaximumBodyLength = 1024 * 1024;
    public const int MaximumRevisionLength = 128;
    public const int MaximumReviewIdLength = 128;

    public static bool IsValidOperation(string? operation) => operation is
        GitBugWriteOperations.IssueEdit or
        GitBugWriteOperations.CommentCreate or
        GitBugWriteOperations.WriteReview or
        GitBugWriteOperations.WriteCommit;

    public static bool IsValidFailure(GitOperationFailureKind kind) => kind is
        GitOperationFailureKind.GitBugWriteInvalidRequest or
        GitOperationFailureKind.GitBugWritePreconditionFailed or
        GitOperationFailureKind.GitBugWriteConflict or
        GitOperationFailureKind.GitBugWriteUnsupportedFormat or
        GitOperationFailureKind.GitBugWriteUnavailable or
        GitOperationFailureKind.GitBugWriteSerializationFailed or
        GitOperationFailureKind.GitBugWriteUnknown;

    public static bool IsValidSerialization(GitBugSerializationMetadata? metadata) =>
        metadata is not null
        && metadata.StorageVersion == SupportedStorageVersion
        && metadata.Encoding == Utf8Encoding
        && metadata.Deterministic;

    public static bool IsValidPreconditions(GitBugWritePreconditions? preconditions) =>
        preconditions is not null
        && preconditions.Repository is not null
        && Uri.TryCreate(preconditions.Repository.RepositoryUrl, UriKind.Absolute, out var repository)
        && IsAllowedRepositoryUri(repository)
        && string.IsNullOrWhiteSpace(repository.UserInfo) 
        && !string.IsNullOrWhiteSpace(preconditions.Repository.WorkspaceName)
        && preconditions.IssueId.Value is { Length: 64 } issueId && issueId.All(Uri.IsHexDigit)
        && IsBounded(preconditions.ExpectedRevision, MaximumRevisionLength)
        && IsValidOperation(preconditions.ReviewedOperation);

    public static bool IsValidIssueEdit(GitBugIssueEditRequest? request) =>
        request is not null
        && IsValidPreconditions(request.Preconditions)
        && request.Preconditions.IssueId == request.IssueId
        && IsValidSerialization(request.Serialization)
        && IsBounded(request.IssueId.Value, 64)
        && IsBounded(request.Title, MaximumTitleLength)
        && IsBounded(request.Description, MaximumBodyLength, allowEmpty: true, allowLineFeeds: true)
        && request.Preconditions.ReviewedOperation == GitBugWriteOperations.IssueEdit;

    public static bool IsValidComment(GitBugCommentCreateRequest? request) =>
        request is not null
        && IsValidPreconditions(request.Preconditions)
        && request.Preconditions.IssueId == request.IssueId
        && IsValidSerialization(request.Serialization)
        && IsBounded(request.Body, MaximumBodyLength, allowEmpty: false, allowLineFeeds: true)
        && request.Preconditions.ReviewedOperation == GitBugWriteOperations.CommentCreate;

    private static bool IsAllowedRepositoryUri(Uri repository) =>
        repository.Scheme == Uri.UriSchemeHttps
        || (repository.Scheme == Uri.UriSchemeHttp
            && (string.Equals(repository.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(repository.Host, out var address) && IPAddress.IsLoopback(address)));

    private static bool IsBounded(
        string? value,
        int maximum,
        bool allowEmpty = false,
        bool allowLineFeeds = false) =>
        value is not null
        && (allowEmpty || value.Length > 0)
        && value.Length <= maximum
        && value.IndexOf('\0') < 0
        && value.IndexOf('\r') < 0
        && (allowLineFeeds || value.IndexOf('\n') < 0);
}

public readonly record struct GitBugIssueId(string Value);

public sealed record GitBugIssueQuery(
    GitBugIssueState? State = null,
    string? SearchText = null,
    string? Cursor = null,
    int PageSize = 50);

public enum GitBugIssueState
{
    Open,
    Closed
}

public sealed record GitBugIssuePage(
    IReadOnlyList<GitBugIssueSummary> Issues,
    string? NextCursor,
    int TotalCount);

public sealed record GitBugIssueSummary(
    GitBugIssueId Id,
    string Title,
    GitBugIssueState State,
    IReadOnlyList<string>? Labels,
    string? AuthorDisplayName);

public sealed record GitBugIssueDetail(
    GitBugIssueSummary Summary,
    string Revision,
    string Description,
    IReadOnlyList<GitBugComment> Comments);

public sealed record GitBugComment(
    string AuthorDisplayName,
    string Body);

public record RepositoryOpenRequest(string RepositoryUrl, string WorkspaceName);
public record RepositoryInfo(string RepositoryUrl, string WorkspaceName, string RootPath, bool WasCloned);

public enum FileFilterKind { DirectoryEntries, TextContent }
public record FilterFilesRequest(string Path, FileFilterKind Kind);
public record FilterFilesResult(string Path, IReadOnlyList<RepositoryFileEntry> Entries, TextFileContent? TextFile = null);
public record UpdateFilesRequest(IReadOnlyList<TextFileUpdate> Updates);
public record TextFileUpdate(string Path, string Content);
public record UpdateFilesResult(IReadOnlyList<string> UpdatedPaths);

public record RepositoryFileEntry(
    string Path,
    string Name,
    RepositoryFileKind Kind,
    long? SizeBytes = null,
    bool IsEditableText = false);
public enum RepositoryFileKind { File, Directory }

public record TextFileContent(
    string Path,
    string Content,
    string? Encoding = "utf-8",
    long? SizeBytes = null);

public record ChangedFile(string Path, GitChangeKind ChangeKind);
public enum GitChangeKind { Added, Modified, Deleted, Renamed, Untracked, Unknown }

/// <summary>Bounded, credential-free metadata used by the Git-Bug review phase.</summary>
public static class BrowserGitCommitContract
{
    public const int MaximumChangedFiles = 1000;
    public const int MaximumCommitMessageLength = 4096;
    public const int MaximumAuthorLength = 512;
    public const int MaximumEmailLength = 512;
    public const int MaximumCommitIdLength = 40;

    public static bool IsValidCommitRequest(CommitRequest? request) =>
        request is not null
        && IsBoundedText(request.Message, MaximumCommitMessageLength)
        && IsBoundedText(request.AuthorName, MaximumAuthorLength)
        && IsBoundedText(request.AuthorEmail, MaximumEmailLength)
        && request.AuthorEmail.Contains('@', StringComparison.Ordinal)
        && request.AuthorEmail.IndexOfAny(['\r', '\n']) < 0;

    public static bool IsValidChangedFiles(IReadOnlyList<ChangedFile>? files) =>
        files is not null
        && files.Count <= MaximumChangedFiles
        && files.All(file => file is not null
            && file.Path.StartsWith("/", StringComparison.Ordinal)
            && file.Path.Length <= 1024
            && file.Path.IndexOfAny(['\\', '\0', '\r', '\n']) < 0);

    private static bool IsBoundedText(string? value, int maximum) =>
        value is not null
        && value.Trim().Length > 0
        && value.Length <= maximum
        && value.IndexOfAny(['\0', '\r', '\n']) < 0;
}

public record CommitRequest(string Message, string AuthorName, string AuthorEmail);
public record CommitInfo(string CommitId, string Message, int ChangedFileCount = 0);

public record PushReview(
    string RepositoryUrl,
    string Branch,
    string OutgoingCommitId,
    string DestinationRef);

public record PushRequest(
    string RepositoryUrl,
    string Branch,
    string OutgoingCommitId,
    string DestinationRef);

public record PushResult(
    string RepositoryUrl,
    string Branch,
    string DestinationRef,
    string PushedCommitId);

/// <summary>Names the reviewed ref namespaces that may cross the publication boundary.</summary>
public enum GitRefNamespace
{
    Branch,
    GitBug
}

/// <summary>Describes a publication update; deletion is represented so the contract can reject it explicitly.</summary>
public enum GitRefUpdateKind
{
    Create,
    Update,
    Delete
}

/// <summary>One bounded, explicit ref update selected for publication.</summary>
public sealed record GitRefUpdate(
    string RefName,
    GitRefNamespace Namespace,
    GitRefUpdateKind Kind,
    string NewObjectId,
    string? ExpectedOldObjectId = null);

/// <summary>One optimistic remote ref precondition captured during review.</summary>
public sealed record GitRemotePrecondition(
    string RefName,
    string? ExpectedObjectId);

/// <summary>Credential-free publication input containing only the reviewed ref set.</summary>
public sealed record GitPublicationRequest(
    string RepositoryUrl,
    string Branch,
    IReadOnlyList<GitRefUpdate> Updates,
    IReadOnlyList<GitRemotePrecondition> RemotePreconditions);

/// <summary>Credential-free publication review shown before an explicit apply operation.</summary>
public sealed record GitPublicationReview(
    string RepositoryUrl,
    string Branch,
    IReadOnlyList<GitRefUpdate> Updates,
    IReadOnlyList<GitRemotePrecondition> RemotePreconditions,
    int BoundedUpdateCount);

/// <summary>Credential-free result metadata for an applied reviewed publication.</summary>
public sealed record GitPublicationReceipt(
    string RepositoryUrl,
    string Branch,
    IReadOnlyList<GitRefUpdate> AppliedUpdates,
    int BoundedUpdateCount);

/// <summary>Pure bounds and allowlist checks for reviewed branch and Git-Bug publication.</summary>
public static class BrowserGitPublicationContract
{
    public const int MaximumUpdates = 100;
    public const int MaximumRemotePreconditions = 100;
    public const int MaximumRefNameLength = 256;
    public const int MaximumObjectIdLength = 64;
    public const int MaximumRepositoryUrlLength = 2048;
    public const int MaximumBranchLength = 256;

    public static bool IsValidUpdate(GitRefUpdate? update) =>
        update is not null
        && Enum.IsDefined(update.Namespace)
        && update.Kind is GitRefUpdateKind.Create or GitRefUpdateKind.Update
        && IsAllowedRef(update.RefName, update.Namespace)
        && IsObjectId(update.NewObjectId)
        && (update.ExpectedOldObjectId is null || IsObjectId(update.ExpectedOldObjectId));

    public static bool IsValidRemotePrecondition(GitRemotePrecondition? precondition) =>
        precondition is not null
        && IsAllowedRefName(precondition.RefName)
        && (precondition.ExpectedObjectId is null || IsObjectId(precondition.ExpectedObjectId));

    public static bool IsValidRequest(GitPublicationRequest? request) =>
        request is not null
        && IsAllowedRepositoryUri(request.RepositoryUrl)
        && IsSafeBranch(request.Branch)
        && request.Updates is not null
        && request.Updates.Count <= MaximumUpdates
        && request.Updates.All(IsValidUpdate)
        && HasUniqueRefs(request.Updates.Select(update => update.RefName))
        && request.RemotePreconditions is not null
        && request.RemotePreconditions.Count <= MaximumRemotePreconditions
        && request.RemotePreconditions.All(IsValidRemotePrecondition)
        && HasUniqueRefs(request.RemotePreconditions.Select(precondition => precondition.RefName));

    public static bool IsValidReview(GitPublicationReview? review) =>
        review is not null
        && IsAllowedRepositoryUri(review.RepositoryUrl)
        && IsSafeBranch(review.Branch)
        && review.BoundedUpdateCount == review.Updates.Count
        && review.BoundedUpdateCount <= MaximumUpdates
        && review.Updates.All(IsValidUpdate)
        && HasUniqueRefs(review.Updates.Select(update => update.RefName))
        && review.RemotePreconditions is not null
        && review.RemotePreconditions.Count <= MaximumRemotePreconditions
        && review.RemotePreconditions.All(IsValidRemotePrecondition)
        && HasUniqueRefs(review.RemotePreconditions.Select(precondition => precondition.RefName));

    public static bool IsAllowedRef(string? refName, GitRefNamespace @namespace) =>
        @namespace switch
        {
            GitRefNamespace.Branch => IsAllowedRefName(refName)
                && refName!.StartsWith("refs/heads/", StringComparison.Ordinal),
            GitRefNamespace.GitBug => IsAllowedRefName(refName)
                && (refName!.StartsWith("refs/bugs/", StringComparison.Ordinal)
                    || refName.StartsWith("refs/identities/", StringComparison.Ordinal)),
            _ => false
        };

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

    private static bool IsSafeBranch(string? branch) =>
        branch is not null
        && branch.Length > 0
        && branch.Length <= MaximumBranchLength
        && branch.IndexOfAny(['\\', '\0', '\r', '\n', ' ', '\t', '~', '^', ':', '?', '*', '[']) < 0;

    private static bool IsObjectId(string? value) =>
        value is not null
        && value.Length is 40 or 64
        && value.Length <= MaximumObjectIdLength
        && value.All(Uri.IsHexDigit);

    private static bool HasUniqueRefs(IEnumerable<string> refs) =>
        refs.Distinct(StringComparer.Ordinal).Count() == refs.Count();

    private static bool IsAllowedRepositoryUri(string? value) =>
        value is not null
        && value.Length <= MaximumRepositoryUrlLength
        && Uri.TryCreate(value, UriKind.Absolute, out var repository)
        && string.IsNullOrWhiteSpace(repository.UserInfo)
        && (repository.Scheme == Uri.UriSchemeHttps
            || (repository.Scheme == Uri.UriSchemeHttp
                && (string.Equals(repository.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                    || IPAddress.TryParse(repository.Host, out var address) && IPAddress.IsLoopback(address))));
}

/// <summary>Describes whether synchronization should only inspect or may apply an accepted review.</summary>
public enum SynchronizationIntent
{
    Fetch,
    Review,
    Apply
}

/// <summary>Stable, user-safe classification of the synchronization decision.</summary>
public enum SynchronizationDecisionState
{
    Current,
    Ahead,
    Divergent,
    ReadyToApply,
    Applied,
    Cancelled,
    Failed
}

/// <summary>Stable categories that may cross the Resource-to-Manager boundary without Git diagnostics.</summary>
public enum SynchronizationFailureCategory
{
    UnsavedEditorChanges,
    UncommittedWorkingTree,
    CredentialRejected,
    NetworkUnavailable,
    UnsupportedRef,
    DivergentHistory,
    Cancelled,
    InvalidRepository,
    Unknown
}

public record SynchronizationRequest(
    string RepositoryUrl,
    string Branch,
    SynchronizationIntent Intent);

/// <summary>Remote commit information used for review; it contains no credentials or transport diagnostics.</summary>
public record IncomingCommitMetadata(
    string CommitId,
    string ParentCommitId,
    string Message,
    string AuthorName,
    DateTimeOffset AuthoredAt);

public record ChangedFileSummary(
    string Path,
    GitChangeKind ChangeKind);

public record SynchronizationReview(
    string RepositoryUrl,
    string Branch,
    string LocalCommitId,
    IncomingCommitMetadata? IncomingCommit,
    IReadOnlyList<ChangedFileSummary> ChangedFiles,
    SynchronizationDecisionState Decision);

/// <summary>A safe message and stable category; internal Accessor details remain outside this contract.</summary>
public record SynchronizationFailure(
    SynchronizationFailureCategory Category,
    string Message);

/// <summary>Stable operation names for bounded branch reads and safe local branch switching.</summary>
public static class BrowserGitBranchHistoryOperations
{
    public const string ListBranches = "git.branch.list";
    public const string CheckoutBranch = "git.branch.checkout";
    public const string ReadHistory = "git.history.read";
}

public enum BranchChoiceKind
{
    Local,
    RemoteTracking
}

/// <summary>Non-sensitive metadata for the checked-out branch, including an empty or detached repository.</summary>
public sealed record CurrentBranch(string? Name, bool IsDetached);

/// <summary>A selectable local or remote-tracking branch name.</summary>
public sealed record BranchChoice(string Name, BranchChoiceKind Kind, bool IsCurrent = false);

/// <summary>Bounded branch discovery output; the count is the number of returned choices, not an unbounded repository total.</summary>
public sealed record BranchSnapshot(
    CurrentBranch? Current,
    IReadOnlyList<BranchChoice> Choices,
    int BoundedResultCount);

/// <summary>Bounded input for a safe local branch checkout request.</summary>
public sealed record CheckoutBranchRequest(string BranchName);

/// <summary>Bounded input for a read-only history request.</summary>
public sealed record BranchHistoryRequest(string? BranchName = null, int Limit = BrowserGitBranchHistoryContract.DefaultHistoryLimit);

/// <summary>Non-sensitive metadata for one recent commit.</summary>
public sealed record CommitHistoryEntry(
    string CommitId,
    string? ParentCommitId,
    string Message,
    string AuthorName,
    DateTimeOffset AuthoredAt);

/// <summary>Bounded recent history output for the requested branch or current checkout.</summary>
public sealed record CommitHistory(
    string? BranchName,
    IReadOnlyList<CommitHistoryEntry> Entries,
    int BoundedResultCount);

/// <summary>Safe categories for branch and history failures; internal Git details never cross this boundary.</summary>
public enum BranchHistoryFailureCategory
{
    RepositoryUnavailable,
    DataMalformed,
    InvalidRequest,
    UnsupportedReference,
    ResultLimitExceeded,
    Unknown
}

public sealed record BranchHistoryFailure(
    BranchHistoryFailureCategory Category,
    string Message);

/// <summary>Pure bounds and safety checks shared by the Accessor and its contract tests.</summary>
public static class BrowserGitBranchHistoryContract
{
    public const int MaximumBranches = 100;
    public const int MaximumHistoryEntries = 100;
    public const int DefaultHistoryLimit = 50;
    public const int MaximumBranchNameLength = 256;
    public const int MaximumCommitIdLength = 40;
    public const int MaximumCommitMessageLength = 4096;
    public const int MaximumAuthorLength = 512;
    public const int MaximumFailureMessageLength = 512;

    public static bool IsValidOperation(string? operation) => operation is
        BrowserGitBranchHistoryOperations.ListBranches or
        BrowserGitBranchHistoryOperations.CheckoutBranch or
        BrowserGitBranchHistoryOperations.ReadHistory;

    public static bool IsValidFailure(GitOperationFailureKind kind) => kind is
        GitOperationFailureKind.BranchHistoryDataUnavailable or
        GitOperationFailureKind.BranchHistoryDataMalformed or
        GitOperationFailureKind.BranchHistoryInvalidRequest or
        GitOperationFailureKind.BranchHistoryUnsupportedReference or
        GitOperationFailureKind.BranchHistoryLimitExceeded or
        GitOperationFailureKind.BranchHistoryUnknown or
        GitOperationFailureKind.BranchSwitchInvalidRequest or
        GitOperationFailureKind.BranchSwitchDirtyWorktree or
        GitOperationFailureKind.BranchSwitchUnsupportedReference or
        GitOperationFailureKind.BranchSwitchUnavailable or
        GitOperationFailureKind.BranchSwitchUnknown;

    public static bool IsValidCurrentBranch(CurrentBranch? branch) =>
        branch is not null
        && (branch.IsDetached
            ? branch.Name is null || IsSafeRefName(branch.Name)
            : IsSafeRefName(branch.Name));

    public static bool IsValidBranchChoice(BranchChoice? choice) =>
        choice is not null && IsSafeRefName(choice.Name);

    public static bool IsValidSnapshot(BranchSnapshot? snapshot) =>
        snapshot is not null
        && snapshot.BoundedResultCount == snapshot.Choices.Count
        && snapshot.BoundedResultCount <= MaximumBranches
        && (snapshot.Current is null || IsValidCurrentBranch(snapshot.Current))
        && snapshot.Choices.All(IsValidBranchChoice)
        && snapshot.Choices.Select(choice => choice.Name)
            .Distinct(StringComparer.Ordinal)
            .Count() == snapshot.Choices.Count;

    public static bool IsValidCheckoutRequest(CheckoutBranchRequest? request) =>
        request is not null && IsSafeRefName(request.BranchName);

    public static bool IsValidRequest(BranchHistoryRequest? request) =>
        request is not null
        && request.Limit is > 0 and <= MaximumHistoryEntries
        && (request.BranchName is null || IsSafeRefName(request.BranchName));

    public static bool IsValidCommitHistoryEntry(CommitHistoryEntry? entry) =>
        entry is not null
        && IsCommitId(entry.CommitId)
        && (entry.ParentCommitId is null || IsCommitId(entry.ParentCommitId))
        && IsSafeText(entry.Message, MaximumCommitMessageLength, allowEmpty: true, allowLineFeeds: true)
        && IsSafeText(entry.AuthorName, MaximumAuthorLength)
        && entry.AuthoredAt != default;

    public static bool IsValidHistory(CommitHistory? history) =>
        history is not null
        && history.BoundedResultCount == history.Entries.Count
        && history.BoundedResultCount <= MaximumHistoryEntries
        && (history.BranchName is null || IsSafeRefName(history.BranchName))
        && history.Entries.All(IsValidCommitHistoryEntry);

    public static bool IsValidFailure(BranchHistoryFailure? failure) =>
        failure is not null
        && IsSafeText(failure.Message, MaximumFailureMessageLength)
        && Enum.IsDefined(failure.Category);

    public static GitOperationFailureKind ToOperationFailure(BranchHistoryFailureCategory category) => category switch
    {
        BranchHistoryFailureCategory.RepositoryUnavailable => GitOperationFailureKind.BranchHistoryDataUnavailable,
        BranchHistoryFailureCategory.DataMalformed => GitOperationFailureKind.BranchHistoryDataMalformed,
        BranchHistoryFailureCategory.InvalidRequest => GitOperationFailureKind.BranchHistoryInvalidRequest,
        BranchHistoryFailureCategory.UnsupportedReference => GitOperationFailureKind.BranchHistoryUnsupportedReference,
        BranchHistoryFailureCategory.ResultLimitExceeded => GitOperationFailureKind.BranchHistoryLimitExceeded,
        _ => GitOperationFailureKind.BranchHistoryUnknown
    };

    private static bool IsCommitId(string? value) =>
        value is not null
        && value.Length == MaximumCommitIdLength
        && value.All(Uri.IsHexDigit);

    private static bool IsSafeRefName(string? value) =>
        value is not null
        && value.Length > 0
        && value.Length <= MaximumBranchNameLength
        && value.IndexOfAny(['\\', '\0', '\r', '\n', ' ', '\t', '~', '^', ':', '?', '*', '[']) < 0
        && !value.StartsWith("/", StringComparison.Ordinal)
        && !value.EndsWith("/", StringComparison.Ordinal)
        && !value.Contains("//", StringComparison.Ordinal)
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains("@{", StringComparison.Ordinal);

    private static bool IsSafeText(
        string? value,
        int maximum,
        bool allowEmpty = false,
        bool allowLineFeeds = false) =>
        value is not null
        && (allowEmpty || value.Trim().Length > 0)
        && value.Length <= maximum
        && value.IndexOf('\0') < 0
        && value.IndexOf('\r') < 0
        && (allowLineFeeds || value.IndexOf('\n') < 0);
}
