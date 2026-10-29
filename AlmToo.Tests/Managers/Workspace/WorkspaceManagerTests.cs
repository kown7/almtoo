using System.Text.Json;
using AlmToo.Accessor.BrowserGitAccessor.Interface;
using AlmToo.Managers.Workspace;
using AlmToo.Managers.Workspace.Interface;
using AlmToo.Tests.Managers.Workspace;
using Xunit;

namespace AlmToo.Tests.Managers.Workspace;

public sealed class WorkspaceManagerTests
{
    private static readonly RepositoryInfo Repository = new(
        "https://github.com/example/project.git", "project", "/workspaces/project", true);
    private static readonly RepositoryFileEntry Readme = new(
        "/README.md", "README.md", RepositoryFileKind.File, 12, true);
    private static readonly PushReview Review = new(
        Repository.RepositoryUrl, "main", "abc123", "refs/heads/main");
    private static readonly GitPublicationReview PublicationReview = new(
        Repository.RepositoryUrl,
        "main",
        [new("refs/bugs/issue", GitRefNamespace.GitBug, GitRefUpdateKind.Update,
            new string('b', 64), new string('a', 64))],
        [new("refs/heads/main", new string('c', 40))],
        1);

    [Fact]
    public void Reconciliation_contracts_accept_bounded_review_shape_and_passive_state_defaults()
    {
        var input = new WorkspaceReconciliationInput(
        [new("/src/README.md", "base", "local", "remote")]);
        var change = new WorkspaceReconciliationChange(
            "/src/README.md",
            WorkspaceReconciliationChangeKind.Divergent,
            [WorkspaceReconciliationResolution.KeepLocal, WorkspaceReconciliationResolution.AcceptRemote]);
        var metadata = new WorkspaceReconciliationApplyMetadata(
            "review-1",
            "remote-revision-1",
            1,
            DateTimeOffset.UtcNow,
            ExplicitlyConfirmed: true);
        var result = new WorkspaceReconciliationResult(
            WorkspaceReconciliationStatus.RequiresResolution,
            [change],
            "Review the divergent file before applying reconciliation.",
            RequiresExplicitApply: true,
            ApplyMetadata: metadata);
        var state = new WorkspaceState();

        Assert.True(WorkspaceReconciliationContract.IsValidInput(input));
        Assert.True(WorkspaceReconciliationContract.IsValidResult(result));
        Assert.True(WorkspaceReconciliationContract.IsValidApplyMetadata(metadata));
        Assert.Equal(WorkspaceReconciliationStatus.Idle, state.ReconciliationStatus);
        Assert.False(state.ReconciliationRequiresApply);
        Assert.False(state.ReconciliationApplyConfirmed);
    }

