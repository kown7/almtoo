using System.Text.Json;
using AlmToo.Accessor.BrowserGitAccessor.Interface;
using AlmToo.Managers.Workspace.Interface;
using Xunit;

namespace AlmToo.Tests.Accessor.BrowserGitAccessor;

public sealed class BrowserGitContractsTests
{
    [Fact]
    public void Synchronization_request_and_review_have_data_only_serialization_shape()
    {
        var request = new SynchronizationRequest(
            "https://github.com/example/project.git",
            "main",
            SynchronizationIntent.Review);
        var review = new SynchronizationReview(
            request.RepositoryUrl,
            request.Branch,
            "local-123",
            new IncomingCommitMetadata(
                "incoming-456",
                "local-123",
                "Update documentation",
                "Example Author",
                DateTimeOffset.Parse("2026-09-28T12:00:00Z")),
            [new ChangedFileSummary("README.md", GitChangeKind.Modified)],
            SynchronizationDecisionState.ReadyToApply);

        using var requestJson = JsonDocument.Parse(JsonSerializer.Serialize(request));
        using var reviewJson = JsonDocument.Parse(JsonSerializer.Serialize(review));

        Assert.Equal("https://github.com/example/project.git", requestJson.RootElement.GetProperty("RepositoryUrl").GetString());
        Assert.Equal("main", requestJson.RootElement.GetProperty("Branch").GetString());
        Assert.Equal((int)SynchronizationIntent.Review, requestJson.RootElement.GetProperty("Intent").GetInt32());
        Assert.Equal("incoming-456", reviewJson.RootElement.GetProperty("IncomingCommit").GetProperty("CommitId").GetString());
        Assert.Equal((int)SynchronizationDecisionState.ReadyToApply, reviewJson.RootElement.GetProperty("Decision").GetInt32());
        Assert.Equal("README.md", reviewJson.RootElement.GetProperty("ChangedFiles")[0].GetProperty("Path").GetString());
    }

