<#
.SYNOPSIS
    Refreshes the reference assemblies from the game and builds CombatInspector.

.DESCRIPTION
    CombatInspector.dll is compiled against the game's OWN assemblies (Assembly-CSharp.dll,
    Unity.Entities.dll, ...), so there is no version guessing: whatever the shipped build contains
    is exactly what the mod binds to.

    libs/  <- game assemblies, re-copied from <GameDir>\<game>_Data\Managed
    libs5/ <- BepInEx 5 loader assemblies   (extracted from the vendored zip on demand)
    libs6/ <- BepInEx 6 loader assemblies   (extracted from the vendored zip on demand)

    The game folder is taken from mod\gamepath.txt (UTF-8, one line) unless -GameDir is given.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Loader bepinex5
    .\build.ps1 -GameDir "D:\Games\SomeOtherInstall"
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$GameDir,
    [ValidateSet('bepinex5', 'bepinex6', 'both')]
    [string]$Loader = 'both',
    [switch]$SkipLibRefresh
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$ModRoot = $PSScriptRoot
. (Join-Path $ModRoot '_common.ps1')

$ProjDir   = Join-Path $ModRoot 'CombatInspector'
$LibsDir   = Join-Path $ModRoot 'libs'
$Libs5Dir  = Join-Path $ModRoot 'libs5'
$Libs6Dir  = Join-Path $ModRoot 'libs6'
$DistDir   = Join-Path $ModRoot 'dist'
$VendorDir = Join-Path $ModRoot 'vendor'

$GameDlls = @(
    'Assembly-CSharp.dll',
    'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll',
    'UnityEngine.UIModule.dll', 'UnityEngine.TextRenderingModule.dll',
    'UnityEngine.InputLegacyModule.dll', 'UnityEngine.PropertiesModule.dll',
    'Unity.InputSystem.dll',
    'Unity.Entities.dll', 'Unity.Entities.Hybrid.dll',
    'Unity.Collections.dll', 'Unity.Collections.LowLevel.ILSupport.dll',
    'Unity.Mathematics.dll', 'Unity.Transforms.dll',
    'Unity.Burst.dll', 'Unity.Physics.dll', 'Unity.Serialization.dll',
    'Newtonsoft.Json.dll'
)

$loaderZips = @(
    @{ Dir = $Libs5Dir; Zip = 'BepInEx_win_x64_5.4.23.5.zip';
       Files = @('BepInEx.dll', '0Harmony.dll') },
    @{ Dir = $Libs6Dir; Zip = 'BepInEx-Unity.Mono-win-x64-6.0.0-pre.2.zip';
       Files = @('BepInEx.Core.dll', 'BepInEx.Unity.Common.dll', 'BepInEx.Unity.Mono.dll', '0Harmony.dll') }
)

if (-not $SkipLibRefresh) {
    Write-Step 'Refreshing reference assemblies from the game'

    $resolved = Resolve-GameDir -Explicit $GameDir -ModRoot $ModRoot
    $layout   = Get-GameLayout -GameDir $resolved
    Write-Ok ("managed folder: " + $layout.Managed)

    New-Item -ItemType Directory -Force -Path $LibsDir | Out-Null
    $missing = @()
    foreach ($dll in $GameDlls) {
        $src = Join-Path $layout.Managed $dll
        if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $LibsDir -Force }
        else { $missing += $dll }
    }
    if ($missing.Count -gt 0) { Write-Warn2 ('missing (build may fail): ' + ($missing -join ', ')) }
    Write-Ok ('libs/ has ' + (Get-ChildItem -LiteralPath $LibsDir -Filter *.dll).Count + ' assemblies')

    foreach ($pair in $loaderZips) {
        $zip = Join-Path $VendorDir $pair.Zip
        if (-not (Test-Path -LiteralPath $zip)) { Write-Warn2 ('vendor zip missing: ' + $pair.Zip); continue }

        $need = @($pair.Files | Where-Object { -not (Test-Path -LiteralPath (Join-Path $pair.Dir $_)) })
        $leaf = Split-Path -Path $pair.Dir -Leaf
        if ($need.Count -eq 0) { Write-Ok ($leaf + '/ already populated'); continue }

        $tmp = Join-Path $env:TEMP ('ci_' + [guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force
        New-Item -ItemType Directory -Force -Path $pair.Dir | Out-Null
        foreach ($f in $pair.Files) {
            $found = Get-ChildItem -LiteralPath $tmp -Recurse -Filter $f -ErrorAction SilentlyContinue |
                     Sort-Object { $_.FullName.Length } | Select-Object -First 1
            if ($found) { Copy-Item -LiteralPath $found.FullName -Destination $pair.Dir -Force }
            else { Write-Warn2 ($f + ' not found inside ' + $pair.Zip) }
        }
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
        Write-Ok ($leaf + '/ populated from ' + $pair.Zip)
    }
}

$targets = switch ($Loader) {
    'bepinex5' { @('bepinex5') }
    'bepinex6' { @('bepinex6') }
    default    { @('bepinex5', 'bepinex6') }
}

foreach ($t in $targets) {
    Write-Step ('Building CombatInspector for ' + $t)
    $out = Join-Path $ProjDir ('bin\Release-' + $t)

    & dotnet build $ProjDir -c Release -p:Loader=$t -p:OutputPath="$out\" -v minimal
    if ($LASTEXITCODE -ne 0) { throw ('build failed for ' + $t) }

    $dll = Join-Path $out 'CombatInspector.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw ('expected output not found: ' + $dll) }

    $dist = Join-Path $DistDir $t
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    Copy-Item -LiteralPath $dll -Destination $dist -Force
    $pdb = Join-Path $out 'CombatInspector.pdb'
    if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $dist -Force }
    Write-Ok ('dist\' + $t + '\CombatInspector.dll (' +
              [math]::Round((Get-Item -LiteralPath $dll).Length / 1KB, 1) + ' KB)')
}

Write-Host "`nBuild complete. Next: .\install.ps1" -ForegroundColor Cyan
