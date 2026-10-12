using System.Text.Json;
using AlmToo.Accessor.BrowserGitAccessor.Interface;
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
}
