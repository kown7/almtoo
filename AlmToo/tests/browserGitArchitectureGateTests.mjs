import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const reviewPath = 'Accessors/Git/DESIGN.md';
const excludedDirectoryNames = new Set(['bin', 'obj', 'node_modules', 'test-results', 'playwright-report', '.git', '.gsd']);

async function collectFiles(relativeDirectory, include) {
  const directory = new URL(`${relativeDirectory}/`, root);
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];

  for (const entry of entries) {
    const relativePath = `${relativeDirectory}/${entry.name}`;
    if (entry.isDirectory() && !excludedDirectoryNames.has(entry.name)) {
      files.push(...await collectFiles(relativePath, include));
    } else if (entry.isFile() && include(relativePath)) {
      files.push(relativePath);
    }
  }

  return files;
}

async function readSources(paths) {
  return new Map(await Promise.all(paths.map(async path => [path, await readFile(new URL(path, root), 'utf8')])));
}

async function productionSnapshot() {
  const clientPaths = [
    'Pages/Home.razor',
    ...await collectFiles('Components', path => /\/Repository[^/]*\.razor(?:\.cs)?$/.test(path))
  ];
  const managerPaths = await collectFiles('Managers/Repositories', path => path.endsWith('.cs'));
  const accessorPaths = await collectFiles('Accessor/BrowserGitAccessor', path => path.endsWith('.cs'));
  const javascriptPaths = ['wwwroot/js/browserGitEngine.js', 'wwwroot/js/pushCredentialSession.js'];

  return {
    clients: await readSources(clientPaths),
    managers: await readSources(managerPaths),
    accessors: await readSources([...accessorPaths, ...javascriptPaths]),
    composition: await readSources(['Program.cs']),
    design: await readFile(new URL(reviewPath, root), 'utf8')
  };
}

function violation(rule, path, message) {
  return { rule, path, message };
}

