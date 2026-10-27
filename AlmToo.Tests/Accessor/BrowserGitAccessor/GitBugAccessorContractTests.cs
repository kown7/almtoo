using System.Text.Json;
using AlmToo.Accessor.BrowserGitAccessor.Interface;
using BrowserGitAccessorService = AlmToo.Accessor.BrowserGitAccessor.Service.BrowserGitAccessor;
using Microsoft.JSInterop;
using Xunit;

namespace AlmToo.Tests.Accessor.BrowserGitAccessor;

public sealed class GitBugAccessorContractTests
{
    private const string IssueId = "a85873c253c45c3d1ca6b84c0810e4b15551cdff38a3f001ae5cd52d879e28e1";
    private const string PrivateValue = "private issue text https://example.test/repository.git secret-token";

    [Fact]
    public void Facet_is_owned_by_existing_browser_accessor_and_contract_is_passive()
    {
        Assert.True(typeof(IGitBugAccessor).IsAssignableFrom(typeof(BrowserGitAccessorService)));

        var contractTypes = new[]
        {
            typeof(GitBugIssueQuery),
            typeof(GitBugIssuePage),
            typeof(GitBugIssueSummary),
            typeof(GitBugIssueDetail),
            typeof(GitBugComment)
        };

        foreach (var type in contractTypes)
        {
            Assert.DoesNotContain(type.GetProperties(), property =>
                property.Name.Contains("path", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("url", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("ref", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("diagnostic", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Supported_list_and_detail_responses_map_to_fixture_proven_contracts()
    {
        var module = new FakeModule(new Dictionary<string, string>
        {
            ["listGitBugIssues"] = Success("gitBug.listIssues", new
            {
                issues = new object[]
                {
                    new
                    {
                        id = IssueId,
                        title = "Unicode café regression 🐛",
                        state = "open",
                        labels = new[] { "defect", "priority:high" },
                        authorDisplayName = "Fixture Author"
                    },
                    new
                    {
                        id = "e51017b91a393da8fdc5575771ac2d1f4bba60fee8bfdaaecbfff991728a9fb8",
                        title = "Minimal issue without optional metadata",
                        state = "open",
                        labels = (string[]?)null,
                        authorDisplayName = "Fixture Author"
                    }
                },
                nextCursor = (string?)null,
                totalCount = 2
            }),
            ["getGitBugIssue"] = Success("gitBug.getIssue", new
            {
                summary = new
                {
                    id = IssueId,
                    title = "Unicode café regression 🐛",
                    state = "open",
                    labels = new[] { "defect", "priority:high" },
                    authorDisplayName = "Fixture Author"
                },
                revision = "1111111111111111111111111111111111111111",
                description = "Opening the résumé view shows naïve text.",
                comments = new[]
                {
                    new { authorDisplayName = "Fixture Author", body = "Confirmed on the browser fixture.\nSecond line with Ελληνικά." }
                }
            })
        });
        await using var accessor = new BrowserGitAccessorService(new FakeJsRuntime(module));

        var page = await accessor.ListIssuesAsync(new(GitBugIssueState.Open, "café", null, 25));
        var detail = await accessor.GetIssueAsync(new(IssueId));

        Assert.True(page.Succeeded);
        Assert.Null(page.Diagnostic);
        Assert.Null(page.FailureKind);
        Assert.Equal("gitBug.listIssues", page.Operation);
        Assert.Equal(2, page.Value!.TotalCount);
        Assert.Equal("Unicode café regression 🐛", page.Value.Issues[0].Title);
        Assert.Null(page.Value.Issues[1].Labels);
        Assert.True(detail.Succeeded);
        Assert.Equal("Opening the résumé view shows naïve text.", detail.Value!.Description);
        Assert.Equal("Confirmed on the browser fixture.\nSecond line with Ελληνικά.", detail.Value.Comments.Single().Body);
        Assert.Equal(new[] { "initialize", "listGitBugIssues", "getGitBugIssue" }, module.Invocations);

        var query = JsonSerializer.SerializeToElement(module.Arguments["listGitBugIssues"].Single());
        Assert.Equal("open", query.GetProperty("state").GetString());
        Assert.Equal("café", query.GetProperty("searchText").GetString());
        Assert.Equal(25, query.GetProperty("pageSize").GetInt32());
        Assert.Equal(IssueId, module.Arguments["getGitBugIssue"].Single());
    }

    [Fact]
    public async Task Null_detail_description_maps_to_safe_empty_text()
    {
        var module = new FakeModule(new Dictionary<string, string>
        {
            ["getGitBugIssue"] = Success("gitBug.getIssue", new
            {
                summary = new
                {
                    id = IssueId,
                    title = "Minimal issue",
                    state = "open",
                    labels = (string[]?)null,
                    authorDisplayName = (string?)null
                },
                revision = "2222222222222222222222222222222222222222",
                description = (string?)null,
                comments = Array.Empty<object>()
            })
        });
        await using var accessor = new BrowserGitAccessorService(new FakeJsRuntime(module));

        var detail = await accessor.GetIssueAsync(new(IssueId));

        Assert.True(detail.Succeeded);
        Assert.Equal(string.Empty, detail.Value!.Description);
        Assert.Empty(detail.Value.Comments);
        Assert.Null(detail.Diagnostic);
    }

    [Fact]
    public async Task Omitted_detail_description_maps_to_safe_empty_text()
    {
        var module = new FakeModule(new Dictionary<string, string>
        {
            ["getGitBugIssue"] = JsonSerializer.Serialize(new
            {
                operation = "gitBug.getIssue",
                succeeded = true,
                message = "Loaded.",
                value = new
                {
                    summary = new
                    {
                        id = IssueId,
                        title = "Minimal issue",
                        state = "open",
                        labels = (string[]?)null,
                        authorDisplayName = (string?)null
                    },
                    revision = "3333333333333333333333333333333333333333",
                    comments = Array.Empty<object>()
                }
            })
        });
        await using var accessor = new BrowserGitAccessorService(new FakeJsRuntime(module));

        var detail = await accessor.GetIssueAsync(new(IssueId));

        Assert.True(detail.Succeeded);
        Assert.Equal(string.Empty, detail.Value!.Description);
        Assert.Empty(detail.Value.Comments);
        Assert.Null(detail.Diagnostic);
    }

    [Theory]
    [InlineData("gitBugDataUnavailable", GitOperationFailureKind.GitBugDataUnavailable, "no supported Git-Bug issue data")]
    [InlineData("gitBugFormatUnsupported", GitOperationFailureKind.GitBugFormatUnsupported, "version is not supported")]
    [InlineData("gitBugDataMalformed", GitOperationFailureKind.GitBugDataMalformed, "could not be read safely")]
    [InlineData("credentialRejected", GitOperationFailureKind.Unknown, "could not be loaded")]
    public async Task Fixture_proven_failures_map_to_fixed_safe_outcomes(
        string bridgeFailure,
        GitOperationFailureKind expectedFailure,
        string expectedMessage)
    {
        var module = new FakeModule(new Dictionary<string, string>
        {
            ["listGitBugIssues"] = Failure("gitBug.listIssues", bridgeFailure),
            ["getGitBugIssue"] = Failure("gitBug.getIssue", bridgeFailure)
        });
        await using var accessor = new BrowserGitAccessorService(new FakeJsRuntime(module));

        var page = await accessor.ListIssuesAsync(new());
        var detail = await accessor.GetIssueAsync(new(IssueId));

        Assert.Equal("gitBug.listIssues", page.Operation);
        Assert.Equal("gitBug.getIssue", detail.Operation);
        AssertSafeFailure(page, expectedFailure, expectedMessage);
        AssertSafeFailure(detail, expectedFailure, expectedMessage);
    }

    [Fact]
    public async Task Malformed_list_and_detail_values_fail_closed_without_private_content()
    {
        var malformedSummary = new
        {
            id = IssueId,
            title = PrivateValue,
            state = "invented",
            labels = Array.Empty<string>(),
            authorDisplayName = "Fixture Author"
        };
        var module = new FakeModule(new Dictionary<string, string>
        {
            ["listGitBugIssues"] = Success("gitBug.listIssues", new
            {
                issues = new[] { malformedSummary },
                nextCursor = (string?)null,
                totalCount = 1
            }),
            ["getGitBugIssue"] = Success("gitBug.getIssue", new
            {
                summary = malformedSummary,
                description = PrivateValue,
                comments = new[] { new { authorDisplayName = "Fixture Author", body = PrivateValue } }
            })
        });
        await using var accessor = new BrowserGitAccessorService(new FakeJsRuntime(module));

        var page = await accessor.ListIssuesAsync(new());
        var detail = await accessor.GetIssueAsync(new(IssueId));

        AssertSafeFailure(page, GitOperationFailureKind.GitBugDataMalformed, "could not be read safely");
        AssertSafeFailure(detail, GitOperationFailureKind.GitBugDataMalformed, "could not be read safely");
    }

    [Fact]
    public async Task Interop_exceptions_and_cancellation_are_reduced_to_fixed_safe_failures()
    {
        var module = new FakeModule(new Dictionary<string, string>
        {
            ["listGitBugIssues"] = FakeModule.ThrowResponse,
            ["getGitBugIssue"] = FakeModule.CancelResponse
        });
        await using var accessor = new BrowserGitAccessorService(new FakeJsRuntime(module));

        var page = await accessor.ListIssuesAsync(new());
        var detail = await accessor.GetIssueAsync(new(IssueId), new CancellationToken(canceled: true));

        AssertSafeFailure(page, GitOperationFailureKind.Unknown, "could not be loaded");
        AssertSafeFailure(detail, GitOperationFailureKind.Unknown, "could not be loaded");
    }

    [Fact]
    public async Task Aggregate_detail_payload_over_four_mebibytes_fails_closed()
    {
        var oneMebibyte = new string('x', 1024 * 1024);
        var module = new FakeModule(new Dictionary<string, string>
        {
            ["getGitBugIssue"] = Success("gitBug.getIssue", new
            {
                summary = new
                {
                    id = IssueId,
                    title = "Bounded issue",
                    state = "open",
                    labels = Array.Empty<string>(),
                    authorDisplayName = "Fixture Author"
                },
                description = oneMebibyte,
                comments = Enumerable.Range(0, 4)
                    .Select(_ => new { authorDisplayName = "Fixture Author", body = oneMebibyte })
                    .ToArray()
            })
        });
        await using var accessor = new BrowserGitAccessorService(new FakeJsRuntime(module));

        var result = await accessor.GetIssueAsync(new(IssueId));

        Assert.False(result.Succeeded);
        Assert.Equal(GitOperationFailureKind.GitBugDataMalformed, result.FailureKind);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public async Task Write_contracts_validate_before_bridge_and_map_supported_requests_safely()
    {
        var module = new FakeModule(new Dictionary<string, string>
        {
            ["editGitBugIssue"] = FakeModule.ThrowResponse,
            ["createGitBugComment"] = FakeModule.ThrowResponse
        });
        await using var accessor = new BrowserGitAccessorService(new FakeJsRuntime(module));
        var issueId = new GitBugIssueId(IssueId);
        var preconditions = new GitBugWritePreconditions(
            new GitBugRepositoryIdentity("https://github.com/example/project.git", "workspace"),
            issueId,
            "revision-1",
            GitBugWriteOperations.IssueEdit);
        var edit = new GitBugIssueEditRequest(
            issueId,
            "Updated title",
            "Updated description",
            preconditions,
            new GitBugSerializationMetadata("v0.11.0", "utf-8", true));
        var invalid = edit with { Title = new string('x', GitBugWriteContract.MaximumTitleLength + 1) };
        var validResult = await accessor.EditIssueAsync(edit);
        var invalidResult = await accessor.EditIssueAsync(invalid);
        var commentResult = await accessor.CreateCommentAsync(new GitBugCommentCreateRequest(
            issueId,
            "A fixture comment",
            preconditions with { ReviewedOperation = GitBugWriteOperations.CommentCreate },
            edit.Serialization));

        Assert.False(validResult.Succeeded);
        Assert.Equal(GitOperationFailureKind.Unknown, validResult.FailureKind);
        Assert.Equal(GitBugWriteOperations.IssueEdit, validResult.Operation);
        Assert.False(invalidResult.Succeeded);
        Assert.Equal(GitOperationFailureKind.GitBugWriteInvalidRequest, invalidResult.FailureKind);
        Assert.False(commentResult.Succeeded);
        Assert.Equal(GitOperationFailureKind.Unknown, commentResult.FailureKind);
        Assert.Null(validResult.Diagnostic);
        Assert.Null(invalidResult.Diagnostic);
        Assert.Null(commentResult.Diagnostic);
        Assert.Equal(new[] { "initialize", "editGitBugIssue", "createGitBugComment" }, module.Invocations);
    }

    [Theory]
    [InlineData("gitBugWriteInvalidRequest", GitOperationFailureKind.GitBugWriteInvalidRequest)]
    [InlineData("gitBugWritePreconditionFailed", GitOperationFailureKind.GitBugWritePreconditionFailed)]
    [InlineData("gitBugWriteConflict", GitOperationFailureKind.GitBugWriteConflict)]
    [InlineData("gitBugWriteUnsupportedFormat", GitOperationFailureKind.GitBugWriteUnsupportedFormat)]
    [InlineData("gitBugWriteUnavailable", GitOperationFailureKind.GitBugWriteUnavailable)]
    [InlineData("gitBugWriteSerializationFailed", GitOperationFailureKind.GitBugWriteSerializationFailed)]
    [InlineData("gitBugWriteUnknown", GitOperationFailureKind.GitBugWriteUnknown)]
    public void Write_failure_names_are_explicitly_mapped(string name, GitOperationFailureKind expected)
    {
        var method = typeof(BrowserGitAccessorService).GetMethod("ParseGitBugFailureKind", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Equal(expected, method!.Invoke(null, [name]));
    }

    [Fact]
    public async Task Invalid_and_oversized_requests_never_cross_the_bridge()
    {
        var module = new FakeModule(new Dictionary<string, string>());
        await using var accessor = new BrowserGitAccessorService(new FakeJsRuntime(module));

        var invalidPage = await accessor.ListIssuesAsync(new(PageSize: 101));
        var oversizedQuery = await accessor.ListIssuesAsync(new(SearchText: new string('x', 257)));
        var invalidId = await accessor.GetIssueAsync(new("not-an-id"));

        AssertInvalid(invalidPage);
        AssertInvalid(oversizedQuery);
        AssertInvalid(invalidId);
        Assert.Empty(module.Invocations);
    }

    [Fact]
    public async Task Publication_accessor_maps_reviewed_branch_and_git_bug_refs_without_credentials()
    {
        var head = new string('a', 40);
        var bugObject = new string('b', 40);
        var identityObject = new string('c', 40);
        var updates = new[]
        {
            new
            {
                refName = "refs/heads/main",
                @namespace = "Branch",
                kind = "Update",
                newObjectId = head,
                expectedOldObjectId = (string?)head
            },
            new
            {
                refName = "refs/bugs/issue-1",
                @namespace = "GitBug",
                kind = "Create",
                newObjectId = bugObject,
                expectedOldObjectId = (string?)null
            },
            new
            {
                refName = "refs/identities/author-1",
                @namespace = "GitBug",
                kind = "Update",
                newObjectId = identityObject,
                expectedOldObjectId = identityObject
            }
        };
        var preconditions = new[]
        {
            new { refName = "refs/heads/main", expectedObjectId = (string?)head },
            new { refName = "refs/bugs/issue-1", expectedObjectId = (string?)null },
            new { refName = "refs/identities/author-1", expectedObjectId = identityObject }
        };
        var module = new FakeModule(new Dictionary<string, string>
        {
            ["inspectPublication"] = Success("inspectPublication", new
            {
                repositoryUrl = "https://github.com/example/project.git",
                branch = "main",
                outgoingCommitId = head,
                destinationRef = "refs/heads/main",
                updates,
                remotePreconditions = preconditions,
                boundedUpdateCount = updates.Length
            }),
            ["getCredentialForPush"] = JsonSerializer.Serialize("fixture-token"),
            ["forgetCredential"] = Success("forgetCredential", true),
            ["publish"] = Success("publish", new
            {
                repositoryUrl = "https://github.com/example/project.git",
                branch = "main",
                appliedUpdates = updates,
                boundedUpdateCount = updates.Length
            })
        });
        await using var accessor = new BrowserGitAccessorService(new FakeJsRuntime(module));

        var reviewResult = await accessor.InspectPublicationAsync();
        Assert.True(reviewResult.Succeeded);
        Assert.NotNull(reviewResult.Value);
        var request = new GitPublicationRequest(
            reviewResult.Value.RepositoryUrl,
            reviewResult.Value.Branch,
            reviewResult.Value.Updates,
            reviewResult.Value.RemotePreconditions);
        var publishResult = await accessor.PublishAsync(request);

        Assert.True(reviewResult.Succeeded);
        Assert.True(publishResult.Succeeded);
        Assert.Equal(3, reviewResult.Value.Updates.Count);
        Assert.Equal("refs/bugs/issue-1", reviewResult.Value.Updates[1].RefName);
        Assert.Equal(3, publishResult.Value.BoundedUpdateCount);
        Assert.Contains("initialize", module.Invocations);
        Assert.Contains("inspectPublication", module.Invocations);
        Assert.Contains("getCredentialForPush", module.Invocations);
        Assert.Contains("publish", module.Invocations);
    }

    [Fact]
    public async Task Publication_accessor_rejects_delete_and_malformed_remote_reviews_before_bridge()
    {
        var module = new FakeModule(new Dictionary<string, string>
        {
            ["publish"] = FakeModule.ThrowResponse,
            ["inspectPublication"] = Success("inspectPublication", new
            {
                repositoryUrl = "https://github.com/example/project.git",
                branch = "main",
                outgoingCommitId = new string('a', 40),
                destinationRef = "refs/heads/main",
                updates = new[]
                {
                    new
                    {
                        refName = "refs/bugs/issue-1",
                        @namespace = "GitBug",
                        kind = "Delete",
                        newObjectId = new string('b', 40),
                        expectedOldObjectId = (string?)null
                    }
                },
                remotePreconditions = Array.Empty<object>(),
                boundedUpdateCount = 1
            })
        });
        await using var accessor = new BrowserGitAccessorService(new FakeJsRuntime(module));

        var invalidRequest = new GitPublicationRequest(
            "https://github.com/example/project.git",
            "main",
            [new GitRefUpdate("refs/bugs/issue-1", GitRefNamespace.GitBug, GitRefUpdateKind.Delete, new string('b', 40))],
            [new GitRemotePrecondition("refs/bugs/issue-1", null)]);
        var invalid = await accessor.PublishAsync(invalidRequest);
        var malformedReview = await accessor.InspectPublicationAsync();

        Assert.False(invalid.Succeeded);
        Assert.Equal(GitOperationFailureKind.UnsupportedRef, invalid.FailureKind);
        Assert.Null(invalid.Diagnostic);
        Assert.False(malformedReview.Succeeded);
        Assert.Equal(GitOperationFailureKind.GitBugDataMalformed, malformedReview.FailureKind);
        Assert.Null(malformedReview.Diagnostic);
        Assert.Equal(new[] { "initialize", "inspectPublication" }, module.Invocations);
    }

    [Fact]
    public void Existing_branch_push_contract_remains_explicit_and_credential_free()
    {
        var request = new PushRequest(
            "https://github.com/example/project",
            "main",
            new string('a', 40),
            "refs/heads/main");
        var pushMethod = typeof(IBrowserGitAccessor).GetMethod(nameof(IBrowserGitAccessor.PushAsync));

        Assert.NotNull(pushMethod);
        Assert.Equal("refs/heads/main", request.DestinationRef);
        Assert.Equal(4, typeof(PushRequest).GetProperties().Length);
        Assert.DoesNotContain(typeof(PushRequest).GetProperties(), property =>
            property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertSafeFailure<T>(
        GitOperationResult<T> result,
        GitOperationFailureKind expectedFailure,
        string expectedMessage)
    {
        var serialized = JsonSerializer.Serialize(result);
        Assert.False(result.Succeeded);
        Assert.Equal(expectedFailure, result.FailureKind);
        Assert.Contains(expectedMessage, result.Message);
        Assert.Null(result.Value);
        Assert.Null(result.Diagnostic);
        Assert.DoesNotContain(PrivateValue, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(IssueId, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("refs/bugs", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("/repository/", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_contracts_allow_multiline_bodies_but_keep_titles_single_line()
    {
        var issueId = new GitBugIssueId(IssueId);
        var preconditions = new GitBugWritePreconditions(
            new("https://github.com/example/project.git", "project"),
            issueId,
            "1111111111111111111111111111111111111111",
            GitBugWriteOperations.IssueEdit);
        var serialization = new GitBugSerializationMetadata(
            GitBugWriteContract.SupportedStorageVersion,
            GitBugWriteContract.Utf8Encoding,
            true);

        Assert.True(GitBugWriteContract.IsValidIssueEdit(new(
            issueId,
            "Single-line title",
            "First line\nSecond line",
            preconditions,
            serialization)));
        Assert.False(GitBugWriteContract.IsValidIssueEdit(new(
            issueId,
            "Title\ncontinued",
            "First line\nSecond line",
            preconditions,
            serialization)));
        Assert.True(GitBugWriteContract.IsValidComment(new(
            issueId,
            "First line\nSecond line",
            preconditions with { ReviewedOperation = GitBugWriteOperations.CommentCreate },
            serialization)));
        Assert.False(GitBugWriteContract.IsValidComment(new(
            issueId,
            "First line\rSecond line",
            preconditions with { ReviewedOperation = GitBugWriteOperations.CommentCreate },
            serialization)));
    }

    private static void AssertInvalid<T>(GitOperationResult<T> result)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(GitOperationFailureKind.Unknown, result.FailureKind);
        Assert.Null(result.Diagnostic);
    }

    private static string Success(string operation, object value) => JsonSerializer.Serialize(new
    {
        operation,
        succeeded = true,
        message = PrivateValue,
        value
    });

    private static string Failure(string operation, string failureKind) => JsonSerializer.Serialize(new
    {
        operation,
        succeeded = false,
        message = PrivateValue,
        diagnostic = PrivateValue,
        failureKind
    });

    private sealed class FakeJsRuntime(FakeModule module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Assert.Equal("import", identifier);
            return ValueTask.FromResult((TValue)(object)module);
        }
    }

    private sealed class FakeModule(IReadOnlyDictionary<string, string> responses) : IJSObjectReference
    {
        public const string ThrowResponse = "__throw_js_exception__";
        public const string CancelResponse = "__throw_operation_canceled__";

        public List<string> Invocations { get; } = [];
        public Dictionary<string, object?[]> Arguments { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Invocations.Add(identifier);
            Arguments[identifier] = args ?? [];
            var json = identifier == "initialize"
                ? JsonSerializer.Serialize(new { operation = "initialize", succeeded = true, message = "Ready." })
                : responses[identifier];
            if (json == ThrowResponse)
            {
                throw new JSException(PrivateValue);
            }
            if (json == CancelResponse)
            {
                throw new OperationCanceledException(PrivateValue, cancellationToken);
            }

            var value = JsonSerializer.Deserialize<TValue>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return ValueTask.FromResult(value!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
