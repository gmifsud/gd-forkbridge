#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Shared helpers for the Backend <-> ISL-Backend alignment scripts.
# Conventions: every script is DRY-RUN unless -Apply; nothing commits unless
# -Commit; nothing ever pushes. Destructive steps call Confirm-Step (HITL).
# ---------------------------------------------------------------------------

$script:Defaults = [ordered]@{
    BackendRoot      = 'D:\Repos\Backend'                                  # canonical; never C:\Repos (junction)
    BackendWorktree  = 'D:\Repos\Backend\worktrees\Backe-migra-1655c'
    BackendBranch    = 'migrations/24326-upgrade-angular-to-version-17'
    BackendTarget    = 'Development'
    IslRoot          = 'D:\Repos\ISL\ISL-Backend'
    IslWorktree      = 'D:\Repos\ISL\ISL-Backend.worktrees\fix\plt-core-module-types-enum'
    IslBranch        = 'fix/plt-core-module-types-enum'
    IslMigrationBase = 'origin/migration/24404-plt-ng17-fork-be'
    IslFixCommit     = 'd5380bd9aa5a3797c3dd7fe53657595e0245191b'
    OverloadCommit   = '4412b4f48636ed57352d8c5e7f76f7287ec26aff'
    LogDir           = 'D:\ForkBridge\state\alignment-2026-10-26'           # harness-owned, outside both repos
}
$script:Yes = $false

function Get-AlignmentDefaults { $script:Defaults }
function Set-AlignmentYes([bool]$Value) { $script:Yes = $Value }

function Write-Step([string]$Message) { Write-Host "`n== $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message)   { Write-Host "   OK  $Message" -ForegroundColor Green }
function Write-Warn2([string]$Message){ Write-Host "   !!  $Message" -ForegroundColor Yellow }
function Write-Plan([string]$Message) { Write-Host "   ->  $Message" -ForegroundColor DarkGray }

function Start-AlignmentLog([string]$Name) {
    $dir = $script:Defaults.LogDir
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir ("{0}-{1}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Name)
    Start-Transcript -Path $path -Append | Out-Null
    Write-Plan "transcript: $path"
}
function Stop-AlignmentLog { try { Stop-Transcript | Out-Null } catch { } }

function Invoke-Git {
    param([Parameter(Mandatory)][string]$Repo, [Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$GitArgs)
    $out = & git -C $Repo @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed in $Repo`n$out" }
    return ($out | Out-String).TrimEnd()
}
function Test-GitQuiet {
    param([Parameter(Mandatory)][string]$Repo, [Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$GitArgs)
    & git -C $Repo @GitArgs *> $null; return ($LASTEXITCODE -eq 0)
}

function Assert-Branch([string]$Repo, [string]$Expected) {
    $actual = Invoke-Git $Repo rev-parse --abbrev-ref HEAD
    if ($actual -ne $Expected) { throw "$Repo is on '$actual', expected '$Expected'. Not switching branches (analysis constraint)." }
    Write-Ok "$Repo on $Expected @ $(Invoke-Git $Repo rev-parse --short HEAD)"
}

function Assert-CleanScope([string]$Repo, [string[]]$Paths) {
    # Only the paths a step will touch must be clean; unrelated user changes are preserved.
    $status = Invoke-Git $Repo status --porcelain -- @Paths
    if ($status) { throw "Uncommitted changes in scope of this step ($Repo):`n$status`nCommit or stash them first." }
    Write-Ok "scope clean: $($Paths -join ', ')"
}

function Get-BlobSha([string]$Repo, [string]$Rev, [string]$Path) {
    $sha = & git -C $Repo rev-parse --verify --quiet "${Rev}:$Path" 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return $sha.Trim()
}

function Assert-BlobEquals([string]$Repo, [string]$RevA, [string]$RevB, [string]$Path) {
    $a = Get-BlobSha $Repo $RevA $Path; $b = Get-BlobSha $Repo $RevB $Path
    if ($a -ne $b) { throw "Blob mismatch for $Path`: $RevA=$a $RevB=$b" }
    Write-Ok "$([IO.Path]::GetFileName($Path)) == $RevB ($($b.Substring(0,9)))"
}

function Confirm-Step([string]$Prompt) {
    if ($script:Yes) { return $true }
    $answer = Read-Host "$Prompt  [y/N]"
    return ($answer -match '^(y|yes)$')
}

function Invoke-Dotnet {
    param([Parameter(Mandatory)][string]$WorkingDirectory, [Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$DotnetArgs)
    Write-Plan "dotnet $($DotnetArgs -join ' ')  (cwd $WorkingDirectory)"
    Push-Location $WorkingDirectory
    try { & dotnet @DotnetArgs; if ($LASTEXITCODE -ne 0) { throw "dotnet $($DotnetArgs -join ' ') failed ($LASTEXITCODE)" } }
    finally { Pop-Location }
}

function Commit-IfRequested([string]$Repo, [bool]$Commit, [string]$Message) {
    if (-not $Commit) { Write-Warn2 "changes staged, NOT committed (pass -Commit). Review: git -C `"$Repo`" diff --cached"; return }
    $trailer = "`n`nCo-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_018bEXVj3Gh5aBUxXdkyFEDw"
    Invoke-Git $Repo commit -q -m ($Message + $trailer) | Out-Null
    Write-Ok "committed: $(Invoke-Git $Repo rev-parse --short HEAD) $Message"
}

Export-ModuleMember -Function * -Variable @()
