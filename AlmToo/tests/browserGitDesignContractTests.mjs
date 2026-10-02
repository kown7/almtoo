import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const designUrl = new URL('../Accessor/BrowserGitAccessor/DESIGN.md', import.meta.url);
const packageUrl = new URL('../package.json', import.meta.url);
const designPath = 'Accessor/BrowserGitAccessor/DESIGN.md';

async function readDesign() {
  return readFile(designUrl, 'utf8');
}

function headings(document) {
  return [...document.matchAll(/^(#{2,4})\s+(.+)$/gm)].map(match => ({
    level: match[1].length,
    title: match[2].trim(),
    index: match.index,
    contentStart: match.index + match[0].length
  }));
}

function headingBody(document, title) {
  const found = headings(document);
  const currentIndex = found.findIndex(heading => heading.title === title);
  if (currentIndex < 0) return null;

  const current = found[currentIndex];
  const next = found.slice(currentIndex + 1).find(heading => heading.level <= current.level);
  return document.slice(current.contentStart, next?.index ?? document.length).trim();
}

function violation(rule, message) {
  return { rule, path: designPath, message };
}

function requireHeading(failures, document, title, minimumLength = 120) {
  const body = headingBody(document, title);
  if (body === null) {
    failures.push(violation('DESIGN_TOPIC_MISSING', `missing heading: ${title}`));
  } else if (body.length < minimumLength) {
    failures.push(violation('DESIGN_TOPIC_THIN', `${title} must contain meaningful architecture content`));
  }
  return body ?? '';
}

