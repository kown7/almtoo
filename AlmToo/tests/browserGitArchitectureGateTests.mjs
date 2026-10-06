import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const reviewPath = 'Accessor/BrowserGitAccessor/DESIGN.md';
const gitBugReviewPath = 'Accessor/BrowserGitAccessor/GIT-BUG-DESIGN.md';
const contractPath = 'Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs';
const gitBugInterfacePath = 'Accessor/BrowserGitAccessor/Interface/IGitBugAccessor.cs';
const browserGitAccessorPath = 'Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs';
const browserGitResourcePath = 'wwwroot/js/browserGitEngine.js';
const excludedDirectoryNames = new Set(['bin', 'obj', 'node_modules', 'test-results', 'playwright-report', '.git', '.gsd']);
const dtoDeclaration = /\b(?:record|enum)\s+(?:GitOperationResult|GitOperationFailureKind|GitBugIssueId|GitBugIssueQuery|GitBugIssueState|GitBugIssuePage|GitBugIssueSummary|GitBugIssueDetail|GitBugComment|RepositoryOpenRequest|RepositoryInfo|FileFilterKind|FilterFilesRequest|FilterFilesResult|UpdateFilesRequest|TextFileUpdate|UpdateFilesResult|RepositoryFileEntry|RepositoryFileKind|TextFileContent|ChangedFile|GitChangeKind|CommitRequest|CommitInfo|PushReview|PushRequest|PushResult|SynchronizationIntent|SynchronizationDecisionState|SynchronizationFailureCategory|SynchronizationRequest|IncomingCommitMetadata|ChangedFileSummary|SynchronizationReview|SynchronizationFailure)\b/;

async function collectFiles(relativeDirectory, include) {
  const directory = new URL(`${relativeDirectory}/`, root);
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const relativePath = `${relativeDirectory}/${entry.name}`;
    if (entry.isDirectory() && !excludedDirectoryNames.has(entry.name)) files.push(...await collectFiles(relativePath, include));
    else if (entry.isFile() && include(relativePath)) files.push(relativePath);
  }
  return files;
}

async function readSources(paths) {
  return new Map(await Promise.all(paths.map(async path => [path, await readFile(new URL(path, root), 'utf8')])));
}

async function productionSnapshot() {
  const clientPaths = [
    ...await collectFiles('Pages', path => /\.(?:razor|cs)$/.test(path)),
    ...await collectFiles('Components', path => /\.(?:razor|cs)$/.test(path))
  ];
  const managerPaths = await collectFiles('Managers/Repositories', path => path.endsWith('.cs'));
  const accessorPaths = await collectFiles('Accessor/BrowserGitAccessor', path => path.endsWith('.cs'));
  return {
    clients: await readSources(clientPaths),
    managers: await readSources(managerPaths),
    accessors: await readSources([...accessorPaths, browserGitResourcePath, 'wwwroot/js/pushCredentialSession.js']),
    contracts: await readSources([contractPath, gitBugInterfacePath]),
    composition: await readSources(['Program.cs']),
    design: await readFile(new URL(reviewPath, root), 'utf8'),
    gitBugDesign: await readFile(new URL(gitBugReviewPath, root), 'utf8')
  };
}

const violation = (rule, path, message) => ({ rule, path, message });
function matchingSources(sources, pattern, rule, message) {
  const failures = [];
  for (const [path, source] of sources) if (pattern.test(source)) failures.push(violation(rule, path, message));
  return failures;
}
function reviewSection(document, heading, nextHeading) {
  const start = document.indexOf(heading);
  if (start < 0) return null;
  const end = nextHeading ? document.indexOf(nextHeading, start + heading.length) : document.length;
  return document.slice(start, end < 0 ? document.length : end);
}

