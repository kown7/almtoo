# IDesign — The Method: ALM Front-End Architecture

**Source:** Juval Löwy, *Righting Software* / IDesign The Method
**Applied to:** ALM Blazor WASM

---

## Layer Model

Utilities are **lateral** (off to the side), not under the stack.  
They may be used by any layer but do not participate in orchestration flow.

```
┌─────────────────────────────────────────────┐
│  Blazor Pages / Components  (UI)            │
│  Pages/*.razor, Layout/*.razor              │
│  → inject Managers only                     │
└──────────────────┬──────────────────────────┘              ┌─────────────────────────────────────┐
                   │                                         │  Utilities  (cross-cutting helpers) │
┌──────────────────▼──────────────────────────┐              │  Services/Utilities/                │
│  Managers   (orchestration / workflows)     │              │  → usable by any layer              │
│  Services/Managers/                         │              │  → no app-logic dependencies        │
│  → call Engines, Accessors                  │              └─────────────────────────────────────┘
└───────────┬───────────────────┬─────────────┘
            │                   │
┌───────────▼──────────┐        │
│  Engines             │        │
│  (stateless logic)   │        │
│  Services/Engines/   │        │
│  → may call Accessors│        │
└───────────┬──────────┘        │
            │                   │
┌───────────▼───────────────────▼─────────────┐
│  Accessors                                  │
│  (data / integration)                       │
│  Services/Accessors/                        │
│                                             │
└─────────────────────────────────────────────┘
```

---

## Class Mapping

### Managers (`Services/Managers/<ManagerName>/`)

Each manager is packaged as a service unit:
- `Interface/` contains `I<ManagerName>.cs` and manager-specific DTOs
- `Service/` contains `<ManagerName>.cs` implementation

| Service | Responsibility |
|-------|---------------|
| `RepoManager` | Orchestrates repo connection: parse URI → validate → fetch tree → warm OPFS cache |
| `RequirementsManager` | Loads `.sdoc` files via Accessor, delegates parsing to `SDocEngine`, returns domain model |
| `IssueManager` | Walks bug refs via Accessor, delegates op-log replay to `GitBugSnapshotEngine`, exposes snapshots |
| `TestManager` | Loads `.feature` files via Accessor, delegates parsing to `GherkinEngine`, returns feature model |
| `IssueWriteManager` | Orchestrates git-bug write sequence (create blob → tree → commit → update ref) via Accessor |

### Engines (`Services/Engines/<EngineName>/`)

Each engine is packaged as a service unit:
- `Interface/` contains `I<EngineName>.cs` and engine-specific DTOs
- `Service/` contains `<EngineName>.cs` implementation

| Service | Responsibility |
|-------|---------------|
| `SDocEngine` | Stateless: parses `.sdoc` `string` → structured requirement document model |
| `GitBugSnapshotEngine` | Stateless: replays OperationPack JSON log in timestamp order → issue snapshot |
| `GherkinEngine` | Stateless: wraps `Gherkin` NuGet v39.1.0 `Parser` — WASM-safe `StringReader` overload only |
| `RepoUriEngine` | Stateless: parses SSH (`git@github.com:owner/repo.git`) and HTTPS URIs → owner/repo tuple |

### Accessors (`Services/Accessors/<AccessorName>/`)

Each accessor is packaged as a service unit:
- `Interface/` contains `I<AccessorName>.cs` and accessor-specific DTOs
- `Service/` contains `<AccessorName>.cs` implementation

| Service | Responsibility |
|-------------------|---------------|
| `GitHubAccessor` | GitHub REST API calls: tree, raw files, bug refs, repo validation, rate limit |
| `OpfsCacheAccessor` | OPFS blob cache via `KristofferStrube.Blazor.FileSystem` (SHA-keyed) |
| `LocalStorageAccessor` | PAT + repo URI persistence via `Blazored.LocalStorage` |

### Utilities (`Services/Utilities/`)

| Category | Responsibility |
|-------|---------------|
| Cross-cutting helpers | Logging helpers, guard/validation helpers, constants, extension methods |
| Policy helpers | Retry/backoff helpers, formatting helpers, shared pure functions |

> **Rule:** DTOs are not centralized in a top-level `Models/` folder.  
> DTOs live with their owning service interface under that service’s `Interface/` folder.

