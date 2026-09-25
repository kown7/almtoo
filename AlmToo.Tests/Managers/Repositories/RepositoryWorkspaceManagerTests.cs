using System.Text.Json;
using AlmToo.Accessor.BrowserGitAccessor.Interface;
using AlmToo.Resource.BrowserGitResource.Data;
using AlmToo.Managers.Repositories;
using Xunit;

namespace AlmToo.Tests.Managers.Repositories;

public sealed class RepositoryWorkspaceManagerTests
{
    private static readonly RepositoryInfo Repository = new(
        "https://github.com/example/project.git", "project", "/workspaces/project", true);
    private static readonly RepositoryFileEntry Readme = new(
        "/README.md", "README.md", RepositoryFileKind.File, 12, true);
    private static readonly PushReview Review = new(
        Repository.RepositoryUrl, "main", "abc123", "refs/heads/main");

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
        Assert.Equal("path-containment", manager.State.Error?.Diagnostic);
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
        Assert.Equal(RepositoryWorkspaceOperation.None, manager.State.Operation);
        Assert.Equal(0, fake.InspectCalls);
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
        var managerStringFields = typeof(RepositoryWorkspaceManager)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string?)field.GetValue(manager));

        Assert.False(succeeded);
        Assert.DoesNotContain(secret, serializedState, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, manager.State.Error?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, manager.State.Error?.Diagnostic ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, managerStringFields);
        Assert.True(manager.State.CredentialReplacementRequired);
    }

    [Fact]
    public async Task Accessor_exception_is_translated_to_safe_type_only_diagnostic()
    {
        var fake = new FakeBrowserAccessor
        {
            FilterAsync = (_, _) => throw new InvalidOperationException("host said secret=abc")
        };
        using var manager = CreateManager(fake);

        var succeeded = await manager.OpenRepositoryAsync(Repository.RepositoryUrl);

        Assert.False(succeeded);
        Assert.Equal("The repository operation could not be completed safely.", manager.State.Error?.Message);
        Assert.Equal(nameof(InvalidOperationException), manager.State.Error?.Diagnostic);
        Assert.DoesNotContain("secret=abc", JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);
    }

    private static RepositoryWorkspaceManager CreateManager(FakeBrowserAccessor fake) => new(fake, fake);

    private static async Task PreparePushAsync(RepositoryWorkspaceManager manager)
    {
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        Assert.True(await manager.StoreCredentialAsync("one-attempt-token"));
        Assert.True(await manager.InspectPushAsync());
        manager.ConfirmPushReview(true);
    }

    private sealed class FakeBrowserAccessor : IBrowserGitAccessor, IBrowserFileAccessor
    {
        public List<string> Calls { get; } = [];
        public List<RepositoryOpenRequest> OpenRequests { get; } = [];
        public List<FilterFilesRequest> FilterRequests { get; } = [];
        public List<UpdateFilesRequest> UpdateRequests { get; } = [];
        public Queue<GitOperationResult<PushReview>> InspectResults { get; } = new();
        public int InspectCalls { get; private set; }
        public int PushCalls { get; private set; }

        public Func<FilterFilesRequest, GitOperationResult<FilterFilesResult>> Filter { get; set; }
        public Func<FilterFilesRequest, CancellationToken, ValueTask<GitOperationResult<FilterFilesResult>>>? FilterAsync { get; set; }
        public Func<CancellationToken, Task<GitOperationResult<IReadOnlyList<ChangedFile>>>>? StatusAsync { get; set; }
        public Func<string, GitOperationResult<bool>> StoreCredential { get; set; } = _ =>
            new GitOperationResult<bool>("store-credential", true, "Stored.", true);
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

        public ValueTask<GitOperationResult<CommitInfo>> CommitAsync(CommitRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("commit");
            return ValueTask.FromResult(new GitOperationResult<CommitInfo>("commit", true, "Committed.", new("abc123", request.Message)));
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
