using AlmToo.Accessor.BrowserGitAccessor.Interface;
using AlmToo.Managers.Workspace;
using AlmToo.Tests.Managers.Workspace;
using Xunit;

namespace AlmToo.Tests.Managers.Workspace;

public sealed class WorkspaceManagerBranchTests
{
    private static readonly RepositoryInfo Repository = new(
        "https://github.com/example/project.git", "project", "/workspaces/project", true);
    private static readonly IReadOnlyList<ChangedFile> CleanStatus = [];
    private static readonly BranchSnapshot FeatureBranches = new(
        new CurrentBranch("feature", false),
        [new BranchChoice("main", BranchChoiceKind.Local), new BranchChoice("feature", BranchChoiceKind.Local, true)],
        2);
    private static readonly CommitHistory FeatureHistory = new(
        "feature",
        [new CommitHistoryEntry(new string('a', 40), null, "Feature work", "Developer", DateTimeOffset.UnixEpoch.AddDays(1))],
        1);

    [Fact]
    public async Task SwitchBranch_requires_fresh_clean_status_and_refreshes_full_workspace()
    {
        var fake = CreateCleanFake();
        fake.BranchesAsync = _ => ValueTask.FromResult(new GitOperationResult<BranchSnapshot>(
            BrowserGitBranchHistoryOperations.ListBranches, true, "Branches refreshed.", FeatureBranches));
        fake.HistoryAsync = (request, _) => ValueTask.FromResult(new GitOperationResult<CommitHistory>(
            BrowserGitBranchHistoryOperations.ReadHistory, true, "History refreshed.", FeatureHistory));
        using var manager = new WorkspaceManager(fake, new NoOpGitBugAccessor());
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));

        var callsBeforeSwitch = fake.Calls.Count;
        var succeeded = await manager.SwitchBranchAsync("feature");

        Assert.True(succeeded);
        Assert.Equal(1, fake.CheckoutCalls);
        Assert.Equal("feature", Assert.Single(fake.CheckoutRequests).BranchName);
        Assert.Equal(["status", "checkout", "filter:DirectoryEntries:/", "status"], fake.Calls.Skip(callsBeforeSwitch).Take(4));
        Assert.Equal(FeatureBranches.Current, manager.State.CurrentBranch);
        Assert.Equal(FeatureBranches.Choices, manager.State.AvailableBranches);
        Assert.Equal(FeatureHistory, manager.State.RecentHistory);
        Assert.Equal("/", manager.State.CurrentPath);
        Assert.Null(manager.State.SelectedFilePath);
        Assert.Empty(manager.State.ChangedFiles);
        Assert.False(manager.State.IsBranchSwitching);
        Assert.True(manager.State.Result?.Succeeded);
        Assert.Contains("Switched to branch feature", manager.State.Result?.Message);
    }

    [Fact]
    public async Task SwitchBranch_rejects_dirty_worktree_without_checkout_or_workspace_mutation()
    {
        var fake = CreateCleanFake();
        using var manager = new WorkspaceManager(fake, new NoOpGitBugAccessor());
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        var beforeCalls = fake.Calls.Count;
        var beforeFilters = fake.FilterRequests.Count;
        fake.StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
            "status", true, "Dirty.", [new ChangedFile("/tracked.txt", GitChangeKind.Modified)]));

        var succeeded = await manager.SwitchBranchAsync("feature");

        Assert.False(succeeded);
        Assert.Equal(0, fake.CheckoutCalls);
        Assert.Equal(["status"], fake.Calls.Skip(beforeCalls));
        Assert.Equal(beforeFilters, fake.FilterRequests.Count);
        Assert.Single(manager.State.ChangedFiles);
        Assert.Equal(GitOperationFailureKind.BranchSwitchDirtyWorktree, manager.State.Error?.FailureKind);
        Assert.Contains("Commit or discard", manager.State.Error?.Message);
        Assert.False(manager.State.IsBranchSwitching);
    }

    [Fact]
    public async Task SwitchBranch_stops_before_checkout_when_status_is_unavailable_and_hides_diagnostics()
    {
        var fake = CreateCleanFake();
        using var manager = new WorkspaceManager(fake, new NoOpGitBugAccessor());
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        fake.StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>(
            "status", false, "raw host response", Value: default, Diagnostic: "token=secret", FailureKind: GitOperationFailureKind.BranchSwitchUnavailable));

        var succeeded = await manager.SwitchBranchAsync("feature");

        Assert.False(succeeded);
        Assert.Equal(0, fake.CheckoutCalls);
        Assert.Equal(GitOperationFailureKind.BranchSwitchUnavailable, manager.State.Error?.FailureKind);
        Assert.Equal("The repository state is unavailable; no branch switch was completed.", manager.State.Error?.Message);
        Assert.DoesNotContain("token=secret", manager.State.Error?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(manager.State.Error?.Diagnostic);
    }

    [Fact]
    public async Task SwitchBranch_cancellation_is_recorded_and_does_not_checkout()
    {
        var fake = CreateCleanFake();
        using var manager = new WorkspaceManager(fake, new NoOpGitBugAccessor());
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.StatusAsync = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new GitOperationResult<IReadOnlyList<ChangedFile>>("status", true, "never", CleanStatus);
        };

        using var cancellation = new CancellationTokenSource();
        var operation = manager.SwitchBranchAsync("feature", cancellation.Token);
        await entered.Task;
        cancellation.Cancel();

        Assert.False(await operation);
        Assert.Equal(0, fake.CheckoutCalls);
        Assert.True(manager.State.Error?.WasCancelled);
        Assert.False(manager.State.IsBranchSwitching);
    }

    [Fact]
    public async Task SwitchBranch_discards_stale_clean_status_after_cancellation_before_checkout()
    {
        var fake = CreateCleanFake();
        using var manager = new WorkspaceManager(fake, new NoOpGitBugAccessor());
        Assert.True(await manager.OpenRepositoryAsync(Repository.RepositoryUrl));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.StatusAsync = async token =>
        {
            entered.SetResult();
            await release.Task;
            return new GitOperationResult<IReadOnlyList<ChangedFile>>("status", true, "Clean.", CleanStatus);
        };

        using var cancellation = new CancellationTokenSource();
        var operation = manager.SwitchBranchAsync("feature", cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        release.SetResult();

        Assert.False(await operation);
        Assert.Equal(0, fake.CheckoutCalls);
        Assert.True(manager.State.Error?.WasCancelled);
        Assert.False(manager.State.IsBranchSwitching);
    }

    private static WorkspaceManagerTests.FakeBrowserAccessor CreateCleanFake()
    {
        var fake = new WorkspaceManagerTests.FakeBrowserAccessor
        {
            StatusAsync = _ => Task.FromResult(new GitOperationResult<IReadOnlyList<ChangedFile>>("status", true, "Clean.", CleanStatus))
        };
        return fake;
    }
}
