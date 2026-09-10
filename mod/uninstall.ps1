<#
.SYNOPSIS
    Removes the CombatInspector plugin, and optionally the BepInEx loader too.

.DESCRIPTION
    The game folder is taken from mod\gamepath.txt (UTF-8, one line) unless -GameDir is given.

.EXAMPLE
    .\uninstall.ps1              # plugin only, loader stays
    .\uninstall.ps1 -Full        # everything, back to pristine
    .\uninstall.ps1 -KeepOutput  # keep the captured json/csv
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$Full,
    [switch]$KeepOutput
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$ModRoot = $PSScriptRoot
. (Join-Path $ModRoot '_common.ps1')

$resolved = Resolve-GameDir -Explicit $GameDir -ModRoot $ModRoot
$layout   = Get-GameLayout -GameDir $resolved
$GameDir  = $layout.GameDir

$procs = @(Get-GameProcesses -GameDir $GameDir)
if ($procs.Count -gt 0) { Write-Warn2 'The game appears to be running - close it for a clean removal.' }

Write-Step 'Removing CombatInspector plugin'
$pluginDir = Join-Path $GameDir 'BepInEx\plugins\CombatInspector'
if (Test-Path -LiteralPath $pluginDir) {
    if ($KeepOutput) {
        Get-ChildItem -LiteralPath $pluginDir -File | Remove-Item -Force
        Write-Ok 'removed plugin files, kept CombatInspectorOut\'
    } else {
        Remove-Item -LiteralPath $pluginDir -Recurse -Force
        Write-Ok ('removed ' + $pluginDir)
    }
} else {
    Write-Warn2 'plugin folder not found (nothing to do)'
}

if ($Full) {
    Write-Step 'Removing BepInEx loader'
    $paths = @(
        (Join-Path $GameDir 'BepInEx'),
        (Join-Path $GameDir 'winhttp.dll'),
        (Join-Path $GameDir 'doorstop_config.ini'),
        (Join-Path $GameDir '.doorstop_version')
    )
    foreach ($p in $paths) {
        if (Test-Path -LiteralPath $p) {
            Remove-Item -LiteralPath $p -Recurse -Force
            Write-Ok ('removed ' + $p)
        }
    }
    Write-Host "`nGame folder is back to a pristine state." -ForegroundColor Green
} else {
    Write-Host "`nLoader left in place (pass -Full to remove it as well)." -ForegroundColor Cyan
}
