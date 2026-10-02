import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const source = name => readFile(new URL(name, root), 'utf8');

test('Browser Git interfaces and passive contract records share the Accessor interface namespace', async () => {
  const [git, file, contracts] = await Promise.all([
    source('Accessor/BrowserGitAccessor/Interface/IBrowserGitAccessor.cs'),
    source('Accessor/BrowserGitAccessor/Interface/IBrowserFileAccessor.cs'),
    source('Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs')
  ]);
  for (const facet of [git, file]) {
    assert.doesNotMatch(facet, /AlmToo\.Resource\.BrowserGitResource\.Data/);
    assert.match(facet, /namespace AlmToo\.Accessor\.BrowserGitAccessor\.Interface;/);
  }
  assert.match(contracts, /namespace AlmToo\.Accessor\.BrowserGitAccessor\.Interface;/);
  assert.doesNotMatch(git + file + contracts, /AlmToo\.Resources|\.Context\b/);
  for (const facet of [git, file]) assert.doesNotMatch(facet, /\b(?:record|enum)\s+(?:GitOperationResult|PushRequest|FilterFilesRequest)\b/);
  assert.match(git, /interface IBrowserGitAccessor : IAsyncDisposable/);
  for (const operation of ['CloneOrOpenAsync', 'GetStatusAsync', 'CommitAsync', 'InspectPushAsync', 'HasCredentialAsync', 'StoreCredentialAsync', 'ForgetCredentialAsync', 'PushAsync', 'SynchronizeAsync']) assert.match(git, new RegExp(operation));
  assert.match(file, /FilterFilesAsync\(FilterFilesRequest request/);
  assert.match(file, /UpdateFilesAsync\(UpdateFilesRequest request/);
  assert.doesNotMatch(git + file, /personalAccessToken|InitializeAsync|ListFilesAsync|ReadTextFileAsync|WriteTextFileAsync/);
  assert.doesNotMatch(contracts, /record SynchronizationFailure\([\s\S]*?(?:Diagnostic|credential|token)/i);
  assert.match(contracts, /SynchronizationFailure\(\s*SynchronizationFailureCategory Category,\s*string Message\)/);
  for (const dto of ['GitOperationResult', 'FilterFilesRequest', 'FilterFilesResult', 'UpdateFilesRequest', 'UpdateFilesResult', 'PushReview', 'PushRequest', 'PushResult', 'SynchronizationRequest', 'IncomingCommitMetadata', 'ChangedFileSummary', 'SynchronizationReview', 'SynchronizationFailure']) assert.match(contracts, new RegExp(`record ${dto}\\b`));
  for (const enumeration of ['SynchronizationIntent', 'SynchronizationDecisionState', 'SynchronizationFailureCategory']) assert.match(contracts, new RegExp(`enum ${enumeration}\\b`));
  assert.doesNotMatch(contracts, /\b(?:static|class|interface)\b|\b(?:Success|Failure)\s*\(|=>/);
});

test('one scoped Service implements both facets and both DI contracts resolve that instance', async () => {
  const [service, program] = await Promise.all([
    source('Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs'),
    source('Program.cs')
  ]);
  assert.match(service, /namespace AlmToo\.Accessor\.BrowserGitAccessor\.Service;/);
  assert.match(service, /sealed class BrowserGitAccessor : IBrowserGitAccessor, IBrowserFileAccessor/);
  assert.match(program, /AddScoped<BrowserGitAccessor>\(\)/);
  assert.match(program, /AddScoped<IBrowserGitAccessor>\(services => services\.GetRequiredService<BrowserGitAccessor>\(\)\)/);
  assert.match(program, /AddScoped<IBrowserFileAccessor>\(services => services\.GetRequiredService<BrowserGitAccessor>\(\)\)/);
  assert.match(program, /AddScoped<RepositoryWorkspaceManager>\(\)/);
  assert.doesNotMatch(program, /AddScoped<IBrowserGitAccessor, BrowserGitAccessor>|AddScoped<IBrowserFileAccessor, BrowserGitAccessor>/);
  assert.doesNotMatch(program, /IRepositoryWorkspaceManager|IVersionControlAccessor|Resources/);
});

test('Accessor validates paths and text as a unit before mutation', async () => {
  const [service, engine] = await Promise.all([
    source('Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs'),
    source('wwwroot/js/browserGitEngine.js')
  ]);
  assert.match(service, /MaxEditableTextBytes = 1024 \* 1024/);
  assert.match(service, /StrictUtf8 = new\(false, true\)/);
  assert.match(service, /candidate\.Contains\('%'\)/);
  assert.match(service, /segment is "\." or "\.\."/);
  assert.ok(service.indexOf('foreach (var update in request.Updates)') < service.indexOf('foreach (var update in validated)'), 'all updates must validate before writes');
  assert.match(engine, /TextDecoder\('utf-8', \{ fatal: true \}\)/);
  assert.match(engine, /sizeBytes > maxEditableTextBytes/);
  assert.match(engine, /normalized\.includes\('%'\)/);
});

test('credential and push boundary is secret-safe, single-attempt, and review-bound', async () => {
  const [service, engine, credential] = await Promise.all([
    source('Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs'),
    source('wwwroot/js/browserGitEngine.js'),
    source('wwwroot/js/pushCredentialSession.js')
  ]);
  assert.doesNotMatch(service, /private .*personalAccessToken|private .*credential;/i);
  assert.match(service, /getCredentialForPush/);
  assert.match(service, /finally[\s\S]*personalAccessToken = null/);
  assert.match(service, /FailureKind == GitOperationFailureKind\.CredentialRejected[\s\S]*ForgetCredentialAsync/);
  assert.match(engine, /samePushState\(reviewed, current\)/);
  assert.match(engine, /force: false/);
  assert.equal((engine.match(/await getGit\(\)\.push\(/g) ?? []).length, 1);
  assert.match(credential, /export function credentialPresence\(\)/);
  assert.match(credential, /export function forgetCredential\(\)[\s\S]*if \(!storage\)[\s\S]*return false/);
  assert.doesNotMatch(credential, /console\.|localStorage|JSON\.stringify/);
});

test('synchronization interop admits only safe review data and removes raw diagnostics', async () => {
  const [service, contracts, engine] = await Promise.all([
    source('Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs'),
    source('Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs'),
    source('wwwroot/js/browserGitEngine.js')
  ]);
  const synchronizationSurface = service.slice(service.indexOf('SynchronizeAsync('), service.indexOf('public async ValueTask<GitOperationResult<bool>> HasCredentialAsync'));

  assert.match(synchronizationSurface, /ValidateSynchronizationCoordinates\(request\.RepositoryUrl, request\.Branch\)/);
  assert.match(synchronizationSurface, /InvokeWithValueAsync<SynchronizationReviewDto, SynchronizationReview>/);
  assert.match(synchronizationSurface, /var sanitized = SanitizeSynchronizationResult\(result\)/);
  assert.match(synchronizationSurface, /sanitized\.Value\.RepositoryUrl, request\.RepositoryUrl/);
  assert.match(synchronizationSurface, /return sanitized;/);
  assert.match(synchronizationSurface, /catch \(OperationCanceledException\)[\s\S]*?SanitizedSynchronizationFailure\(GitOperationFailureKind\.Unknown\)/);
  assert.match(service, /SanitizeSynchronizationResult[\s\S]*?Diagnostic = null, FailureKind = null/);
  assert.match(service, /SanitizedSynchronizationFailure[\s\S]*?Diagnostic: null, FailureKind: failureKind/);
  assert.match(service, /ValidateCommitId\(LocalCommitId/);
  assert.match(service, /ValidateIncomingCommit/);
  assert.match(service, /ReadyToApply or SynchronizationDecisionState\.Applied\) && incoming is null/);
  assert.match(service, /TryNormalizeRepositoryPath\(Path, allowRoot: false/);
  assert.match(service, /GitChangeKind\.Added or GitChangeKind\.Modified or GitChangeKind\.Deleted/);
  assert.doesNotMatch(synchronizationSurface, /(?:personalAccessToken|github_pat|accessToken|\bPAT\b)/i);
  assert.doesNotMatch(contracts.match(/record Synchronization(?:Request|Review|Failure)\([\s\S]*?\);/g)?.join('\n') ?? '', /(?:token|credential|diagnostic)/i);
  assert.match(engine, /export async function synchronize\(request\)/);
  assert.match(engine, /return failureWithKind\(operation, 'Repository synchronization could not be completed\.', classifyPushFailure\(error\)\)/);
  assert.match(engine, /diagnostic: null/);
  assert.doesNotMatch(engine.slice(engine.indexOf('export async function synchronize'), engine.indexOf('export async function push')), /console\.|personalAccessToken|github_pat/);
});

test('Accessor normalizes cancellation, interop failures, initialization, and partial disposal', async () => {
  const service = await source('Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs');
  assert.match(service, /initializationTask \?\?= InitializeAsync/);
  assert.match(service, /catch \(OperationCanceledException\)/);
  assert.match(service, /catch \(JSException\)/);
  assert.doesNotMatch(service, /exception\.ToString|catch \([^)]* exception\)/i);
  assert.match(service, /moduleTask is \{ IsCompletedSuccessfully: true \}/);
  assert.match(service, /credentialModuleTask is \{ IsCompletedSuccessfully: true \}/);
});
