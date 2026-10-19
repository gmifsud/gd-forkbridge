#Requires -Version 7.0
<#
.SYNOPSIS
    Creates a self-contained ForkBridge demo: four fixture repositories with real git histories
    (QiSL/ISL backend + frontend, the frontend fork moved its shared library to a different path),
    bare "origin" remotes, a demo forkbridge.yml and a ready-to-transfer feature branch.

.DESCRIPTION
    Nothing outside -Path is touched. Your real repositories are never used.

.EXAMPLE
    ./New-ForkBridgeDemo.ps1 -Path D:\Temp\fb-demo
    cd D:\Temp\fb-demo
    forkbridge plan --pair backend --from isl --to qisl --source-ref feature/shared-kernel-fix --config .\forkbridge.yml
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if (Test-Path $Path) { throw "'$Path' already exists - choose a new directory." }
$root = (New-Item -ItemType Directory -Path $Path).FullName
$remotes = New-Item -ItemType Directory (Join-Path $root 'remotes')
$work = New-Item -ItemType Directory (Join-Path $root 'work')
$env:GIT_AUTHOR_NAME = 'Demo Author'; $env:GIT_AUTHOR_EMAIL = 'author@example.com'
$env:GIT_COMMITTER_NAME = 'Demo Committer'; $env:GIT_COMMITTER_EMAIL = 'committer@example.com'

function W([string]$repo, [string]$rel, [string]$text) {
    $p = Join-Path $repo $rel
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    [IO.File]::WriteAllText($p, $text.Replace("`r`n", "`n"))
}
function C([string]$repo, [string]$msg) { git -C $repo add -A; git -C $repo commit -q -m $msg }

function New-Origin([string]$name, [string]$dir) {
    git init -q --bare -b development (Join-Path $remotes "$name.git")
    git init -q -b development $dir
    git -C $dir config core.autocrlf false
    git -C $dir remote add origin (Join-Path $remotes "$name.git")
}
function New-Fork([string]$from, [string]$name, [string]$dir) {
    git -c core.autocrlf=false clone -q --bare (Join-Path $remotes "$from.git") (Join-Path $remotes "$name.git")
    git -c core.autocrlf=false clone -q --config core.autocrlf=false (Join-Path $remotes "$name.git") $dir
    git -C $dir config core.autocrlf false
}

# ---- backend ------------------------------------------------------------------------------------
$qb = Join-Path $work 'Backend'; $ib = Join-Path $work 'ISL-Backend'
New-Origin 'Backend' $qb
W $qb 'Directory.Packages.props' "<Project>`n  <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>`n  <ItemGroup><PackageVersion Include=`"Newtonsoft.Json`" Version=`"13.0.3`" /></ItemGroup>`n</Project>`n"
W $qb 'QiSL.Fabric/Kernel/QiSL.Platform.SharedKernel.Domain/QiSL.Platform.SharedKernel.Domain.csproj' "<Project Sdk=`"Microsoft.NET.Sdk`">`n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>`n  <ItemGroup><PackageReference Include=`"Newtonsoft.Json`" /></ItemGroup>`n</Project>`n"
W $qb 'QiSL.Fabric/Kernel/QiSL.Platform.SharedKernel.Domain/Entity.cs' "namespace QiSL.Platform.SharedKernel.Domain;`n`npublic class Entity`n{`n    public int Id { get; set; }`n    public string Name { get; set; } = `"`";`n}`n"
W $qb 'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Core/QiSL.Platform.Core.csproj' "<Project Sdk=`"Microsoft.NET.Sdk`">`n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>`n  <ItemGroup><ProjectReference Include=`"../../../../Kernel/QiSL.Platform.SharedKernel.Domain/QiSL.Platform.SharedKernel.Domain.csproj`" /></ItemGroup>`n</Project>`n"
W $qb 'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Core/Describer.cs' "namespace QiSL.Platform.Core;`n`npublic class Describer`n{`n    public string Describe(QiSL.Platform.SharedKernel.Domain.Entity e) => e.Name;`n}`n"
W $qb 'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.Host/appsettings.json' "{ `"Env`": `"qisl`" }`n"
W $qb 'QiSL.App.VAM/src/QiSL.App.VAM.Core/QiSL.App.VAM.Core.csproj' "<Project Sdk=`"Microsoft.NET.Sdk`">`n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>`n  <ItemGroup><Reference Include=`"QiSL.Platform.SharedKernel.Domain`"><HintPath>..\..\..\lib\QiSL.Platform.SharedKernel.Domain.dll</HintPath></Reference></ItemGroup>`n</Project>`n"
W $qb 'QiSL.App.VAM/src/QiSL.App.VAM.Core/Vehicle.cs' "namespace QiSL.App.VAM.Core;`npublic class Vehicle : QiSL.Platform.SharedKernel.Domain.Entity { }`n"
[IO.File]::WriteAllBytes((New-Item -Force -ItemType File (Join-Path $qb 'lib/QiSL.Platform.SharedKernel.Domain.dll')).FullName, [byte[]](0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0))
W $qb 'README.md' "# QiSL Backend (demo)`n"
C $qb 'Initial platform'
git -C $qb push -q origin development

