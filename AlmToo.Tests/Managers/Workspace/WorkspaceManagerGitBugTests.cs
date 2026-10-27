using System.Text.Json;
using AlmToo.Accessor.BrowserGitAccessor.Interface;
using AlmToo.Managers.Workspace;
using Xunit;

namespace AlmToo.Tests.Managers.Workspace;

public sealed class WorkspaceManagerGitBugTests
{
    private static WorkspaceManager CreateManager(IGitBugAccessor accessor)
    {
        var browser = new AlmToo.Tests.Managers.Workspace.WorkspaceManagerTests.FakeBrowserAccessor();
        return new WorkspaceManager(browser, accessor);
    }

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
        FirstIssue, "revision-1", "A safe description", [new("Grace", "A safe comment")]);

    [Fact]
    public async Task List_detail_and_return_preserve_coherent_list_context()
    {
        var fake = new FakeGitBugAccessor();
        using var manager = CreateManager(fake);
        var changes = 0;
        manager.StateChanged += () => changes++;
        manager.SetRepository(Repository);

        Assert.True(await manager.LoadIssuesAsync(new(GitBugIssueState.Open, "issue", PageSize: 25)));
        Assert.Equal(GitBugWorkspaceStatus.ListReady, manager.State.GitBug.Status);
        Assert.Equal([FirstIssue, SecondIssue], manager.State.GitBug.Issues);
        Assert.Equal(2, manager.State.GitBug.TotalCount);

        Assert.True(await manager.OpenIssueAsync(FirstId));
        Assert.Equal(GitBugWorkspaceStatus.DetailReady, manager.State.GitBug.Status);
        Assert.Equal(FirstDetail, manager.State.GitBug.SelectedIssue);
        Assert.Equal([FirstIssue, SecondIssue], manager.State.GitBug.Issues);

        manager.ReturnToIssueList();

        Assert.Equal(GitBugWorkspaceStatus.ListReady, manager.State.GitBug.Status);
        Assert.Null(manager.State.GitBug.SelectedIssueId);
        Assert.Null(manager.State.GitBug.SelectedIssue);
        Assert.Equal([FirstIssue, SecondIssue], manager.State.GitBug.Issues);
        Assert.Equal(6, changes);
    }

    [Fact]
    public async Task Page_size_is_clamped_and_malformed_queries_are_rejected_before_accessor_calls()
    {
        var fake = new FakeGitBugAccessor();
        using var manager = CreateManager(fake);

        Assert.False(await manager.LoadIssuesAsync(new()));
        Assert.Empty(fake.ListQueries);
        Assert.Equal(GitBugWorkspaceStatus.Idle, manager.State.GitBug.Status);

        manager.SetRepository(Repository);
        Assert.True(await manager.LoadIssuesAsync(new(PageSize: 0)));
        Assert.Equal(1, fake.ListQueries.Single().PageSize);

        Assert.True(await manager.LoadIssuesAsync(new(PageSize: 500)));
        Assert.Equal(100, fake.ListQueries.Last().PageSize);

        Assert.False(await manager.LoadIssuesAsync(new(SearchText: new string('x', 257))));
        Assert.False(await manager.LoadIssuesAsync(new(SearchText: "bad\rquery")));
        Assert.False(await manager.LoadIssuesAsync(new((GitBugIssueState)99)));
        Assert.Equal(2, fake.ListQueries.Count);
        Assert.Equal(GitBugWorkspaceStatus.Failed, manager.State.GitBug.Status);
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
        Assert.Contains("current issue list", manager.State.GitBug.Message);
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

        Assert.Equal(expectedStatus, manager.State.GitBug.Status);
        Assert.Contains(expectedMessage, manager.State.GitBug.Message, StringComparison.OrdinalIgnoreCase);
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

        Assert.Equal(GitBugWorkspaceStatus.Unsupported, manager.State.GitBug.Status);
        Assert.Equal([FirstIssue, SecondIssue], manager.State.GitBug.Issues);
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
        Assert.Equal(SecondIssue, Assert.Single(manager.State.GitBug.Issues));
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
        Assert.Equal(GitBugWorkspaceStatus.Idle, manager.State.GitBug.Status);
        Assert.True(manager.State.GitBug.RepositoryAvailable);
        Assert.Empty(manager.State.GitBug.Issues);
        Assert.Null(manager.State.GitBug.SelectedIssue);
        Assert.DoesNotContain("/workspaces/other", JsonSerializer.Serialize(manager.State), StringComparison.Ordinal);

        release.SetResult();
        Assert.False(await load);
        Assert.Empty(manager.State.GitBug.Issues);
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
        Assert.Equal(GitBugWorkspaceStatus.Failed, manager.State.GitBug.Status);
        Assert.Contains("cancelled", manager.State.GitBug.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(manager.State.GitBug.CanRetry);
    }

    [Fact]
    public async Task Multiline_issue_description_is_accepted_and_forwarded_to_accessor()
    {
        var fake = new FakeGitBugAccessor
        {
            DetailHandler = (_, _) => ValueTask.FromResult(Success(FirstDetail)),
            EditHandler = request => ValueTask.FromResult(Success(new GitBugIssueEditResult(request.IssueId, "revision", "review")))
        };
        using var manager = CreateManager(fake);
        manager.SetRepository(Repository);
        Assert.True(await manager.LoadIssuesAsync(new(SearchText: "reviewed")));
        Assert.True(await manager.OpenIssueAsync(FirstId));

        Assert.True(await manager.EditSelectedIssueAsync("Updated title", "First line\nSecond line"));

        var request = Assert.Single(fake.EditRequests);
        Assert.Equal("First line\nSecond line", request.Description);
    }

    [Fact]
    public async Task Multiline_comment_is_accepted_and_forwarded_to_accessor()
    {
        var fake = new FakeGitBugAccessor
        {
            DetailHandler = (_, _) => ValueTask.FromResult(Success(FirstDetail)),
            CommentHandler = request => ValueTask.FromResult(Success(new GitBugCommentCreateResult(
                request.IssueId, "comment", "revision", "review")))
        };
        using var manager = CreateManager(fake);
        manager.SetRepository(Repository);
        Assert.True(await manager.LoadIssuesAsync(new(SearchText: "reviewed")));
        Assert.True(await manager.OpenIssueAsync(FirstId));

        Assert.True(await manager.AddCommentToSelectedIssueAsync("First line\nSecond line"));

        var request = Assert.Single(fake.CommentRequests);
        Assert.Equal("First line\nSecond line", request.Body);
    }

    [Fact]
    public async Task Valid_issue_edit_calls_accessor_once_and_refreshes_detail_and_list()
    {
        var fake = new FakeGitBugAccessor
        {
            DetailHandler = (_, _) => ValueTask.FromResult(Success(FirstDetail with { Description = string.Empty })),
            EditHandler = request => ValueTask.FromResult(Success(new GitBugIssueEditResult(request.IssueId, "revision", "review")))
        };
        using var manager = CreateManager(fake);
        manager.SetRepository(Repository);
        Assert.True(await manager.LoadIssuesAsync(new(SearchText: "reviewed")));
        Assert.True(await manager.OpenIssueAsync(FirstId));

        Assert.True(await manager.EditSelectedIssueAsync("Updated title", string.Empty));

        var request = Assert.Single(fake.EditRequests);
        Assert.Equal(FirstId, request.IssueId);
        Assert.Equal("Updated title", request.Title);
        Assert.Equal(string.Empty, request.Description);
        Assert.Equal(GitBugWorkspaceStatus.DetailReady, manager.State.GitBug.Status);
        Assert.Equal(string.Empty, manager.State.GitBug.SelectedIssue!.Description);
        Assert.True(manager.State.GitBug.LastWriteSucceeded);
        Assert.True(manager.State.GitBug.LastRefreshSucceeded);
        Assert.Contains("No commit or push", manager.State.GitBug.Message);
    }

    [Fact]
    public async Task Issue_edit_and_comment_refresh_issue_state_without_starting_commit_or_push()
    {
        var fake = new FakeGitBugAccessor
        {
            DetailHandler = (_, _) => ValueTask.FromResult(Success(FirstDetail)),
            EditHandler = request => ValueTask.FromResult(Success(new GitBugIssueEditResult(request.IssueId, "revision-edit", "review-edit"))),
            CommentHandler = request => ValueTask.FromResult(Success(new GitBugCommentCreateResult(
                request.IssueId, "comment", "revision-comment", "review-comment")))
        };
        using var manager = CreateManager(fake);
        manager.SetRepository(Repository);
        Assert.True(await manager.LoadIssuesAsync(new(SearchText: "reviewed")));
        Assert.True(await manager.OpenIssueAsync(FirstId));

        fake.Calls.Clear();
        Assert.True(await manager.EditSelectedIssueAsync("Updated title", "Updated description"));
        Assert.Equal(["edit", "detail", "list"], fake.Calls);
        Assert.DoesNotContain("commit", fake.Calls);
        Assert.DoesNotContain("push", fake.Calls);

        fake.Calls.Clear();
        Assert.True(await manager.AddCommentToSelectedIssueAsync("A follow-up comment"));
        Assert.Equal(["comment", "detail", "list"], fake.Calls);
        Assert.DoesNotContain("commit", fake.Calls);
        Assert.DoesNotContain("push", fake.Calls);
    }

    [Fact]
    public async Task Invalid_edit_and_comment_inputs_do_not_call_accessor()
    {
        var fake = new FakeGitBugAccessor();
        using var manager = CreateManager(fake);
        manager.SetRepository(Repository);
        Assert.True(await manager.LoadIssuesAsync(new()));
        Assert.True(await manager.OpenIssueAsync(FirstId));

        Assert.False(await manager.EditSelectedIssueAsync("bad\n title", "description"));
        Assert.False(await manager.EditSelectedIssueAsync("Valid title", null!));
        Assert.False(await manager.AddCommentToSelectedIssueAsync(string.Empty));
        Assert.Empty(fake.EditRequests);
        Assert.Empty(fake.CommentRequests);
        Assert.Contains("valid", manager.State.GitBug.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(GitBugWorkspaceStatus.Failed, manager.State.GitBug.Status);

        fake.DetailHandler = (_, _) => ValueTask.FromResult(Success(FirstDetail with { Summary = SecondIssue }));
        Assert.False(await manager.OpenIssueAsync(FirstId));
        Assert.Equal(GitOperationFailureKind.GitBugDataMalformed, manager.State.GitBug.FailureKind);
        Assert.Null(manager.State.GitBug.SelectedIssue);
    }

    private static WorkspaceManager CreateManager(FakeGitBugAccessor fake) => CreateManager((IGitBugAccessor)fake);

    private static GitOperationResult<T> Success<T>(T value) =>
        new("test", true, "Succeeded.", value);

    private static GitOperationResult<T> Failure<T>(GitOperationFailureKind failureKind) =>
        new("test", false, "raw failure", Value: default, Diagnostic: "raw diagnostic", FailureKind: failureKind);

    private sealed class FakeGitBugAccessor : IGitBugAccessor
    {
        public List<string> Calls { get; } = [];
        public List<GitBugIssueQuery> ListQueries { get; } = [];
        public List<GitBugIssueId> DetailIds { get; } = [];
        public List<GitBugIssueEditRequest> EditRequests { get; } = [];
        public List<GitBugCommentCreateRequest> CommentRequests { get; } = [];
        public Func<GitBugIssueQuery, CancellationToken, ValueTask<GitOperationResult<GitBugIssuePage>>> ListHandler { get; set; } =
            (_, _) => ValueTask.FromResult(Success(new GitBugIssuePage([FirstIssue, SecondIssue], null, 2)));
        public Func<GitBugIssueId, CancellationToken, ValueTask<GitOperationResult<GitBugIssueDetail>>> DetailHandler { get; set; } =
            (_, _) => ValueTask.FromResult(Success(FirstDetail));
        public Func<GitBugIssueEditRequest, ValueTask<GitOperationResult<GitBugIssueEditResult>>> EditHandler { get; set; } =
            _ => ValueTask.FromResult(Failure<GitBugIssueEditResult>(GitOperationFailureKind.GitBugWriteUnavailable));
        public Func<GitBugCommentCreateRequest, ValueTask<GitOperationResult<GitBugCommentCreateResult>>> CommentHandler { get; set; } =
            _ => ValueTask.FromResult(Failure<GitBugCommentCreateResult>(GitOperationFailureKind.GitBugWriteUnavailable));

        public ValueTask<GitOperationResult<GitBugIssuePage>> ListIssuesAsync(
            GitBugIssueQuery query,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("list");
            ListQueries.Add(query);
            return ListHandler(query, cancellationToken);
        }

        public ValueTask<GitOperationResult<GitBugIssueDetail>> GetIssueAsync(
            GitBugIssueId issueId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("detail");
            DetailIds.Add(issueId);
            return DetailHandler(issueId, cancellationToken);
        }

        public ValueTask<GitOperationResult<GitBugIssueEditResult>> EditIssueAsync(
            GitBugIssueEditRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("edit");
            EditRequests.Add(request);
            return EditHandler(request);
        }

        public ValueTask<GitOperationResult<GitBugCommentCreateResult>> CreateCommentAsync(
            GitBugCommentCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("comment");
            CommentRequests.Add(request);
            return CommentHandler(request);
        }
    }
}
