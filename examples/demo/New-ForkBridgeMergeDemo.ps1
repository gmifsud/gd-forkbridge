#Requires -Version 7.0
<#
.SYNOPSIS
    Creates a self-contained sandbox for the Merge tab: one fixture repository with a bare "origin", a
    forkbridge.yml (merge section with a trusted, harmless validation step) and branches that exercise every
    analysis outcome and the supported conflict types.

.DESCRIPTION
    Nothing outside -Path is touched. Your real repositories are never used.

    Branches (all pushed to origin):
      develop               target branch
      feature/checkout      conflicts with develop: text conflict (src/Greeting.cs), modify/delete
                            (src/Legacy.cs), binary conflict (assets/logo.bin); plus an auto-merged file
                            (src/appsettings.json) and an incoming file (src/Checkout.cs)
      feature/fast-forward  only ahead of develop  -> "Fast-forward possible"
      feature/merged        already merged         -> "Already up to date"
      feature/rename        renames a file develop deletes -> unsupported (blocked) conflict

.EXAMPLE
    ./New-ForkBridgeMergeDemo.ps1 -Path D:\Temp\fb-merge-demo
    forkbridge-web --config D:\Temp\fb-merge-demo\forkbridge.yml
    # open the printed URL -> Merge -> repository demo-shop -> Refresh refs
    # source feature/checkout, target develop -> Analyze merge -> Start merge session
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

$repo = Join-Path $work 'shop'
$origin = Join-Path $remotes 'shop.git'
function W([string]$rel, [string]$text) {
    $p = Join-Path $repo $rel
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    [IO.File]::WriteAllText($p, $text.Replace("`r`n", "`n"))
}
function B([string]$rel, [byte[]]$bytes) {
    $p = Join-Path $repo $rel
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    [IO.File]::WriteAllBytes($p, $bytes)
}
function G { git -C $repo @args }
function C([string]$msg) { G add -A; G commit -q -m $msg }

git init -q --bare -b develop $origin
git init -q -b develop $repo
G config core.autocrlf false
G config user.name 'Demo User'
G config user.email 'demo@example.com'
G remote add origin $origin

# --- base ---------------------------------------------------------------------------------------------
W 'src/Greeting.cs' @"
namespace Shop;

public static class Greeting
{
    public static string Hello(string name) => `$"Hello, {name}!";
}
"@
W 'src/appsettings.json' @"
{
  "currency": "EUR",
  "pageSize": 20,
  "locale": "en-MT",
  "timeoutSeconds": 30,
  "theme": "light"
}
"@
W 'src/Legacy.cs' "namespace Shop;`n`npublic static class Legacy { public static int Version => 1; }`n"
W 'src/Big.cs' (((1..40) | ForEach-Object { "// line $_" }) -join "`n")
B 'assets/logo.bin' ([byte[]](0, 1, 2, 3))
W 'README.md' "# Demo shop`n"
C 'base'
G push -q origin develop

# --- source: feature/checkout ---------------------------------------------------------------------------
G switch -q -c feature/checkout
W 'src/Greeting.cs' @"
namespace Shop;

public static class Greeting
{
    public static string Hello(string name) => `$"Welcome to the shop, {name}!";
}
"@
W 'src/appsettings.json' @"
{
  "currency": "EUR",
  "pageSize": 50,
  "locale": "en-MT",
  "timeoutSeconds": 30,
  "theme": "light"
}
"@
W 'src/Checkout.cs' "namespace Shop;`n`npublic sealed class Checkout { }`n"
G rm -q src/Legacy.cs
B 'assets/logo.bin' ([byte[]](0, 1, 2, 9))
C 'checkout: greeting, page size, remove legacy, new logo'
G push -q origin feature/checkout

# --- target: develop moves on ---------------------------------------------------------------------------
G switch -q develop
W 'src/Greeting.cs' @"
namespace Shop;

public static class Greeting
{
    public static string Hello(string name) => `$"Hi {name}, good to see you!";
}
"@
W 'src/appsettings.json' @"
{
  "currency": "EUR",
  "pageSize": 20,
  "locale": "en-MT",
  "timeoutSeconds": 30,
  "theme": "dark"
}
"@
W 'src/Legacy.cs' "namespace Shop;`n`npublic static class Legacy { public static int Version => 2; }`n"
B 'assets/logo.bin' ([byte[]](0, 1, 2, 7))
C 'develop: greeting, dark theme, legacy v2, new logo'
G push -q origin develop

# --- other outcomes -------------------------------------------------------------------------------------
G switch -q develop
G switch -q -c feature/merged
W 'docs/merged.md' "merged`n"
C 'merged'
G push -q origin feature/merged
G switch -q develop
G merge -q --no-ff -m 'merge feature/merged' feature/merged
G push -q origin develop

G switch -q -c feature/rename develop~1
G mv src/Big.cs src/Huge.cs
C 'rename Big -> Huge'
G push -q origin feature/rename
G switch -q develop
G rm -q src/Big.cs
C 'remove Big'
G push -q origin develop

G switch -q -c feature/fast-forward
W 'docs/faq.md' "# FAQ`n"
C 'faq'
G push -q origin feature/fast-forward
G switch -q develop

$cfg = @"
# yaml-language-server: `$schema=$((Join-Path $PSScriptRoot '..' '..' 'schema' 'forkbridge.schema.json').Replace('\','/'))
version: 1
stateDir: state
repositories:
  demo-shop:
    path: work/shop
    remote: origin
merge:
  branchPrefix: forkbridge/merge
  repositories:
    demo-shop:
      validation:
        trusted: true          # demo only: a harmless git check on the candidate
        steps:
          - id: whitespace
            kind: lint
            run: [git, diff, --check, HEAD^1, HEAD]
            timeoutSeconds: 60
"@
[IO.File]::WriteAllText((Join-Path $root 'forkbridge.yml'), $cfg)
Write-Host "Merge demo ready in $root"
Write-Host "  forkbridge-web --config `"$(Join-Path $root 'forkbridge.yml')`""
Write-Host "  Merge -> demo-shop -> Refresh refs -> source feature/checkout, target develop -> Analyze merge"
