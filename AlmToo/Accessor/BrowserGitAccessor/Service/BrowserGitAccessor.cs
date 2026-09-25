using System.Text;
using System.Text.Json.Serialization;
using AlmToo.Accessor.BrowserGitAccessor.Interface;
using AlmToo.Resource.BrowserGitResource.Data;
using Microsoft.JSInterop;

namespace AlmToo.Accessor.BrowserGitAccessor.Service;

/// <summary>
/// Cohesive browser-platform Accessor for the active Git workspace and its text files.
/// </summary>
public sealed class BrowserGitAccessor : IBrowserGitAccessor, IBrowserFileAccessor
{
    private const string ModulePath = "./js/browserGitEngine.js";
    private const string CredentialModulePath = "./js/pushCredentialSession.js";
    private const int MaxEditableTextBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly IJSRuntime jsRuntime;
    private Task<IJSObjectReference>? moduleTask;
    private Task<IJSObjectReference>? credentialModuleTask;
    private Task<GitOperationResult>? initializationTask;

    public BrowserGitAccessor(IJSRuntime jsRuntime)
    {
        this.jsRuntime = jsRuntime;
    }

    public async ValueTask<GitOperationResult<RepositoryInfo>> CloneOrOpenAsync(
        RepositoryOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null
            || !Uri.TryCreate(request.RepositoryUrl, UriKind.Absolute, out var repository)
            || !string.Equals(repository.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(repository.UserInfo)
            || string.IsNullOrWhiteSpace(request.WorkspaceName)
            || request.WorkspaceName.Length > 80
            || request.WorkspaceName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            return new GitOperationResult<RepositoryInfo>("cloneOrOpen", false, "The repository could not be cloned or opened.", Value: default, Diagnostic: "A credential-free HTTPS URL and a normalized workspace name are required.");
        }

        return await InvokeWithValueAsync<RepositoryInfoDto, RepositoryInfo>(
            "cloneOrOpen",
            "cloneOrOpen",
            value => value.ToRepositoryInfo(),
            cancellationToken,
            request);
    }

    public async ValueTask<GitOperationResult<FilterFilesResult>> FilterFilesAsync(
        FilterFilesRequest request,
        CancellationToken cancellationToken = default)
    {
        const string operation = "filterFiles";
        if (request is null
            || !Enum.IsDefined(request.Kind)
            || !TryNormalizeRepositoryPath(request.Path, request.Kind == FileFilterKind.DirectoryEntries, out var path))
        {
            return new GitOperationResult<FilterFilesResult>(operation, false, "The requested repository path is not valid.", Value: default, Diagnostic: "The path must be normalized and contained in the active checkout.");
        }

        if (request.Kind == FileFilterKind.DirectoryEntries)
        {
            var entries = await InvokeWithValueAsync<List<RepositoryFileEntryDto>, IReadOnlyList<RepositoryFileEntry>>(
                "listFiles", "listFiles", values => values.Select(value => value.ToRepositoryFileEntry()).ToArray(), cancellationToken, path);
            return entries.Succeeded
                ? new GitOperationResult<FilterFilesResult>(operation, true, entries.Message, new(path, entries.Value!))
                : new GitOperationResult<FilterFilesResult>(operation, false, entries.Message, Value: default, Diagnostic: entries.Diagnostic, FailureKind: entries.FailureKind);
        }

        if (!IsSupportedTextPath(path))
        {
            return new GitOperationResult<FilterFilesResult>(operation, false, "The requested file is not supported text.", Value: default, Diagnostic: "Only allowlisted UTF-8 text files can be read.");
        }

        var text = await InvokeWithValueAsync<TextFileContentDto, TextFileContent>(
            "readTextFile", "readTextFile", value => value.ToTextFileContent(), cancellationToken, path);
        return text.Succeeded
            ? new GitOperationResult<FilterFilesResult>(operation, true, text.Message, new(path, Array.Empty<RepositoryFileEntry>(), text.Value))
            : new GitOperationResult<FilterFilesResult>(operation, false, text.Message, Value: default, Diagnostic: text.Diagnostic, FailureKind: text.FailureKind);
    }

