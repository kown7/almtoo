import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const source = name => readFile(new URL(name, root), 'utf8');

test('Browser Git interfaces and passive contract records share the Accessor interface namespace', async () => {
  const [git, contracts] = await Promise.all([
    source('Accessor/BrowserGitAccessor/Interface/IBrowserGitAccessor.cs'),
    source('Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs')
  ]);
  for (const facet of [git]) {
    assert.doesNotMatch(facet, /AlmToo\.Resource\.BrowserGitResource\.Data/);
    assert.match(facet, /namespace AlmToo\.Accessor\.BrowserGitAccessor\.Interface;/);
  }
  assert.match(contracts, /namespace AlmToo\.Accessor\.BrowserGitAccessor\.Interface;/);
  assert.doesNotMatch(git + contracts, /AlmToo\.Resources|\.Context\b/);
  assert.doesNotMatch(git, /\b(?:record|enum)\s+(?:GitOperationResult|PushRequest|FilterFilesRequest)\b/);
  assert.match(git, /interface IBrowserGitAccessor : IAsyncDisposable/);
  for (const operation of ['CloneOrOpenAsync', 'GetStatusAsync', 'CommitAsync', 'InspectPublicationAsync', 'PublishAsync', 'InspectPushAsync', 'HasCredentialAsync', 'StoreCredentialAsync', 'ForgetCredentialAsync', 'PushAsync', 'FilterFilesAsync', 'UpdateFilesAsync', 'SynchronizeAsync']) assert.match(git, new RegExp(operation));
  assert.doesNotMatch(git, /personalAccessToken|InitializeAsync|ListFilesAsync|ReadTextFileAsync|WriteTextFileAsync/);
  assert.doesNotMatch(contracts, /record SynchronizationFailure\([\s\S]*?(?:Diagnostic|credential|token)/i);
  assert.match(contracts, /SynchronizationFailure\(\s*SynchronizationFailureCategory Category,\s*string Message\)/);
  for (const dto of ['GitOperationResult', 'FilterFilesRequest', 'FilterFilesResult', 'UpdateFilesRequest', 'UpdateFilesResult', 'PushReview', 'PushRequest', 'PushResult', 'SynchronizationRequest', 'IncomingCommitMetadata', 'ChangedFileSummary', 'SynchronizationReview', 'SynchronizationFailure']) assert.match(contracts, new RegExp(`record ${dto}\\b`));
  assert.match(contracts, /record ChangedFile\(\s*string Path,\s*GitChangeKind ChangeKind/);
  assert.match(contracts, /record CommitRequest\(\s*string Message,\s*string AuthorName,\s*string AuthorEmail/);
  assert.match(contracts, /record CommitInfo\(\s*string CommitId,\s*string Message,\s*int ChangedFileCount/);
  assert.match(git, /GetStatusAsync\(CancellationToken cancellationToken/);
  assert.match(git, /CommitAsync\(CommitRequest request, CancellationToken cancellationToken/);
  for (const enumeration of ['SynchronizationIntent', 'SynchronizationDecisionState', 'SynchronizationFailureCategory']) assert.match(contracts, new RegExp(`enum ${enumeration}\\b`));
  const passiveContracts = contracts.slice(0, contracts.indexOf('public static class GitBugWriteOperations'));
  assert.doesNotMatch(passiveContracts, /\b(?:static|class|interface)\b|\b(?:Success|Failure)\s*\(|=>/);
});

test('one scoped Accessor implements the core and Git-Bug contracts' , async () => {
  const [service, program] = await Promise.all([
    source('Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs'),
    source('Program.cs')
  ]);
  assert.match(service, /namespace AlmToo\.Accessor\.BrowserGitAccessor\.Service;/);
  assert.match(service, /sealed class BrowserGitAccessor : IBrowserGitAccessor, IGitBugAccessor/);
  assert.match(program, /AddScoped<BrowserGitAccessor>\(\)/);
  assert.match(program, /AddScoped<IBrowserGitAccessor>\(services => services\.GetRequiredService<BrowserGitAccessor>\(\)\)/);
  assert.match(program, /AddScoped<IGitBugAccessor>\(services => services\.GetRequiredService<BrowserGitAccessor>\(\)\)/);
  assert.match(program, /AddScoped<WorkspaceManager>\(\)/);
  assert.doesNotMatch(program, /AddScoped<IBrowserGitAccessor, BrowserGitAccessor>|AddScoped<IGitBugAccessor, BrowserGitAccessor>/);
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

test('publication contracts allow only reviewed branch and Git-Bug refs with bounded preconditions', async () => {
  const [facet, contracts, service, engine] = await Promise.all([
    source('Accessor/BrowserGitAccessor/Interface/IBrowserGitAccessor.cs'),
    source('Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs'),
    source('Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs'),
    source('wwwroot/js/browserGitEngine.js')
  ]);

  assert.match(facet, /interface IBrowserGitAccessor/);
  assert.match(facet, /InspectPublicationAsync/);
  assert.match(facet, /PublishAsync\(\s*GitPublicationRequest request/);
  for (const record of ['GitRefUpdate', 'GitRemotePrecondition', 'GitPublicationRequest', 'GitPublicationReview', 'GitPublicationReceipt']) {
    assert.match(contracts, new RegExp(`record ${record}\\b`));
  }
  assert.match(contracts, /enum GitRefNamespace/);
  assert.match(contracts, /Branch/);
  assert.match(contracts, /GitBug/);
  assert.match(contracts, /MaximumUpdates = 100/);
  assert.match(contracts, /MaximumRemotePreconditions = 100/);
  assert.match(contracts, /update\.Kind is GitRefUpdateKind\.Create or GitRefUpdateKind\.Update/);
  assert.match(contracts, /HasUniqueRefs\(request\.Updates\.Select\(update => update\.RefName\)\)/);
  assert.match(contracts, /BoundedUpdateCount == review\.Updates\.Count/);
  assert.match(service, /BrowserGitPublicationContract\.IsValidReview/);
  assert.match(service, /BrowserGitPublicationContract\.IsValidRequest/);
  assert.match(service, /FailureKind: GitOperationFailureKind\.UnsupportedRef/);
  assert.match(engine, /refs\/heads\//);
  assert.match(engine, /refs\/bugs\//);
  assert.match(engine, /refs\/identities\//);
  assert.match(engine, /const refspecs = reviewed\.updates\.map/);
  assert.match(engine, /remoteAhead/);
  assert.match(engine, /networkUnavailable/);
  assert.match(engine, /diagnostic: null/);
});

test('remote snapshot boundary is typed, bounded, and strictly read-only', async () => {
  const [facet, contracts, service, engine] = await Promise.all([
    source('Accessor/BrowserGitAccessor/Interface/IBrowserGitAccessor.cs'),
    source('Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs'),
    source('Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs'),
    source('wwwroot/js/browserGitEngine.js')
  ]);

  assert.match(facet, /GetRemoteSnapshotAsync\(RemoteSnapshotRequest request, CancellationToken cancellationToken/);
  for (const record of ['RemoteSnapshotRequest', 'RemoteSnapshotRef', 'RemoteSnapshotCommit', 'RemoteSnapshotBlob', 'RemoteSnapshot']) {
    assert.match(contracts, new RegExp(`record ${record}\\b`));
  }
  assert.match(contracts, /MaximumEntries = 1000/);
  assert.match(contracts, /Distinct\(StringComparer\.Ordinal\)/);
  assert.match(service, /InvokeWithValueAsync<RemoteSnapshotDto, RemoteSnapshot>/);
  assert.match(service, /BrowserGitRemoteSnapshotContract\.IsValidSnapshot/);
  assert.match(service, /Diagnostic: null/);

  const remoteSurface = engine.slice(
    engine.indexOf('export async function getRemoteSnapshot'),
    engine.indexOf('export async function checkoutBranch'));
  assert.match(remoteSurface, /resolveRef\(\{ fs, dir, ref: resolvedRef \}\)/);
  assert.match(remoteSurface, /readCommit/);
  assert.match(remoteSurface, /readRemoteSnapshotTree/);
  assert.match(engine, /git\.readTree/);
  assert.match(engine, /git\.readBlob/);
  assert.match(remoteSurface, /remoteSnapshotLimitExceeded/);
  assert.match(remoteSurface, /failureKind,[\s\S]*?null\);/);
  assert.doesNotMatch(remoteSurface, /fetch\(|checkout\(|writeFile\(|commit\(|push\(|persistFilesystem\(/);
});

test('reviewed reconciliation apply is preconditioned, bounded, and rollback-safe', async () => {
  const [facet, contracts, service, engine] = await Promise.all([
    source('Accessor/BrowserGitAccessor/Interface/IBrowserGitAccessor.cs'),
    source('Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs'),
    source('Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs'),
    source('wwwroot/js/browserGitEngine.js')
  ]);

  assert.match(facet, /ApplyReviewedReconciliationAsync\(ReviewedReconciliationApplyRequest request/);
  for (const record of [
    'ReviewedReconciliationSelection',
    'ReviewedReconciliationRemotePrecondition',
    'ReviewedReconciliationApplyRequest',
    'ReviewedReconciliationApplyReceipt'
  ]) {
    assert.match(contracts, new RegExp(`record ${record}\\b`));
  }
  assert.match(contracts, /selection\.Resolution is "KeepLocal" or "AcceptRemote"/);
  assert.match(contracts, /SetEquals\(request\.RemotePreconditions/);
  assert.match(service, /BrowserGitReconciliationContract\.IsValidRequest/);
  assert.match(service, /SanitizeReviewedReconciliationResult/);
  assert.match(service, /ReconciliationStaleRemote/);
  assert.match(service, /Diagnostic: null/);

  const applySurface = engine.slice(
    engine.indexOf('export async function applyReviewedReconciliation'),
    engine.indexOf('async function captureSynchronizationRefs'));
  assert.match(applySurface, /resolveRef\(\{ fs, dir, ref: remoteRef \}\)/);
  assert.match(applySurface, /expectedRemoteObjectId/);
  assert.match(applySurface, /readBlob/);
  assert.match(applySurface, /writeFile/);
  assert.match(applySurface, /restoreReviewedReconciliationFiles/);
  assert.match(applySurface, /persistFilesystem/);
  assert.match(applySurface, /reconciliationStaleRemote/);
  assert.match(applySurface, /reconciliationUnsupportedResolution/);
  assert.match(applySurface, /reconciliationCancelled/);
  assert.match(applySurface, /failureWithKind\([\s\S]*?, null\)/);
  assert.doesNotMatch(applySurface, /git\.(?:fetch|push|commit)\s*\(/);
  assert.doesNotMatch(applySurface, /personalAccessToken|github_pat|credential/i);
});
