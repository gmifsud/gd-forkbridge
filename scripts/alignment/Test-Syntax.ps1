# Parse-only check of every script in this folder (no execution). Run: pwsh -File .\Test-Syntax.ps1
$bad = 0
Get-ChildItem $PSScriptRoot -Include *.ps1,*.psm1 -Recurse | ForEach-Object {
    $errs = $null; [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$null, [ref]$errs) | Out-Null
    if ($errs) { $bad++; Write-Host "FAIL $($_.Name)" -ForegroundColor Red; $errs | ForEach-Object { Write-Host "   L$($_.Extent.StartLineNumber): $($_.Message)" } }
    else { Write-Host "ok   $($_.Name)" -ForegroundColor Green }
}
exit $bad
