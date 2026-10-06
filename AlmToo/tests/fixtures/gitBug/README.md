# Versioned Git-Bug fixture

This directory contains immutable, read-only repository evidence for AlmToo's browser-local Git-Bug discovery work. It is test infrastructure only and introduces no production browser, Accessor, Manager, or Client behavior.

## Supported fixture

| Field | Value |
| --- | --- |
| Fixture directory | `v0.11.0/` |
| Git-Bug release | `v0.11.0` (`d1819bd1dbd6`, Go 1.27.1) |
| Release date | 2026-09-22 |
| Release provenance | <https://github.com/git-bug/git-bug/releases/tag/v0.11.0> |
| Linux amd64 binary | <https://github.com/git-bug/git-bug/releases/download/v0.11.0/git-bug_linux_amd64> |
| Binary SHA-256 | `431199b5997ec8e6de9fa7e2caab13fe222268b0110d0c85a4354907197e7ce9` |
| Published checksum file | <https://github.com/git-bug/git-bug/releases/download/v0.11.0/checksums.txt> |
| Checksum-file SHA-256 | `2a5f2c1c5b2924e70238f9eb8c47dbb3d13b34c850b2fc009178ec32bd7a6123` |

The release, artifact URLs, release commit, published timestamps, and hashes are duplicated in `v0.11.0/fixture-manifest.json` so validation does not depend on the network.

## Scenarios

The disposable repository was initialized with one deterministic worktree commit and populated through the pinned Git-Bug CLI's non-interactive commands. It contains exactly:

1. an open issue with two labels, a description, a follow-up comment, and Unicode across title/body/comment;
2. an open minimal issue whose labels are `null`, whose login is empty, and which has no follow-up comment;
3. a closed issue with two labels, a description, and a follow-up comment.

`v0.11.0/expected/` records the pinned CLI's observed list and detail JSON. These files are an oracle for fixture scenarios, not permission for production code to execute the CLI or depend blindly on CLI JSON.

## Layout

- `v0.11.0/repository/` is the repository's Git directory stored under a trackable name rather than as a nested `.git` directory.
- `v0.11.0/worktree/` contains the matching tracked worktree file.
- `v0.11.0/expected/` contains CLI-observed scenario JSON.
- `v0.11.0/fixture-manifest.json` seals provenance, scenario IDs, every data-file hash, and repository-state snapshots.
- `validate-fixture.mjs` validates the fixture read-only.

A test that mounts the fixture must map `repository/` to the browser repository's `.git` directory and `worktree/` to its worktree. It must never alter the checked-in source fixture.

## Repository-state invariants

The manifest is the exact invariant record. Validation requires all of the following to remain byte-for-byte stable:

- symbolic `HEAD` and every head, issue, and identity ref;
- index byte length and SHA-256;
- all worktree file SHA-256 values;
- three issue refs and one identity ref;
- 28 reachable Git-Bug objects plus the three objects for the worktree's initial commit;
- an aggregate object-store SHA-256 and an individual SHA-256 for every repository, worktree, and expected-output file.

The validator also resolves each Git-Bug ref and reads its tip through the project's pinned `isomorphic-git` dependency, verifies every declared scenario, and takes the repository snapshot again afterward. Its output contains aggregate categories and counts only—never issue content, ref names, object IDs, or object bytes.

## Validation

From the repository root:

```sh
node AlmToo/tests/fixtures/gitBug/validate-fixture.mjs
node --test AlmToo/tests/gitBugFixtureValidationTests.mjs
```

Or run both through:

```sh
npm --prefix AlmToo run test:git-bug-fixture
```

The validator has no network dependency and invokes neither Git nor Git-Bug. Missing data, malformed JSON, and any content/state mismatch fail closed with fixed safe categories.

## Browser-local discovery proof

`../../gitBugFixtureDiscoveryTests.mjs` uses the same pinned `isomorphic-git` package as the browser Git Resource to read issue and identity refs, commits, trees, and blobs. It proves the observed `version-4` issue format and identity schema version `2` reproduce the fixture's IDs, titles, state, nullable labels, author display names, descriptions, comments, and Unicode text.

The tests derive three disposable variants from a copy of the sealed fixture:

- removed issue refs → `metadata-absent`;
- missing referenced object → `malformed`;
- a synthetic next commit with a `version-5` marker → `unsupported-format`.

Every success and failure case compares SHA-256 snapshots of all fixture files before and after discovery. Negative outcomes return no partial issue model. Test names and results contain categories only; they do not log issue content, refs, identities, object IDs, hashes, or object bytes.

## Compatibility boundary

This fixture proves browser-local discovery only for Git-Bug `v0.11.0` issue marker `version-4`, identity schema version `2`, and the operation forms actually observed in the fixture. It does not imply compatibility with earlier or later releases, repositories lacking metadata, malformed metadata, packed-only variants, hand-authored lookalikes, or operation/status forms absent from the fixture. All other representations fail closed as absent, malformed, or unsupported rather than yielding a partial model.
