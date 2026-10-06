using System.Text.Json;
using AlmToo.Accessor.BrowserGitAccessor.Interface;
using AlmToo.Managers.GitBug;
using Xunit;

namespace AlmToo.Tests.Managers.GitBug;

public sealed class GitBugWorkspaceManagerTests
{
    private const string FirstIdValue = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string SecondIdValue = "2222222222222222222222222222222222222222222222222222222222222222";
    private static readonly GitBugIssueId FirstId = new(FirstIdValue);
    private static readonly GitBugIssueId SecondId = new(SecondIdValue);
    private static readonly RepositoryInfo Repository = new(
        "https://github.com/example/project.git", "project", "/workspaces/project", true);
    private static readonly GitBugIssueSummary FirstIssue = new(
        FirstId, "First issue", GitBugIssueState.Open, ["bug"], "Ada");
    private static readonly GitBugIssueSummary SecondIssue = new(
        SecondId, "Second issue", GitBugIssueState.Closed, null, null);
    private static readonly GitBugIssueDetail FirstDetail = new(
        FirstIssue, "A safe description", [new("Grace", "A safe comment")]);

    [Fact]
    public async Task List_detail_and_return_preserve_coherent_list_context()
    {
        var fake = new FakeGitBugAccessor();
        using var manager = CreateManager(fake);
        var changes = 0;
        manager.StateChanged += () => changes++;
        manager.SetRepository(Repository);

        Assert.True(await manager.LoadIssuesAsync(new(GitBugIssueState.Open, "issue", PageSize: 25)));
        Assert.Equal(GitBugWorkspaceStatus.ListReady, manager.State.Status);
        Assert.Equal([FirstIssue, SecondIssue], manager.State.Issues);
        Assert.Equal(2, manager.State.TotalCount);

        Assert.True(await manager.OpenIssueAsync(FirstId));
        Assert.Equal(GitBugWorkspaceStatus.DetailReady, manager.State.Status);
        Assert.Equal(FirstDetail, manager.State.SelectedIssue);
        Assert.Equal([FirstIssue, SecondIssue], manager.State.Issues);

        manager.ReturnToIssueList();

        Assert.Equal(GitBugWorkspaceStatus.ListReady, manager.State.Status);
        Assert.Null(manager.State.SelectedIssueId);
        Assert.Null(manager.State.SelectedIssue);
        Assert.Equal([FirstIssue, SecondIssue], manager.State.Issues);
        Assert.Equal(6, changes);
    }

    [Fact]
    public async Task Page_size_is_clamped_and_malformed_queries_are_rejected_before_accessor_calls()
    {
        var fake = new FakeGitBugAccessor();
        using var manager = CreateManager(fake);

        Assert.False(await manager.LoadIssuesAsync(new()));
        Assert.Empty(fake.ListQueries);
        Assert.Equal(GitBugWorkspaceStatus.Idle, manager.State.Status);

        manager.SetRepository(Repository);
        Assert.True(await manager.LoadIssuesAsync(new(PageSize: 0)));
        Assert.Equal(1, fake.ListQueries.Single().PageSize);

        Assert.True(await manager.LoadIssuesAsync(new(PageSize: 500)));
        Assert.Equal(100, fake.ListQueries.Last().PageSize);

        Assert.False(await manager.LoadIssuesAsync(new(SearchText: new string('x', 257))));
        Assert.False(await manager.LoadIssuesAsync(new(SearchText: "bad\rquery")));
        Assert.False(await manager.LoadIssuesAsync(new((GitBugIssueState)99)));
        Assert.Equal(2, fake.ListQueries.Count);
        Assert.Equal(GitBugWorkspaceStatus.Failed, manager.State.Status);
    }

    [Fact]
    public async Task Invalid_or_nonlisted_issue_is_rejected_before_accessor_call()
    {
        var fake = new FakeGitBugAccessor();
        using var manager = CreateManager(fake);
        manager.SetRepository(Repository);
        Assert.True(await manager.LoadIssuesAsync(new()));

        Assert.False(await manager.OpenIssueAsync(new("short")));
        Assert.False(await manager.OpenIssueAsync(new(new string('a', 64))));

        Assert.Empty(fake.DetailIds);
        Assert.Contains("current issue list", manager.State.Message);
    }

