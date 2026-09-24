const credentialStorageKey = 'almtoo.push-credential.v1';

function getSessionStorage() {
  try {
    return globalThis.sessionStorage ?? null;
  } catch {
    return null;
  }
}

export function credentialPresence() {
  const storage = getSessionStorage();
  if (!storage) {
    return -1;
  }

  try {
    const credential = storage.getItem(credentialStorageKey);
    if (typeof credential !== 'string' || credential.trim().length === 0) {
      if (credential !== null) {
        storage.removeItem(credentialStorageKey);
      }
      return 0;
    }

    return 1;
  } catch {
    return -1;
  }
}

export function hasCredential() {
  return credentialPresence() === 1;
}

export function storeCredential(credential) {
  if (typeof credential !== 'string' || credential.trim().length === 0) {
    return false;
  }

  const storage = getSessionStorage();
  if (!storage) {
    return false;
  }

  try {
    storage.setItem(credentialStorageKey, credential);
    return storage.getItem(credentialStorageKey) === credential;
  } catch {
    return false;
  }
}

// This is the module's only value-returning credential path. Call it only immediately
// before an explicit push and do not retain or render the returned value.
export function getCredentialForPush() {
  const storage = getSessionStorage();
  if (!storage) {
    return null;
  }

  try {
    const credential = storage.getItem(credentialStorageKey);
    if (typeof credential !== 'string' || credential.trim().length === 0) {
      if (credential !== null) {
        storage.removeItem(credentialStorageKey);
      }
      return null;
    }

    return credential;
  } catch {
    return null;
  }
}

export function forgetCredential() {
  const storage = getSessionStorage();
  if (!storage) {
    return false;
  }

  try {
    storage.removeItem(credentialStorageKey);
    return storage.getItem(credentialStorageKey) === null;
  } catch {
    return false;
  }
}