    [Fact]
    public void Synchronization_failure_exposes_only_safe_category_and_message()
    {
        var failure = new SynchronizationFailure(
            SynchronizationFailureCategory.NetworkUnavailable,
            "The repository could not be reached. Try again later.");

        var json = JsonSerializer.Serialize(failure);

        Assert.Equal((int)SynchronizationFailureCategory.NetworkUnavailable, JsonDocument.Parse(json).RootElement.GetProperty("Category").GetInt32());
        Assert.Contains("The repository could not be reached", json);
        Assert.DoesNotContain("Diagnostic", json);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pat", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitBug_write_contracts_are_immutable_bounded_and_credential_free()
    {
        var issueId = new GitBugIssueId(new string('a', 64));
        var preconditions = new GitBugWritePreconditions(
            new GitBugRepositoryIdentity("https://github.com/example/project.git", "workspace"),
            issueId,
            "revision-1",
            GitBugWriteOperations.IssueEdit);
        var request = new GitBugIssueEditRequest(
            issueId,
            "Updated title",
            "Updated description",
            preconditions,
            new GitBugSerializationMetadata(
                GitBugWriteContract.SupportedStorageVersion,
                GitBugWriteContract.Utf8Encoding,
                Deterministic: true));

        Assert.True(GitBugWriteContract.IsValidIssueEdit(request));
        Assert.Equal("gitBug.issue.edit", GitBugWriteOperations.IssueEdit);
        Assert.Equal("gitBug.comment.create", GitBugWriteOperations.CommentCreate);
        Assert.Equal("gitBug.write.review", GitBugWriteOperations.WriteReview);
        Assert.Equal("gitBug.write.commit", GitBugWriteOperations.WriteCommit);
        Assert.All(new[]
        {
            typeof(GitBugIssueEditRequest), typeof(GitBugCommentCreateRequest),
            typeof(GitBugWritePreconditions), typeof(GitBugWriteReview),
            typeof(GitBugIssueEditResult), typeof(GitBugCommentCreateResult),
            typeof(GitBugWriteReceipt)
        }, type => Assert.DoesNotContain(type.GetProperties(), property =>
            property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("diagnostic", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void GitBug_write_contract_rejects_ambiguous_or_unsupported_values()
    {
        var issueId = new GitBugIssueId(new string('a', 64));
        var preconditions = new GitBugWritePreconditions(
            new GitBugRepositoryIdentity("http://user:secret@example.test/repository.git", "workspace"),
            issueId,
            "revision\nwith-control",
            GitBugWriteOperations.IssueEdit);
        var request = new GitBugIssueEditRequest(
            issueId,
            new string('x', GitBugWriteContract.MaximumTitleLength + 1),
            "description",
            preconditions,
            new GitBugSerializationMetadata("v0.10.0", "utf-16", Deterministic: false));

        Assert.False(GitBugWriteContract.IsValidIssueEdit(request));
        Assert.False(GitBugWriteContract.IsValidOperation("gitBug.issue.delete"));
        Assert.True(GitBugWriteContract.IsValidFailure(GitOperationFailureKind.GitBugWriteConflict));
        Assert.False(GitBugWriteContract.IsValidFailure(GitOperationFailureKind.Unknown));
    }

    [Fact]
    public void Synchronization_contracts_do_not_model_credentials_or_raw_diagnostics()
    {
        var contractTypes = new[]
        {
            typeof(SynchronizationRequest),
            typeof(IncomingCommitMetadata),
            typeof(ChangedFileSummary),
            typeof(SynchronizationReview),
            typeof(SynchronizationFailure)
        };

        foreach (var type in contractTypes)
        {
            var propertyNames = type.GetProperties().Select(property => property.Name);
            Assert.DoesNotContain(propertyNames, name =>
                name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("diagnostic", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Reconciliation_contracts_expose_only_review_safe_metadata()
    {
        var contractTypes = new[]
        {
            typeof(WorkspaceFileComparison),
            typeof(WorkspaceReconciliationInput),
            typeof(WorkspaceReconciliationChange),
            typeof(WorkspaceReconciliationApplyMetadata),
            typeof(WorkspaceReconciliationResult)
        };

        foreach (var type in contractTypes)
        {
            var propertyNames = type.GetProperties().Select(property => property.Name);
            Assert.DoesNotContain(propertyNames, name =>
                name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("diagnostic", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("exception", StringComparison.OrdinalIgnoreCase));
        }

        var result = new WorkspaceReconciliationResult(
            WorkspaceReconciliationStatus.Unavailable,
            [],
            "The remote comparison is unavailable.",
            RequiresExplicitApply: false,
            FailureKind: WorkspaceReconciliationFailureKind.RemoteUnavailable);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));

        Assert.Equal(
            (int)WorkspaceReconciliationFailureKind.RemoteUnavailable,
            json.RootElement.GetProperty("FailureKind").GetInt32());
        Assert.DoesNotContain("token", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnostic", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Branch_snapshot_contract_accepts_current_empty_and_bounded_choices()
    {
        var emptyRepository = new BranchSnapshot(null, [], 0);
        var snapshot = new BranchSnapshot(
            new CurrentBranch("main", IsDetached: false),
            [
                new BranchChoice("main", BranchChoiceKind.Local, IsCurrent: true),
                new BranchChoice("origin/main", BranchChoiceKind.RemoteTracking)
            ],
            2);

        Assert.True(BrowserGitBranchHistoryContract.IsValidSnapshot(emptyRepository));
        Assert.True(BrowserGitBranchHistoryContract.IsValidSnapshot(snapshot));
        Assert.Equal(BrowserGitBranchHistoryOperations.ListBranches, "git.branch.list");
        Assert.DoesNotContain(snapshot.GetType().GetProperties(), property =>
            property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("diagnostic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Branch_and_history_contracts_reject_malformed_oversized_and_unsafe_values()
    {
        var unsafeBranch = new BranchChoice("feature..unsafe", BranchChoiceKind.Local);
        var oversizedBranches = new BranchSnapshot(
            new CurrentBranch("main", IsDetached: false),
            Enumerable.Range(0, BrowserGitBranchHistoryContract.MaximumBranches + 1)
                .Select(index => new BranchChoice($"branch-{index}", BranchChoiceKind.Local))
                .ToArray(),
            BrowserGitBranchHistoryContract.MaximumBranches + 1);
        var invalidRequest = new BranchHistoryRequest("feature\nunsafe", BrowserGitBranchHistoryContract.DefaultHistoryLimit);
        var malformedCommit = new CommitHistoryEntry(
            new string('g', BrowserGitBranchHistoryContract.MaximumCommitIdLength),
            null,
            "message",
            "Author",
            DateTimeOffset.UtcNow);

        Assert.False(BrowserGitBranchHistoryContract.IsValidBranchChoice(unsafeBranch));
        Assert.False(BrowserGitBranchHistoryContract.IsValidSnapshot(oversizedBranches));
        Assert.False(BrowserGitBranchHistoryContract.IsValidRequest(invalidRequest));
        Assert.False(BrowserGitBranchHistoryContract.IsValidCommitHistoryEntry(malformedCommit));
        Assert.False(BrowserGitBranchHistoryContract.IsValidCurrentBranch(new CurrentBranch(
            new string('x', BrowserGitBranchHistoryContract.MaximumBranchNameLength + 1),
            IsDetached: false)));
    }

    [Fact]
    public void History_contract_accepts_bounded_entries_and_exposes_categorized_safe_failures()
    {
        var history = new CommitHistory(
            "main",
            [new CommitHistoryEntry(
                new string('a', 40),
                null,
                "Initial commit",
                "Example Author",
                DateTimeOffset.Parse("2026-09-28T12:00:00Z"))],
            1);
        var failure = new BranchHistoryFailure(
            BranchHistoryFailureCategory.DataMalformed,
            "The browser returned malformed commit metadata.");

        Assert.True(BrowserGitBranchHistoryContract.IsValidHistory(history));
        Assert.True(BrowserGitBranchHistoryContract.IsValidFailure(failure));
        Assert.Equal(
            GitOperationFailureKind.BranchHistoryDataMalformed,
            BrowserGitBranchHistoryContract.ToOperationFailure(failure.Category));
        Assert.False(BrowserGitBranchHistoryContract.IsValidFailure(new BranchHistoryFailure(
            failure.Category,
            new string('x', BrowserGitBranchHistoryContract.MaximumFailureMessageLength + 1))));
        Assert.DoesNotContain("token", JsonSerializer.Serialize(failure), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnostic", JsonSerializer.Serialize(failure), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Local_commit_contract_bounds_metadata_and_never_models_credentials_or_push()
    {
        var request = new CommitRequest("Review local changes", "Example Author", "author@example.test");
        var info = new CommitInfo(new string('a', 40), request.Message, ChangedFileCount: 2);

        Assert.True(BrowserGitCommitContract.IsValidCommitRequest(request));
        Assert.Equal(40, info.CommitId.Length);
        Assert.Equal(2, info.ChangedFileCount);
        Assert.False(BrowserGitCommitContract.IsValidCommitRequest(new CommitRequest(string.Empty, request.AuthorName, request.AuthorEmail)));
        Assert.False(BrowserGitCommitContract.IsValidCommitRequest(new CommitRequest(request.Message, request.AuthorName, "not-an-email")));
        Assert.True(new CommitInfo(new string('a', BrowserGitCommitContract.MaximumCommitIdLength + 1), request.Message, 1).CommitId.Length > BrowserGitCommitContract.MaximumCommitIdLength);

        foreach (var type in new[] { typeof(CommitRequest), typeof(CommitInfo), typeof(ChangedFile) })
        {
            Assert.DoesNotContain(type.GetProperties(), property =>
                property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("push", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("diagnostic", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Publication_contract_accepts_branch_and_git_bug_updates_with_bounded_safe_metadata()
    {
        var head = new string('a', 40);
        var bugObject = new string('b', 40);
        var identityObject = new string('c', 40);
        var updates = new GitRefUpdate[]
        {
            new("refs/heads/main", GitRefNamespace.Branch, GitRefUpdateKind.Update, head, head),
            new("refs/bugs/issue-1", GitRefNamespace.GitBug, GitRefUpdateKind.Create, bugObject),
            new("refs/identities/author-1", GitRefNamespace.GitBug, GitRefUpdateKind.Update, identityObject, identityObject)
        };
        var preconditions = new GitRemotePrecondition[]
        {
            new("refs/heads/main", head),
            new("refs/bugs/issue-1", null),
            new("refs/identities/author-1", identityObject)
        };
        var review = new GitPublicationReview(
            "https://github.com/example/project.git",
            "main",
            updates,
            preconditions,
            updates.Length);
        var request = new GitPublicationRequest(review.RepositoryUrl, review.Branch, updates, preconditions);

        Assert.True(BrowserGitPublicationContract.IsValidReview(review));
        Assert.True(BrowserGitPublicationContract.IsValidRequest(request));
        Assert.Equal(updates.Length, review.BoundedUpdateCount);
        Assert.Equal(GitRefUpdateKind.Update, updates[0].Kind);
        Assert.DoesNotContain(typeof(GitPublicationRequest).GetProperties(), property =>
            property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("diagnostic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Remote_snapshot_contract_accepts_stable_path_scoped_metadata_without_secrets()
    {
        var commitId = new string('a', 40);
        var treeId = new string('b', 40);
        var blobId = new string('c', 40);
        var snapshot = new RemoteSnapshot(
            new RemoteSnapshotRef("refs/remotes/origin/main", commitId),
            new RemoteSnapshotCommit(
                commitId,
                treeId,
                new string('d', 40),
                "Remote review",
                "Example Author",
                DateTimeOffset.Parse("2026-09-28T12:00:00Z")),
            [new RemoteSnapshotBlob("/README.md", blobId, 12)],
            1);

        Assert.True(BrowserGitRemoteSnapshotContract.IsValidRequest(new RemoteSnapshotRequest("refs/remotes/origin/main")));
        Assert.True(BrowserGitRemoteSnapshotContract.IsValidSnapshot(snapshot));
        Assert.Equal("refs/remotes/origin/main", snapshot.RemoteRef);
        Assert.Equal(commitId, snapshot.SourceRevision);
        Assert.Equal("git.remote.snapshot", BrowserGitRemoteSnapshotOperations.Read);
        Assert.DoesNotContain(typeof(RemoteSnapshot).GetProperties(), property =>
            property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("diagnostic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Remote_snapshot_contract_rejects_malformed_duplicate_unsafe_oversized_and_secret_shaped_values()
    {
        var commitId = new string('a', 40);
        var treeId = new string('b', 40);
        var validCommit = new RemoteSnapshotCommit(commitId, treeId, null, "Message", "Author", DateTimeOffset.UtcNow);
        var validRef = new RemoteSnapshotRef("refs/remotes/origin/main", commitId);
        var validBlob = new RemoteSnapshotBlob("/README.md", new string('c', 40), 12);

        Assert.False(BrowserGitRemoteSnapshotContract.IsValidRequest(new RemoteSnapshotRequest("refs/remotes/origin/main?token=secret")));
        Assert.False(BrowserGitRemoteSnapshotContract.IsValidRequest(new RemoteSnapshotRequest("refs/remotes/origin/main", BrowserGitRemoteSnapshotContract.MaximumEntries + 1)));
        Assert.False(BrowserGitRemoteSnapshotContract.IsValidSnapshot(new RemoteSnapshot(
            validRef,
            validCommit,
            [validBlob, validBlob],
            2)));
        Assert.False(BrowserGitRemoteSnapshotContract.IsValidSnapshot(new RemoteSnapshot(
            validRef,
            validCommit,
            [new RemoteSnapshotBlob("/../secrets.txt", validBlob.ObjectId, validBlob.SizeBytes)],
            1)));
        Assert.False(BrowserGitRemoteSnapshotContract.IsValidBlob(validBlob with
        {
            SizeBytes = BrowserGitRemoteSnapshotContract.MaximumBlobSizeBytes + 1
        }));
        Assert.False(BrowserGitRemoteSnapshotContract.IsValidCommit(validCommit with { CommitId = "malformed" }));
        Assert.True(BrowserGitRemoteSnapshotContract.IsValidFailure(GitOperationFailureKind.RemoteSnapshotDataMalformed));
        Assert.False(BrowserGitRemoteSnapshotContract.IsValidFailure(GitOperationFailureKind.Unknown));
    }

    [Fact]
    public void Publication_contract_rejects_unsupported_namespaces_deletes_malformed_refs_and_unbounded_sets()
    {
        var objectId = new string('a', 40);
        var validUpdate = new GitRefUpdate("refs/bugs/issue-1", GitRefNamespace.GitBug, GitRefUpdateKind.Create, objectId);
        var validPrecondition = new GitRemotePrecondition(validUpdate.RefName, null);
        var baseRequest = new GitPublicationRequest(
            "https://github.com/example/project.git",
            "main",
            [validUpdate],
            [validPrecondition]);

        Assert.False(BrowserGitPublicationContract.IsValidUpdate(
            new GitRefUpdate("refs/tags/v1", GitRefNamespace.Branch, GitRefUpdateKind.Update, objectId)));
        Assert.False(BrowserGitPublicationContract.IsValidUpdate(
            new GitRefUpdate("refs/bugs/issue-1", GitRefNamespace.GitBug, GitRefUpdateKind.Delete, objectId)));
        Assert.False(BrowserGitPublicationContract.IsValidUpdate(
            new GitRefUpdate("refs/bugs/issue\n1", GitRefNamespace.GitBug, GitRefUpdateKind.Create, objectId)));
        Assert.False(BrowserGitPublicationContract.IsValidUpdate(
            new GitRefUpdate("refs/bugs/issue-1", GitRefNamespace.GitBug, GitRefUpdateKind.Create, "not-an-object")));
        Assert.False(BrowserGitPublicationContract.IsValidRemotePrecondition(
            new GitRemotePrecondition("refs/tags/v1\n", null)));
        Assert.False(BrowserGitPublicationContract.IsValidRequest(baseRequest with
        {
            Updates = [validUpdate, validUpdate]
        }));
        Assert.False(BrowserGitPublicationContract.IsValidRequest(baseRequest with
        {
            Updates = Enumerable.Repeat(validUpdate, BrowserGitPublicationContract.MaximumUpdates + 1).ToArray()
        }));
        Assert.False(BrowserGitPublicationContract.IsValidReview(new GitPublicationReview(
            baseRequest.RepositoryUrl,
            baseRequest.Branch,
            [validUpdate],
            [validPrecondition],
            BoundedUpdateCount: 2)));
    }

    [Fact]
    public void Reviewed_reconciliation_contract_requires_exact_bounded_selections_and_remote_preconditions()
    {
        var sourceRevision = new string('a', 40);
        var remoteObjectId = new string('b', 40);
        var request = new ReviewedReconciliationApplyRequest(
            "review-1",
            sourceRevision,
            2,
            [
                new ReviewedReconciliationSelection("/README.md", "KeepLocal"),
                new ReviewedReconciliationSelection("/docs/guide.md", "AcceptRemote")
            ],
            [
                new ReviewedReconciliationRemotePrecondition("/README.md", remoteObjectId),
                new ReviewedReconciliationRemotePrecondition("/docs/guide.md", null)
            ]);

        Assert.True(BrowserGitReconciliationContract.IsValidRequest(request));
        Assert.False(BrowserGitReconciliationContract.IsValidRequest(request with
        {
            Selections = [request.Selections[0], request.Selections[0]]
        }));
        Assert.False(BrowserGitReconciliationContract.IsValidRequest(request with
        {
            Selections = [new ReviewedReconciliationSelection("/docs/../secret.txt", "AcceptRemote")]
        }));
        Assert.False(BrowserGitReconciliationContract.IsValidRequest(request with
        {
            Selections = [
                new ReviewedReconciliationSelection("/README.md", "UnknownResolution"),
                request.Selections[1]
            ]
        }));
        Assert.False(BrowserGitReconciliationContract.IsValidRequest(request with
        {
            RemotePreconditions = [
                request.RemotePreconditions[0],
                new ReviewedReconciliationRemotePrecondition("/other.md", null)
            ]
        }));
        Assert.False(BrowserGitReconciliationContract.IsValidFailure(GitOperationFailureKind.Unknown));
        Assert.True(BrowserGitReconciliationContract.IsValidFailure(GitOperationFailureKind.ReconciliationStaleRemote));
    }
}
