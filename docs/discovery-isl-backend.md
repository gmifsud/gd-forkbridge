# Phase A discovery report (2026-10-13)

Scope agreed for this phase: **read-only** inspection of `D:\Repos\ISL\ISL-Backend` only. QiSL
Backend was visible as a names-only folder listing (the session reached it only through the
`C:\Repos` junction); Frontend and ISL-Frontend were not connected. Nothing in any repository was
modified. Run `forkbridge discover --write-config draft.yml` locally to complete discovery for all four.

## ISL-Backend — VERIFIED

| Fact | Value |
|---|---|
| Object format / shallow / bare | sha1 / no / no |
| Branches | 1 local (`development`), 89 remote-tracking |
| origin | `https://<redacted>@dev.azure.com/ISLMalta/EBS%20%28Platform%29/_git/Backend` |
| History | 16,658 commits, root 2019-07-11 — shared with QiSL Backend (ISL is a true fork) |
| SDK pin | **no `global.json`** — the .NET SDK is not pinned |
| Target frameworks | net8.0 ×96, netcoreapp3.1 ×15, netstandard2.0 ×6, netcoreapp2.2 ×6, net7.0 ×3, net8.0;netstandard2.0 ×1, netstandard2.1 ×1, net8.0-windows ×1 |
| Projects / solutions | 133 csproj; 25 solutions (`solutions/*.sln`, module solutions, `QiSL.Platform.Web - All Modules - ISL.sln`) |
| Central package management | `Directory.Packages.props`, `ManagePackageVersionsCentrally=true`, 148 versions |
| **Consumer reference style** | LOB modules reference Fabric **binaries**: 480 `<HintPath>..\..\..\lib\*.dll`; `Reference Include="QiSL.Platform.SharedKernel.Domain"` ×51, `QiSL.Platform.Core` ×49, … |
| Generated/tracked outputs | `lib/` 286 files, `nupkg/` 251 files, 293 `.tt`/`.Designer.cs`/`.g.cs` |
| npm | only `QiSL.Platform.Web.Public` (yarn.lock) and bower/wwwroot vendored manifests |
| Pipeline | `azure-pipelines.yml`: `DotNetCoreCLI@2` build `QiSL.Platform.Web.Host` Release; tests reference **QiSL.App.Practice / QiSL.App.Registration, which do not exist in ISL** (stale, inherited from QiSL) |
| Fork pipeline | `azure-pipelines-isl-fork.yml`: pushes `Development-ISL` from `qwkinsights…/QiSL/_git/Backend` to `…/Backend-ISL` (`--force-with-lease`) |
| Provenance | 0 `Fork-Over-Source` trailers; previous fork-overs were merges (`fork-backend-development`, "NG Fork", "Moved out of SRC") |
| Shared-area activity since 2026-08-01 | Fabric 26 commits, Entity 1, WF 4 |

Top level: `QiSL.App.{Customer, Entity, EyeConnect, MTL, WF, WRH}`, `QiSL.Fabric/{BaseTemplate.0, Kernel, Platform, test}`,
`builds docs lib nupkg scripts solutions`, `Directory.Packages.props`, `nuget*.config`, pipelines.

ISL-only hosts inside the shared Fabric tree: `QiSL.Platform.Web.ISL.Host`, `QiSL.Platform.Web.ThirdParty.ISL.Host`
→ product-specific paths **nested under a shared directory** (handled by policy precedence).

## QiSL Backend — UNVERIFIED (names only)

Top level: `QiSL.App.{Entity, NL, Practice, Registration, SalesPortal, SL, VAM, WF, WRH}`, `QiSL.Fabric`,
`QiSL.GridQuery`, `Quality`, `infrastructure`, `patches`, `postman`, `backups`, tooling dot-folders.

## Proposed mappings (PROPOSED — not confirmed)

| Component | Basis | Status |
|---|---|---|
| `QiSL.Fabric/Kernel`, `QiSL.Fabric/Platform`, `QiSL.Fabric/BaseTemplate.0` | shared history + same structure in ISL; QiSL content not inspected | proposed |
| `QiSL.App.Entity`, `QiSL.App.WF`, `QiSL.App.WRH` | directory name present in both — **name alone is not evidence** | proposed, needs `discover` similarity evidence |
| `Directory.Packages.props` | present in both by name | proposed |
| `QiSL.GridQuery` | QiSL only | placeholder (decide whether ISL should receive it) |
| QiSL-only modules NL, Practice, Registration, SalesPortal, SL, VAM | product-specific (QiSL) | proposed |
| ISL-only Customer, EyeConnect, MTL + ISL hosts | product-specific (ISL) | verified present in ISL only |

## Design consequences

1. **Binary coupling through `lib/`.** Shared-code changes reach LOB modules only after the producing
   projects rebuild `lib/*.dll`. `lib/**` is classified generated (block); the dependency graph maps
   assembly references back to producing projects so product consumers are still identified; validation
   must build producers before consumers.
2. **No SDK pin** → validation results depend on the agent's SDK. Recommend adding `global.json`.
3. **Stale pipeline** in ISL references QiSL-only tests → treat `azure-pipelines*.yml` as target-pinned.
4. **Merge-based history** → ancestry already records QiSL commits as merged while some content was
   kept at ISL versions; expect `ANCESTRY_CONTENT_MISMATCH` findings and use reconcile with a baseline.
