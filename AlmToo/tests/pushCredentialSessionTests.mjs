import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { afterEach, test } from 'node:test';

import {
  forgetCredential,
  getCredentialForPush,
  hasCredential,
  storeCredential
} from '../wwwroot/js/pushCredentialSession.js';

const originalDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'sessionStorage');
const moduleUrl = new URL('../wwwroot/js/pushCredentialSession.js', import.meta.url);

function installStorage(initialEntries = []) {
  const values = new Map(initialEntries);
  const storage = {
    getItem(key) {
      return values.has(key) ? values.get(key) : null;
    },
    setItem(key, value) {
      values.set(key, String(value));
    },
    removeItem(key) {
      values.delete(key);
    }
  };
  Object.defineProperty(globalThis, 'sessionStorage', { configurable: true, value: storage });
  return { storage, values };
}

function installUnavailableStorage() {
  Object.defineProperty(globalThis, 'sessionStorage', {
    configurable: true,
    get() {
      throw new Error('storage unavailable');
    }
  });
}

afterEach(() => {
  if (originalDescriptor) {
    Object.defineProperty(globalThis, 'sessionStorage', originalDescriptor);
  } else {
    delete globalThis.sessionStorage;
  }
});

test('credential is retained for a same-tab reload and returned only to the push path', () => {
  installStorage();
  const credential = ['fixture', 'credential'].join('-');

  assert.equal(storeCredential(credential), true);
  assert.equal(hasCredential(), true);
  assert.equal(getCredentialForPush(), credential);
});

test('empty credentials are rejected without creating token-present state', () => {
  const { values } = installStorage();

  assert.equal(storeCredential('   '), false);
  assert.equal(hasCredential(), false);
  assert.equal(values.size, 0);
});

test('corrupt stored values are removed and never returned', () => {
  const { values } = installStorage([['almtoo.push-credential.v1', 42]]);

  assert.equal(hasCredential(), false);
  assert.equal(getCredentialForPush(), null);
  assert.equal(values.size, 0);
});

test('forget is idempotent and removes the one scoped value', () => {
  const { values } = installStorage([['almtoo.push-credential.v1', 'fixture-credential'], ['unrelated', 'keep']]);

  assert.equal(forgetCredential(), true);
  assert.equal(forgetCredential(), true);
  assert.equal(hasCredential(), false);
  assert.equal(values.get('unrelated'), 'keep');
});

test('unavailable session storage fails closed without throwing', () => {
  installUnavailableStorage();

  assert.equal(hasCredential(), false);
  assert.equal(storeCredential('fixture-credential'), false);
  assert.equal(getCredentialForPush(), null);
  assert.equal(forgetCredential(), false);
});

test('module source keeps one private key and has no logging or serialization sink', async () => {
  const source = await readFile(moduleUrl, 'utf8');
  const storageKeyDeclarations = source.match(/const credentialStorageKey/g) ?? [];

  assert.equal(storageKeyDeclarations.length, 1);
  assert.doesNotMatch(source, /console\.|JSON\.stringify|localStorage/);
  assert.doesNotMatch(source, /github_pat_|ghp_/);
  assert.match(source, /export function getCredentialForPush\(\)/);
});

test('storage operation failures do not expose values or escape the module', () => {
  const storage = {
    getItem() { throw new Error('blocked'); },
    setItem() { throw new Error('blocked'); },
    removeItem() { throw new Error('blocked'); }
  };
  Object.defineProperty(globalThis, 'sessionStorage', { configurable: true, value: storage });

  assert.equal(hasCredential(), false);
  assert.equal(storeCredential('fixture-credential'), false);
  assert.equal(getCredentialForPush(), null);
  assert.equal(forgetCredential(), false);
});
