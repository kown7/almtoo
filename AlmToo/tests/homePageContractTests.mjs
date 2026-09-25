import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const source = name => readFile(new URL(name, root), 'utf8');

async function clientSources() {
  return Promise.all([
    source('Pages/Home.razor'),
    source('Components/RepositoryFileBrowser.razor'),
    source('Components/RepositoryChangedFiles.razor'),
    source('Components/RepositoryCommitForm.razor')
  ]);
}

test('composition registers one scoped browser workspace Accessor and concrete Manager', async () => {
  const program = await source('Program.cs');

  assert.match(program, /AddScoped<BrowserGitAccessor>\(\)/);
  assert.match(program, /AddScoped<IBrowserGitAccessor>\(services => services\.GetRequiredService<BrowserGitAccessor>\(\)\)/);
  assert.match(program, /AddScoped<IBrowserFileAccessor>\(services => services\.GetRequiredService<BrowserGitAccessor>\(\)\)/);
  assert.match(program, /AddScoped<RepositoryWorkspaceManager>\(\)/);
  assert.doesNotMatch(program, /IRepositoryWorkspaceManager|IVersionControlAccessor|Resources/);
});

test('Home injects the concrete repository Manager and has no Client to Accessor edge', async () => {
  const [home, ...children] = await clientSources();
  const allClients = [home, ...children].join('\n');

  assert.match(home, /@inject RepositoryWorkspaceManager Workspace/);
  assert.match(home, /@using AlmToo\.Managers\.Repositories/);
  assert.doesNotMatch(allClients, /IBrowserGitAccessor|IBrowserFileAccessor|BrowserGitAccessor|AlmToo\.Accessor|AlmToo\.Services\.Git/);
  assert.doesNotMatch(allClients, /IJSRuntime|IJSObjectReference|JSImport|InvokeAsync<.*>\("(?:storeCredential|getCredentialForPush|forgetCredential|hasCredential)"/);
  assert.doesNotMatch(allClients, /IRepositoryWorkspaceManager|IVersionControlAccessor|Resources/);
});

test('Home delegates the complete repository workflow to the Manager', async () => {
  const home = await source('Pages/Home.razor');

  for (const delegation of [
    'Workspace.InitializeCredentialStateAsync()',
    'Workspace.OpenRepositoryAsync(repositoryUrl)',
    'Workspace.LoadDirectoryAsync(path)',
    'Workspace.SelectFileAsync(path)',
    'Workspace.EditSelectedFile(',
    'Workspace.SaveSelectedFileAsync()',
    'Workspace.RefreshStatusAsync()',
    'Workspace.CommitAsync(input)',
    'Workspace.InspectPushAsync()',
    'Workspace.StoreCredentialAsync(credentialForAttempt)',
    'Workspace.ForgetCredentialAsync()',
    'Workspace.PushReviewedCommitAsync(State.PushReview)',
    'Workspace.CancelCurrentOperation'
  ]) assert.ok(home.includes(delegation), `missing Manager delegation: ${delegation}`);

  assert.match(home, /Workspace\.StateChanged \+= HandleStateChanged/);
  assert.match(home, /Workspace\.StateChanged -= HandleStateChanged/);
  assert.doesNotMatch(home, /new RepositoryOpenRequest|new PushRequest|NormalizeRepositoryPath|GetPushRecoveryGuidance|CreateWorkspaceName/);
});

test('Home renders Manager-owned state and passes that state to presentation children', async () => {
  const home = await source('Pages/Home.razor');

  assert.match(home, /private RepositoryWorkspaceState State => Workspace\.State/);
  assert.match(home, /<RepositoryFileBrowser State="State"[\s\S]*OnDirectorySelected="LoadDirectoryAsync"[\s\S]*OnFileSelected="SelectFileAsync"/);
  assert.match(home, /<RepositoryChangedFiles State="State" OnRefreshRequested="RefreshStatusAsync"/);
  assert.match(home, /<RepositoryCommitForm State="State" OnCommitRequested="CommitAsync"/);
  assert.match(home, /@State\.Repository\.RepositoryUrl/);
  assert.match(home, /@State\.EditableFileContent/);
  assert.match(home, /@State\.Result\.Message/);
  assert.doesNotMatch(home, /\.Diagnostic/);
});

test('repository child components are presentation-only Manager state and callback surfaces', async () => {
  const [fileBrowser, changedFiles, commitForm] = await Promise.all([
    source('Components/RepositoryFileBrowser.razor'),
    source('Components/RepositoryChangedFiles.razor'),
    source('Components/RepositoryCommitForm.razor')
  ]);
  const children = [fileBrowser, changedFiles, commitForm].join('\n');

  for (const child of [fileBrowser, changedFiles, commitForm]) {
    assert.match(child, /@using AlmToo\.Managers\.Repositories/);
    assert.match(child, /RepositoryWorkspaceState State/);
    assert.doesNotMatch(child, /@inject|Workspace\.|Accessor|IJSRuntime/);
  }
  assert.match(fileBrowser, /EventCallback<string> OnDirectorySelected/);
  assert.match(fileBrowser, /EventCallback<string> OnFileSelected/);
  assert.match(changedFiles, /EventCallback OnRefreshRequested/);
  assert.match(commitForm, /EventCallback<RepositoryCommitInput> OnCommitRequested/);
  assert.doesNotMatch(children, /\b(?:CommitRequest|PushRequest|RepositoryOpenRequest)\b/);
});

test('file browser distinguishes directories editable text and unsupported files', async () => {
  const fileBrowser = await source('Components/RepositoryFileBrowser.razor');

  assert.match(fileBrowser, /entry\.Kind\.ToString\(\) == "Directory"/);
  assert.match(fileBrowser, /OnDirectorySelected\.InvokeAsync\(entry\.Path\)/);
  assert.match(fileBrowser, /entry\.IsEditableText/);
  assert.match(fileBrowser, /OnFileSelected\.InvokeAsync\(entry\.Path\)/);
  assert.match(fileBrowser, /Binary or oversized file not available for text editing/);
  assert.match(fileBrowser, /aria-disabled="true"/);
});

test('commit form validates presentation input and delegates a Manager-owned DTO', async () => {
  const commitForm = await source('Components/RepositoryCommitForm.razor');

  assert.match(commitForm, /@onsubmit="CreateCommitAsync"/);
  assert.match(commitForm, /CommitMessage\.Trim\(\)/);
  assert.match(commitForm, /Enter a commit message\./);
  assert.match(commitForm, /Enter an author name\./);
  assert.match(commitForm, /Enter a valid author email\./);
  assert.match(commitForm, /new RepositoryCommitInput\(message, authorName, authorEmail\)/);
  assert.match(commitForm, /State\.CreatedCommit\.CommitId/);
  assert.match(commitForm, /State\.CreatedCommit\.Message/);
});

test('credential and push presentation reveal presence only and preserve exact review coordinates', async () => {
  const home = await source('Pages/Home.razor');

  assert.match(home, /id="github-pat"[\s\S]*type="password"[\s\S]*value="@credentialInput"/);
  assert.match(home, /credentialInput = string\.Empty;[\s\S]*Workspace\.StoreCredentialAsync\(credentialForAttempt\)[\s\S]*credentialForAttempt = string\.Empty/);
  assert.match(home, /data-credential-state="present"/);
  assert.match(home, /data-push-review="origin">@State\.PushReview\.RepositoryUrl/);
  assert.match(home, /data-push-review="branch">@State\.PushReview\.Branch/);
  assert.match(home, /data-push-review="destination">@State\.PushReview\.DestinationRef/);
  assert.match(home, /data-push-review="sha">@State\.PushReview\.OutgoingCommitId/);
  assert.match(home, /checked="@State\.PushConfirmed"/);
  assert.match(home, /State\.PushReview is not null && State\.PushConfirmed && State\.HasCredential && !State\.IsBusy/);
  assert.doesNotMatch(home, /sessionStorage|localStorage|getCredentialForPush|personalAccessToken|\.Diagnostic/);
});

test('failure and cancellation state remain accessible and manually recoverable', async () => {
  const home = await source('Pages/Home.razor');

  assert.match(home, /Cancel current operation/);
  assert.match(home, /data-push-failure="@State\.PushFailureCategory"/);
  assert.match(home, /role="@\(State\.PushFailureCategory is null \? "status" : "alert"\)"/);
  assert.match(home, /aria-live="@\(State\.PushFailureCategory is null \? "polite" : "assertive"\)"/);
  assert.match(home, /CredentialReplacementRequired/);
  assert.match(home, /Review push/);
  assert.match(home, /Push reviewed commit/);
});

test('repository browser and push review styles retain stateful responsive surfaces', async () => {
  const [homeStyles, fileBrowserStyles, commitStyles] = await Promise.all([
    source('Pages/Home.razor.css'),
    source('Components/RepositoryFileBrowser.razor.css'),
    source('Components/RepositoryCommitForm.razor.css')
  ]);

  assert.match(homeStyles, /\.repository-status/);
  assert.match(homeStyles, /\.file-editor__textarea/);
  assert.match(homeStyles, /\.push-panel/);
  assert.match(homeStyles, /\.push-review__sha/);
  assert.match(homeStyles, /\.status-message\[data-push-failure\]/);
  assert.match(fileBrowserStyles, /\.file-browser__entry--unsupported/);
  assert.match(commitStyles, /\.commit-form__confirmation/);
});
