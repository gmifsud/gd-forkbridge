# ModuleTypes composition (ISL-Backend drop-in)

Lets ISL add module codes without editing Backend's enum files and without keeping a full copy of them.

At build time `IslComposeModuleTypes` reads the upstream `Enums/ModuleTypes.cs` in
`QiSL.Platform.SharedKernel.Domain` and `QiSL.App.BaseTemplate.Core.Shared`, and appends the
fork members listed in `ISL.Overrides/ModuleTypes.Fork.props`. The upstream files are never edited.

## Files (copy into ISL-Backend at the same paths)

| File | Replaces |
|---|---|
| `QiSL.Fabric/Directory.Build.targets` | the existing file (the `WebContentFolderHelper` swap is kept unchanged) |
| `QiSL.Fabric/ISL.Overrides/ModuleTypes.Fork.props` | new |

Delete `QiSL.Fabric/ISL.Overrides/{Kernel,BaseTemplate}/Enums/ModuleTypes.cs`.

## Rules

- **Add an ISL module:** one `<IslModuleType Include="NAME" Value="next free 1000+" />` line. No Backend change.
- **Names** are persisted as `LobModule_Code`: never rename or remove a shipped one.
- **Values** are fixed for ever. ISL modules consume the enum through `lib/*.dll` (HintPath), and enum values are compiled into callers as numbers. The 1000+ range cannot collide with upstream's implicit 0..n values, so a stale `lib/` DLL still maps `CUSTOMER` to `CUSTOMER` after upstream adds modules (tested).
- **Upstream adopts a fork name:** the build warns. Remove the fork line and rebuild every `lib/` DLL that uses it.
- **Guard:** `branch-alignment/Align.Config.psd1` fails `04-Test-Alignment.ps1` if either repo starts using `ModuleTypes` by ordinal.

## Adopting (one-time)

ISL members move from 9..13 (static override) to 1000..1004. Stored data is unaffected (names are stored), but all `lib/` DLLs that use `ModuleTypes` must be rebuilt together on each machine:

```powershell
dotnet build "solutions\QiSL.Platform.Web - All Modules - ISL.sln" -c Development -m:1 -nr:false
```

Don't commit `lib/*.dll` (team convention). The rebuild only has to happen locally: everyone who pulls the change rebuilds `lib/` in the same order.

## Limits

Works while the shared projects are compiled from source in each repo. Once they ship as NuGet packages, the enum is fixed at pack time and must become open string codes registered by each module.