function matchingSources(sources, pattern, rule, message) {
  const failures = [];
  for (const [path, source] of sources) {
    if (pattern.test(source)) failures.push(violation(rule, path, message));
  }
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
  const production = new Map([
    ...snapshot.clients,
    ...snapshot.managers,
    ...snapshot.accessors,
    ...snapshot.composition
  ]);

  failures.push(...matchingSources(
    snapshot.clients,
    /\b(?:IBrowserGitAccessor|IBrowserFileAccessor|BrowserGitAccessor|IVersionControlAccessor|BrowserGitService|IBrowserGitService|GitService)\b|AlmToo\.(?:Accessor|Accessors|Services\.Git)\b/,
    'CLIENT_ACCESSOR_EDGE',
    'repository Client must depend on the concrete Manager, never an Accessor type or namespace'
  ));
  failures.push(...matchingSources(
    snapshot.clients,
    /\b(?:IJSRuntime|IJSObjectReference|JSImport)\b|\bInvokeAsync\s*</,
    'CLIENT_GIT_INTEROP',
    'repository Client must not own Git-related JavaScript interop'
  ));

  for (const [path, source] of snapshot.managers) {
    const declaredManagers = new Set([...source.matchAll(/\b(?:class|record)\s+([A-Z]\w*Manager)\b/g)].map(match => match[1]));
    const referencedManagers = new Set([...source.matchAll(/\b([A-Z]\w*Manager)\b/g)].map(match => match[1]));
    for (const referenced of referencedManagers) {
      if (!declaredManagers.has(referenced)) {
        failures.push(violation('MANAGER_TO_MANAGER', path, `Manager must not depend on another Manager (${referenced})`));
      }
    }
    if (/\bIJSRuntime\b|Microsoft\.JSInterop/.test(source)) {
      failures.push(violation('MANAGER_PLATFORM_INTEROP', path, 'Manager must reach browser Git only through Accessor contracts'));
    }
  }

  failures.push(...matchingSources(
    snapshot.accessors,
    /AlmToo\.(?:Managers|Pages|Components)\b|\b(?:NavigationManager|RenderFragment|EventCallback|RepositoryWorkspaceState|StateChanged)\b/,
    'ACCESSOR_WORKFLOW_OR_UI',
    'Accessor must not own Manager workflow or Client presentation state'
  ));

  failures.push(...matchingSources(
    production,
    /\b(?:IVersionControlAccessor|VersionControlAccessor|IBrowserGitService|BrowserGitService)\b|AlmToo\.(?:Accessors\.Git|Services\.Git)\b/,
    'LEGACY_GIT_ARCHITECTURE',
    'legacy Browser Git service name or namespace is forbidden'
  ));

  failures.push(...matchingSources(
    production,
    /(?:namespace|using)\s+AlmToo\.Resources(?:\.|;)|AlmToo\.Resources\.Git\b/,
    'DTO_RESOURCE_DRIFT',
    'Browser Git DTOs are Accessor contracts and must not move to a Resource namespace'
  ));

  const contracts = snapshot.accessors.get('Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs') ?? '';
  if (!/namespace\s+AlmToo\.Accessor\.BrowserGitAccessor\.Interface\s*;/.test(contracts)) {
    failures.push(violation('DTO_ACCESSOR_OWNERSHIP', 'Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs', 'DTO contracts must remain in the Accessor interface namespace'));
  }

  const program = snapshot.composition.get('Program.cs') ?? '';
  const registrations = [
    [/AddScoped<BrowserGitAccessor>\s*\(\s*\)/, 'concrete scoped BrowserGitAccessor registration'],
    [/AddScoped<IBrowserGitAccessor>\s*\(\s*\w+\s*=>\s*\w+\.GetRequiredService<BrowserGitAccessor>\s*\(\s*\)\s*\)/, 'IBrowserGitAccessor factory resolving the shared concrete instance'],
    [/AddScoped<IBrowserFileAccessor>\s*\(\s*\w+\s*=>\s*\w+\.GetRequiredService<BrowserGitAccessor>\s*\(\s*\)\s*\)/, 'IBrowserFileAccessor factory resolving the shared concrete instance'],
    [/AddScoped<RepositoryWorkspaceManager>\s*\(\s*\)/, 'concrete scoped RepositoryWorkspaceManager registration']
  ];
  for (const [pattern, expectation] of registrations) {
    if (!pattern.test(program)) failures.push(violation('SCOPED_COMPOSITION', 'Program.cs', `missing ${expectation}`));
  }
  if (/AddScoped<IRepositoryWorkspaceManager>/.test(program)) {
    failures.push(violation('MANAGER_INTERFACE', 'Program.cs', 'the cohesive concrete Manager must remain interface-free'));
  }

  const reviewHeadings = [
    '### 1. Layer assignments',
    '### 2. Dependency direction',
    '### 3. Interface justification',
    '### 4. Cohesion and decomposition',
    '### 5. Verification placement',
    '### 6. Exceptions',
    '## Compliance statement'
  ];
  for (let index = 0; index < reviewHeadings.length - 1; index += 1) {
    const heading = reviewHeadings[index];
    const section = reviewSection(snapshot.design, heading, reviewHeadings[index + 1]);
    if (section === null) {
      failures.push(violation('REVIEW_COVERAGE', reviewPath, `missing checklist section: ${heading}`));
    } else if (!/\*\*Result:\*\*\s+(?:PASS|NOT APPLICABLE)\b/.test(section)) {
      failures.push(violation('REVIEW_RESULT', reviewPath, `${heading} must record PASS or NOT APPLICABLE with rationale`));
    }
  }

  const layerReview = reviewSection(snapshot.design, reviewHeadings[0], reviewHeadings[1]) ?? '';
  const interfaceReview = reviewSection(snapshot.design, reviewHeadings[2], reviewHeadings[3]) ?? '';
  const requiredReviewStructure = [
    [layerReview, /Pages\/Home\.razor[\s\S]*\|\s*Client\s*\|/, 'Home Client layer mapping'],
    [layerReview, /RepositoryWorkspaceManager[\s\S]*\|\s*Manager\s*\|/, 'repository Manager layer mapping'],
    [layerReview, /IBrowserGitAccessor[\s\S]*\|\s*Accessor\s*\|/, 'Browser Git Accessor layer mapping'],
    [layerReview, /BrowserGitContracts\.cs[\s\S]*\|\s*Accessor contract data\s*\|/, 'DTO Accessor-contract mapping'],
    [interfaceReview, /IBrowserGitAccessor[^\n]*platform boundary[^\n]*substitution seam/i, 'IBrowserGitAccessor boundary and substitution rationale'],
    [interfaceReview, /IBrowserFileAccessor[^\n]*platform boundary[^\n]*substitution seam/i, 'IBrowserFileAccessor boundary and substitution rationale'],
    [interfaceReview, /RepositoryWorkspaceManager[^\n]*interface-free/i, 'interface-free concrete Manager rationale'],
    [snapshot.design, /\*\*Overall result:\*\*\s+PASS\b/, 'overall passing result']
  ];
  for (const [document, pattern, description] of requiredReviewStructure) {
    if (!pattern.test(document)) failures.push(violation('REVIEW_DIVERGENCE', reviewPath, `missing ${description}`));
  }
  if (/\*\*Result:\*\*\s+FAIL\b|\*\*Overall result:\*\*\s+FAIL\b/.test(snapshot.design)) {
    failures.push(violation('REVIEW_FAILURE', reviewPath, 'implementation-state review contains an unexplained failure'));
  }

  return failures;
}

