import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const source = name => readFile(new URL(name, root), 'utf8');

test('Browser Git contracts use the approved adjacent generic namespace and exact facets', async () => {
  const [git, file, contracts] = await Promise.all([
    source('Accessor/BrowserGitAccessor/Interface/IBrowserGitAccessor.cs'),
    source('Accessor/BrowserGitAccessor/Interface/IBrowserFileAccessor.cs'),
    source('Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs')
  ]);
  for (const text of [git, file, contracts]) {
    assert.match(text, /namespace AlmToo\.Accessor\.BrowserGitAccessor\.Interface;/);
    assert.doesNotMatch(text, /\.Context|Resources/);
  }
  assert.match(git, /interface IBrowserGitAccessor : IAsyncDisposable/);
  for (const operation of ['CloneOrOpenAsync', 'GetStatusAsync', 'CommitAsync', 'InspectPushAsync', 'HasCredentialAsync', 'StoreCredentialAsync', 'ForgetCredentialAsync', 'PushAsync']) assert.match(git, new RegExp(operation));
  assert.match(file, /FilterFilesAsync\(FilterFilesRequest request/);
  assert.match(file, /UpdateFilesAsync\(UpdateFilesRequest request/);
  assert.doesNotMatch(git + file, /personalAccessToken|InitializeAsync|ListFilesAsync|ReadTextFileAsync|WriteTextFileAsync/);
  for (const dto of ['FilterFilesRequest', 'FilterFilesResult', 'UpdateFilesRequest', 'UpdateFilesResult', 'PushReview', 'PushRequest', 'PushResult']) assert.match(contracts, new RegExp(`record ${dto}\\b`));
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
  assert.doesNotMatch(program, /AddScoped<IBrowserGitAccessor, BrowserGitAccessor>|AddScoped<IBrowserFileAccessor, BrowserGitAccessor>/);
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

test('Accessor normalizes cancellation, interop failures, initialization, and partial disposal', async () => {
  const service = await source('Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs');
  assert.match(service, /initializationTask \?\?= InitializeAsync/);
  assert.match(service, /catch \(OperationCanceledException\)/);
  assert.match(service, /catch \(JSException\)/);
  assert.doesNotMatch(service, /exception\.ToString|catch \([^)]* exception\)/i);
  assert.match(service, /moduleTask is \{ IsCompletedSuccessfully: true \}/);
  assert.match(service, /credentialModuleTask is \{ IsCompletedSuccessfully: true \}/);
}
);