    public async ValueTask<GitOperationResult<UpdateFilesResult>> UpdateFilesAsync(
        UpdateFilesRequest request,
        CancellationToken cancellationToken = default)
    {
        const string operation = "updateFiles";
        if (request?.Updates is not { Count: > 0 })
        {
            return new GitOperationResult<UpdateFilesResult>(operation, false, "No file updates were supplied.", Value: default, Diagnostic: "At least one text update is required.");
        }

        var validated = new List<TextFileUpdate>(request.Updates.Count);
        var uniquePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var update in request.Updates)
        {
            if (update is null || update.Content is null
                || !TryNormalizeRepositoryPath(update.Path, allowRoot: false, out var path)
                || !IsSupportedTextPath(path)
                || !TryGetUtf8ByteCount(update.Content, out var byteCount)
                || byteCount > MaxEditableTextBytes
                || !uniquePaths.Add(path))
            {
                return new GitOperationResult<UpdateFilesResult>(operation, false, "The requested file updates are not valid.", Value: default, Diagnostic: "Paths must be unique, contained text-file paths with valid UTF-8 content no larger than 1 MiB.");
            }
            validated.Add(new(path, update.Content));
        }

        foreach (var update in validated)
        {
            var result = await InvokeAsync("writeTextFile", "writeTextFile", cancellationToken, update.Path, update.Content);
            if (!result.Succeeded)
            {
                return new GitOperationResult<UpdateFilesResult>(operation, false, result.Message, Value: default, Diagnostic: result.Diagnostic, FailureKind: result.FailureKind);
            }
        }

        return new GitOperationResult<UpdateFilesResult>(operation, true, "The browser-local files were updated.", new(validated.Select(update => update.Path).ToArray()));
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
        if (request is null
            || string.IsNullOrWhiteSpace(request.Message)
            || string.IsNullOrWhiteSpace(request.AuthorName)
            || string.IsNullOrWhiteSpace(request.AuthorEmail)
            || request.Message.IndexOfAny(['\0', '\r']) >= 0
            || request.AuthorName.IndexOfAny(['\0', '\r', '\n']) >= 0
            || request.AuthorEmail.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            return new GitOperationResult<CommitInfo>("commit", false, "A browser-local commit could not be created.", Value: default, Diagnostic: "A non-empty message and valid author metadata are required.");
        }