export function inspectArchitecture(snapshot) {
  const failures = [];
  const production = new Map([...snapshot.clients, ...snapshot.managers, ...snapshot.accessors, ...snapshot.contracts, ...snapshot.composition]);

  failures.push(...matchingSources(snapshot.clients, /\b(?:IBrowserGitAccessor|IBrowserFileAccessor|IGitBugAccessor|BrowserGitAccessor|browserGitEngine|GitBugIssue(?:Id|Query|State|Page|Summary|Detail)|GitBugComment|IVersionControlAccessor|BrowserGitService|IBrowserGitService|GitService)\b|AlmToo\.(?:Accessor|Accessors|Services\.Git)\b/, 'CLIENT_ACCESSOR_EDGE', 'Client must depend on a concrete Manager and passive Manager state, never a Browser Git or Git-Bug Accessor contract, engine, or namespace'));
  failures.push(...matchingSources(snapshot.clients, /\b(?:IJSRuntime|IJSObjectReference|JSImport)\b|\bInvokeAsync\s*</, 'CLIENT_GIT_INTEROP', 'Client must not own Git-related JavaScript interop'));
  failures.push(...matchingSources(snapshot.clients, /\.Diagnostic\b|\b(?:personalAccessToken|accessToken)\b/i, 'CLIENT_DIAGNOSTIC_EXPOSURE', 'Client must not expose raw diagnostics or credential values'));

  for (const [path, source] of snapshot.managers) {
    const declared = new Set([...source.matchAll(/\b(?:class|record)\s+([A-Z]\w*Manager)\b/g)].map(match => match[1]));
    const referenced = new Set([...source.matchAll(/\b([A-Z]\w*Manager)\b/g)].map(match => match[1]));
    for (const name of referenced) if (!declared.has(name)) failures.push(violation('MANAGER_TO_MANAGER', path, `Manager must not depend on another Manager (${name})`));
    if (/\bIJSRuntime\b|Microsoft\.JSInterop/.test(source)) failures.push(violation('MANAGER_PLATFORM_INTEROP', path, 'Manager must reach browser Git only through Accessor contracts'));
  }

  failures.push(...matchingSources(snapshot.accessors, /AlmToo\.(?:Managers|Pages|Components)\b|\b(?:NavigationManager|RenderFragment|EventCallback|RepositoryWorkspaceState|StateChanged|CredentialReplacementRequired|ConfirmPush|AutoRetry)\b/, 'ACCESSOR_WORKFLOW_OR_UI', 'Accessor must not own Manager workflow policy or Client presentation state'));
  failures.push(...matchingSources(production, /\b(?:IVersionControlAccessor|VersionControlAccessor|IBrowserGitService|BrowserGitService)\b|AlmToo\.(?:Accessors\.Git|Services\.Git)\b/, 'LEGACY_GIT_ARCHITECTURE', 'legacy Browser Git service name or namespace is forbidden'));

  const contract = snapshot.contracts.get(contractPath);
  if (contract === undefined) {
    failures.push(violation('CONTRACT_PATH', contractPath, 'missing canonical Browser Git Accessor contract'));
  } else {
    if (!/namespace\s+AlmToo\.Accessor\.BrowserGitAccessor\.Interface\s*;/.test(contract)) failures.push(violation('CONTRACT_NAMESPACE', contractPath, 'Browser Git DTOs must use the Accessor interface namespace'));
    if (!dtoDeclaration.test(contract)) failures.push(violation('CONTRACT_CONTENT', contractPath, 'Browser Git contract must declare the passive request, result, and value records and enums'));
    const executableContract = contract.replace(/^\s*\/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '');
    if (/\b(?:class|interface|static)\b|=>|\b(?:Success|Failure)\b\s*\(|\bRepositoryWorkspaceState\b|\b(?:get|set)\s*\{|\b(?:if|for|foreach|while|switch|throw|return|await)\b/.test(executableContract)) failures.push(violation('CONTRACT_BEHAVIOR', contractPath, 'Browser Git contract must contain passive records and enums only, with no methods or executable bodies'));
  }
  for (const [path, source] of snapshot.accessors) {
    if (path !== contractPath && dtoDeclaration.test(source)) failures.push(violation('CONTRACT_PATH', path, `Browser Git DTOs must be declared only at ${contractPath}`));
  }

  const gitBugInterface = snapshot.contracts.get(gitBugInterfacePath) ?? '';
  if (!/namespace\s+AlmToo\.Accessor\.BrowserGitAccessor\.Interface\s*;/.test(gitBugInterface)
      || !/interface\s+IGitBugAccessor\b/.test(gitBugInterface)
      || !/ListIssuesAsync\s*\(/.test(gitBugInterface)
      || !/GetIssueAsync\s*\(/.test(gitBugInterface)) {
    failures.push(violation('GIT_BUG_FACET', gitBugInterfacePath, 'Git-Bug list and detail must remain on the co-located Accessor facet'));
  }
  const browserGitAccessor = snapshot.accessors.get(browserGitAccessorPath) ?? '';
  if (!/class\s+BrowserGitAccessor\s*:[^{\n]*\bIGitBugAccessor\b/.test(browserGitAccessor)) {
    failures.push(violation('GIT_BUG_FACET', browserGitAccessorPath, 'the existing concrete BrowserGitAccessor must directly implement IGitBugAccessor'));
  }
  const browserGitResource = snapshot.accessors.get(browserGitResourcePath) ?? '';
  for (const exportName of ['listGitBugIssues', 'getGitBugIssue']) {
    if (!new RegExp(`export\\s+async\\s+function\\s+${exportName}\\b`).test(browserGitResource)) {
      failures.push(violation('GIT_BUG_RESOURCE', browserGitResourcePath, `missing read-only Resource export ${exportName}`));
    }
  }

  const program = snapshot.composition.get('Program.cs') ?? '';
  const registrations = [
    [/AddScoped<BrowserGitAccessor>\s*\(\s*\)/, 'concrete scoped BrowserGitAccessor registration'],
    [/AddScoped<IBrowserGitAccessor>\s*\(\s*\w+\s*=>\s*\w+\.GetRequiredService<BrowserGitAccessor>\s*\(\s*\)\s*\)/, 'IBrowserGitAccessor factory resolving the shared concrete instance'],
    [/AddScoped<IBrowserFileAccessor>\s*\(\s*\w+\s*=>\s*\w+\.GetRequiredService<BrowserGitAccessor>\s*\(\s*\)\s*\)/, 'IBrowserFileAccessor factory resolving the shared concrete instance'],
    [/AddScoped<IGitBugAccessor>\s*\(\s*\w+\s*=>\s*\w+\.GetRequiredService<BrowserGitAccessor>\s*\(\s*\)\s*\)/, 'IGitBugAccessor factory resolving the shared concrete instance'],
    [/AddScoped<RepositoryWorkspaceManager>\s*\(\s*\)/, 'concrete scoped RepositoryWorkspaceManager registration']
  ];
  for (const [pattern, expectation] of registrations) if (!pattern.test(program)) failures.push(violation('SCOPED_COMPOSITION', 'Program.cs', `missing ${expectation}`));
  if (/AddScoped<IRepositoryWorkspaceManager>/.test(program)) failures.push(violation('MANAGER_INTERFACE', 'Program.cs', 'the cohesive concrete Manager must remain interface-free'));

  const headings = ['### 1. Layer assignments', '### 2. Dependency direction', '### 3. Interface justification', '### 4. Cohesion and decomposition', '### 5. Verification placement', '### 6. Exceptions', '## Compliance statement'];
  for (let index = 0; index < headings.length - 1; index += 1) {
    const section = reviewSection(snapshot.design, headings[index], headings[index + 1]);
    if (section === null) failures.push(violation('REVIEW_COVERAGE', reviewPath, `missing checklist section: ${headings[index]}`));
    else if (!/\*\*Result:\*\*\s+(?:PASS|NOT APPLICABLE)\b/.test(section)) failures.push(violation('REVIEW_RESULT', reviewPath, `${headings[index]} must record PASS or NOT APPLICABLE with rationale`));
  }
  const layerReview = reviewSection(snapshot.design, headings[0], headings[1]) ?? '';
  const interfaceReview = reviewSection(snapshot.design, headings[2], headings[3]) ?? '';
  const required = [
    [layerReview, /Pages\/Home\.razor[\s\S]*\|\s*Client\s*\|/, 'Home Client layer mapping'],
    [layerReview, /RepositoryWorkspaceManager[\s\S]*\|\s*Manager\s*\|/, 'repository Manager layer mapping'],
    [layerReview, /browserGitEngine\.js[\s\S]*\|\s*Accessor\s*\|/, 'browser Git engine Accessor layer mapping'],
    [layerReview, /IBrowserGitAccessor[\s\S]*\|\s*Accessor\s*\|/, 'Browser Git Accessor layer mapping'],
    [layerReview, /Accessor\/BrowserGitAccessor\/Interface\/BrowserGitContracts\.cs[\s\S]*\|\s*Accessor contract\s*\|/, 'Browser Git Accessor contract mapping'],
    [interfaceReview, /IBrowserGitAccessor[^\n]*platform boundary[^\n]*substitution seam/i, 'IBrowserGitAccessor boundary and substitution rationale'],
    [interfaceReview, /IBrowserFileAccessor[^\n]*platform boundary[^\n]*substitution seam/i, 'IBrowserFileAccessor boundary and substitution rationale'],
    [interfaceReview, /RepositoryWorkspaceManager[^\n]*interface-free/i, 'interface-free concrete Manager rationale'],
    [snapshot.design, /\*\*Overall result:\*\*\s+PASS\b/, 'overall passing result']
  ];
  for (const [document, pattern, description] of required) if (!pattern.test(document)) failures.push(violation('REVIEW_DIVERGENCE', reviewPath, `missing ${description}`));
  if (/\*\*Result:\*\*\s+FAIL\b|\*\*Overall result:\*\*\s+FAIL\b/.test(snapshot.design)) failures.push(violation('REVIEW_FAILURE', reviewPath, 'implementation-state review contains an unexplained failure'));

  const gitBugReview = reviewSection(snapshot.gitBugDesign ?? '', '### T03 iDesign review', '## Manager and Client behavior');
  const gitBugReviewRequirements = [
    [/\*\*Result:\*\*\s+PASS\b/, 'passing T03 result'],
    [/\bClient\b[\s\S]*\bManager\b[\s\S]*\bAccessor\b[\s\S]*\bResource\b/, 'explicit one-way layer assignment'],
    [/IGitBugAccessor[\s\S]*external-format[\s\S]*platform boundary/i, 'IGitBugAccessor boundary justification'],
    [/no architectural exception/i, 'exception disposition']
  ];
  if (gitBugReview === null) failures.push(violation('GIT_BUG_REVIEW', gitBugReviewPath, 'missing T03 iDesign review'));
  else for (const [pattern, expectation] of gitBugReviewRequirements) {
    if (!pattern.test(gitBugReview)) failures.push(violation('GIT_BUG_REVIEW', gitBugReviewPath, `T03 review is missing ${expectation}`));
  }
  return failures;
}

function assertViolation(snapshot, expectedRule, expectedPath) {
  const failures = inspectArchitecture(snapshot);
  assert.ok(failures.some(failure => failure.rule === expectedRule && failure.path === expectedPath), `expected ${expectedRule} at ${expectedPath}; received ${JSON.stringify(failures)}`);
}

function fixture(overrides = {}) {
  const validReview = [
    '### 1. Layer assignments\n| Pages/Home.razor | Client | Render | Manager | Accessor |\n| RepositoryWorkspaceManager | Manager | Coordinate | Accessor | Manager |\n| browserGitEngine.js | Accessor | Integrate | Platform | Client |\n| IBrowserGitAccessor | Accessor | Integrate | Platform | Client |\n| Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs | Accessor contract | Transfer | Accessor interfaces and data | Behavior |\n**Result:** PASS',
    '### 2. Dependency direction\nOne way.\n**Result:** PASS',
    '### 3. Interface justification\n| IBrowserGitAccessor | browser Git platform boundary and substitution seam |\n| IBrowserFileAccessor | browser filesystem platform boundary and substitution seam |\nThe concrete RepositoryWorkspaceManager remains interface-free.\n**Result:** PASS',
    '### 4. Cohesion and decomposition\nCohesive.\n**Result:** PASS',
    '### 5. Verification placement\nCovered.\n**Result:** PASS',
    '### 6. Exceptions\nNone.\n**Result:** NOT APPLICABLE',
    '## Compliance statement\n**Overall result:** PASS'
  ].join('\n');
  const base = {
    clients: new Map([['Pages/Home.razor', '@inject RepositoryWorkspaceManager Workspace']]),
    managers: new Map([['Managers/Repositories/RepositoryWorkspaceManager.cs', 'public sealed class RepositoryWorkspaceManager { }']]),
    accessors: new Map([
      ['Accessor/BrowserGitAccessor/Interface/IBrowserGitAccessor.cs', 'public interface IBrowserGitAccessor { }'],
      [browserGitAccessorPath, 'public sealed class BrowserGitAccessor : IGitBugAccessor { }'],
      [browserGitResourcePath, 'export async function listGitBugIssues() {} export async function getGitBugIssue() {}']
    ]),
    contracts: new Map([
      [contractPath, 'namespace AlmToo.Accessor.BrowserGitAccessor.Interface; public record GitOperationResult(string Operation, bool Succeeded, string Message);'],
      [gitBugInterfacePath, 'namespace AlmToo.Accessor.BrowserGitAccessor.Interface; public interface IGitBugAccessor { ValueTask ListIssuesAsync(); ValueTask GetIssueAsync(); }']
    ]),
    composition: new Map([['Program.cs', 'services.AddScoped<BrowserGitAccessor>(); services.AddScoped<IBrowserGitAccessor>(s => s.GetRequiredService<BrowserGitAccessor>()); services.AddScoped<IBrowserFileAccessor>(s => s.GetRequiredService<BrowserGitAccessor>()); services.AddScoped<IGitBugAccessor>(s => s.GetRequiredService<BrowserGitAccessor>()); services.AddScoped<RepositoryWorkspaceManager>();']]),
    design: validReview,
    gitBugDesign: '### T03 iDesign review\nClient to Manager to Accessor to Resource. IGitBugAccessor protects the external-format and platform boundary. No architectural exception is required.\n**Result:** PASS\n## Manager and Client behavior'
  };
  return { ...base, ...overrides };
}

test('implementation satisfies the bounded browser Git iDesign architecture gate', async () => {
  const failures = inspectArchitecture(await productionSnapshot());
  assert.deepEqual(failures, [], failures.map(({ rule, path, message }) => `${rule}: ${path}: ${message}`).join('\n'));
});

test('negative fixtures identify dependency and ownership violations at concrete paths', () => {
  assert.deepEqual(inspectArchitecture(fixture()), [], 'negative-fixture baseline must satisfy every gate rule');
  assertViolation(fixture({ clients: new Map([['Pages/Home.razor', 'using Git = AlmToo.Accessor.BrowserGitAccessor.Interface.IBrowserGitAccessor;']]) }), 'CLIENT_ACCESSOR_EDGE', 'Pages/Home.razor');
  assertViolation(fixture({ clients: new Map([['Pages/Issues.razor', '@inject IGitBugAccessor Issues']]) }), 'CLIENT_ACCESSOR_EDGE', 'Pages/Issues.razor');
  assertViolation(fixture({ clients: new Map([['Components/IssueList.razor', '@code { GitBugIssueQuery Query = new(); }']]) }), 'CLIENT_ACCESSOR_EDGE', 'Components/IssueList.razor');
  assertViolation(fixture({ clients: new Map([['Pages/Home.razor', 'await browserGitEngine.synchronize(request);']]) }), 'CLIENT_ACCESSOR_EDGE', 'Pages/Home.razor');
  assertViolation(fixture({ clients: new Map([['Components/RepositoryFileBrowser.razor', '@inject IJSRuntime GitRuntime']]) }), 'CLIENT_GIT_INTEROP', 'Components/RepositoryFileBrowser.razor');
  assertViolation(fixture({ managers: new Map([['Managers/Repositories/RepositoryWorkspaceManager.cs', 'sealed class RepositoryWorkspaceManager { BillingManager other; }']]) }), 'MANAGER_TO_MANAGER', 'Managers/Repositories/RepositoryWorkspaceManager.cs');
  assertViolation(fixture({ accessors: new Map([['Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs', 'sealed class BrowserGitAccessor { RepositoryWorkspaceState state; void ConfirmPush() {} }']]) }), 'ACCESSOR_WORKFLOW_OR_UI', 'Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs');
});

test('negative fixtures enforce Accessor contract co-location, passive shape, and composition', () => {
  assertViolation(fixture({ contracts: new Map([[contractPath, 'namespace AlmToo.Accessor.BrowserGitAccessor.Interface; public record GitOperationResult(string Operation) { public static GitOperationResult Success() => new("x"); }']]) }), 'CONTRACT_BEHAVIOR', contractPath);
  assertViolation(fixture({ contracts: new Map([[contractPath, 'namespace AlmToo.Accessor.BrowserGitAccessor.Service; public record GitOperationResult(string Operation);']]) }), 'CONTRACT_NAMESPACE', contractPath);
  assertViolation(fixture({ accessors: new Map([['Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs', 'public record PushRequest(string Ref);']]) }), 'CONTRACT_PATH', 'Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs');
  assertViolation(fixture({ composition: new Map([['Program.cs', 'services.AddScoped<BrowserGitAccessor>();']]) }), 'SCOPED_COMPOSITION', 'Program.cs');
  assertViolation(fixture({ accessors: new Map([
    [browserGitAccessorPath, 'public sealed class BrowserGitAccessor { }'],
    [browserGitResourcePath, 'export async function listGitBugIssues() {} export async function getGitBugIssue() {}']
  ]) }), 'GIT_BUG_FACET', browserGitAccessorPath);
  assertViolation(fixture({ accessors: new Map([
    [browserGitAccessorPath, 'public sealed class BrowserGitAccessor : IGitBugAccessor { }'],
    [browserGitResourcePath, 'export async function listGitBugIssues() {}']
  ]) }), 'GIT_BUG_RESOURCE', browserGitResourcePath);
  assertViolation(fixture({ gitBugDesign: '### T03 iDesign review\n**Result:** FAIL\n## Manager and Client behavior' }), 'GIT_BUG_REVIEW', gitBugReviewPath);
  assertViolation(fixture({ design: '### 1. Layer assignments\n**Result:** PASS' }), 'REVIEW_COVERAGE', reviewPath);
});
