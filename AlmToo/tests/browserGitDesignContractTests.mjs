import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const designUrl = new URL('../Accessors/Git/DESIGN.md', import.meta.url);
const packageUrl = new URL('../package.json', import.meta.url);

const trackedSources = [
  ['AlmToo/Pages/Home.razor', 'Client'],
  ['AlmToo/Components/RepositoryFileBrowser.razor', 'Client'],
  ['AlmToo/Components/RepositoryChangedFiles.razor', 'Client'],
  ['AlmToo/Components/RepositoryCommitForm.razor', 'Client'],
  ['AlmToo/Program.cs', 'Resource'],
  ['AlmToo/Services/Git/IBrowserGitService.cs', 'Accessor'],
  ['AlmToo/Services/Git/BrowserGitService.cs', 'Accessor'],
  ['AlmToo/Services/Git/GitOperationResult.cs', 'Resource'],
  ['AlmToo/wwwroot/js/browserGitEngine.js', 'Accessor'],
  ['AlmToo/wwwroot/js/pushCredentialSession.js', 'Accessor'],
  ['AlmToo/tools/copy-git-browser-assets.mjs', 'Resource'],
  ['AlmToo/package.json', 'Resource'],
  ['AlmToo/tests/browserGitEngineTests.mjs', 'Accessor'],
  ['AlmToo/tests/pushCredentialSessionTests.mjs', 'Accessor'],
  ['AlmToo/tests/homePageContractTests.mjs', 'Client'],
  ['AlmToo/tests/browserGitDesignContractTests.mjs', 'Resource'],
  ['AlmToo/e2e/credentialSafety.spec.mjs', 'Resource'],
  ['AlmToo/e2e/liveGitHubPush.spec.mjs', 'Resource']
];

const sourceUrls = trackedSources
  .filter(([path]) => !path.endsWith('browserGitDesignContractTests.mjs'))
  .map(([path]) => new URL(`../../${path}`, import.meta.url));

async function readDesign() {
  return readFile(designUrl, 'utf8');
}

function section(source, heading) {
  const escaped = heading.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const endOfDocument = '(?![\\s\\S])';
  return source.match(new RegExp(`^## ${escaped}[ \\t]*\\r?\\n([\\s\\S]*?)(?=^##[ \\t]|${endOfDocument})`, 'm'))?.[1] ?? '';
}

function tableValue(source, field) {
  const escaped = field.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return source.match(new RegExp(`^\\| ${escaped} \\| ([^|]+) \\|$`, 'mi'))?.[1]?.trim() ?? '';
}

test('design contract reads only explicit tracked project sources', async () => {
  const forbidden = /(?:^|[/\\])(?:\.gsd|\.planning|\.audits)(?:[/\\]|$)/;
  for (const url of [designUrl, packageUrl, ...sourceUrls]) {
    assert.doesNotMatch(url.pathname, forbidden, `design contract read target must be tracked project source: ${url.pathname}`);
  }

  await Promise.all([designUrl, packageUrl, ...sourceUrls].map(url => readFile(url)));
});

test('design contains every durable architecture section', async () => {
  const design = await readDesign();
  for (const heading of [
    'Purpose and scope',
    'Design status and approval gate',
    'Current-state inventory',
    'Current call and dependency graph',
    'Target layer map',
    'Responsibilities and dependency rules',
    'Contracts',
    'Current workflow and data flows',
    'Failure Modes',
    'Security and trust boundaries',
    'Load Profile',
    'Negative Tests',
    'Migration sequence',
    'Verification strategy',
    'Risks and extension points',
    'Embedded iDesign review'
  ]) {
    assert.ok(section(design, heading).trim().length > 40, `DESIGN.md section "${heading}" must be present and populated`);
  }
});

test('current source inventory maps every affected tracked surface to a target layer', async () => {
  const inventory = section(await readDesign(), 'Current-state inventory');

  for (const [path, expectedLayer] of trackedSources) {
    const row = inventory.split('\n').find(line => line.includes(`\`${path}\``));
    assert.ok(row, `current-state inventory must map ${path}`);
    assert.match(row, new RegExp(`\\b${expectedLayer}\\b`, 'i'), `${path} must have intended ${expectedLayer} ownership`);
  }

  assert.match(
    inventory,
    /JavaScript modules are implementation details of the \*\*Accessor boundary\*\*, not separate workflow services/i,
    'inventory must classify both JavaScript modules as private Accessor implementation details');
});