        return await InvokeWithValueAsync<CommitInfoDto, CommitInfo>(
            "commit",
            "commit",
            value => value.ToCommitInfo(),
            cancellationToken,
            request);
    }

    public async ValueTask<GitOperationResult<PushReview>> InspectPushAsync(
        CancellationToken cancellationToken = default)
    {
        return await InvokeWithValueAsync<PushReviewDto, PushReview>(
            "inspectPush",
            "inspectPush",
            value => value.ToPushReview(),
            cancellationToken);
    }

    public async ValueTask<GitOperationResult<bool>> HasCredentialAsync(CancellationToken cancellationToken = default)
    {
        const string operation = "hasCredential";
        try
        {
            var module = await GetCredentialModuleAsync(cancellationToken);
            var presence = await module.InvokeAsync<int>("credentialPresence", cancellationToken);
            return presence switch
            {
                0 => new GitOperationResult<bool>(operation, true, CredentialSuccessMessage(operation), false),
                1 => new GitOperationResult<bool>(operation, true, CredentialSuccessMessage(operation), true),
                _ => new GitOperationResult<bool>(operation, false, CredentialFailureMessage(operation), Value: default, Diagnostic: "Tab-scoped credential storage is unavailable.", FailureKind: GitOperationFailureKind.Unknown)
            };
        }
        catch (OperationCanceledException)
        {
            return new GitOperationResult<bool>(operation, false, CredentialFailureMessage(operation), Value: default, Diagnostic: "The credential operation was canceled.", FailureKind: GitOperationFailureKind.Unknown);
        }
        catch (Exception)
        {
            return new GitOperationResult<bool>(operation, false, CredentialFailureMessage(operation), Value: default, Diagnostic: "The tab-scoped credential module could not complete the operation.", FailureKind: GitOperationFailureKind.Unknown);
        }
    }

    public ValueTask<GitOperationResult<bool>> StoreCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return ValueTask.FromResult(new GitOperationResult<bool>("storeCredential", false, "The credential could not be stored.", Value: default, Diagnostic: "A non-empty credential is required."));
        }

        return InvokeCredentialAsync("storeCredential", "storeCredential", credential, falseIsFailure: true, cancellationToken);
    }

    public ValueTask<GitOperationResult<bool>> ForgetCredentialAsync(CancellationToken cancellationToken = default) =>
        InvokeCredentialAsync("forgetCredential", "forgetCredential", null, falseIsFailure: true, cancellationToken);

    public async ValueTask<GitOperationResult<PushResult>> PushAsync(
        PushRequest request,
        CancellationToken cancellationToken = default)
    {
        string? personalAccessToken = null;
        try
        {
            if (request is null)
            {
                return SanitizedPushFailure(GitOperationFailureKind.Unknown);
            }

            ValidatePushCoordinates(request.RepositoryUrl, request.Branch, request.DestinationRef);
            ValidateCommitId(request.OutgoingCommitId, nameof(request.OutgoingCommitId));
            var initialized = await EnsureInitializedAsync(cancellationToken);
            if (!initialized.Succeeded)
            {
                return SanitizedPushFailure(GitOperationFailureKind.Unknown);
            }
            var credentialModule = await GetCredentialModuleAsync(cancellationToken);
            personalAccessToken = await credentialModule.InvokeAsync<string?>("getCredentialForPush", cancellationToken);
            if (string.IsNullOrWhiteSpace(personalAccessToken))
            {
                return SanitizedPushFailure(GitOperationFailureKind.CredentialRejected);
            }

            var result = await InvokeWithValueAsync<PushResultDto, PushResult>(
                "push", "push", value => value.ToPushResult(), cancellationToken, request, personalAccessToken);
            result = SanitizePushFailure(RedactCredential(result, personalAccessToken));

            if (!result.Succeeded && result.FailureKind == GitOperationFailureKind.CredentialRejected)
            {
                await ForgetCredentialAsync(CancellationToken.None);
            }

            if (result.Succeeded && (result.Value is null
                || result.Value.RepositoryUrl != request.RepositoryUrl
                || result.Value.Branch != request.Branch
                || result.Value.DestinationRef != request.DestinationRef
                || result.Value.PushedCommitId != request.OutgoingCommitId))
            {
                return SanitizedPushFailure(GitOperationFailureKind.Unknown);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return SanitizedPushFailure(GitOperationFailureKind.Unknown);
        }
        catch (Exception)
        {
            return SanitizedPushFailure(GitOperationFailureKind.Unknown);
        }
        finally
        {
            personalAccessToken = null;
        }
    }

    private static GitOperationResult<PushResult> SanitizePushFailure(
        GitOperationResult<PushResult> result)
    {
        if (result.Succeeded)
        {
            return result with { FailureKind = null };
        }

        return SanitizedPushFailure(result.FailureKind ?? GitOperationFailureKind.Unknown);
    }

    private static GitOperationResult<PushResult> SanitizedPushFailure(
        GitOperationFailureKind failureKind)
    {
        var diagnostic = failureKind switch
        {
            GitOperationFailureKind.CredentialRejected => "The remote rejected the supplied credentials or repository permission.",
            GitOperationFailureKind.RemoteAhead => "The remote branch contains commits that are not in the reviewed local history.",
            GitOperationFailureKind.NetworkUnavailable => "The remote could not be reached from this browser.",
            GitOperationFailureKind.UnsupportedRef => "The checked-out ref is not a supported local branch.",
            _ => "The push failed without exposing remote response details."
        };

        return new GitOperationResult<PushResult>("push", false, "The reviewed commit could not be pushed.", Value: default, Diagnostic: diagnostic, FailureKind: failureKind);
    }

    private static GitOperationResult<PushResult> RedactCredential(
        GitOperationResult<PushResult> result,
        string personalAccessToken)
    {
        static string? Redact(string? value, string secret) => value?.Replace(
            secret,
            "[REDACTED]",
            StringComparison.Ordinal);

        return result with
        {
            Message = Redact(result.Message, personalAccessToken) ?? "The reviewed commit could not be pushed.",
            Diagnostic = Redact(result.Diagnostic, personalAccessToken)
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (moduleTask is { IsCompletedSuccessfully: true })
        {
            await (await moduleTask).DisposeAsync();
        }
        if (credentialModuleTask is { IsCompletedSuccessfully: true })
        {
            await (await credentialModuleTask).DisposeAsync();
        }
    }

    private async ValueTask<GitOperationResult<bool>> InvokeCredentialAsync(
        string operation,
        string identifier,
        string? credential,
        bool falseIsFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            var module = await GetCredentialModuleAsync(cancellationToken);
            var value = credential is null
                ? await module.InvokeAsync<bool>(identifier, cancellationToken)
                : await module.InvokeAsync<bool>(identifier, cancellationToken, credential);
            return !value && falseIsFailure
                ? new GitOperationResult<bool>(operation, false, CredentialFailureMessage(operation), Value: default, Diagnostic: "Tab-scoped credential storage was unavailable or rejected the operation.", FailureKind: GitOperationFailureKind.Unknown)
                : new GitOperationResult<bool>(operation, true, CredentialSuccessMessage(operation), value);
        }
        catch (OperationCanceledException)
        {
            return new GitOperationResult<bool>(operation, false, CredentialFailureMessage(operation), Value: default, Diagnostic: "The credential operation was canceled.", FailureKind: GitOperationFailureKind.Unknown);
        }
        catch (Exception)
        {
            return new GitOperationResult<bool>(operation, false, CredentialFailureMessage(operation), Value: default, Diagnostic: "The tab-scoped credential module could not complete the operation.", FailureKind: GitOperationFailureKind.Unknown);
        }
    }

    private static string CredentialSuccessMessage(string operation) => operation switch
    {
        "hasCredential" => "Credential presence was checked.",
        "storeCredential" => "The credential was stored for this browser tab.",
        _ => "The credential was forgotten."
    };

    private static string CredentialFailureMessage(string operation) => operation switch
    {
        "hasCredential" => "Credential presence could not be checked.",
        "storeCredential" => "The credential could not be stored.",
        _ => "The credential could not be forgotten."
    };

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
            return new GitOperationResult<TValue>(result.Operation, false, result.Message, Value: default, Diagnostic: result.Diagnostic, FailureKind: ParseFailureKind(result.FailureKind));
        }

        if (result.Value is null)
        {
            return new GitOperationResult<TValue>(result.Operation, false, result.Message, Value: default, Diagnostic: $"Browser Git operation '{result.Operation}' succeeded without a value.");
        }

        try
        {
            return new GitOperationResult<TValue>(result.Operation, true, result.Message, mapValue(result.Value));
        }
        catch (Exception)
        {
            return new GitOperationResult<TValue>(operation, false, $"Browser Git operation '{operation}' returned an unexpected response.", Value: default, Diagnostic: "The response value did not match the expected contract.");
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
            ? new GitOperationResult(result.Operation, true, result.Message)
            : new GitOperationResult(result.Operation, false, result.Message, Diagnostic: result.Diagnostic, FailureKind: ParseFailureKind(result.FailureKind));
    }

    private static GitOperationFailureKind? ParseFailureKind(string? failureKind) => failureKind switch
    {
        "credentialRejected" => GitOperationFailureKind.CredentialRejected,
        "remoteAhead" => GitOperationFailureKind.RemoteAhead,
        "networkUnavailable" => GitOperationFailureKind.NetworkUnavailable,
        "unsupportedRef" => GitOperationFailureKind.UnsupportedRef,
        "unknown" => GitOperationFailureKind.Unknown,
        _ => null
    };

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

            if (response is null)
            {
                return BrowserGitResponse<TValue>.Failure(
                    operation,
                    $"Browser Git operation '{operation}' did not return a response.",
                    "The JavaScript operation returned null.");
            }

            if (!string.Equals(response.Operation, operation, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(response.Message))
            {
                return BrowserGitResponse<TValue>.Failure(
                    operation,
                    $"Browser Git operation '{operation}' returned an unexpected response.",
                    "The JavaScript response envelope did not match the expected contract.");
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            return BrowserGitResponse<TValue>.Failure(
                operation,
                $"Browser Git operation '{operation}' was canceled.",
                "The operation was canceled before it returned a result.");
        }
        catch (JSException)
        {
            return BrowserGitResponse<TValue>.Failure(
                operation,
                $"Browser Git operation '{operation}' failed in JavaScript interop.",
                "JavaScript interop failed without exposing exception details.");
        }
        catch (InvalidOperationException)
        {
            return BrowserGitResponse<TValue>.Failure(
                operation,
                $"Browser Git operation '{operation}' could not be invoked.",
                "The JavaScript module was unavailable or could not be invoked.");
        }
        catch (Exception)
        {
            return BrowserGitResponse<TValue>.Failure(
                operation,
                $"Browser Git operation '{operation}' could not be completed.",
                "An unexpected interop boundary failure occurred.");
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
                return new GitOperationResult("initialize", false, "Browser Git storage could not be initialized.", Diagnostic: "The JavaScript initialize operation did not return a response.");
            }

            return response.Succeeded
                ? new GitOperationResult(response.Operation, true, response.Message)
                : new GitOperationResult(response.Operation, false, response.Message, Diagnostic: response.Diagnostic);
        }
        catch (OperationCanceledException)
        {
            return new GitOperationResult("initialize", false, "Browser Git storage initialization was canceled.", Diagnostic: "The operation was canceled before initialization completed.");
        }
        catch (JSException)
        {
            return new GitOperationResult("initialize", false, "Browser Git storage could not be initialized.", Diagnostic: "JavaScript initialization failed without exposing exception details.");
        }
        catch (InvalidOperationException)
        {
            return new GitOperationResult("initialize", false, "Browser Git storage could not be initialized.", Diagnostic: "The JavaScript module was unavailable or could not be imported.");
        }
        catch (Exception)
        {
            return new GitOperationResult("initialize", false, "Browser Git storage could not be initialized.", Diagnostic: "An unexpected initialization boundary failure occurred.");
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

    private Task<IJSObjectReference> GetCredentialModuleAsync(CancellationToken cancellationToken)
    {
        credentialModuleTask ??= jsRuntime.InvokeAsync<IJSObjectReference>(
            "import",
            cancellationToken,
            CredentialModulePath).AsTask();

        return credentialModuleTask;
    }

    private static bool TryNormalizeRepositoryPath(string? candidate, bool allowRoot, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)
            || !string.Equals(candidate, candidate.Trim(), StringComparison.Ordinal)
            || !candidate.StartsWith('/')
            || candidate.Contains('\\')
            || candidate.Contains('%')
            || candidate.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            return false;
        }

        var value = candidate.StartsWith('/') ? candidate : $"/{candidate}";
        if (value == "/")
        {
            normalized = value;
            return allowRoot;
        }

        if (value.EndsWith('/'))
        {
            return false;
        }

        var segments = value[1..].Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            return false;
        }

        normalized = "/" + string.Join('/', segments);
        return true;
    }

    private static bool IsSupportedTextPath(string path)
    {
        var name = Path.GetFileName(path);
        if (name is "README" or "LICENSE" or "Dockerfile" or "Makefile" or ".gitignore" or ".editorconfig")
        {
            return true;
        }

        return Path.GetExtension(name).ToLowerInvariant() is
            ".txt" or ".md" or ".markdown" or ".json" or ".js" or ".mjs" or ".cjs"
            or ".ts" or ".tsx" or ".jsx" or ".css" or ".scss" or ".html" or ".xml"
            or ".yml" or ".yaml" or ".toml" or ".ini" or ".cfg" or ".cs" or ".razor"
            or ".csproj" or ".sln" or ".sh" or ".py" or ".java" or ".go" or ".rs";
    }

    private static bool TryGetUtf8ByteCount(string content, out int byteCount)
    {
        try
        {
            byteCount = StrictUtf8.GetByteCount(content);
            return true;
        }
        catch (EncoderFallbackException)
        {
            byteCount = 0;
            return false;
        }
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

        [JsonPropertyName("failureKind")]
        public string? FailureKind { get; init; }

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

    private sealed record PushReviewDto
    {
        [JsonPropertyName("repositoryUrl")]
        public string RepositoryUrl { get; init; } = string.Empty;

        [JsonPropertyName("branch")]
        public string Branch { get; init; } = string.Empty;

        [JsonPropertyName("outgoingCommitId")]
        public string OutgoingCommitId { get; init; } = string.Empty;

        [JsonPropertyName("destinationRef")]
        public string DestinationRef { get; init; } = string.Empty;

        public PushReview ToPushReview()
        {
            ValidatePushCoordinates(RepositoryUrl, Branch, DestinationRef);
            ValidateCommitId(OutgoingCommitId, nameof(OutgoingCommitId));
            return new(RepositoryUrl, Branch, OutgoingCommitId, DestinationRef);
        }
    }

    private sealed record PushResultDto
    {
        [JsonPropertyName("repositoryUrl")]
        public string RepositoryUrl { get; init; } = string.Empty;

        [JsonPropertyName("branch")]
        public string Branch { get; init; } = string.Empty;

        [JsonPropertyName("destinationRef")]
        public string DestinationRef { get; init; } = string.Empty;

        [JsonPropertyName("pushedCommitId")]
        public string PushedCommitId { get; init; } = string.Empty;

        public PushResult ToPushResult()
        {
            ValidatePushCoordinates(RepositoryUrl, Branch, DestinationRef);
            ValidateCommitId(PushedCommitId, nameof(PushedCommitId));
            return new(RepositoryUrl, Branch, DestinationRef, PushedCommitId);
        }
    }

    private static void ValidatePushCoordinates(
        string repositoryUrl,
        string branch,
        string destinationRef)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repository)
            || !string.Equals(repository.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(repository.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(repository.UserInfo))
        {
            throw new InvalidOperationException("The push repository is not a credential-free GitHub HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(branch)
            || !string.Equals(destinationRef, $"refs/heads/{branch}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The push branch and destination ref do not match.");
        }
    }

    private static void ValidateCommitId(string commitId, string propertyName)
    {
        if (commitId.Length != 40 || !commitId.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException($"{propertyName} is not a full commit SHA.");
        }
    }
}