function assertViolation(snapshot, expectedRule, expectedPath) {
  const failures = inspectArchitecture(snapshot);
  assert.ok(
    failures.some(failure => failure.rule === expectedRule && failure.path === expectedPath),
    `expected ${expectedRule} at ${expectedPath}; received ${JSON.stringify(failures)}`
  );
}

function fixture(overrides = {}) {
  const validReview = [
    '### 1. Layer assignments\n| Pages/Home.razor | Client | Render | Manager | Accessor |\n| RepositoryWorkspaceManager | Manager | Coordinate | Accessor | Manager |\n| IBrowserGitAccessor | Accessor | Integrate | Platform | Client |\n| BrowserGitContracts.cs | Accessor contract data | Transfer | Data | Behavior |\n**Result:** PASS',
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
      ['Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs', 'namespace AlmToo.Accessor.BrowserGitAccessor.Interface;'],
      ['Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs', 'public sealed class BrowserGitAccessor { }']
    ]),
    composition: new Map([['Program.cs', 'services.AddScoped<BrowserGitAccessor>(); services.AddScoped<IBrowserGitAccessor>(s => s.GetRequiredService<BrowserGitAccessor>()); services.AddScoped<IBrowserFileAccessor>(s => s.GetRequiredService<BrowserGitAccessor>()); services.AddScoped<RepositoryWorkspaceManager>();']]),
    design: validReview
  };
  return { ...base, ...overrides };
}

test('implementation satisfies the bounded browser Git iDesign architecture gate', async () => {
  const failures = inspectArchitecture(await productionSnapshot());
  assert.deepEqual(failures, [], failures.map(({ rule, path, message }) => `${rule}: ${path}: ${message}`).join('\n'));
});

test('negative fixtures identify forbidden rules and concrete source paths', () => {
  assert.deepEqual(inspectArchitecture(fixture()), [], 'negative-fixture baseline must satisfy every gate rule');
  assertViolation(fixture({ clients: new Map([['Pages/Home.razor', 'using Git = AlmToo.Accessor.BrowserGitAccessor.Interface.IBrowserGitAccessor;']]) }), 'CLIENT_ACCESSOR_EDGE', 'Pages/Home.razor');
  assertViolation(fixture({ clients: new Map([['Components/RepositoryFileBrowser.razor', '@inject IJSRuntime GitRuntime']]) }), 'CLIENT_GIT_INTEROP', 'Components/RepositoryFileBrowser.razor');
  assertViolation(fixture({ managers: new Map([['Managers/Repositories/RepositoryWorkspaceManager.cs', 'sealed class RepositoryWorkspaceManager { BillingManager other; }']]) }), 'MANAGER_TO_MANAGER', 'Managers/Repositories/RepositoryWorkspaceManager.cs');
  assertViolation(fixture({ accessors: new Map([
    ['Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs', 'namespace AlmToo.Accessor.BrowserGitAccessor.Interface;'],
    ['Accessor/BrowserGitAccessor/Service/BrowserGitService.cs', 'sealed class BrowserGitService { }']
  ]) }), 'LEGACY_GIT_ARCHITECTURE', 'Accessor/BrowserGitAccessor/Service/BrowserGitService.cs');
  assertViolation(fixture({ accessors: new Map([
    ['Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs', 'namespace AlmToo.Resources.Git;'],
    ['Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs', 'sealed class BrowserGitAccessor { }']
  ]) }), 'DTO_RESOURCE_DRIFT', 'Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs');
});

test('negative fixtures protect Accessor ownership composition and review coverage', () => {
  assertViolation(fixture({ accessors: new Map([
    ['Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs', 'namespace AlmToo.Accessor.BrowserGitAccessor.Interface;'],
    ['Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs', 'using AlmToo.Managers.Repositories; sealed class BrowserGitAccessor { RepositoryWorkspaceState state; }']
  ]) }), 'ACCESSOR_WORKFLOW_OR_UI', 'Accessor/BrowserGitAccessor/Service/BrowserGitAccessor.cs');
  assertViolation(fixture({ composition: new Map([['Program.cs', 'services.AddScoped<BrowserGitAccessor>();']]) }), 'SCOPED_COMPOSITION', 'Program.cs');
  assertViolation(fixture({ design: '### 1. Layer assignments\n**Result:** PASS' }), 'REVIEW_COVERAGE', reviewPath);
});
