# Git-Bug malformed-ref fixtures

These descriptors are deterministic, read-only mutations of the pinned `v0.11.0` fixture. They are intentionally kept outside the immutable repository snapshot so the snapshot's SHA-256 manifest remains authoritative. A Resource test can materialize a scenario from `../malformed-scenarios.json` against `../v0.11.0` and must never mutate the source fixture.

## Scenarios

| Scenario | Invariant | Mutation | Repairability |
| --- | --- | --- | --- |
| `duplicate-operations` | `DuplicateOperations` | Repeat one operation identifier | Not repairable |
| `missing-nonce` | `MissingNonce` | Remove the operation nonce | Repairable |
| `stale-edit-clock-n` | `StaleEditClock` | Decrease edit-clock-N | Not repairable |
| `missing-edit-clock-n` | `MissingEditClock` | Remove edit-clock-N | Repairable |
| `invalid-parent-topology` | `InvalidParentTopology` | Repeat or self-reference a parent | Not repairable |
| `unsupported-marker` | `UnsupportedMarker` | Replace `version-4` with `version-5` | Not repairable |
| `ambiguous-history` | `AmbiguousHistory` | Present two equally valid parent branches | Not repairable |

Every scenario records the expected source revision, affected `refs/bugs/<object-id>` ref, latest valid parent when one can be established, and bounded traversal counts. The descriptors contain no URLs, credentials, raw Git transport responses, or operation payload bodies.