    [Fact]
    public async Task Reconciliation_review_maps_remote_snapshot_through_engine_without_write_operations()
    {
        var fake = new FakeBrowserAccessor
        {
            StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
                "status", true, "Clean.", [])),
            RemoteSnapshotAsync = (_, _) => ValueTask.FromResult(new GitOperationResult<RemoteSnapshot>(
                BrowserGitRemoteSnapshotOperations.Read,
                true,
                "Remote snapshot ready.",
                CreateRemoteSnapshot(new RemoteSnapshotBlob("/remote.txt", new string('b', 40), 12))))
        };
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        fake.Calls.Clear();

        Assert.True(await manager.ReviewReconciliationAsync());

        var change = Assert.Single(manager.State.Reconciliation!.Changes);
        Assert.Equal(WorkspaceReconciliationChangeKind.RemoteOnly, change.Kind);
        Assert.Equal(new string('b', 40), manager.State.ReconciliationApply!.SourceRevision);
        Assert.Equal(WorkspaceReconciliationStatus.Ready, manager.State.ReconciliationStatus);
        Assert.DoesNotContain("checkout", fake.Calls);
        Assert.DoesNotContain("update", fake.Calls);
        Assert.DoesNotContain("commit", fake.Calls);
        Assert.DoesNotContain("push", fake.Calls);
    }

    [Fact]
    public async Task Reconciliation_review_maps_local_only_and_divergent_status_safely()
    {
        var fake = new FakeBrowserAccessor
        {
            RemoteSnapshotAsync = (_, _) => ValueTask.FromResult(new GitOperationResult<RemoteSnapshot>(
                BrowserGitRemoteSnapshotOperations.Read,
                true,
                "Remote snapshot ready.",
                CreateRemoteSnapshot(new RemoteSnapshotBlob("/remote.txt", new string('b', 40), 12))))
        };
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));

        fake.StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
            "status", true, "Local only.", [new("/local.txt", GitChangeKind.Added)]));
        Assert.True(await manager.ReviewReconciliationAsync());
        Assert.Equal(WorkspaceReconciliationChangeKind.LocalOnly,
            Assert.Single(manager.State.Reconciliation!.Changes, change => change.Path == "/local.txt").Kind);

        fake.StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
            "status", true, "Divergent.", [new("/remote.txt", GitChangeKind.Modified)]));
        Assert.True(await manager.ReviewReconciliationAsync());
        Assert.Equal(WorkspaceReconciliationStatus.RequiresResolution, manager.State.ReconciliationStatus);
        Assert.Equal(WorkspaceReconciliationChangeKind.Divergent,
            Assert.Single(manager.State.Reconciliation!.Changes).Kind);
        Assert.True(manager.State.ReconciliationRequiresApply);
    }

    [Fact]
    public async Task Reconciliation_review_maps_unavailable_remote_without_accessor_diagnostics()
    {
        const string secret = "PAT=remote-secret";
        var fake = new FakeBrowserAccessor
        {
            StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
                "status", true, "Clean.", [])),
            RemoteSnapshotAsync = (_, _) => ValueTask.FromResult(new GitOperationResult<RemoteSnapshot>(
                BrowserGitRemoteSnapshotOperations.Read,
                false,
                $"raw remote response {secret}",
                Value: default,
                Diagnostic: secret,
                FailureKind: GitOperationFailureKind.RemoteSnapshotDataUnavailable))
        };
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));

        Assert.False(await manager.ReviewReconciliationAsync());

        Assert.Equal(WorkspaceReconciliationStatus.Unavailable, manager.State.ReconciliationStatus);
        Assert.Equal(WorkspaceReconciliationFailureKind.RemoteUnavailable, manager.State.Reconciliation!.FailureKind);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);
        Assert.DoesNotContain("checkout", fake.Calls);
        Assert.DoesNotContain("update", fake.Calls);
        Assert.DoesNotContain("commit", fake.Calls);
        Assert.DoesNotContain("push", fake.Calls);
    }

    [Fact]
    public async Task Reconciliation_review_maps_remote_ahead_to_unavailable_and_allows_safe_retry()
    {
        const string secret = "PAT=remote-ahead-secret";
        var fake = new FakeBrowserAccessor
        {
            StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
                "status", true, "Clean.", [])),
            RemoteSnapshotAsync = (_, _) => ValueTask.FromResult(new GitOperationResult<RemoteSnapshot>(
                BrowserGitRemoteSnapshotOperations.Read,
                false,
                $"raw remote-ahead response {secret}",
                Value: default,
                Diagnostic: secret,
                FailureKind: GitOperationFailureKind.RemoteAhead))
        };
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        fake.Calls.Clear();

        Assert.False(await manager.ReviewReconciliationAsync());

        Assert.Equal(WorkspaceReconciliationStatus.Unavailable, manager.State.ReconciliationStatus);
        Assert.Equal(WorkspaceReconciliationFailureKind.RemoteUnavailable, manager.State.ReconciliationFailureKind);
        Assert.Equal(GitOperationFailureKind.RemoteAhead, manager.State.Error?.FailureKind);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);
        Assert.DoesNotContain("checkout", fake.Calls);
        Assert.DoesNotContain("update", fake.Calls);
        Assert.DoesNotContain("commit", fake.Calls);
        Assert.DoesNotContain("push", fake.Calls);
        Assert.DoesNotContain("publication-review", fake.Calls);
        Assert.DoesNotContain("sync:Apply", fake.Calls);

        fake.RemoteSnapshotAsync = (_, _) => ValueTask.FromResult(new GitOperationResult<RemoteSnapshot>(
            BrowserGitRemoteSnapshotOperations.Read,
            true,
            "Remote snapshot ready.",
            CreateRemoteSnapshot(new RemoteSnapshotBlob("/remote.txt", new string('b', 40), 12))));
        fake.Calls.Clear();

        Assert.True(await manager.ReviewReconciliationAsync());
        Assert.Equal(WorkspaceReconciliationStatus.Ready, manager.State.ReconciliationStatus);
        Assert.Equal(new string('b', 40), manager.State.ReconciliationSourceRevision);
        Assert.DoesNotContain("checkout", fake.Calls);
        Assert.DoesNotContain("update", fake.Calls);
        Assert.DoesNotContain("commit", fake.Calls);
        Assert.DoesNotContain("push", fake.Calls);
        Assert.DoesNotContain("publication-review", fake.Calls);
        Assert.DoesNotContain("sync:Apply", fake.Calls);
    }

    [Fact]
    public async Task Reconciliation_review_rejects_unsafe_duplicate_payloads_and_unsupported_remote_results()
    {
        const string secret = "token=malformed-remote-secret";
        var malformed = new RemoteSnapshot(
            new RemoteSnapshotRef("refs/remotes/origin/main", new string('b', 40)),
            new RemoteSnapshotCommit(
                new string('b', 40),
                new string('c', 40),
                null,
                "Remote review",
                "Reviewer",
                DateTimeOffset.UnixEpoch),
            [
                new RemoteSnapshotBlob("/../unsafe.txt", new string('d', 40), 1),
                new RemoteSnapshotBlob("/same.txt", new string('e', 40), 1),
                new RemoteSnapshotBlob("/same.txt", new string('f', 40), 1)
            ],
            3);
        Assert.False(BrowserGitRemoteSnapshotContract.IsValidSnapshot(malformed));

        var fake = new FakeBrowserAccessor
        {
            StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
                "status", true, "Clean.", [])),
            RemoteSnapshotAsync = (_, _) => ValueTask.FromResult(new GitOperationResult<RemoteSnapshot>(
                BrowserGitRemoteSnapshotOperations.Read,
                true,
                $"malformed remote response {secret}",
                malformed,
                Diagnostic: secret))
        };
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        fake.Calls.Clear();

        Assert.False(await manager.ReviewReconciliationAsync());

        Assert.Equal(WorkspaceReconciliationStatus.Failed, manager.State.ReconciliationStatus);
        Assert.Equal(WorkspaceReconciliationFailureKind.MalformedInput, manager.State.ReconciliationFailureKind);
        Assert.Equal(GitOperationFailureKind.RemoteSnapshotDataMalformed, manager.State.Error?.FailureKind);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);
        Assert.DoesNotContain("checkout", fake.Calls);
        Assert.DoesNotContain("update", fake.Calls);
        Assert.DoesNotContain("commit", fake.Calls);
        Assert.DoesNotContain("push", fake.Calls);
        Assert.DoesNotContain("publication-review", fake.Calls);
        Assert.DoesNotContain("sync:Apply", fake.Calls);

        fake.RemoteSnapshotAsync = (_, _) => ValueTask.FromResult(new GitOperationResult<RemoteSnapshot>(
            BrowserGitRemoteSnapshotOperations.Read,
            false,
            $"unsupported remote response {secret}",
            Value: default,
            Diagnostic: secret,
            FailureKind: GitOperationFailureKind.RemoteSnapshotUnsupportedReference));
        fake.Calls.Clear();

        Assert.False(await manager.ReviewReconciliationAsync());

        Assert.Equal(WorkspaceReconciliationStatus.Unsupported, manager.State.ReconciliationStatus);
        Assert.Equal(WorkspaceReconciliationFailureKind.UnsupportedFormat, manager.State.ReconciliationFailureKind);
        Assert.Equal(GitOperationFailureKind.RemoteSnapshotUnsupportedReference, manager.State.Error?.FailureKind);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);
        Assert.DoesNotContain("checkout", fake.Calls);
        Assert.DoesNotContain("update", fake.Calls);
        Assert.DoesNotContain("commit", fake.Calls);
        Assert.DoesNotContain("push", fake.Calls);
        Assert.DoesNotContain("publication-review", fake.Calls);
        Assert.DoesNotContain("sync:Apply", fake.Calls);
    }

    [Fact]
    public async Task Reconciliation_review_discards_late_completion_from_an_older_generation()
    {
        var firstRemoteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRemote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteCall = 0;
        var fake = new FakeBrowserAccessor
        {
            StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
                "status", true, "Clean.", [])),
            RemoteSnapshotAsync = async (_, _) =>
            {
                if (Interlocked.Increment(ref remoteCall) == 1)
                {
                    firstRemoteEntered.SetResult();
                    await releaseFirstRemote.Task;
                }

                return new GitOperationResult<RemoteSnapshot>(
                    BrowserGitRemoteSnapshotOperations.Read,
                    true,
                    "Remote snapshot ready.",
                    CreateRemoteSnapshot(new RemoteSnapshotBlob("/newer.txt", new string('b', 40), 12)));
            }
        };
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));

        var first = InvokeReconciliationCore(manager);
        await firstRemoteEntered.Task;
        var second = InvokeReconciliationCore(manager);

        Assert.True(await second);
        var currentReviewId = manager.State.ReconciliationReviewId;
        Assert.Equal(WorkspaceReconciliationStatus.Ready, manager.State.ReconciliationStatus);
        Assert.StartsWith("reconciliation-2-", currentReviewId);

        releaseFirstRemote.SetResult();
        Assert.False(await first);

        Assert.Equal(currentReviewId, manager.State.ReconciliationReviewId);
        Assert.Equal(WorkspaceReconciliationStatus.Ready, manager.State.ReconciliationStatus);
        Assert.Equal(1, manager.State.ReconciliationChangeCount);
        Assert.DoesNotContain("checkout", fake.Calls);
        Assert.DoesNotContain("update", fake.Calls);
        Assert.DoesNotContain("commit", fake.Calls);
        Assert.DoesNotContain("push", fake.Calls);
        Assert.DoesNotContain("publication-review", fake.Calls);
        Assert.DoesNotContain("sync:Apply", fake.Calls);
    }

    [Fact]
    public async Task Reconciliation_review_cancellation_preserves_cancelled_inspectable_state()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        fake.StatusAsync = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new GitOperationResult<IReadOnlyList<ChangedFile>>("status", true, "never", []);
        };

        var review = manager.ReviewReconciliationAsync();
        await entered.Task;
        manager.CancelCurrentOperation();

        Assert.False(await review);
        Assert.Equal(WorkspaceReconciliationStatus.Cancelled, manager.State.ReconciliationStatus);
        Assert.True(manager.State.Error?.WasCancelled);
        Assert.Equal(WorkspaceOperation.None, manager.State.Operation);
    }

    [Fact]
    public void Reconciliation_contracts_reject_unbounded_duplicate_unsafe_and_credential_shaped_values()
    {
        var duplicatePaths = new WorkspaceReconciliationInput(
        [
            new("/same.txt", "base", "local", "remote"),
            new("/same.txt", "base", "local", "remote")
        ]);
        var unsafeComparison = new WorkspaceFileComparison(
            "/../secrets.txt",
            "revision\nwith-control",
            "local",
            "remote");
        var tooManyResolutions = new WorkspaceReconciliationChange(
            "/file.txt",
            WorkspaceReconciliationChangeKind.Divergent,
            [
                WorkspaceReconciliationResolution.KeepLocal,
                WorkspaceReconciliationResolution.AcceptRemote,
                WorkspaceReconciliationResolution.KeepLocal
            ]);
        var unsafeMetadata = new WorkspaceReconciliationApplyMetadata(
            "review\nwith-control",
            "revision",
            WorkspaceReconciliationContract.MaximumFiles + 1,
            DateTimeOffset.UtcNow);

        Assert.False(WorkspaceReconciliationContract.IsValidInput(duplicatePaths));
        Assert.False(WorkspaceReconciliationContract.IsValidComparison(unsafeComparison));
        Assert.False(WorkspaceReconciliationContract.IsValidChange(tooManyResolutions));
        Assert.False(WorkspaceReconciliationContract.IsValidApplyMetadata(unsafeMetadata));
        Assert.DoesNotContain(typeof(WorkspaceReconciliationResult).GetProperties(), property =>
            property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("diagnostic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OpenRepository_sequences_open_directory_and_status_into_coherent_state()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);

        var succeeded = await manager.OpenRepositoryAsync(" https://github.com/example/project.git ");

        Assert.True(succeeded);
        Assert.Equal(["open", "filter:DirectoryEntries:/", "status"], fake.Calls);
        Assert.Equal(Repository, manager.State.Repository);
        Assert.Equal(Readme, Assert.Single(manager.State.Entries));
        Assert.Equal("/", manager.State.CurrentPath);
        Assert.Equal(new ChangedFile("/README.md", GitChangeKind.Modified), Assert.Single(manager.State.ChangedFiles));
        Assert.False(manager.State.IsBusy);
        Assert.True(manager.State.Result?.Succeeded);
        Assert.Null(manager.State.Error);
        Assert.Equal("project", fake.OpenRequests.Single().WorkspaceName);
    }

    [Fact]
    public async Task OpenRepository_rejects_blank_input_without_calling_an_accessor()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);

        var succeeded = await manager.OpenRepositoryAsync("   ");

        Assert.False(succeeded);
        Assert.Empty(fake.Calls);
        Assert.Contains("Enter a public repository URL", manager.State.Error?.Message);
        Assert.Null(manager.State.Repository);
    }

    [Fact]
    public async Task Directory_traversal_failure_from_accessor_is_preserved_as_inspectable_safe_state()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        fake.Filter = request => request.Path.Contains("..", StringComparison.Ordinal)
            ? new GitOperationResult<FilterFilesResult>("filter-files", false, "Repository paths cannot traverse the workspace.", Value: default, Diagnostic: "path-containment", FailureKind: GitOperationFailureKind.Unknown)
            : fake.DefaultFilter(request);

        var succeeded = await manager.LoadDirectoryAsync("../secrets");

        Assert.False(succeeded);
        Assert.Equal("/../secrets", fake.FilterRequests.Last().Path);
        Assert.Equal("Repository paths cannot traverse the workspace.", manager.State.Error?.Message);
        Assert.DoesNotContain("path-containment", JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);
        Assert.Equal(GitOperationFailureKind.Unknown, manager.State.Error?.FailureKind);
        Assert.Empty(manager.State.Entries);
    }

    [Fact]
    public async Task File_edit_and_save_use_file_accessor_then_refresh_status()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        Assert.True(await manager.SelectFileAsync(Readme.Path));

        manager.EditSelectedFile("changed\n");
        Assert.True(manager.State.HasUnsavedChanges);
        var succeeded = await manager.SaveSelectedFileAsync();

        Assert.True(succeeded);
        var update = Assert.Single(fake.UpdateRequests);
        var file = Assert.Single(update.Updates);
        Assert.Equal(Readme.Path, file.Path);
        Assert.Equal("changed\n", file.Content);
        Assert.Equal("changed\n", manager.State.SelectedFileContent);
        Assert.False(manager.State.HasUnsavedChanges);
        Assert.Equal("status", fake.Calls.Last());
    }

    [Fact]
    public async Task Unsupported_or_missing_file_selection_is_rejected_before_read()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        var initialFilters = fake.FilterRequests.Count;

        Assert.False(await manager.SelectFileAsync("/missing.txt"));
        Assert.Equal(initialFilters, fake.FilterRequests.Count);
        Assert.Contains("no longer visible", manager.State.Error?.Message);
    }

    [Fact]
    public async Task Status_refresh_stays_local_and_publication_review_is_explicit()
    {
        var fake = new FakeBrowserAccessor
        {
            StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
                "status", true, "Clean.", [])),
            PublicationReviewAsync = _ => ValueTask.FromResult(new GitOperationResult<GitPublicationReview>(
                "inspect-publication", true, "Refs reviewed.", PublicationReview))
        };
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));

        fake.Calls.Clear();
        Assert.True(await manager.RefreshStatusAsync());
        Assert.Equal(["status"], fake.Calls);
        Assert.Null(manager.State.PublicationReview);

        Assert.True(await manager.ReviewPublicationAsync());
        Assert.Equal(["status", "publication-review"], fake.Calls);
        Assert.Equal(PublicationReview, manager.State.PublicationReview);
        Assert.True(manager.State.HasChangedRefs);
        Assert.DoesNotContain("commit", fake.Calls);
        Assert.DoesNotContain("push", fake.Calls);
    }

    [Fact]
    public async Task Commit_rejects_malformed_metadata_before_accessor_call()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));

        var succeeded = await manager.CommitAsync(new CommitRequest("", "Developer", "dev@example.com"));

        Assert.False(succeeded);
        Assert.DoesNotContain("commit", fake.Calls);
        Assert.Contains("required", manager.State.Error?.Message);
    }

    [Fact]
    public async Task Commit_workflow_keeps_proposed_input_manager_owned_and_forwards_accessor_contract_only_at_the_boundary()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        Assert.True(await manager.ReviewCommitAsync());

        var input = new WorkspaceCommitInput("Review central ownership", "Ada", "ada@example.com");
        manager.SetProposedCommit(input);

        Assert.Same(input, manager.State.ProposedCommit);
        Assert.True(await manager.ConfirmLocalCommitAsync());
        Assert.Contains("commit", fake.Calls);
        Assert.Null(manager.State.ProposedCommit);
        Assert.True(manager.State.LastWriteSucceeded);
    }

    [Fact]
    public async Task Commit_cancellation_preserves_bounded_cancelled_state_without_fabricating_a_commit()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        Assert.True(await manager.ReviewCommitAsync());
        manager.SetProposedCommit(new WorkspaceCommitInput("Cancel safely", "Ada", "ada@example.com"));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.CommitHandler = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new GitOperationResult<CommitInfo>("commit", true, "never", new("unexpected", "never"));
        };

        var operation = manager.ConfirmLocalCommitAsync();
        await entered.Task;
        manager.CancelCurrentOperation();

        Assert.False(await operation);
        Assert.True(manager.State.Error?.WasCancelled);
        Assert.Null(manager.State.CreatedCommit);
        Assert.Null(manager.State.LastWriteSucceeded);
        Assert.Equal(WorkspaceOperation.None, manager.State.Operation);
    }

    [Fact]
    public async Task Cancellation_is_forwarded_and_recorded_without_fabricating_success()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.StatusAsync = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new GitOperationResult<IReadOnlyList<ChangedFile>>("status", true, "impossible", []);
        };

        var operation = manager.RefreshStatusAsync();
        await entered.Task;
        Assert.True(manager.State.IsBusy);
        manager.CancelCurrentOperation();
        var succeeded = await operation;

        Assert.False(succeeded);
        Assert.False(manager.State.IsBusy);
        Assert.True(manager.State.Error?.WasCancelled);
        Assert.Equal("The repository operation was cancelled.", manager.State.Error?.Message);
    }

    [Fact]
    public async Task Conflicting_operation_is_rejected_while_active_operation_continues()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.StatusAsync = async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return new GitOperationResult<IReadOnlyList<ChangedFile>>("status", true, "Status refreshed.", []);
        };

        var active = manager.RefreshStatusAsync();
        await entered.Task;
        var conflicting = await manager.InspectPushAsync();
        release.SetResult();

        Assert.False(conflicting);
        Assert.True(await active);
        Assert.Equal(WorkspaceOperation.None, manager.State.Operation);
        Assert.Equal(0, fake.InspectCalls);
    }

    [Fact]
    public async Task Synchronization_review_preserves_decision_and_uses_review_intent()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        fake.StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>("status", true, "Clean.", []));
        fake.Synchronize = request => new GitOperationResult<SynchronizationReview>("synchronize", true, "Incoming changes.",
            new(request.RepositoryUrl, request.Branch, "local", null, [], SynchronizationDecisionState.ReadyToApply));

        Assert.True(await manager.ReviewSynchronizationAsync());

        Assert.Equal(1, fake.SynchronizeCalls);
        Assert.Equal("sync:Review", fake.Calls.Last());
        Assert.Equal(SynchronizationDecisionState.ReadyToApply, manager.State.SynchronizationDecision);
        Assert.Contains("ready", manager.State.Result?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Synchronization_apply_requires_clean_tree_and_exact_review_then_refreshes_status()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        fake.StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>("status", true, "Clean.", []));
        Assert.True(await manager.ReviewSynchronizationAsync());
        var review = Assert.IsType<SynchronizationReview>(manager.State.SynchronizationReview);

        Assert.True(await manager.ApplySynchronizationAsync(review));

        Assert.Equal(2, fake.SynchronizeCalls);
        Assert.Contains("sync:Apply", fake.Calls);
        Assert.Equal(SynchronizationDecisionState.Applied, manager.State.SynchronizationDecision);
        Assert.Equal("status", fake.Calls.Last());
    }

    [Fact]
    public async Task Synchronization_apply_rejects_unsaved_editor_before_accessor_call()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        Assert.True(await manager.SelectFileAsync(Readme.Path));
        manager.EditSelectedFile("unsaved");
        var review = new SynchronizationReview(Repository.RepositoryUrl, "main", "local", null, [], SynchronizationDecisionState.ReadyToApply);

        Assert.False(await manager.ApplySynchronizationAsync(review));

        Assert.Equal(0, fake.SynchronizeCalls);
        Assert.Equal(SynchronizationFailureCategory.UnsavedEditorChanges, manager.State.SynchronizationFailureCategory);
        Assert.DoesNotContain("diagnostic", manager.State.Error?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Synchronization_failure_maps_to_safe_category_without_accessor_diagnostic()
    {
        var fake = new FakeBrowserAccessor
        {
            Synchronize = _ => new GitOperationResult<SynchronizationReview>("synchronize", false, "raw remote response with token", Value: default,
                Diagnostic: "PAT=secret raw stack", FailureKind: GitOperationFailureKind.NetworkUnavailable)
        };
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));

        Assert.False(await manager.ReviewSynchronizationAsync());

        Assert.Equal(SynchronizationFailureCategory.NetworkUnavailable, manager.State.SynchronizationFailureCategory);
        Assert.Contains("could not be reached", manager.State.Error?.Message);
        Assert.DoesNotContain("PAT=secret", JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Synchronization_cancellation_records_cancelled_state()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.SynchronizeHandler = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new GitOperationResult<SynchronizationReview>("synchronize", true, "never", default);
        };

        var operation = manager.ReviewSynchronizationAsync();
        await entered.Task;
        manager.CancelCurrentOperation();

        Assert.False(await operation);
        Assert.Equal(SynchronizationFailureCategory.Cancelled, manager.State.SynchronizationFailureCategory);
        Assert.Equal(SynchronizationDecisionState.Cancelled, manager.State.SynchronizationDecision);
    }

    [Fact]
    public async Task Push_rejects_mismatched_review_without_reinspection_or_transport()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        await PreparePushAsync(manager);
        var altered = Review with { DestinationRef = "refs/heads/other" };

        var succeeded = await manager.PushReviewedCommitAsync(altered);

        Assert.False(succeeded);
        Assert.Equal(1, fake.InspectCalls);
        Assert.Equal(0, fake.PushCalls);
        Assert.Null(manager.State.PushReview);
        Assert.Contains("does not match", manager.State.Error?.Message);
    }

    [Fact]
    public async Task Push_rejects_coordinates_that_become_stale_after_confirmation()
    {
        var fake = new FakeBrowserAccessor();
        using var manager = CreateManager(fake);
        await PreparePushAsync(manager);
        fake.InspectResults.Enqueue(new GitOperationResult<PushReview>("inspect-push", true, "Changed.", Review with { OutgoingCommitId = "def456" }));

        var succeeded = await manager.PushReviewedCommitAsync(Review);

        Assert.False(succeeded);
        Assert.Equal(2, fake.InspectCalls);
        Assert.Equal(0, fake.PushCalls);
        Assert.Null(manager.State.PushReview);
        Assert.False(manager.State.PushConfirmed);
        Assert.Contains("changed after review", manager.State.Error?.Message);
    }

    [Fact]
    public async Task Credential_rejection_requires_explicit_replacement_review_and_retry()
    {
        var fake = new FakeBrowserAccessor
        {
            Push = _ => new GitOperationResult<PushResult>("push", false, "Credential rejected.", FailureKind: GitOperationFailureKind.CredentialRejected)
        };
        using var manager = CreateManager(fake);
        await PreparePushAsync(manager);
        fake.InspectResults.Enqueue(new GitOperationResult<PushReview>("inspect-push", true, "Still current.", Review));

        Assert.False(await manager.PushReviewedCommitAsync(Review));
        Assert.Equal(1, fake.PushCalls);
        Assert.False(manager.State.HasCredential);
        Assert.True(manager.State.CredentialReplacementRequired);
        Assert.Equal(GitOperationFailureKind.CredentialRejected, manager.State.PushFailureKind);

        Assert.False(await manager.PushReviewedCommitAsync(Review));
        Assert.Equal(1, fake.PushCalls);

        fake.Push = request => new GitOperationResult<PushResult>("push", true, "Pushed.", new(request.RepositoryUrl, request.Branch, request.DestinationRef, request.OutgoingCommitId));
        Assert.True(await manager.StoreCredentialAsync("replacement-token"));
        Assert.True(await manager.InspectPushAsync());
        manager.ConfirmPushReview(true);
        fake.InspectResults.Enqueue(new GitOperationResult<PushReview>("inspect-push", true, "Still current.", Review));

        Assert.True(await manager.PushReviewedCommitAsync(Review));
        Assert.Equal(2, fake.PushCalls);
        Assert.Null(manager.State.PushReview);
    }

    [Fact]
    public async Task Credential_text_is_never_retained_or_exposed_even_when_accessor_echoes_it()
    {
        const string secret = "ghp_DO_NOT_RETAIN_123";
        var fake = new FakeBrowserAccessor
        {
            StoreCredential = credential => new GitOperationResult<bool>("store-credential", false, $"unsafe {credential}", Value: default, Diagnostic: $"unsafe {credential}", FailureKind: GitOperationFailureKind.Unknown)
        };
        using var manager = CreateManager(fake);

        var succeeded = await manager.StoreCredentialAsync(secret);
        var serializedState = JsonSerializer.Serialize(manager.State);
        var managerStringFields = typeof(WorkspaceManager)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string?)field.GetValue(manager));

        Assert.False(succeeded);
        Assert.DoesNotContain(secret, serializedState, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, manager.State.Error?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, managerStringFields);
        Assert.True(manager.State.CredentialReplacementRequired);
    }

    [Fact]
    public async Task Accessor_exception_is_translated_to_safe_failure_state()
    {
        var fake = new FakeBrowserAccessor
        {
            FilterAsync = (_, _) => throw new InvalidOperationException("host said secret=abc")
        };
        using var manager = CreateManager(fake);

        var succeeded = await manager.OpenRepositoryAsync(Repository.RepositoryUrl);

        Assert.False(succeeded);
        Assert.Equal("The repository operation could not be completed safely.", manager.State.Error?.Message);
        Assert.Equal(GitOperationFailureKind.Unknown, manager.State.Error?.FailureKind);
        Assert.DoesNotContain("secret=abc", JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewedReconciliationRequiresSelectionAndExplicitConfirmation()
    {
        var remoteObjectId = new string('b', 40);
        var fake = new FakeBrowserAccessor
        {
            StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
                "status", true, "Status ready.", [])),
            RemoteSnapshotAsync = (_, _) => ValueTask.FromResult(new GitOperationResult<RemoteSnapshot>(
                BrowserGitRemoteSnapshotOperations.Read,
                true,
                "Remote snapshot ready.",
                CreateRemoteSnapshot(new RemoteSnapshotBlob("/remote.txt", remoteObjectId, 12))))
        };
        var applyCalls = 0;
        fake.ReviewedReconciliationApplyAsyncHandler = (request, _) =>
        {
            applyCalls++;
            return ValueTask.FromResult(new GitOperationResult<ReviewedReconciliationApplyReceipt>(
                BrowserGitReconciliationOperations.Apply,
                true,
                "Applied.",
                new ReviewedReconciliationApplyReceipt(
                    request.ReviewId,
                    request.SourceRevision,
                    request.Selections.Select(selection => selection.Path).ToArray(),
                    request.Selections.Count)));
        };

        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        Assert.True(await manager.ReviewReconciliationAsync());
        var metadata = manager.State.ReconciliationApply!;
        var request = new WorkspaceReconciliationApplyRequest(
            metadata.ReviewId,
            metadata.SourceRevision,
            [new WorkspaceReconciliationResolutionSelection(
                "/remote.txt", WorkspaceReconciliationResolution.AcceptRemote)],
            [new WorkspaceReconciliationRemotePrecondition("/remote.txt", remoteObjectId)],
            ExplicitlyConfirmed: false);

        Assert.True(manager.SelectReconciliationResolution(request));
        Assert.False(await manager.ApplyReconciliationAsync());
        Assert.Equal(0, applyCalls);
        Assert.Equal(WorkspaceReconciliationFailureKind.ApplyRejected, manager.State.ReconciliationFailureKind);
        Assert.True(manager.State.ReconciliationRetryable);

        manager.ConfirmReconciliationApply(true);
        Assert.True(await manager.ApplyReviewedReconciliationAsync());
        Assert.Equal(1, applyCalls);
        Assert.Equal(WorkspaceReconciliationStatus.Applied, manager.State.ReconciliationStatus);
        Assert.False(manager.State.ReconciliationRetryable);
    }

    [Fact]
    public async Task ReviewedReconciliationWriteFailurePreservesRetryableReview()
    {
        var remoteObjectId = new string('b', 40);
        var fake = new FakeBrowserAccessor
        {
            StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
                "status", true, "Status ready.", [])),
            RemoteSnapshotAsync = (_, _) => ValueTask.FromResult(new GitOperationResult<RemoteSnapshot>(
                BrowserGitRemoteSnapshotOperations.Read,
                true,
                "Remote snapshot ready.",
                CreateRemoteSnapshot(new RemoteSnapshotBlob("/remote.txt", remoteObjectId, 12))))
        };
        var applyCalls = 0;
        fake.ReviewedReconciliationApplyAsyncHandler = (request, _) =>
        {
            applyCalls++;
            if (applyCalls == 1)
            {
                return ValueTask.FromResult(new GitOperationResult<ReviewedReconciliationApplyReceipt>(
                    BrowserGitReconciliationOperations.Apply,
                    false,
                    "write failed: secret=do-not-leak",
                    Value: default,
                    Diagnostic: "secret=do-not-leak",
                    FailureKind: GitOperationFailureKind.ReconciliationWriteFailed));
            }

            return ValueTask.FromResult(new GitOperationResult<ReviewedReconciliationApplyReceipt>(
                BrowserGitReconciliationOperations.Apply,
                true,
                "Applied.",
                new ReviewedReconciliationApplyReceipt(
                    request.ReviewId,
                    request.SourceRevision,
                    request.Selections.Select(selection => selection.Path).ToArray(),
                    request.Selections.Count)));
        };

        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        Assert.True(await manager.ReviewReconciliationAsync());
        var metadata = manager.State.ReconciliationApply!;
        var request = new WorkspaceReconciliationApplyRequest(
            metadata.ReviewId,
            metadata.SourceRevision,
            [new WorkspaceReconciliationResolutionSelection(
                "/remote.txt", WorkspaceReconciliationResolution.AcceptRemote)],
            [new WorkspaceReconciliationRemotePrecondition("/remote.txt", remoteObjectId)],
            ExplicitlyConfirmed: false);
        Assert.True(manager.SelectReconciliationResolution(request));
        manager.ConfirmReconciliationApply(true);

        Assert.False(await manager.ApplyReviewedReconciliationAsync());
        Assert.Equal(WorkspaceReconciliationFailureKind.WriteFailed, manager.State.ReconciliationFailureKind);
        Assert.True(manager.State.ReconciliationRetryable);
        Assert.Equal(metadata.ReviewId, manager.State.ReconciliationReviewId);
        Assert.DoesNotContain("secret=do-not-leak", JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);

        Assert.True(await manager.ApplyReviewedReconciliationAsync());
        Assert.Equal(2, applyCalls);
        Assert.Equal(WorkspaceReconciliationStatus.Applied, manager.State.ReconciliationStatus);
    }

    [Fact]
    public async Task StaleRemoteInvalidatesReviewedPlanWithoutRetryingIt()
    {
        var remoteObjectId = new string('b', 40);
        var fake = new FakeBrowserAccessor
        {
            StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
                "status", true, "Status ready.", [])),
            RemoteSnapshotAsync = (_, _) => ValueTask.FromResult(new GitOperationResult<RemoteSnapshot>(
                BrowserGitRemoteSnapshotOperations.Read,
                true,
                "Remote snapshot ready.",
                CreateRemoteSnapshot(new RemoteSnapshotBlob("/remote.txt", remoteObjectId, 12))))
        };
        var applyCalls = 0;
        fake.ReviewedReconciliationApplyAsyncHandler = (_, _) =>
        {
            applyCalls++;
            return ValueTask.FromResult(new GitOperationResult<ReviewedReconciliationApplyReceipt>(
                BrowserGitReconciliationOperations.Apply,
                false,
                "stale remote: secret=do-not-leak",
                Value: default,
                Diagnostic: "secret=do-not-leak",
                FailureKind: GitOperationFailureKind.ReconciliationStaleRemote));
        };

        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        Assert.True(await manager.ReviewReconciliationAsync());
        var metadata = manager.State.ReconciliationApply!;
        Assert.True(manager.SelectReconciliationResolution(new WorkspaceReconciliationApplyRequest(
            metadata.ReviewId,
            metadata.SourceRevision,
            [new WorkspaceReconciliationResolutionSelection(
                "/remote.txt", WorkspaceReconciliationResolution.AcceptRemote)],
            [new WorkspaceReconciliationRemotePrecondition("/remote.txt", remoteObjectId)],
            ExplicitlyConfirmed: true)));
        manager.ConfirmReconciliationApply(true);

        Assert.False(await manager.ApplyReviewedReconciliationAsync());
        Assert.Equal(1, applyCalls);
        Assert.Equal(WorkspaceReconciliationFailureKind.StaleRemote, manager.State.ReconciliationFailureKind);
        Assert.False(manager.State.ReconciliationRetryable);
        Assert.Null(manager.State.ReconciliationApply);
        Assert.DoesNotContain("secret=do-not-leak", JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);

        Assert.False(await manager.ApplyReviewedReconciliationAsync());
        Assert.Equal(1, applyCalls);
    }

    [Fact]
    public async Task CancelledReviewedApplySuppressesLateAccessorCompletion()
    {
        var remoteObjectId = new string('b', 40);
        var started = new TaskCompletionSource<ReviewedReconciliationApplyRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GitOperationResult<ReviewedReconciliationApplyReceipt>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBrowserAccessor
        {
            StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
                "status", true, "Status ready.", [])),
            RemoteSnapshotAsync = (_, _) => ValueTask.FromResult(new GitOperationResult<RemoteSnapshot>(
                BrowserGitRemoteSnapshotOperations.Read,
                true,
                "Remote snapshot ready.",
                CreateRemoteSnapshot(new RemoteSnapshotBlob("/remote.txt", remoteObjectId, 12))))
        };
        fake.ReviewedReconciliationApplyAsyncHandler = async (request, _) =>
        {
            started.TrySetResult(request);
            return await release.Task;
        };

        using var manager = CreateManager(fake);
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        Assert.True(await manager.ReviewReconciliationAsync());
        var metadata = manager.State.ReconciliationApply!;
        Assert.True(manager.SelectReconciliationResolution(new WorkspaceReconciliationApplyRequest(
            metadata.ReviewId,
            metadata.SourceRevision,
            [new WorkspaceReconciliationResolutionSelection(
                "/remote.txt", WorkspaceReconciliationResolution.AcceptRemote)],
            [new WorkspaceReconciliationRemotePrecondition("/remote.txt", remoteObjectId)],
            ExplicitlyConfirmed: true)));
        manager.ConfirmReconciliationApply(true);

        var applyTask = manager.ApplyReviewedReconciliationAsync();
        var request = await started.Task;
        manager.CancelCurrentOperation();
        release.TrySetResult(new GitOperationResult<ReviewedReconciliationApplyReceipt>(
            BrowserGitReconciliationOperations.Apply,
            true,
            "Applied.",
            new ReviewedReconciliationApplyReceipt(
                request.ReviewId,
                request.SourceRevision,
                request.Selections.Select(selection => selection.Path).ToArray(),
                request.Selections.Count)));

        Assert.False(await applyTask);
        Assert.Equal(WorkspaceReconciliationStatus.Cancelled, manager.State.ReconciliationStatus);
        Assert.True(manager.State.ReconciliationRetryable);
        Assert.NotNull(manager.State.ReconciliationApply);
    }

    private static Task<bool> InvokeReconciliationCore(WorkspaceManager manager, string remoteRef = "refs/remotes/origin/main")
    {
        var method = typeof(WorkspaceManager).GetMethod(
            "ReviewReconciliationCoreAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task<bool>)method!.Invoke(manager, [remoteRef, CancellationToken.None])!;
    }

    private static WorkspaceManager CreateManager(FakeBrowserAccessor fake) => new(fake, new NoOpGitBugAccessor());

    private static RemoteSnapshot CreateRemoteSnapshot(params RemoteSnapshotBlob[] blobs)
    {
        var revision = new string('b', 40);
        return new RemoteSnapshot(
            new RemoteSnapshotRef("refs/remotes/origin/main", revision),
            new RemoteSnapshotCommit(
                revision,
                new string('c', 40),
                null,
                "Remote review",
                "Reviewer",
                DateTimeOffset.UnixEpoch),
            blobs,
            blobs.Length);
    }

    private static async Task PreparePushAsync(WorkspaceManager manager)
    {
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        Assert.True(await manager.StoreCredentialAsync("one-attempt-token"));
        Assert.True(await manager.InspectPushAsync());
        manager.ConfirmPushReview(true);
    }

    internal sealed class FakeBrowserAccessor : IBrowserGitAccessor
    {
        public List<string> Calls { get; } = [];
        public List<RepositoryOpenRequest> OpenRequests { get; } = [];
        public List<FilterFilesRequest> FilterRequests { get; } = [];
        public List<UpdateFilesRequest> UpdateRequests { get; } = [];
        public Queue<GitOperationResult<PushReview>> InspectResults { get; } = new();
        public int InspectCalls { get; private set; }
        public int PushCalls { get; private set; }
        public int SynchronizeCalls { get; private set; }
        public int CheckoutCalls { get; private set; }
        public List<CheckoutBranchRequest> CheckoutRequests { get; } = [];
        public Func<SynchronizationRequest, GitOperationResult<SynchronizationReview>> Synchronize { get; set; } = request =>
            new GitOperationResult<SynchronizationReview>("synchronize", true, "Ready.", new(
                request.RepositoryUrl, request.Branch, "local", new("incoming", "local", "Incoming", "Author", DateTimeOffset.UnixEpoch), [],
                request.Intent == SynchronizationIntent.Apply ? SynchronizationDecisionState.Applied : SynchronizationDecisionState.ReadyToApply));

        public Func<FilterFilesRequest, GitOperationResult<FilterFilesResult>> Filter { get; set; }
        public Func<FilterFilesRequest, CancellationToken, ValueTask<GitOperationResult<FilterFilesResult>>>? FilterAsync { get; set; }
        public Func<CancellationToken, Task<GitOperationResult<IReadOnlyList<ChangedFile>>>>? StatusAsync { get; set; }
        public Func<CancellationToken, ValueTask<GitOperationResult<GitPublicationReview>>>? PublicationReviewAsync { get; set; }
        public Func<CancellationToken, ValueTask<GitOperationResult<BranchSnapshot>>>? BranchesAsync { get; set; }
        public Func<RemoteSnapshotRequest, CancellationToken, ValueTask<GitOperationResult<RemoteSnapshot>>>? RemoteSnapshotAsync { get; set; }
        public Func<ReviewedReconciliationApplyRequest, CancellationToken, ValueTask<GitOperationResult<ReviewedReconciliationApplyReceipt>>>? ReviewedReconciliationApplyAsyncHandler { get; set; }
        public Func<BranchHistoryRequest, CancellationToken, ValueTask<GitOperationResult<CommitHistory>>>? HistoryAsync { get; set; }
        public Func<CheckoutBranchRequest, CancellationToken, ValueTask<GitOperationResult>>? CheckoutAsync { get; set; }
        public Func<CancellationToken, Task<GitOperationResult<SynchronizationReview>>>? SynchronizeHandler { get; set; }
        public Func<string, GitOperationResult<bool>> StoreCredential { get; set; } = _ =>
            new GitOperationResult<bool>("store-credential", true, "Stored.", true);
        public Func<CommitRequest, CancellationToken, ValueTask<GitOperationResult<CommitInfo>>>? CommitHandler { get; set; }
        public Func<PushRequest, GitOperationResult<PushResult>> Push { get; set; } = request =>
            new GitOperationResult<PushResult>("push", true, "Pushed.", new(request.RepositoryUrl, request.Branch, request.DestinationRef, request.OutgoingCommitId));

        public FakeBrowserAccessor()
        {
            Filter = DefaultFilter;
        }

        public GitOperationResult<FilterFilesResult> DefaultFilter(FilterFilesRequest request) => request.Kind switch
        {
            FileFilterKind.DirectoryEntries => new GitOperationResult<FilterFilesResult>("filter-files", true, "Files loaded.", new(request.Path, [Readme])),
            _ => new GitOperationResult<FilterFilesResult>("filter-files", true, "File loaded.", new(request.Path, [], new(request.Path, "original\n", SizeBytes: 9)))
        };

        public ValueTask<GitOperationResult<RepositoryInfo>> CloneOrOpenAsync(RepositoryOpenRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("open");
            OpenRequests.Add(request);
            return ValueTask.FromResult(new GitOperationResult<RepositoryInfo>("open", true, "Repository ready.", Repository));
        }

        public async ValueTask<GitOperationResult<IReadOnlyList<ChangedFile>>> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("status");
            if (StatusAsync is not null)
            {
                return await StatusAsync(cancellationToken);
            }
            return new GitOperationResult<IReadOnlyList<ChangedFile>>("status", true, "Status refreshed.", [new("/README.md", GitChangeKind.Modified)]);
        }

        public async ValueTask<GitOperationResult<GitPublicationReview>> InspectPublicationAsync(CancellationToken cancellationToken = default)
        {
            if (PublicationReviewAsync is null)
            {
                throw new NotSupportedException("Publication review is not configured in this test double.");
            }

            Calls.Add("publication-review");
            return await PublicationReviewAsync(cancellationToken);
        }

        public ValueTask<GitOperationResult<GitPublicationReceipt>> PublishAsync(
            GitPublicationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<GitOperationResult<GitPublicationReceipt>>(
                new NotSupportedException("Publication is not configured in this test double."));

        public async ValueTask<GitOperationResult<BranchSnapshot>> GetBranchesAsync(CancellationToken cancellationToken = default) =>
            BranchesAsync is null
                ? new GitOperationResult<BranchSnapshot>(
                    BrowserGitBranchHistoryOperations.ListBranches,
                    true,
                    "No branches loaded in this test double.",
                    new BranchSnapshot(null, [], 0))
                : await BranchesAsync(cancellationToken);

        public async ValueTask<GitOperationResult<RemoteSnapshot>> GetRemoteSnapshotAsync(RemoteSnapshotRequest request, CancellationToken cancellationToken = default) =>
            RemoteSnapshotAsync is null
                ? new GitOperationResult<RemoteSnapshot>(
                    BrowserGitRemoteSnapshotOperations.Read,
                    false,
                    "Remote snapshot is not configured in this test double.",
                    Value: default,
                    FailureKind: GitOperationFailureKind.RemoteSnapshotDataUnavailable)
                : await RemoteSnapshotAsync(request, cancellationToken);

        public async ValueTask<GitOperationResult<ReviewedReconciliationApplyReceipt>> ApplyReviewedReconciliationAsync(
            ReviewedReconciliationApplyRequest request,
            CancellationToken cancellationToken = default) =>
            ReviewedReconciliationApplyAsyncHandler is null
                ? new GitOperationResult<ReviewedReconciliationApplyReceipt>(
                    BrowserGitReconciliationOperations.Apply,
                    false,
                    "Reviewed reconciliation is not configured in this test double.",
                    Value: default,
                    FailureKind: GitOperationFailureKind.ReconciliationDataUnavailable)
                : await ReviewedReconciliationApplyAsyncHandler(request, cancellationToken);

        public async ValueTask<GitOperationResult> CheckoutBranchAsync(CheckoutBranchRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("checkout");
            CheckoutCalls++;
            CheckoutRequests.Add(request);
            if (CheckoutAsync is not null)
            {
                return await CheckoutAsync(request, cancellationToken);
            }

            return new GitOperationResult(
                BrowserGitBranchHistoryOperations.CheckoutBranch,
                true,
                "Branch switched in this test double.");
        }

        public async ValueTask<GitOperationResult<CommitHistory>> GetHistoryAsync(BranchHistoryRequest request, CancellationToken cancellationToken = default) =>
            HistoryAsync is null
                ? new GitOperationResult<CommitHistory>(
                    BrowserGitBranchHistoryOperations.ReadHistory,
                    true,
                    "No history loaded in this test double.",
                    new CommitHistory(request.BranchName, [], 0))
                : await HistoryAsync(request, cancellationToken);

        public async ValueTask<GitOperationResult<CommitInfo>> CommitAsync(CommitRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("commit");
            return CommitHandler is null
                ? new GitOperationResult<CommitInfo>("commit", true, "Committed.", new("abc123", request.Message))
                : await CommitHandler(request, cancellationToken);
        }

        public ValueTask<GitOperationResult<PushReview>> InspectPushAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("inspect");
            InspectCalls++;
            return ValueTask.FromResult(InspectResults.Count > 0
                ? InspectResults.Dequeue()
                : new GitOperationResult<PushReview>("inspect-push", true, "Ready.", Review));
        }

        public ValueTask<GitOperationResult<bool>> HasCredentialAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new GitOperationResult<bool>("has-credential", true, "Checked.", false));

        public ValueTask<GitOperationResult<bool>> StoreCredentialAsync(string credential, CancellationToken cancellationToken = default)
        {
            Calls.Add("store-credential");
            return ValueTask.FromResult(StoreCredential(credential));
        }

        public ValueTask<GitOperationResult<bool>> ForgetCredentialAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new GitOperationResult<bool>("forget-credential", true, "Forgotten.", true));

        public ValueTask<GitOperationResult<PushResult>> PushAsync(PushRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("push");
            PushCalls++;
            return ValueTask.FromResult(Push(request));
        }

        public async ValueTask<GitOperationResult<SynchronizationReview>> SynchronizeAsync(SynchronizationRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add($"sync:{request.Intent}");
            SynchronizeCalls++;
            return SynchronizeHandler is null ? Synchronize(request) : await SynchronizeHandler(cancellationToken);
        }

        public async ValueTask<GitOperationResult<FilterFilesResult>> FilterFilesAsync(FilterFilesRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add($"filter:{request.Kind}:{request.Path}");
            FilterRequests.Add(request);
            return FilterAsync is null ? Filter(request) : await FilterAsync(request, cancellationToken);
        }

        public ValueTask<GitOperationResult<UpdateFilesResult>> UpdateFilesAsync(UpdateFilesRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("update");
            UpdateRequests.Add(request);
            return ValueTask.FromResult(new GitOperationResult<UpdateFilesResult>("update-files", true, "Saved.", new(request.Updates.Select(update => update.Path).ToArray())));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