    [Theory]
    [InlineData(GitOperationFailureKind.GitBugDataUnavailable, GitBugWorkspaceStatus.Unavailable, "no supported")]
    [InlineData(GitOperationFailureKind.GitBugFormatUnsupported, GitBugWorkspaceStatus.Unsupported, "not supported")]
    [InlineData(GitOperationFailureKind.GitBugDataMalformed, GitBugWorkspaceStatus.Failed, "read safely")]
    [InlineData(GitOperationFailureKind.Unknown, GitBugWorkspaceStatus.Failed, "could not be loaded")]
    public async Task Typed_accessor_failures_become_fixed_safe_state(
        GitOperationFailureKind failureKind,
        GitBugWorkspaceStatus expectedStatus,
        string expectedMessage)
    {
        const string secret = "PAT=do-not-leak";
        var fake = new FakeGitBugAccessor
        {
            ListHandler = (_, _) => ValueTask.FromResult(new GitOperationResult<GitBugIssuePage>(
                "gitBug.listIssues", false, $"raw {secret}", Value: default,
                Diagnostic: $"stack {secret}", FailureKind: failureKind))
        };
        using var manager = CreateManager(fake);
        manager.SetRepository(Repository);

        Assert.False(await manager.LoadIssuesAsync(new()));

        Assert.Equal(expectedStatus, manager.State.Status);
        Assert.Contains(expectedMessage, manager.State.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_reload_preserves_the_previous_list_for_dismissal_or_retry()
    {
        var fake = new FakeGitBugAccessor();
        using var manager = CreateManager(fake);
        manager.SetRepository(Repository);
        Assert.True(await manager.LoadIssuesAsync(new()));
        fake.ListHandler = (_, _) => ValueTask.FromResult(Failure<GitBugIssuePage>(GitOperationFailureKind.GitBugFormatUnsupported));

        Assert.False(await manager.LoadIssuesAsync(new(GitBugIssueState.Closed)));

        Assert.Equal(GitBugWorkspaceStatus.Unsupported, manager.State.Status);
        Assert.Equal([FirstIssue, SecondIssue], manager.State.Issues);
    }

    [Fact]
    public async Task New_list_intent_cancels_and_rejects_a_stale_completion()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        var fake = new FakeGitBugAccessor
        {
            ListHandler = async (query, token) =>
            {
                if (query.SearchText == "first")
                {
                    firstToken = token;
                    firstEntered.SetResult();
                    await releaseFirst.Task;
                    return Success(new GitBugIssuePage([FirstIssue], null, 1));
                }

                return Success(new GitBugIssuePage([SecondIssue], null, 1));
            }
        };
        using var manager = CreateManager(fake);
        manager.SetRepository(Repository);

        var firstLoad = manager.LoadIssuesAsync(new(SearchText: "first"));
        await firstEntered.Task;
        var secondLoad = manager.LoadIssuesAsync(new(SearchText: "second"));

        Assert.True(await secondLoad);
        Assert.True(firstToken.IsCancellationRequested);
        releaseFirst.SetResult();
        Assert.False(await firstLoad);
        Assert.Equal(SecondIssue, Assert.Single(manager.State.Issues));
        Assert.Equal("second", fake.ListQueries.Last().SearchText);
    }

    [Fact]
    public async Task Repository_change_cancels_active_load_and_resets_all_issue_state()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var fake = new FakeGitBugAccessor
        {
            ListHandler = async (_, token) =>
            {
                observedToken = token;
                entered.SetResult();
                await release.Task;
                return Success(new GitBugIssuePage([FirstIssue], null, 1));
            }
        };
        using var manager = CreateManager(fake);
        manager.SetRepository(Repository);

        var load = manager.LoadIssuesAsync(new());
        await entered.Task;
        manager.SetRepository(Repository with { RootPath = "/workspaces/other" });

        Assert.True(observedToken.IsCancellationRequested);
        Assert.Equal(GitBugWorkspaceStatus.Idle, manager.State.Status);
        Assert.True(manager.State.RepositoryAvailable);
        Assert.Empty(manager.State.Issues);
        Assert.Null(manager.State.SelectedIssue);
        Assert.DoesNotContain("/workspaces/other", JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);

        release.SetResult();
        Assert.False(await load);
        Assert.Empty(manager.State.Issues);
    }