New-Fork 'Backend' 'ISL-Backend' $ib
git -C $ib rm -q -r QiSL.App.VAM
W $ib 'QiSL.App.MTL/src/QiSL.App.MTL.Core/QiSL.App.MTL.Core.csproj' "<Project Sdk=`"Microsoft.NET.Sdk`">`n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>`n  <ItemGroup><Reference Include=`"QiSL.Platform.SharedKernel.Domain`"><HintPath>..\..\..\lib\QiSL.Platform.SharedKernel.Domain.dll</HintPath></Reference></ItemGroup>`n</Project>`n"
W $ib 'QiSL.App.MTL/src/QiSL.App.MTL.Core/FuelTransaction.cs' "namespace QiSL.App.MTL.Core;`npublic class FuelTransaction : QiSL.Platform.SharedKernel.Domain.Entity { }`n"
W $ib 'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.ISL.Host/appsettings.json' "{ `"Env`": `"isl-host`" }`n"
W $ib 'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.Host/appsettings.json' "{ `"Env`": `"isl`" }`n"
C $ib 'ISL fork: MTL module, ISL host'
git -C $ib push -q origin development
# An ISL shared-kernel fix to send upstream, mixed with an MTL change in a second commit.
git -C $ib switch -q -c feature/shared-kernel-fix
W $ib 'QiSL.Fabric/Kernel/QiSL.Platform.SharedKernel.Domain/Entity.cs' "namespace QiSL.Platform.SharedKernel.Domain;`n`npublic class Entity`n{`n    public int Id { get; set; }`n    public string Name { get; set; } = `"`";`n    public bool IsDeleted { get; set; }`n}`n"
C $ib 'SharedKernel: soft-delete flag on Entity'
W $ib 'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Core/Describer.cs' "namespace QiSL.Platform.Core;`n`npublic class Describer`n{`n    public string Describe(QiSL.Platform.SharedKernel.Domain.Entity e) => e.IsDeleted ? `"(deleted)`" : e.Name;`n}`n"
W $ib 'QiSL.App.MTL/src/QiSL.App.MTL.Core/FuelTransaction.cs' "namespace QiSL.App.MTL.Core;`npublic class FuelTransaction : QiSL.Platform.SharedKernel.Domain.Entity { public decimal Litres { get; set; } }`n"
C $ib 'Describer honours soft delete; MTL litres'
git -C $ib push -q origin feature/shared-kernel-fix
git -C $ib switch -q development

# ---- frontend -----------------------------------------------------------------------------------
$qf = Join-Path $work 'Frontend'; $if = Join-Path $work 'ISL-Frontend'
New-Origin 'Frontend' $qf
W $qf 'package.json' "{`n  `"name`": `"qisl-frontend`",`n  `"packageManager`": `"npm@10.9.4`",`n  `"dependencies`": { `"@angular/core`": `"21.2.23`" }`n}`n"
W $qf 'package-lock.json' "{ `"lockfileVersion`": 3 }`n"
W $qf 'libs/shared-ui/src/format.ts' "export function formatMoney(v: number): string {`n  return v.toFixed(2);`n}`n"
W $qf 'apps/qisl-app/src/main.ts' "import { formatMoney } from '../../../libs/shared-ui/src/format';`nconsole.log(formatMoney(1));`n"
C $qf 'Initial frontend'
git -C $qf push -q origin development
New-Fork 'Frontend' 'ISL-Frontend' $if
New-Item -ItemType Directory -Force (Join-Path $if 'projects/common') | Out-Null
git -C $if mv libs/shared-ui projects/common/shared-ui
git -C $if rm -q -r apps/qisl-app
W $if 'apps/isl-app/src/main.ts' "import { formatMoney } from '../../../projects/common/shared-ui/src/format';`nconsole.log(formatMoney(2));`n"
C $if 'ISL fork: shared-ui moved to projects/common'
git -C $if push -q origin development
git -C $qf switch -q -c feature/currency
W $qf 'libs/shared-ui/src/format.ts' "export function formatMoney(v: number, currency = 'EUR'): string {`n  return ``$`{v.toFixed(2)`} `$`{currency`}``;`n}`n"
C $qf 'shared-ui: currency in formatMoney'
git -C $qf push -q origin feature/currency
git -C $qf switch -q development