test('target map and dependency rules enforce D010 through D013', async () => {
  const design = await readDesign();
  const target = section(design, 'Target layer map');
  const rules = section(design, 'Responsibilities and dependency rules');

  assert.match(target, /`GitWorkspaceManager` \| Manager \(concrete\)/, 'target must name one concrete GitWorkspaceManager');
  assert.match(target, /`IBrowserGitAccessor` \| Accessor interface/, 'target must name the single Accessor interface');
  assert.match(target, /`BrowserGitAccessor` \| Accessor implementation/, 'target must name the renamed Accessor implementation');
  assert.match(target, /Git requests\/results\/state records and enums \| Resource/, 'target must assign Git data contracts to Resources');
  assert.match(target, /`browserGitEngine\.js` and `pushCredentialSession\.js` \| Accessor implementation detail/, 'target must keep both JavaScript modules inside the Accessor boundary');

  for (const decision of ['D010', 'D011', 'D012', 'D013']) {
    assert.match(rules, new RegExp(`\\| ${decision} \\|`), `${decision} constraint must be recorded`);
  }
  assert.match(rules, /Clients do not call the Accessor or Git-related `IJSRuntime` directly/i, 'D011 Client dependency prohibition must be explicit');
  assert.match(rules, /Retain exactly one Accessor interface/i, 'D012 single-interface constraint must be explicit');
  assert.match(rules, /Manager concrete and reject a pass-through Manager interface/i, 'D012 concrete Manager constraint must be explicit');
  assert.match(rules, /`AlmToo\/Accessors\/Git\/DESIGN\.md`[\s\S]*explicit human approval/i, 'D013 location and approval constraint must be explicit');
});

test('design preserves current workflows and result observability', async () => {
  const design = await readDesign();
  const flows = section(design, 'Current workflow and data flows');
  for (const heading of [
    'Clone or open and initialization',
    'Browse and read',
    'Edit and write',
    'Status refresh',
    'Commit',
    'Push review and confirmation',
    'Session credential acquisition, forget, and retry',
    'Cancellation, module lifetime, disposal, and result propagation'
  ]) {
    assert.match(flows, new RegExp(`^### ${heading.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`, 'm'), `workflow must document ${heading}`);
  }

  const contracts = section(design, 'Contracts');
  for (const signal of ['Operation', 'Succeeded', 'Message', 'Diagnostic', 'FailureKind']) {
    assert.match(contracts, new RegExp(`\\b${signal}\\b`), `contracts must preserve GitOperationResult.${signal}`);
  }
  assert.match(contracts, /initialization[\s\S]*idempotent/i, 'contracts must cover idempotent initialization');
  assert.match(contracts, /disposed asynchronously|disposes? successfully imported modules/i, 'contracts must cover module disposal');
});

