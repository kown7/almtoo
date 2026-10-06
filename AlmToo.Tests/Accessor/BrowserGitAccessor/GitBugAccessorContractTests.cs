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
