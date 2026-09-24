import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const designUrl = new URL('../Accessors/Git/DESIGN.md', import.meta.url);
const packageUrl = new URL('../package.json', import.meta.url);

async function readDesign() {
  return readFile(designUrl, 'utf8');
}

function extractHeading(document, marker, heading, stoppingPrefixes) {
  const lines = document.split('\n');
  const start = lines.findIndex(line => line === `${marker} ${heading}`);
  assert.notEqual(start, -1, `design must contain "${marker} ${heading}"`);

  let end = lines.length;
  for (let index = start + 1; index < lines.length; index += 1) {
    if (stoppingPrefixes.some(prefix => lines[index].startsWith(prefix))) {
      end = index;
      break;
    }
  }

  return lines.slice(start + 1, end).join('\n');
}

function section(document, heading) {
  return extractHeading(document, '##', heading, ['## ']);
}

function subsection(document, heading) {
  return extractHeading(document, '###', heading, ['### ', '## ']);
}

test('design is scoped to the Browser Git Resource Access contract', async () => {
  const design = await readDesign();

  for (const heading of [
    'Status and approval',
    'Contract ownership',
    'Operation facets',
    'Operations',
    'DTO catalogue',
    'Contract invariants',
    'iDesign review'
  ]) {
    section(design, heading);
  }

  assert.doesNotMatch(design, /^## Repository workspace operations$/m);
  assert.doesNotMatch(design, /RepositoryWorkspaceManager|RepositoryWorkspaceState/);
  assert.doesNotMatch(design, /Home\.razor/);
  assert.match(design, /Manager workflows, Manager state, Client behavior[\s\S]*do not belong in this contract/);
  assert.ok(design.length < 25_000, 'Accessor contract should remain concise');
});

test('approval gate requires a genuine human approval record', async () => {
  const approval = section(await readDesign(), 'Status and approval');

  for (const field of ['Design status', 'Implementation authorization', 'Reviewer', 'Review date', 'Source revision', 'Disposition']) {
    assert.match(approval, new RegExp(`\\| ${field} \\|`), `approval must record ${field}`);
  }

  assert.match(approval, /Design status \| Approved/);
  assert.match(approval, /Implementation authorization \| Authorized for S02/);
  assert.match(approval, /Reviewer \| Project owner \(human reviewer\)/);
  assert.match(approval, /Review date \| \d{4}-\d{2}-\d{2}/);
  assert.match(approval, /Source revision \| Git `[0-9a-f]{40}`; reviewed design SHA-256 `[0-9a-f]{64}`/);
  assert.match(approval, /Disposition \| Approved as currently designed/);
  assert.doesNotMatch(approval, /Not recorded|Pending human approval/);
  assert.match(approval, /Automated checks cannot approve/i);
});

test('generic contracts follow the iDesign namespace nomenclature without a Context segment', async () => {
  const design = await readDesign();
  const ownership = section(design, 'Contract ownership');
  const catalogue = section(design, 'DTO catalogue');

  assert.match(ownership, /Git interface \| `AlmToo\.Accessor\.BrowserGitAccessor\.Interface\.IBrowserGitAccessor`/);
  assert.match(ownership, /File interface \| `AlmToo\.Accessor\.BrowserGitAccessor\.Interface\.IBrowserFileAccessor`/);
  assert.match(ownership, /Service \| `AlmToo\.Accessor\.BrowserGitAccessor\.Service\.BrowserGitAccessor`/);
  assert.match(ownership, /DTO namespace \| `AlmToo\.Accessor\.BrowserGitAccessor\.Interface`/);
  assert.match(ownership, /DTOs are data-transfer contracts adjacent to the Resource Access interfaces/);
  assert.match(catalogue, /generic `AlmToo\.Accessor\.BrowserGitAccessor\.Interface` namespace/);
  assert.match(catalogue, /No Context segment is present because the application currently has only the generic UI context/);

  assert.doesNotMatch(design, /AlmToo\.Accessors(?:\.|`)/);
  assert.doesNotMatch(design, /AlmToo\.Resources(?:\.|`)/);
  assert.doesNotMatch(design, /Resources\/Git|Resources\.Git/);
  assert.doesNotMatch(design, /^## Resource catalogue$/m);
  assert.match(design, /do not constitute a separate Resource layer or namespace/i);
});

test('operation facets separate Git and file caller capabilities on one Service', async () => {
  const facets = section(await readDesign(), 'Operation facets');

  for (const facet of [
    'Repository workspace',
    'Working tree',
    'Reviewed push',
    'Credential storage',
    'File query',
    'File update',
    'Lifetime'
  ]) {
    assert.match(facets, new RegExp(`\\| ${facet} \\|`), `facet ${facet} must be described`);
  }

  assert.match(facets, /`IBrowserGitAccessor`/);
  assert.match(facets, /`IBrowserFileAccessor`/);
  assert.match(facets, /Service \| Lifetime/);
});

test('Accessor operations hide initialization and state text-only file semantics', async () => {
  const operations = section(await readDesign(), 'Operations');

  for (const operation of [
    'CloneOrOpenAsync',
    'FilterFilesAsync',
    'UpdateFilesAsync',
    'GetStatusAsync',
    'CommitAsync',
    'InspectPushAsync',
    'HasCredentialAsync',
    'StoreCredentialAsync',
    'ForgetCredentialAsync',
    'PushAsync',
    'DisposeAsync'
  ]) {
    assert.match(operations, new RegExp('\\| `' + operation + '`'), `Accessor contract must describe ${operation}`);
  }

  assert.match(operations, /`initialize` is not a public operation/);
  assert.match(operations, /privately and idempotently imports[\s\S]*initializes browser storage/i);
  assert.match(operations, /invalid[\s\S]*oversized content fails/i);
  assert.match(operations, /force-with-lease/i);
});

test('interface signatures match the separated Git and file operation surfaces', async () => {
  const operations = section(await readDesign(), 'Operations');
  const signatureBlock = operations.match(/```csharp([\s\S]*?)```/)?.[1];
  assert.ok(signatureBlock, 'Accessor contract must include a C# signature block');

  assert.match(signatureBlock, /namespace AlmToo\.Accessor\.BrowserGitAccessor\.Interface/);
  assert.match(signatureBlock, /public interface IBrowserGitAccessor : IAsyncDisposable/);
  assert.match(signatureBlock, /public interface IBrowserFileAccessor/);
  assert.match(signatureBlock, /FilterFilesAsync\([\s\S]*FilterFilesRequest request/);
  assert.match(signatureBlock, /UpdateFilesAsync\([\s\S]*UpdateFilesRequest request/);
  assert.equal((signatureBlock.match(/ValueTask</g) ?? []).length, 10);
  assert.equal((signatureBlock.match(/ValueTask<GitOperationResult>/g) ?? []).length, 0);
  assert.doesNotMatch(signatureBlock, /ListFilesAsync|ReadTextFileAsync|WriteTextFileAsync/);
  assert.doesNotMatch(signatureBlock, /InitializeAsync/);
  assert.doesNotMatch(signatureBlock, /personalAccessToken|password/i);
});

test('DTO catalogue documents every Accessor boundary type', async () => {
  const catalogue = section(await readDesign(), 'DTO catalogue');

  for (const dto of [
    'GitOperationResult',
    'GitOperationFailureKind',
    'RepositoryOpenRequest',
    'RepositoryInfo',
    'FilterFilesRequest',
    'FileFilterKind',
    'FilterFilesResult',
    'UpdateFilesRequest',
    'TextFileUpdate',
    'UpdateFilesResult',
    'RepositoryFileEntry',
    'TextFileContent',
    'ChangedFile',
    'CommitRequest',
    'CommitInfo',
    'PushReview',
    'PushRequest',
    'PushResult'
  ]) {
    assert.match(catalogue, new RegExp('^#{3,4} `' + dto + '(?:<T>)?`', 'm'), `DTO catalogue must describe ${dto}`);
  }

  for (const enumValue of ['CredentialRejected', 'RemoteAhead', 'NetworkUnavailable', 'UnsupportedRef', 'Unknown']) {
    assert.match(catalogue, new RegExp('\\| `' + enumValue + '` \\|'));
  }
});

test('DTO semantics prevent secret and binary-content ambiguity', async () => {
  const catalogue = section(await readDesign(), 'DTO catalogue');

  assert.match(catalogue, /`Message`[\s\S]*safe to render/i);
  assert.match(catalogue, /`Diagnostic`[\s\S]*must never render it/i);
  assert.match(catalogue, /`Value` is meaningful only when `Succeeded` is true/i);
  assert.match(catalogue, /Invalid UTF-8 fails rather than silently replacing bytes/i);
  assert.match(catalogue, /General binary access requires separate byte-oriented operations and DTOs/i);
});

test('invariants cover lifecycle, cancellation, paths, push, and credentials', async () => {
  const invariants = section(await readDesign(), 'Contract invariants');

  for (const heading of [
    'Initialization and lifetime',
    'Cancellation',
    'Paths and editable text',
    'Push coordinates',
    'Credentials and safe failures'
  ]) {
    subsection(invariants, heading);
  }

  assert.match(invariants, /Initialization is private, lazy, idempotent/i);
  assert.match(invariants, /Cancellation is forwarded/i);
  assert.match(invariants, /`\.\.`[\s\S]*encoded traversal[\s\S]*rejected/i);
  assert.match(invariants, /force-with-lease/i);
  assert.match(invariants, /Credentials never enter DTOs, instance fields, rendered markup, URLs, logs, diagnostics, exception text/i);
  assert.match(invariants, /Failed pushes are never replayed automatically/i);
});

test('embedded iDesign review has no unexplained failure', async () => {
  const review = section(await readDesign(), 'iDesign review');

  for (const check of ['Layer assignment', 'Dependency direction', 'Interface justification', 'Cohesion', 'Failure ownership']) {
    assert.match(review, new RegExp(`\\| ${check} \\| PASS \\|`), `${check} must pass`);
  }

  assert.match(review, /DTOs are adjacent contract data, not a separate service layer/);
  assert.match(review, /Managers or Engines may call the Accessor/);
  assert.match(review, /Exceptions \| None/);
  assert.doesNotMatch(review, /\| FAIL \|/);
});

test('package test script includes the design contract and behavioral suites', async () => {
  const packageJson = JSON.parse(await readFile(packageUrl, 'utf8'));
  const testScript = packageJson.scripts?.test ?? '';

  for (const suite of [
    'browserGitEngineTests.mjs',
    'pushCredentialSessionTests.mjs',
    'homePageContractTests.mjs',
    'browserGitDesignContractTests.mjs'
  ]) {
    assert.match(testScript, new RegExp(suite.replaceAll('.', '\\.')), `npm test must include ${suite}`);
  }
});
