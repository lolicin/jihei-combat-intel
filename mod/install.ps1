<#
.SYNOPSIS
    Installs BepInEx + the CombatInspector plugin into the game folder.

.DESCRIPTION
    Everything this writes is additive and lives next to the game executable:
        winhttp.dll             Unity Doorstop proxy (the loader's entry point)
        doorstop_config.ini     Doorstop configuration
        .doorstop_version
        BepInEx\                loader core, config, logs, plugins
        BepInEx\plugins\CombatInspector\CombatInspector.dll
        BepInEx\plugins\CombatInspector\CombatInspectorOut\   (created at runtime)

    No game asset and no managed assembly is modified, so the game's own files stay intact and
    uninstall.ps1 -Full returns the folder to a pristine state.

    The game folder is taken from mod\gamepath.txt (UTF-8, one line) unless -GameDir is given.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -Loader bepinex6 -ForceLoader
    .\install.ps1 -PluginOnly
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$GameDir,
    [ValidateSet('bepinex5', 'bepinex6')]
    [string]$Loader = 'bepinex5',
    [switch]$PluginOnly,
    [switch]$ForceLoader
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$ModRoot = $PSScriptRoot
. (Join-Path $ModRoot '_common.ps1')

$DistDir   = Join-Path $ModRoot 'dist'
$VendorDir = Join-Path $ModRoot 'vendor'

$zips = @{
    'bepinex5' = 'BepInEx_win_x64_5.4.23.5.zip'
    'bepinex6' = 'BepInEx-Unity.Mono-win-x64-6.0.0-pre.2.zip'
}

$resolved = Resolve-GameDir -Explicit $GameDir -ModRoot $ModRoot
$layout   = Get-GameLayout -GameDir $resolved
$GameDir  = $layout.GameDir

$procs = @(Get-GameProcesses -GameDir $GameDir)
if ($procs.Count -gt 0) { throw 'The game is running. Close it before installing.' }

Write-Ok ('game   : ' + $GameDir)
Write-Ok ('exe    : ' + $layout.ExeName)
Write-Ok ('managed: ' + $layout.Managed)

# ---------------------------------------------------------------- loader

if (-not $PluginOnly) {
    Write-Step ('Installing loader (' + $Loader + ')')
    $zip = Join-Path $VendorDir $zips[$Loader]
    if (-not (Test-Path -LiteralPath $zip)) { throw ('vendor zip missing: ' + $zip) }

    $existing = Join-Path $GameDir 'winhttp.dll'
    if ((Test-Path -LiteralPath $existing) -and -not $ForceLoader) {
        Write-Warn2 'winhttp.dll already present - leaving the existing loader alone. Use -ForceLoader to overwrite.'
    } else {
        Expand-Archive -LiteralPath $zip -DestinationPath $GameDir -Force
        Write-Ok ('extracted ' + $zips[$Loader] + ' into the game folder')
    }
}

# ---------------------------------------------------------------- plugin

Write-Step 'Deploying CombatInspector plugin'
$src = Join-Path $DistDir ($Loader + '\CombatInspector.dll')
if (-not (Test-Path -LiteralPath $src)) {
    throw ('plugin not built for ' + $Loader + ' yet: ' + $src + "`nRun .\build.ps1 first.")
}

$pluginDir = Join-Path $GameDir 'BepInEx\plugins\CombatInspector'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item -LiteralPath $src -Destination $pluginDir -Force
$pdb = $src -replace '\.dll$', '.pdb'
if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $pluginDir -Force }
Write-Ok ($pluginDir + '\CombatInspector.dll')

# ---------------------------------------------------------------- report

$logPath    = Join-Path $GameDir 'BepInEx\LogOutput.log'
$cfgPath    = Join-Path $GameDir 'BepInEx\config\com.jhx9676.mechcore.combatinspector.cfg'
$outPath    = Join-Path $pluginDir 'CombatInspectorOut'

Write-Host ''
Write-Host 'Installed. Now:' -ForegroundColor Cyan
Write-Host '  1. Start the game through Steam and enter a run.'
Write-Host '  2. F9  = live overlay (your stats + every enemy HP / position / distance / AI type)'
Write-Host '     F10 = write latest.json, latest_deep.json and enemies.csv to:'
Write-Host ('           ' + $outPath)
Write-Host '  3. While the game runs:'
Write-Host '       curl http://127.0.0.1:8790/health'
Write-Host '       curl http://127.0.0.1:8790/state'
Write-Host '       curl "http://127.0.0.1:8790/deep?enemies=5&depth=4"'
Write-Host ''
Write-Host ('  loader log : ' + $logPath)
Write-Host ('  mod config : ' + $cfgPath + '   (created on first run)')
Write-Host '  uninstall  : .\uninstall.ps1        (plugin only)'
Write-Host '               .\uninstall.ps1 -Full  (loader too)'
Write-Host ''
Write-Host 'If the loader log says nothing about CombatInspector, the loader itself did not attach.' -ForegroundColor Yellow
Write-Host 'Retry with the other loader:' -ForegroundColor Yellow
Write-Host ('  .\build.ps1 -Loader bepinex6 ; .\install.ps1 -Loader bepinex6 -ForceLoader') -ForegroundColor Yellow
