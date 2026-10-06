import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const workflow = await readFile(new URL('../../.github/workflows/deploy-pages.yml', import.meta.url), 'utf8');

test('Pages workflow builds inside the matching .NET SDK container', () => {
  assert.match(workflow, /container:\s*\n\s*image:\s*mcr\.microsoft\.com\/dotnet\/sdk:10\.0/);
  assert.match(workflow, /runs-on:\s*ubuntu-latest/);
});

test('Pages workflow publishes the Blazor client once to the declared output', () => {
  const publishCommands = workflow.match(/^\s*run:\s*dotnet publish\b.*$/gm) ?? [];

  assert.equal(publishCommands.length, 1);
  assert.match(workflow, /working-directory:\s*AlmToo/);
  assert.match(
    workflow,
    /run:\s*dotnet publish AlmToo\.csproj --configuration Release --output publish/
  );
  assert.match(workflow, /path:\s*AlmToo\/publish\/wwwroot/);
  assert.match(workflow, /steps\.pages\.outputs\.base_path/);
  assert.match(workflow, /sed -i .*<base href=/);
});

test('Pages workflow runs for main pushes and manual dispatches', () => {
  assert.match(workflow, /on:\s*\n\s*push:\s*\n\s*branches:\s*\n\s*- main/);
  assert.match(workflow, /^\s*workflow_dispatch:\s*$/m);
});

test('Pages workflow grants only the explicit delivery permissions', () => {
  assert.match(
    workflow,
    /permissions:\s*\n\s*contents:\s*read\s*\n\s*pages:\s*write\s*\n\s*id-token:\s*write/
  );
});

test('Pages workflow bounds overlapping deployments', () => {
  assert.match(
    workflow,
    /concurrency:\s*\n\s*group:\s*pages\s*\n\s*cancel-in-progress:\s*true/
  );
});

test('Pages upload uses the official action and exact published artifact', () => {
  assert.match(
    workflow,
    /- name:\s*Upload Pages artifact\s*\n\s*uses:\s*actions\/upload-pages-artifact@v3\s*\n\s*with:\s*\n\s*path:\s*AlmToo\/publish\/wwwroot/
  );
});

test('Pages deployment waits for the build and publishes its environment URL', () => {
  assert.match(workflow, /deploy:\s*\n\s*name:\s*Deploy Pages artifact\s*\n\s*needs:\s*build/);
  assert.match(
    workflow,
    /environment:\s*\n\s*name:\s*github-pages\s*\n\s*url:\s*\$\{\{\s*steps\.deployment\.outputs\.page_url\s*\}\}/
  );
  assert.match(
    workflow,
    /- name:\s*Deploy to GitHub Pages\s*\n\s*id:\s*deployment\s*\n\s*uses:\s*actions\/deploy-pages@v4/
  );
});

test('Pages delivery does not inject repository secrets or browser Git credentials', () => {
  const secretReferences = workflow.match(/\$\{\{\s*secrets(?:\.|\[)/gi) ?? [];
  const environmentKeys = [...workflow.matchAll(/^\s+([A-Z][A-Z0-9_]*):\s*.+$/gm)]
    .map((match) => match[1]);
  const credentialEnvironmentKeys = environmentKeys.filter((key) =>
    /(?:^|_)(?:TOKEN|PAT|PASSWORD|USERNAME|CREDENTIALS?)(?:_|$)/.test(key)
  );

  assert.deepEqual(secretReferences, []);
  assert.deepEqual(credentialEnvironmentKeys, []);
  assert.doesNotMatch(workflow, /persist-credentials:\s*true/i);
  assert.doesNotMatch(workflow, /git\s+config\b.*(?:credential|extraheader)/i);
});