export function inspectDesign(document) {
  const failures = [];

  for (const title of [
    'Status and scope',
    'Architecture overview',
    'Dependency rules',
    'Contract ownership',
    'Composition and lifetime',
    'Accessor operation contract',
    'Accessor contract catalogue',
    'End-to-end workflows',
    'Failure taxonomy and diagnostics',
    'Contract invariants',
    'Migration sequence and implementation status',
    'Testing and observability strategy',
    'Load and performance profile',
    'Known risks',
    'Extension points',
    'iDesign review',
    'Compliance statement'
  ]) {
    requireHeading(failures, document, title);
  }

  for (const workflow of [
    'Clone or open',
    'Browse',
    'Edit and save',
    'Status',
    'Commit',
    'Synchronization review and apply',
    'Reviewed push',
    'Credential recovery and forget',
    'Cancellation and concurrency'
  ]) {
    requireHeading(failures, document, workflow, 100);
  }

  const currentTopology = requireHeading(failures, document, 'Current-state topology', 300);
  const targetTopology = requireHeading(failures, document, 'Target topology', 400);
  const dependencies = headingBody(document, 'Dependency rules') ?? '';
  const ownership = headingBody(document, 'Contract ownership') ?? '';
  const composition = headingBody(document, 'Composition and lifetime') ?? '';
  const operations = headingBody(document, 'Accessor operation contract') ?? '';
  const contract = headingBody(document, 'Accessor contract catalogue') ?? '';
  const failuresSection = headingBody(document, 'Failure taxonomy and diagnostics') ?? '';
  const migration = headingBody(document, 'Migration sequence and implementation status') ?? '';
  const testing = headingBody(document, 'Testing and observability strategy') ?? '';
  const load = headingBody(document, 'Load and performance profile') ?? '';
  const risks = headingBody(document, 'Known risks') ?? '';
  const extensions = headingBody(document, 'Extension points') ?? '';
  const review = headingBody(document, 'iDesign review') ?? '';

  const requiredContent = [
    [currentTopology, /Client[\s\S]*Manager[\s\S]*Accessor[\s\S]*(?:JavaScript|browser)/i, 'current layer and platform call path'],
    [targetTopology, /Accessor\/BrowserGitAccessor\/Interface\/BrowserGitContracts\.cs/, 'Accessor contract canonical path'],
    [targetTopology, /AlmToo\.Accessor\.BrowserGitAccessor\.Interface/, 'Accessor contract canonical namespace'],
    [targetTopology, /RepositoryWorkspaceState[\s\S]*(?:does \*\*not\*\* move|does not move)[\s\S]*Manager-owned/i, 'Manager-state and Accessor-contract distinction'],
    [dependencies, /### Allowed edges[\s\S]*### Forbidden edges/, 'allowed and forbidden dependency edges'],
    [dependencies, /Client -> Manager -> Accessor -> platform/, 'one-way runtime call direction'],
    [ownership, /`IBrowserGitAccessor` is retained because it protects[^.]*platform boundary[^.]*substitution seam[^.]*\./i, 'IBrowserGitAccessor platform-boundary justification'],
    [ownership, /`IBrowserFileAccessor` is retained because it protects[^.]*platform boundary[^.]*substitution seam[^.]*\./i, 'IBrowserFileAccessor platform-boundary justification'],
    [ownership, /RepositoryWorkspaceManager`?\s+needs no interface[\s\S]*no (?:external or )?platform boundary/i, 'concrete Manager interface decision'],
    [composition, /scoped[\s\S]*same concrete scoped instance[\s\S]*dispose/i, 'shared scoped composition and disposal'],
    [operations, /CloneOrOpenAsync[\s\S]*FilterFilesAsync[\s\S]*UpdateFilesAsync[\s\S]*GetStatusAsync[\s\S]*CommitAsync[\s\S]*InspectPushAsync[\s\S]*HasCredentialAsync[\s\S]*StoreCredentialAsync[\s\S]*ForgetCredentialAsync[\s\S]*PushAsync[\s\S]*SynchronizeAsync[\s\S]*DisposeAsync/, 'complete Accessor operation surface'],
    [contract, /records and enums[\s\S]*no methods or executable bodies/i, 'passive Resource constraint'],
    [contract, /No credential field exists/i, 'credential-free Accessor contract'],
    [failuresSection, /JavaScript module import[\s\S]*Browser filesystem[\s\S]*GitHub clone[\s\S]*GitHub push[\s\S]*sessionStorage[\s\S]*Cancellation/i, 'external dependency failure paths'],
    [failuresSection, /Node architecture tests report rule and source path[\s\S]*Playwright retains failure-only trace\/screenshots/i, 'failure localization surfaces'],
    [migration, /Implemented by S04 T02[\s\S]*autonomous browser re-proof remains S04 T03/, 'truthful staged implementation status'],
    [migration, /source ownership change[\s\S]*preserved record shapes[\s\S]*source-path architecture rules detect stale namespace consumers/i, 'completed Resource migration contract'],
    [testing, /browserGitDesignContractTests\.mjs[\s\S]*browserGitArchitectureGateTests\.mjs[\s\S]*RepositoryWorkspaceManagerTests\.cs[\s\S]*Playwright/i, 'layered verification strategy'],
    [load, /10x[\s\S]*SemaphoreSlim[\s\S]*fail fast/i, '10x breakpoint and protection'],
    [risks, /storage quotas[\s\S]*CORS[\s\S]*Large repositories[\s\S]*credential leak/i, 'known operational and security risks'],
    [extensions, /binary file[\s\S]*pagination[\s\S]*credential stores[\s\S]*remote hosts/i, 'bounded extension points'],
    [operations, /SynchronizationRequest[\s\S]*SynchronizationReview[\s\S]*fast-forward-only/i, 'synchronization contract and safety boundary'],
    [review, /This implementation-state review copies every section of `docs\/IDESIGN-REVIEW\.md`/, 'implementation-state review provenance']
  ];

  for (const [body, pattern, description] of requiredContent) {
    if (!pattern.test(body)) failures.push(violation('DESIGN_CONTENT_MISSING', `missing ${description}`));
  }

  const contractIsCoLocated = /Accessor\/BrowserGitAccessor\/Interface\/BrowserGitContracts\.cs/.test(targetTopology)
    && /AlmToo\.Accessor\.BrowserGitAccessor\.Interface/.test(targetTopology);
  if (!contractIsCoLocated) {
    failures.push(violation('CONTRACT_COLOCATION', 'shared Browser Git data contracts must be co-located with the Accessor interfaces'));
  }

  if (/Resource\/BrowserGitResource\/Data\/BrowserGitContracts\.cs|AlmToo\.Resource\.BrowserGitResource\.Data/.test(targetTopology)) {
    failures.push(violation('STALE_RESOURCE_OWNERSHIP', 'target must not retain the former Resource DTO ownership path or namespace'));
  }

  const reviewHeadings = [
    '1. Layer assignments',
    '2. Dependency direction',
    '3. Interface justification',
    '4. Cohesion and decomposition',
    '5. Verification placement',
    '6. Exceptions'
  ];
  for (const title of reviewHeadings) {
    const body = headingBody(document, title);
    if (body === null) {
      failures.push(violation('IDESIGN_REVIEW_MISSING', `missing iDesign review section: ${title}`));
    } else if (!/\*\*Result:\*\*\s+(?:PASS|NOT APPLICABLE)\b/.test(body)) {
      failures.push(violation('IDESIGN_REVIEW_RESULT', `${title} must record PASS or NOT APPLICABLE`));
    }
  }
  if (/\*\*(?:Result:|Overall result:)\*\*\s+FAIL\b/.test(review)) {
    failures.push(violation('IDESIGN_REVIEW_FAILURE', 'implementation-state review contains an unexplained failure'));
  }
  if (!/\*\*Overall result:\*\*\s+PASS\b/.test(document)) {
    failures.push(violation('IDESIGN_REVIEW_RESULT', 'missing overall passing iDesign result'));
  }

  return failures;
}

function assertRule(failures, rule) {
  assert.ok(
    failures.some(failure => failure.rule === rule && failure.path === designPath),
    `expected ${rule} at ${designPath}; received ${JSON.stringify(failures)}`
  );
}

test('design is a complete current and target Browser Git architecture baseline', async () => {
  const failures = inspectDesign(await readDesign());
  assert.deepEqual(failures, [], failures.map(({ rule, path, message }) => `${rule}: ${path}: ${message}`).join('\n'));
});

test('negative fixtures reject missing architecture topics and review results', async () => {
  const design = await readDesign();
  const missingFailureTaxonomy = design.replace(
    /^## Failure taxonomy and diagnostics[\s\S]*?(?=^## Contract invariants)/m,
    ''
  );
  assertRule(inspectDesign(missingFailureTaxonomy), 'DESIGN_TOPIC_MISSING');

  const missingReviewResult = design.replace(
    /(### 4\. Cohesion and decomposition[\s\S]*?)\*\*Result:\*\* PASS/,
    '$1Result intentionally omitted'
  );
  assertRule(inspectDesign(missingReviewResult), 'IDESIGN_REVIEW_RESULT');
});

test('negative fixtures reject the former Resource DTO ownership', async () => {
  const design = await readDesign();
  const oldResourceTarget = design
    .replaceAll('Accessor/BrowserGitAccessor/Interface/BrowserGitContracts.cs', 'Resource/BrowserGitResource/Data/BrowserGitContracts.cs')
    .replaceAll('AlmToo.Accessor.BrowserGitAccessor.Interface', 'AlmToo.Resource.BrowserGitResource.Data');
  assertRule(inspectDesign(oldResourceTarget), 'STALE_RESOURCE_OWNERSHIP');
});

test('negative fixtures require a platform-boundary justification for both retained interfaces', async () => {
  const design = await readDesign();
  const unjustified = design.replace(
    /`IBrowserFileAccessor` is retained because[^.]+\./,
    '`IBrowserFileAccessor` is retained for dependency injection and mocking convenience.'
  );
  assertRule(inspectDesign(unjustified), 'DESIGN_CONTENT_MISSING');
});

test('the document is truthful about Accessor contract co-location and remaining browser re-proof', async () => {
  const design = await readDesign();
  const status = headingBody(design, 'Status and scope');
  const current = headingBody(design, 'Current-state topology');
  const migration = headingBody(design, 'Migration sequence and implementation status');

  assert.match(status, /DTO contract co-location[\s\S]*Implemented beside the Accessor interfaces/i);
  assert.match(current, /current source keeps[\s\S]*Accessor\/BrowserGitAccessor\/Interface\/BrowserGitContracts\.cs[\s\S]*AlmToo\.Accessor\.BrowserGitAccessor\.Interface/i);
  assert.match(migration, /Import Accessor contract records[\s\S]*Implemented by S04 T02/);
  assert.match(migration, /autonomous browser re-proof remains S04 T03/);
});

test('package test script includes design, architecture, Accessor, engine, credential, and Client contracts', async () => {
  const packageJson = JSON.parse(await readFile(packageUrl, 'utf8'));
  const testScript = packageJson.scripts?.test ?? '';

  for (const suite of [
    'browserGitEngineTests.mjs',
    'pushCredentialSessionTests.mjs',
    'homePageContractTests.mjs',
    'browserGitDesignContractTests.mjs',
    'browserGitAccessorContractTests.mjs',
    'browserGitArchitectureGateTests.mjs'
  ]) {
    assert.match(testScript, new RegExp(suite.replaceAll('.', '\\.')), `npm test must include ${suite}`);
  }
});
