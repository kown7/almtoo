using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace AlmToo.Services.Git;

/// <summary>
/// Browser Git service backed by the JavaScript module in wwwroot/js/browserGitEngine.js.
/// The service keeps JavaScript interop details out of UI components while preserving the
/// structured result shape defined by the contract layer.
/// </summary>
public sealed class BrowserGitService : IBrowserGitService, IAsyncDisposable
{
    private const string ModulePath = "./js/browserGitEngine.js";

    private readonly IJSRuntime jsRuntime;
    private Task<IJSObjectReference>? moduleTask;
    private Task<GitOperationResult>? initializationTask;

    public BrowserGitService(IJSRuntime jsRuntime)
    {
        this.jsRuntime = jsRuntime;
    }

    public async ValueTask<GitOperationResult<RepositoryInfo>> CloneOrOpenAsync(
        RepositoryOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return GitOperationResult<RepositoryInfo>.Failure(
                "cloneOrOpen",
                "The repository could not be cloned or opened.",
                "Repository open request is required.");
        }

        return await InvokeWithValueAsync<RepositoryInfoDto, RepositoryInfo>(
            "cloneOrOpen",
            "cloneOrOpen",
            value => value.ToRepositoryInfo(),
            cancellationToken,
            request);
    }

    public async ValueTask<GitOperationResult<IReadOnlyList<RepositoryFileEntry>>> ListFilesAsync(
        string path = "/",
        CancellationToken cancellationToken = default)
    {
        return await InvokeWithValueAsync<List<RepositoryFileEntryDto>, IReadOnlyList<RepositoryFileEntry>>(
            "listFiles",
            "listFiles",
            entries => entries.Select(entry => entry.ToRepositoryFileEntry()).ToArray(),
            cancellationToken,
            path);
    }

    public async ValueTask<GitOperationResult<TextFileContent>> ReadTextFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        return await InvokeWithValueAsync<TextFileContentDto, TextFileContent>(
            "readTextFile",
            "readTextFile",
            value => value.ToTextFileContent(),
            cancellationToken,
            path);
    }

    public async ValueTask<GitOperationResult> WriteTextFileAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        return await InvokeAsync(
            "writeTextFile",
            "writeTextFile",
            cancellationToken,
            path,
            content);
    }

    public async ValueTask<GitOperationResult<IReadOnlyList<ChangedFile>>> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        return await InvokeWithValueAsync<List<ChangedFileDto>, IReadOnlyList<ChangedFile>>(
            "getStatus",
            "getStatus",
            files => files.Select(file => file.ToChangedFile()).ToArray(),
            cancellationToken);
    }

    public async ValueTask<GitOperationResult<CommitInfo>> CommitAsync(
        CommitRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return GitOperationResult<CommitInfo>.Failure(
                "commit",
                "A browser-local commit could not be created.",
                "Commit request is required.");
        }

        return await InvokeWithValueAsync<CommitInfoDto, CommitInfo>(
            "commit",
            "commit",
            value => value.ToCommitInfo(),
            cancellationToken,
            request);
    }

    public async ValueTask DisposeAsync()
    {
        if (moduleTask is not { IsCompletedSuccessfully: true })
        {
            return;
        }

        var module = await moduleTask;
        await module.DisposeAsync();
    }

    private async Task<GitOperationResult<TValue>> InvokeWithValueAsync<TInteropValue, TValue>(
        string operation,
        string identifier,
        Func<TInteropValue, TValue> mapValue,
        CancellationToken cancellationToken,
        params object?[] args)
    {
        var result = await InvokeResponseAsync<TInteropValue>(operation, identifier, cancellationToken, args);

        if (!result.Succeeded)
        {
            return GitOperationResult<TValue>.Failure(result.Operation, result.Message, result.Diagnostic);
        }

        if (result.Value is null)
        {
            return GitOperationResult<TValue>.Failure(
                result.Operation,
                result.Message,
                $"Browser Git operation '{result.Operation}' succeeded without a value.");
        }

        try
        {
            return GitOperationResult<TValue>.Success(
                result.Operation,
                result.Message,
                mapValue(result.Value));
        }
        catch (Exception exception)
        {
            return GitOperationResult<TValue>.Failure(
                result.Operation,
                $"Browser Git operation '{result.Operation}' returned an unexpected response.",
                exception.ToString());
        }
    }

    private async Task<GitOperationResult> InvokeAsync(
        string operation,
        string identifier,
        CancellationToken cancellationToken,
        params object?[] args)
    {
        var result = await InvokeResponseAsync<object>(operation, identifier, cancellationToken, args);

        return result.Succeeded
            ? GitOperationResult.Success(result.Operation, result.Message)
            : GitOperationResult.Failure(result.Operation, result.Message, result.Diagnostic);
    }

    private async Task<BrowserGitResponse<TValue>> InvokeResponseAsync<TValue>(
        string operation,
        string identifier,
        CancellationToken cancellationToken,
        params object?[] args)
    {
        try
        {
            var initializationResult = await EnsureInitializedAsync(cancellationToken);
            if (!initializationResult.Succeeded)
            {
                return BrowserGitResponse<TValue>.Failure(operation, initializationResult.Message, initializationResult.Diagnostic);
            }

            var module = await GetModuleAsync(cancellationToken);
            var response = await module.InvokeAsync<BrowserGitResponse<TValue>>(
                identifier,
                cancellationToken,
                args);

            return response ?? BrowserGitResponse<TValue>.Failure(
                operation,
                $"Browser Git operation '{operation}' did not return a response.",
                null);
        }
        catch (JSException exception)
        {
            return BrowserGitResponse<TValue>.Failure(
                operation,
                $"Browser Git operation '{operation}' failed in JavaScript interop.",
                exception.ToString());
        }
        catch (InvalidOperationException exception)
        {
            return BrowserGitResponse<TValue>.Failure(
                operation,
                $"Browser Git operation '{operation}' could not be invoked.",
                exception.ToString());
        }
    }

    private async Task<GitOperationResult> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        initializationTask ??= InitializeAsync(cancellationToken);
        return await initializationTask;
    }

    private async Task<GitOperationResult> InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var module = await GetModuleAsync(cancellationToken);
            var response = await module.InvokeAsync<BrowserGitResponse<object>>(
                "initialize",
                cancellationToken);

            if (response is null)
            {
                return GitOperationResult.Failure(
                    "initialize",
                    "Browser Git storage could not be initialized.",
                    "The JavaScript initialize operation did not return a response.");
            }

            return response.Succeeded
                ? GitOperationResult.Success(response.Operation, response.Message)
                : GitOperationResult.Failure(response.Operation, response.Message, response.Diagnostic);
        }
        catch (JSException exception)
        {
            return GitOperationResult.Failure(
                "initialize",
                "Browser Git storage could not be initialized.",
                exception.ToString());
        }
        catch (InvalidOperationException exception)
        {
            return GitOperationResult.Failure(
                "initialize",
                "Browser Git storage could not be initialized.",
                exception.ToString());
        }
    }

    private Task<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        moduleTask ??= jsRuntime.InvokeAsync<IJSObjectReference>(
            "import",
            cancellationToken,
            ModulePath).AsTask();

        return moduleTask;
    }

    private sealed record BrowserGitResponse<TValue>
    {
        [JsonPropertyName("operation")]
        public string Operation { get; init; } = string.Empty;

        [JsonPropertyName("succeeded")]
        public bool Succeeded { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("value")]
        public TValue? Value { get; init; }

        [JsonPropertyName("diagnostic")]
        public string? Diagnostic { get; init; }

        public static BrowserGitResponse<TValue> Failure(string operation, string message, string? diagnostic) =>
            new()
            {
                Operation = operation,
                Succeeded = false,
                Message = message,
                Diagnostic = diagnostic
            };
    }

    private sealed record RepositoryInfoDto
    {
        [JsonPropertyName("repositoryUrl")]
        public string RepositoryUrl { get; init; } = string.Empty;

        [JsonPropertyName("workspaceName")]
        public string WorkspaceName { get; init; } = string.Empty;

        [JsonPropertyName("rootPath")]
        public string RootPath { get; init; } = string.Empty;

        [JsonPropertyName("wasCloned")]
        public bool WasCloned { get; init; }

        public RepositoryInfo ToRepositoryInfo() =>
            new(RepositoryUrl, WorkspaceName, RootPath, WasCloned);
    }

    private sealed record RepositoryFileEntryDto
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; init; } = string.Empty;

        [JsonPropertyName("sizeBytes")]
        public long? SizeBytes { get; init; }

        [JsonPropertyName("isEditableText")]
        public bool IsEditableText { get; init; }

        public RepositoryFileEntry ToRepositoryFileEntry() =>
            new(
                Path,
                Name,
                Enum.TryParse<RepositoryFileKind>(Kind, ignoreCase: true, out var kind) ? kind : RepositoryFileKind.File,
                SizeBytes,
                IsEditableText);
    }

    private sealed record TextFileContentDto
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;

        [JsonPropertyName("encoding")]
        public string? Encoding { get; init; }

        [JsonPropertyName("sizeBytes")]
        public long? SizeBytes { get; init; }

        public TextFileContent ToTextFileContent() =>
            new(Path, Content, Encoding, SizeBytes);
    }

    private sealed record ChangedFileDto
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("changeKind")]
        public string ChangeKind { get; init; } = string.Empty;

        public ChangedFile ToChangedFile() =>
            new(
                Path,
                Enum.TryParse<GitChangeKind>(ChangeKind, ignoreCase: true, out var changeKind) ? changeKind : GitChangeKind.Unknown);
    }

    private sealed record CommitInfoDto
    {
        [JsonPropertyName("commitId")]
        public string CommitId { get; init; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        public CommitInfo ToCommitInfo() =>
            new(CommitId, Message);
    }
}
