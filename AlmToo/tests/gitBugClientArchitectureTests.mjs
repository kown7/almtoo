import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const source = path => readFile(new URL(path, root), 'utf8');

async function gitBugClientSources() {
  const [page, list, detail] = await Promise.all([
    source('Pages/GitBug.razor'),
    source('Components/GitBugIssueList.razor'),
    source('Components/GitBugIssueDetail.razor')
  ]);
  return { page, list, detail, all: [page, list, detail].join('\n') };
}

test('Git-Bug navigation and composition expose the concrete Manager Client', async () => {
  const [nav, program, manager] = await Promise.all([
    source('Layout/NavMenu.razor'),
    source('Program.cs'),
    source('Managers/GitBug/GitBugWorkspaceManager.cs')
  ]);

  assert.match(nav, /href="git-bug"[\s\S]*Git-Bug Issues/);
  assert.match(program, /AddScoped<GitBugWorkspaceManager>\(\)/);
  assert.match(manager, /LoadIssuesAsync\(CancellationToken cancellationToken = default\)/);
  assert.match(manager, /OpenIssueAsync\(string issueId, CancellationToken cancellationToken = default\)/);
  assert.doesNotMatch(program, /IGitBugWorkspaceManager/);
});

test('Git-Bug page renders every Manager-owned state and delegates intent', async () => {
  const { page } = await gitBugClientSources();

  assert.match(page, /@inject GitBugWorkspaceManager GitBugWorkspace/);
  assert.match(page, /private GitBugWorkspaceState State => GitBugWorkspace\.State/);
  for (const status of [
    'LoadingList', 'LoadingDetail', 'ListReady', 'DetailReady', 'Unavailable', 'Unsupported', 'Failed'
  ]) assert.match(page, new RegExp(`GitBugWorkspaceStatus\\.${status}`), `missing state: ${status}`);

  for (const delegation of [
    'GitBugWorkspace.SetRepository(RepositoryWorkspace.State.Repository)',
    'GitBugWorkspace.LoadIssuesAsync()',
    'GitBugWorkspace.OpenIssueAsync(issueId)',
    'GitBugWorkspace.ReturnToIssueList()',
    'GitBugWorkspace.CancelCurrentOperation()'
  ]) assert.ok(page.includes(delegation), `missing Manager delegation: ${delegation}`);

  assert.match(page, /GitBugWorkspace\.StateChanged \+= HandleGitBugStateChanged/);
  assert.match(page, /GitBugWorkspace\.StateChanged -= HandleGitBugStateChanged/);
  assert.match(page, /RepositoryWorkspace\.StateChanged \+= HandleRepositoryStateChanged/);
  assert.match(page, /RepositoryWorkspace\.StateChanged -= HandleRepositoryStateChanged/);
});

test('Git-Bug children are passive accessible list and detail views', async () => {
  const { list, detail } = await gitBugClientSources();

  for (const child of [list, detail]) {
    assert.match(child, /GitBugWorkspaceState State/);
    assert.doesNotMatch(child, /@inject|WorkspaceManager|Accessor|IJSRuntime|NavigationManager/);
  }

  assert.match(list, /<ul[^>]*aria-label="Git-Bug issue list"/);
  assert.match(list, /<button type="button"[\s\S]*aria-label="Open issue: @issue\.Title"/);
  assert.match(list, /EventCallback<string> OnIssueSelected/);
  assert.match(list, /OnIssueSelected\.InvokeAsync\(issue\.Id\.Value\)/);
  assert.match(detail, /<article[^>]*aria-labelledby="gitbug-detail-heading"/);
  assert.match(detail, /<button[^>]*@onclick="OnBackRequested"/);
  assert.match(detail, /Comments \(@issue\.Comments\.Count\)/);
  assert.match(detail, /EventCallback OnBackRequested/);
});

test('external issue content is rendered as text with no raw HTML or integration escape hatch', async () => {
  const { all } = await gitBugClientSources();

  for (const textBinding of [
    '@issue.Title', '@issue.AuthorDisplayName', '@label', '@issue.Summary.Title',
    '@issue.Summary.AuthorDisplayName', '@issue.Description', '@comment.AuthorDisplayName', '@comment.Body'
  ]) assert.ok(all.includes(textBinding), `missing text binding: ${textBinding}`);

  assert.doesNotMatch(all, /MarkupString|@\(\(MarkupString\)|innerHTML|AddMarkupContent|HtmlString/);
  assert.doesNotMatch(all, /AlmToo\.Accessor|IGitBugAccessor|BrowserGitAccessor|browserGitEngine|IJSRuntime|IJSObjectReference|JSImport/);
  assert.doesNotMatch(all, /new\s+GitBugIssue(?:Id|Query|State|Page|Summary|Detail)|GitBugComment\s+[A-Za-z_]/);
  assert.doesNotMatch(all, /type="(?:password|url)"|fetch\s*\(|HttpClient|\.Diagnostic\b|credential|token/i);
});

test('loading and failure states use accessible live semantics and safe recovery', async () => {
  const { page } = await gitBugClientSources();

  assert.match(page, /aria-busy="true"/);
  assert.match(page, /role="status" aria-live="polite"/);
  assert.match(page, /role="@\(State\.Status == GitBugWorkspaceStatus\.Failed \? "alert" : "status"\)"/);
  assert.match(page, /State\.SelectedIssueId is not null[\s\S]*Back to issues/);
  assert.match(page, /State\.CanRetry[\s\S]*Try again/);
  assert.match(page, /GitBugWorkspace\.OpenIssueAsync\(selectedIssueId\.Value\)/);
});

test('Git-Bug styles preserve focus, long text, and responsive controls', async () => {
  const styles = await source('Pages/GitBug.razor.css');

  assert.match(styles, /::deep \.gitbug-list__select:focus-visible/);
  assert.match(styles, /::deep \.gitbug-prose[\s\S]*white-space: pre-wrap/);
  assert.match(styles, /overflow-wrap: anywhere/);
  assert.match(styles, /@media \(max-width: 640px\)/);
  assert.match(styles, /data-gitbug-state="failed"/);
});