# ---- config -------------------------------------------------------------------------------------
$fwd = { param($p) $p.Replace('\', '/') }
$check = '$bad = Get-ChildItem -Recurse -File -Exclude *.dll | Select-String -SimpleMatch BUILD_BREAK; if ($bad) { $bad; exit 1 } else { ''build ok'' }'
@"
# ForkBridge DEMO configuration - fixture repositories only.
version: 1
stateDir: "$(& $fwd (Join-Path $root 'state'))"
repositories:
  qisl-backend:  { path: "$(& $fwd $qb)", kind: dotnet }
  isl-backend:   { path: "$(& $fwd $ib)", kind: dotnet }
  qisl-frontend: { path: "$(& $fwd $qf)", kind: angular }
  isl-frontend:  { path: "$(& $fwd $if)", kind: angular }
publish: { provider: none }
pairs:
  backend:
    products: { qisl: qisl-backend, isl: isl-backend }
    defaults: { targetBase: development, sourceBase: development }
    directions:
      - { from: qisl, to: isl, modes: [pick, reconcile] }
      - { from: isl, to: qisl, modes: [pick, reconcile] }
    components:
      - { id: shared-kernel, status: confirmed, paths: { qisl: QiSL.Fabric/Kernel, isl: QiSL.Fabric/Kernel } }
      - { id: platform, status: confirmed, paths: { qisl: QiSL.Fabric/Platform, isl: QiSL.Fabric/Platform }, dependsOn: [shared-kernel] }
      - { id: central-packages, status: confirmed, paths: { qisl: Directory.Packages.props, isl: Directory.Packages.props } }
    productSpecific:
      qisl: ["QiSL.App.VAM/**"]
      isl: ["QiSL.App.MTL/**", "QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.ISL.Host/**"]
    targetPinned:
      qisl: ["**/appsettings*.json"]
      isl: ["**/appsettings*.json"]
    generated:
      - { pattern: "lib/**", action: block, note: "tracked build outputs - regenerate in the target" }
    ignored: ["README.md"]
    validation:
      default:
        trusted: true
        steps:
          - { id: build, kind: build, run: ["pwsh", "-NoProfile", "-Command", "$check"] }
  frontend:
    products: { qisl: qisl-frontend, isl: isl-frontend }
    defaults: { targetBase: development, sourceBase: development }
    directions:
      - { from: qisl, to: isl, modes: [pick, reconcile] }
      - { from: isl, to: qisl, modes: [pick] }
    components:
      - { id: shared-ui, status: confirmed, paths: { qisl: libs/shared-ui, isl: projects/common/shared-ui } }
      - { id: workspace, status: proposed, paths: { qisl: package.json, isl: package.json }, evidence: "PLACEHOLDER - verify before confirming" }
    productSpecific:
      qisl: ["apps/qisl-app/**"]
      isl: ["apps/isl-app/**"]
    ignored: ["package-lock.json"]
    validation:
      default:
        trusted: true
        steps:
          - { id: typecheck, kind: typecheck, run: ["pwsh", "-NoProfile", "-Command", "$check"] }
"@ | Set-Content -Encoding utf8NoBOM (Join-Path $root 'forkbridge.yml')

Write-Host "Demo created in $root" -ForegroundColor Green
Write-Host @"

Try:
  cd "$root"
  forkbridge discover --config .\forkbridge.yml
  forkbridge plan --pair backend --from isl --to qisl --source-ref feature/shared-kernel-fix --config .\forkbridge.yml
  forkbridge simulate --plan <plan-id> --trust-validation --config .\forkbridge.yml
  forkbridge plan --pair frontend --from qisl --to isl --source-ref feature/currency --config .\forkbridge.yml
  forkbridge-web --config .\forkbridge.yml
"@