    [Fact]
    public async Task Accessor_normalized_cancellation_is_visible_without_fabricating_success()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeGitBugAccessor
        {
            ListHandler = async (_, token) =>
            {
                entered.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return Success(new GitBugIssuePage([], null, 0));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return Failure<GitBugIssuePage>(GitOperationFailureKind.Unknown);
                }
            }
        };
        using var manager = CreateManager(fake);
        manager.SetRepository(Repository);

        var load = manager.LoadIssuesAsync(new());
        await entered.Task;
        manager.CancelCurrentOperation();

        Assert.False(await load);
        Assert.Equal(GitBugWorkspaceStatus.Failed, manager.State.Status);
        Assert.Contains("cancelled", manager.State.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(manager.State.CanRetry);
    }

    [Fact]
    public async Task Accessor_exception_and_mismatched_detail_are_sanitized()
    {
        const string secret = "secret-object-content";
        var fake = new FakeGitBugAccessor();
        using var manager = CreateManager(fake);
        manager.SetRepository(Repository);
        Assert.True(await manager.LoadIssuesAsync(new()));
        fake.DetailHandler = (_, _) => ValueTask.FromException<GitOperationResult<GitBugIssueDetail>>(
            new InvalidOperationException(secret));

        Assert.False(await manager.OpenIssueAsync(FirstId));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);
        Assert.Equal(GitBugWorkspaceStatus.Failed, manager.State.Status);

        fake.DetailHandler = (_, _) => ValueTask.FromResult(Success(FirstDetail with { Summary = SecondIssue }));
        Assert.False(await manager.OpenIssueAsync(FirstId));
        Assert.Equal(GitOperationFailureKind.GitBugDataMalformed, manager.State.FailureKind);
        Assert.Null(manager.State.SelectedIssue);
    }

    private static GitBugWorkspaceManager CreateManager(FakeGitBugAccessor fake) => new(fake);

    private static GitOperationResult<T> Success<T>(T value) =>
        new("test", true, "Succeeded.", value);

    private static GitOperationResult<T> Failure<T>(GitOperationFailureKind failureKind) =>
        new("test", false, "raw failure", Value: default, Diagnostic: "raw diagnostic", FailureKind: failureKind);

    private sealed class FakeGitBugAccessor : IGitBugAccessor
    {
        public List<GitBugIssueQuery> ListQueries { get; } = [];
        public List<GitBugIssueId> DetailIds { get; } = [];
        public Func<GitBugIssueQuery, CancellationToken, ValueTask<GitOperationResult<GitBugIssuePage>>> ListHandler { get; set; } =
            (_, _) => ValueTask.FromResult(Success(new GitBugIssuePage([FirstIssue, SecondIssue], null, 2)));
        public Func<GitBugIssueId, CancellationToken, ValueTask<GitOperationResult<GitBugIssueDetail>>> DetailHandler { get; set; } =
            (_, _) => ValueTask.FromResult(Success(FirstDetail));

        public ValueTask<GitOperationResult<GitBugIssuePage>> ListIssuesAsync(
            GitBugIssueQuery query,
            CancellationToken cancellationToken = default)
        {
            ListQueries.Add(query);
            return ListHandler(query, cancellationToken);
        }

        public ValueTask<GitOperationResult<GitBugIssueDetail>> GetIssueAsync(
            GitBugIssueId issueId,
            CancellationToken cancellationToken = default)
        {
            DetailIds.Add(issueId);
            return DetailHandler(issueId, cancellationToken);
        }
    }
}