---

## Dependency Rules (enforceable at review)

| Caller | May depend on | MUST NOT depend on |
|--------|--------------|-------------------|
| Pages | Managers, Utilities | Engines, Accessors |
| Managers | Engines, Accessors, Utilities | Each other (no Manager → Manager) |
| Engines | Accessors, Utilities | Managers, other Engines |
| Accessors | Utilities | Engines, Managers, other Accessors |
| Utilities | Nothing | Managers, Engines, Accessors, Pages |

---

## Folder Structure

```
Services/
├── Managers/
│   ├── RepoManager/
│   │   ├── Interface/
│   │   │   ├── IRepoManager.cs
│   │   │   └── RepoManagerDtos.cs
│   │   └── Service/
│   │       └── RepoManager.cs
│   ├── RequirementsManager/
│   │   ├── Interface/
│   │   │   ├── IRequirementsManager.cs
│   │   │   └── RequirementsManagerDtos.cs
│   │   └── Service/
│   │       └── RequirementsManager.cs
│   ├── IssueManager/
│   │   ├── Interface/
│   │   │   ├── IIssueManager.cs
│   │   │   └── IssueManagerDtos.cs
│   │   └── Service/
│   │       └── IssueManager.cs
│   ├── TestManager/
│   │   ├── Interface/
│   │   │   ├── ITestManager.cs
│   │   │   └── TestManagerDtos.cs
│   │   └── Service/
│   │       └── TestManager.cs
│   └── IssueWriteManager/
│       ├── Interface/
│       │   ├── IIssueWriteManager.cs
│       │   └── IssueWriteManagerDtos.cs
│       └── Service/
│           └── IssueWriteManager.cs
├── Engines/
│   ├── SDocEngine/
│   │   ├── Interface/
│   │   │   ├── ISDocEngine.cs
│   │   │   └── SDocEngineDtos.cs
│   │   └── Service/
│   │       └── SDocEngine.cs
│   ├── GitBugSnapshotEngine/
│   │   ├── Interface/
│   │   │   ├── IGitBugSnapshotEngine.cs
│   │   │   └── GitBugSnapshotEngineDtos.cs
│   │   └── Service/
│   │       └── GitBugSnapshotEngine.cs
│   ├── GherkinEngine/
│   │   ├── Interface/
│   │   │   ├── IGherkinEngine.cs
│   │   │   └── GherkinEngineDtos.cs
│   │   └── Service/
│   │       └── GherkinEngine.cs
│   └── RepoUriEngine/
│       ├── Interface/
│       │   ├── IRepoUriEngine.cs
│       │   └── RepoUriEngineDtos.cs
│       └── Service/
│           └── RepoUriEngine.cs
├── Accessors/
│   ├── GitHubAccessor/
│   │   ├── Interface/
│   │   │   ├── IGitHubAccessor.cs
│   │   │   └── GitHubAccessorDtos.cs
│   │   └── Service/
│   │       └── GitHubAccessor.cs
│   ├── OpfsCacheAccessor/
│   │   ├── Interface/
│   │   │   ├── ICacheAccess.cs
│   │   │   └── OpfsCacheDtos.cs
│   │   └── Service/
│   │       └── OpfsCacheAccessor.cs
│   └── LocalStorageAccessor/
│       ├── Interface/
│       │   ├── IConfigAccess.cs
│       │   └── LocalStorageConfigDtos.cs
│       └── Service/
│           └── LocalStorageAccessor.cs
└── Utilities/
    └── (cross-cutting helpers as needed)
```

---

## Phase 1 Impact

The existing `Services/RepoConfigService.cs` and `Services/GitHubApiService.cs` (from Wave 1) do not follow The Method. They must be refactored in Phase 1 replanning:

| Old class | New layer | New shape |
|-----------|-----------|----------|
| `RepoConfigService` | split across Accessor + Manager interfaces/services | `LocalStorageAccessor` + `RepoManager` service units |
| `GitHubApiService` | Accessor service unit | `GitHubAccessor` (Interface + Service) |
| `OpfsCacheService` | Accessor service unit | `OpfsCacheAccessor` (Interface + Service) |
| _(new)_ | Engine service unit | `RepoUriEngine` (Interface + Service) |

