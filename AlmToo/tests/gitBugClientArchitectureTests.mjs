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
    source('Managers/Workspace/Service/WorkspaceGitBugFacet.cs')
  ]);

  assert.match(nav, /href="git-bug"[\s\S]*Git-Bug Issues/);
  assert.match(program, /AddScoped<WorkspaceManager>\(\)/);
  assert.match(manager, /LoadIssuesAsync\(\)/);
  assert.match(manager, /LoadIssuesAsync\([\s\S]*GitBugIssueQuery query,[\s\S]*CancellationToken cancellationToken = default\)/);
  assert.match(manager, /OpenIssueAsync\(string issueId\)/);
  assert.match(manager, /OpenIssueAsync\([\s\S]*GitBugIssueId issueId,[\s\S]*CancellationToken cancellationToken = default\)/);
  assert.doesNotMatch(program, /IWorkspaceManager/);
});

test('WorkspaceManager owns repository, publication, and Git-Bug use-cases', async () => {
  const [page, home, gitBugManager, gitBugState, repositoryManager, repositoryState] = await Promise.all([
    source('Pages/GitBug.razor'),
    source('Pages/Home.razor'),
    source('Managers/Workspace/Service/WorkspaceGitBugFacet.cs'),
    source('Managers/Workspace/Interface/WorkspaceState.cs'),
    source('Managers/Workspace/Service/WorkspaceManager.cs'),
    source('Managers/Workspace/Interface/WorkspaceState.cs')
  ]);

  assert.doesNotMatch(page, /AlmToo\.Accessor|\bCommitRequest\b|IGitBugAccessor|BrowserGitAccessor/);
  assert.match(page, /Workspace\.SetProposedCommit\(new WorkspaceCommitInput\(/);
  assert.match(page, /RepositoryState="Workspace\.State"/);
  assert.match(page, /OnReviewCommitRequested="ReviewCommitAsync"/);
  assert.match(page, /OnReviewPushRequested="ReviewPushAsync"/);
  assert.match(page, /OnPushRequested="PushAsync"/);
  assert.match(home, /Workspace\.State/);
  assert.match(home, /RepositoryChangedFiles/);
  assert.doesNotMatch(gitBugManager, /^\s*(?:public|private|protected)[^{\n]*(?:Commit|Push)[^{\n]*\(/m);
  assert.match(gitBugState, /public GitBugState GitBug/);

  for (const lifecycleMethod of [
    'ReviewCommitAsync', 'ReviewPublicationAsync', 'SetProposedCommit', 'ConfirmLocalCommitAsync', 'CommitAsync',
    'InspectPushAsync', 'StoreCredentialAsync', 'PushReviewedCommitAsync', 'PublishReviewedPublicationAsync', 'CancelCurrentOperation'
  ]) assert.match(repositoryManager, new RegExp(`\\b${lifecycleMethod}\\b`), `missing repository lifecycle owner: ${lifecycleMethod}`);
  assert.match(repositoryState, /GitPublicationReview\? PublicationReview/);
  assert.match(repositoryState, /GitRefUpdate> ChangedRefs/);
  assert.match(repositoryState, /WorkspaceCommitInput\? ProposedCommit/);
  assert.match(repositoryState, /PushReview\? PushReview/);
  assert.match(repositoryState, /CredentialReplacementRequired/);
});

test('Git-Bug page renders every Manager-owned state and delegates intent', async () => {
  const { page } = await gitBugClientSources();

  assert.match(page, /@inject WorkspaceManager Workspace/);
  assert.match(page, /private GitBugState State => Workspace\.State\.GitBug/);
  for (const status of [
    'LoadingList', 'LoadingDetail', 'ListReady', 'DetailReady', 'Unavailable', 'Unsupported', 'Failed'
  ]) assert.match(page, new RegExp(`GitBugWorkspaceStatus\\.${status}`), `missing state: ${status}`);

  for (const delegation of [
    'Workspace.LoadIssuesAsync()',
    'Workspace.OpenIssueAsync(issueId)',
    'Workspace.ReturnToIssueList()',
    'Workspace.CancelCurrentOperation()'
  ]) assert.ok(page.includes(delegation), `missing Manager delegation: ${delegation}`);

  assert.match(page, /Workspace\.StateChanged \+= HandleStateChanged/);
  assert.match(page, /Workspace\.StateChanged -= HandleStateChanged/);
});

test('issue writes refresh the central repository state without a second publication lifecycle', async () => {
  const [page, workflow] = await Promise.all([
    source('Pages/GitBug.razor'),
    source('e2e/gitBugWriteWorkflow.spec.mjs')
  ]);

  const editAndComment = page.slice(
    page.indexOf('private async Task EditIssueAsync'),
    page.indexOf('private async Task RefreshRepositoryStatusAsync'));
  assert.match(editAndComment, /Workspace\.EditSelectedIssueAsync\([\s\S]*Workspace\.RefreshStatusAsync\(\)/);
  assert.match(editAndComment, /Workspace\.AddCommentToSelectedIssueAsync\([\s\S]*Workspace\.RefreshStatusAsync\(\)/);
  assert.doesNotMatch(editAndComment, /CommitAsync|PushAsync|InspectPushAsync|Publish/);

  const editCheckpoint = workflow.indexOf("await expect(acceptancePage.getByRole('textbox', { name: 'Description' })).toHaveValue(editedDescription");
  const reviewCheckpoint = workflow.indexOf("setPhase('review Git-Bug refs')");
  assert.ok(editCheckpoint >= 0 && reviewCheckpoint > editCheckpoint, 'browser workflow must review only after issue writes');
  assert.match(workflow.slice(editCheckpoint, reviewCheckpoint), /Confirm and create local commit[\s\S]*toHaveCount\(0\)/);
  assert.match(workflow.slice(editCheckpoint, reviewCheckpoint), /Publish reviewed refs[\s\S]*toHaveCount\(0\)/);
});

test('Git-Bug children are passive accessible list and detail views', async () => {
  const { list, detail } = await gitBugClientSources();

  for (const child of [list, detail]) {
    assert.match(child, /GitBugState State/);
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
  assert.match(all, /type="password"[\s\S]*autocomplete="new-password"/);
  assert.match(all, /credential is kept only for this browser tab/);
  assert.doesNotMatch(all, /@credential|@State\.[A-Za-z]*Credential|fetch\s*\(|HttpClient|\.Diagnostic\b|sessionStorage|localStorage/i);
});

test('loading and failure states use accessible live semantics and safe recovery', async () => {
  const { page } = await gitBugClientSources();

  assert.match(page, /aria-busy="true"/);
  assert.match(page, /role="status" aria-live="polite"/);
  assert.match(page, /role="@\(State\.Status == GitBugWorkspaceStatus\.Failed \? "alert" : "status"\)"/);
  assert.match(page, /State\.SelectedIssueId is not null[\s\S]*Back to issues/);
  assert.match(page, /State\.CanRetry[\s\S]*Try again/);
  assert.match(page, /Workspace\.OpenIssueAsync\(selectedIssueId\.Value\)/);
});

test('Git-Bug styles preserve focus, long text, and responsive controls', async () => {
  const styles = await source('Pages/GitBug.razor.css');

  assert.match(styles, /::deep \.gitbug-list__select:focus-visible/);
  assert.match(styles, /::deep \.gitbug-prose[\s\S]*white-space: pre-wrap/);
  assert.match(styles, /overflow-wrap: anywhere/);
  assert.match(styles, /@media \(max-width: 640px\)/);
  assert.match(styles, /data-gitbug-state="failed"/);
});

test('Git-Bug browser acceptance is fixture-backed and enforces the read-only Client boundary', async () => {
  const [specification, fixture] = await Promise.all([
    source('e2e/gitBugIssueBrowser.spec.mjs'),
    source('e2e/gitBugFixture.mjs')
  ]);

  for (const state of ['supported', 'unavailable', 'unsupported']) {
    assert.match(specification, new RegExp(`openFixtureRepository\\(page, '${state}'\\)`));
  }
  assert.match(specification, /snapshotGitBugRepository\(page\)/);
  assert.match(specification, /fixtureSnapshotDigest[\s\S]*toBe\(before\)/);
  assert.match(specification, /remoteRequests[\s\S]*toEqual\(\[\]\)/);
  assert.match(specification, /input\[type="password"\]/);
  assert.match(specification, /Back to issues/);
  assert.match(fixture, /tests\/fixtures\/gitBug\/v0\.11\.0/);
  assert.match(fixture, /new globalThis\.LightningFS\('almtoo-git', \{ wipe: true \}\)/);
  assert.match(fixture, /crypto\.subtle\.digest\('SHA-256'/);
  assert.doesNotMatch(`${specification}\n${fixture}`, /personalAccessToken|sessionStorage|localStorage/);
});