test('failure, trust, credential, redaction, and load contracts are explicit', async () => {
  const design = await readDesign();
  const failures = section(design, 'Failure Modes');
  for (const boundary of [
    'JavaScript module or vendor asset load',
    'Browser storage/filesystem',
    'Network, CORS proxy, or remote Git',
    'Authentication or permission',
    'Non-fast-forward remote',
    'Cancellation or disposal',
    'Malformed interop payload',
    'Secret-bearing external failure'
  ]) {
    assert.match(failures, new RegExp(boundary.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i'), `failure model must cover ${boundary}`);
  }

  const security = section(design, 'Security and trust boundaries');
  for (const subject of ['Repository URL', 'repository paths', 'repository content', 'commit message/author metadata', 'session credentials', 'Browser storage', 'remote Git']) {
    assert.match(security, new RegExp(subject.replace('/', '\\/'), 'i'), `security model must classify ${subject}`);
  }
  for (const prohibition of ['persist', 'logs', 'rendered markup', 'diagnostics', 'serialized errors']) {
    assert.match(security, new RegExp(prohibition, 'i'), `credential safety must prohibit leakage through ${prohibition}`);
  }
  assert.match(security, /redacted|redaction/i, 'security model must require redacted failures');

  const load = section(design, 'Load Profile');
  for (const axis of ['browser memory', 'repository size/file count', 'interop payload', 'status/diff', 'remote latency', '10x']) {
    assert.match(load, new RegExp(axis.replace('/', '\\/'), 'i'), `load profile must cover ${axis}`);
  }
  assert.match(load, /does not invent|No supported numeric/i, 'load profile must not fabricate numeric limits');
});

test('negative, migration, and verification coverage is implementation-ready for the next design increment', async () => {
  const design = await readDesign();
  const negatives = section(design, 'Negative Tests');
  for (const scenario of [
    'Malformed/unsupported repository',
    'Unsafe/missing repository path',
    'Absent repository/worktree',
    'Invalid commit input and no changes',
    'Absent/invalid/corrupt credential',
    'Rejected/non-fast-forward push',
    'Secret-bearing/throwing/malformed external failures'
  ]) {
    assert.match(negatives, new RegExp(scenario.replaceAll('/', '\\/'), 'i'), `negative-test matrix must cover ${scenario}`);
  }

  const migration = section(design, 'Migration sequence');
  for (const checkpoint of ['Resources', 'Rename/move', 'credential-session interop', 'GitWorkspaceManager', 'DI composition', 'rewire Clients', 'Relocate/update tests', 'legacy service names']) {
    assert.match(migration, new RegExp(checkpoint.replace('/', '\\/'), 'i'), `migration sequence must include ${checkpoint}`);
  }
  assert.match(migration, /rollback/i, 'migration must require rollback-safe checkpoints');

  const verification = section(design, 'Verification strategy');
  for (const proof of ['browserGitDesignContractTests.mjs', 'browserGitEngineTests.mjs', 'pushCredentialSessionTests.mjs', 'homePageContractTests.mjs', 'npm test --prefix AlmToo', 'Blazor build', 'credentialSafety.spec.mjs', 'liveGitHubPush.spec.mjs']) {
    assert.match(verification, new RegExp(proof.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i'), `verification strategy must retain ${proof}`);
  }
});

test('embedded iDesign review is complete and has no unexplained failure', async () => {
  const review = section(await readDesign(), 'Embedded iDesign review');
  for (const heading of [
    '1. Layer assignments',
    '2. Dependency direction',
    '3. Interface justification',
    '4. Cohesion and decomposition',
    '5. Verification placement',
    '6. Exceptions',
    'Compliance statement'
  ]) {
    assert.match(review, new RegExp(`^### ${heading.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`, 'm'), `embedded iDesign review must include ${heading}`);
  }

  assert.equal((review.match(/\*\*Result:\*\* PASS/g) ?? []).length, 5, 'all five applicable iDesign review result rows must PASS');
  assert.match(review, /### 6\. Exceptions\s+\nNone\./, 'review exceptions must explicitly be None or documented');
  assert.doesNotMatch(review, /\*\*(?:Result|Overall result):\*\* FAIL\b/, 'embedded review must contain no unexplained FAIL result');
  assert.match(review, /\*\*Overall result:\*\* PASS/, 'embedded iDesign review must have a PASS overall result');
  assert.match(review, /single Accessor interface protects the JavaScript\/browser platform boundary[\s\S]*Manager stays concrete because an interface would be pass-through/i, 'D012 interface justification must name the boundary, substitution seam, and concrete Manager rationale');
});

test('approval metadata distinguishes pending review from genuine approval', async () => {
  const approval = section(await readDesign(), 'Design status and approval gate');
  const status = tableValue(approval, 'Design status');
  const authorization = tableValue(approval, 'Implementation authorization');
  const reviewer = tableValue(approval, 'Reviewer');
  const reviewDate = tableValue(approval, 'Review date');
  const sourceRevision = tableValue(approval, 'Source revision');
  const disposition = tableValue(approval, 'Disposition');

  assert.ok(status, 'approval gate must record Design status');
  assert.ok(authorization, 'approval gate must record Implementation authorization');
  assert.ok(reviewer, 'approval gate must record Reviewer');
  assert.ok(reviewDate, 'approval gate must record Review date');
  assert.ok(sourceRevision, 'approval gate must record Source revision');
  assert.ok(disposition, 'approval gate must record Disposition');

  if (/^Pending human approval$/i.test(status)) {
    assert.match(authorization, /^Blocked$/i, 'pending human approval must keep implementation blocked');
    assert.match(disposition, /^Pending human approval$/i, 'pending status must have a pending disposition');
    for (const [field, value] of [['Reviewer', reviewer], ['Review date', reviewDate], ['Source revision', sourceRevision]]) {
      assert.match(value, /^Not recorded$/i, `pending approval ${field} must honestly remain Not recorded`);
    }
  } else if (/^Approved$/i.test(status)) {
    assert.match(authorization, /^Granted$/i, 'approved design must explicitly grant implementation authorization');
    assert.match(disposition, /^Approved$/i, 'approved design must have an approved disposition');
    for (const [field, value] of [['Reviewer', reviewer], ['Review date', reviewDate], ['Source revision', sourceRevision]]) {
      assert.doesNotMatch(value, /^(?:Not recorded|Pending|TBD|N\/A)$/i, `approved design requires a real ${field}`);
    }
    assert.match(reviewDate, /^\d{4}-\d{2}-\d{2}$/, 'approved design review date must be ISO YYYY-MM-DD');
  } else {
    assert.match(status, /^Rejected$/i, 'design status must be Pending human approval, Approved, or Rejected');
    assert.match(authorization, /^Blocked$/i, 'rejected design must keep implementation blocked');
  }

  assert.match(approval, /Passing automated contract tests proves document completeness only; it never grants implementation authorization/i, 'approval gate must not treat automated success as human approval');
});

test('package test script includes the design contract without dropping behavioral suites', async () => {
  const packageJson = JSON.parse(await readFile(packageUrl, 'utf8'));
  const script = packageJson.scripts?.test ?? '';

  for (const suite of [
    'tests/browserGitEngineTests.mjs',
    'tests/pushCredentialSessionTests.mjs',
    'tests/homePageContractTests.mjs',
    'tests/browserGitDesignContractTests.mjs'
  ]) {
    assert.match(script, new RegExp(suite.replaceAll('.', '\\.')), `npm test must include ${suite}`);
  }
});
