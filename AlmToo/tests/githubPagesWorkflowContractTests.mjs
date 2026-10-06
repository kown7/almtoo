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

test('Pages workflow supports automatic and manual deployment', () => {
  assert.match(workflow, /push:\s*\n\s*branches:\s*\n\s*- main/);
  assert.match(workflow, /workflow_dispatch:/);
  assert.match(workflow, /contents:\s*read/);
  assert.match(workflow, /pages:\s*write/);
  assert.match(workflow, /id-token:\s*write/);
});

test('Pages deployment uses the official action and waits for the build', () => {
  assert.match(workflow, /deploy:\s*\n\s*name:\s*Deploy Pages artifact\s*\n\s*needs:\s*build/);
  assert.match(workflow, /uses:\s*actions\/upload-pages-artifact@v3/);
  assert.match(workflow, /uses:\s*actions\/deploy-pages@v4/);
});
