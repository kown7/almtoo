import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const source = path => readFile(new URL(path, root), 'utf8');

const branchPath = 'Components/RepositoryBranchSelector.razor';
const historyPath = 'Components/RepositoryCommitHistory.razor';

async function clientSources() {
  const [branch, history, home] = await Promise.all([
    source(branchPath),
    source(historyPath),
    source('Pages/Home.razor')
  ]);
  return { branch, history, home };
}

test('branch and history Clients use passive Manager state and explicit intent callbacks', async () => {
  const { branch, history } = await clientSources();

  for (const [path, client] of [[branchPath, branch], [historyPath, history]]) {
    assert.match(client, /@using AlmToo\.Managers\.Repositories/);
    assert.match(client, /RepositoryWorkspaceState State/);
    assert.doesNotMatch(client, /@inject\b|\bWorkspace\./, `${path} must not orchestrate the Manager directly`);
    assert.doesNotMatch(client, /IBrowserGitAccessor|BrowserGitAccessor|IJSRuntime|IJSObjectReference|JSImport|InvokeAsync\s*</, `${path} must not depend on Accessor or browser interop APIs`);
    assert.doesNotMatch(client, /State\.[A-Za-z]\w*\s*=(?!=)/, `${path} must not mutate passive Manager state`);
  }

  assert.match(branch, /EventCallback OnRefreshRequested/);
  assert.match(branch, /EventCallback<string> OnBranchSelected/);
  assert.match(history, /EventCallback OnRetryRequested/);
});

test('branch selection exposes accessible loading, failure, empty, and busy contracts', async () => {
  const { branch } = await clientSources();

  assert.match(branch, /<section class="repository-branch-selector" aria-labelledby="repository-branch-heading">/);
  assert.match(branch, /<h2 id="repository-branch-heading">Branches<\/h2>/);
  assert.match(branch, /<label for="repository-branch">Switch to a local branch<\/label>/);
  assert.match(branch, /<select id="repository-branch"[\s\S]*@onchange="HandleBranchChanged"[\s\S]*disabled="@IsBusy"/);
  assert.match(branch, /<button[\s\S]*type="button"[\s\S]*@onclick="OnRefreshRequested"[\s\S]*disabled="@IsBusy"/);
  assert.match(branch, /role="status" aria-live="polite">Loading branch choices/);
  assert.match(branch, /role="alert" aria-live="assertive"/);
  assert.match(branch, /Retry branch loading/);
  assert.match(branch, /No local branches are available/);
  assert.match(branch, /Save the edited file and commit or discard changed files before switching branches/);
  assert.match(branch, /@foreach \(var branch in State\.AvailableBranches\)/);
  assert.match(branch, /string\.Equals\(branch\.Kind\.ToString\(\), "Local", StringComparison\.Ordinal\)/);
});

test('recent history exposes bounded results, keyboard controls, and safe recovery states', async () => {
  const { history } = await clientSources();

  assert.match(history, /<section class="repository-commit-history" aria-labelledby="repository-history-heading">/);
  assert.match(history, /<h2 id="repository-history-heading">Recent commits<\/h2>/);
  assert.match(history, /BoundedResultCount/);
  assert.match(history, /role="status" aria-live="polite">Loading recent commit history/);
  assert.match(history, /role="alert" aria-live="assertive"/);
  assert.match(history, /No commits were found for this branch yet/);
  assert.match(history, /<button[\s\S]*type="button"[\s\S]*@onclick="OnRetryRequested"[\s\S]*disabled="@IsBusy"/);
  assert.match(history, /aria-label="Recent commit history"|aria-label="Commit list"/);
  assert.match(history, /ShortCommitId\(commit\.CommitId\)/);
  assert.match(history, /@foreach \(var commit in State\.RecentHistory\.Entries\)/);
});

test('repository Clients remain Accessor-free while Home composes the concrete Manager', async () => {
  const { branch, history, home } = await clientSources();

  for (const [path, client] of [[branchPath, branch], [historyPath, history], ['Pages/Home.razor', home]]) {
    assert.doesNotMatch(client, /AlmToo\.Accessor|IBrowserGitAccessor|BrowserGitAccessor|IJSRuntime|IJSObjectReference|JSImport/, `${path} must remain Accessor-free`);
  }
  assert.match(home, /@inject RepositoryWorkspaceManager Workspace/);
  assert.doesNotMatch(home, /RepositoryWorkspaceManager\s*\.[A-Za-z]+|new\s+RepositoryWorkspaceManager/);

  assert.match(home, /<RepositoryBranchSelector State="State"[\s\S]*OnRefreshRequested="LoadBranchHistoryAsync"[\s\S]*OnBranchSelected="SwitchBranchAsync" \/>/);
  assert.match(home, /<RepositoryCommitHistory State="State"[\s\S]*OnRetryRequested="LoadBranchHistoryAsync"\s*\/>/);
  assert.match(home, /private async Task LoadBranchHistoryAsync\(\) => await Workspace\.LoadBranchHistoryAsync\(\);/);
  assert.match(home, /private async Task SwitchBranchAsync\(string branchName\) => await Workspace\.SwitchBranchAsync\(branchName\);/);
});
