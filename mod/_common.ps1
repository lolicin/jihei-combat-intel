# Shared helpers for build.ps1 / install.ps1 / uninstall.ps1.
#
# These files are deliberately pure ASCII. The game's install path contains CJK characters and a
# full-width colon, and Windows PowerShell 5.1 reads BOM-less .ps1 files as ANSI, which mangles
# them. So the path lives in gamepath.txt (UTF-8) and everything else is discovered by globbing.

function Write-Step($m)  { Write-Host "`n== $m" -ForegroundColor Cyan }
function Write-Ok($m)    { Write-Host "   ok   $m" -ForegroundColor Green }
function Write-Warn2($m) { Write-Host "   warn  $m" -ForegroundColor Yellow }

function Resolve-GameDir {
    param(
        [string]$Explicit,
        [Parameter(Mandatory)][string]$ModRoot
    )

    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "GameDir does not exist: $Explicit" }
        return (Get-Item -LiteralPath $Explicit).FullName
    }

    $cfg = Join-Path $ModRoot 'gamepath.txt'
    if (Test-Path -LiteralPath $cfg) {
        $p = Get-Content -LiteralPath $cfg -Encoding UTF8 -Raw
        if ($p) {
            $p = $p.Trim().Trim('"').Trim("'")
            if ($p -and (Test-Path -LiteralPath $p)) { return (Get-Item -LiteralPath $p).FullName }
            if ($p) { Write-Warn2 "gamepath.txt points at a missing folder: $p" }
        }
    }
    throw "No game folder. Create mod\gamepath.txt (UTF-8, one line) or pass -GameDir."
}

function Get-GameLayout {
    param([Parameter(Mandatory)][string]$GameDir)

    $data = Get-ChildItem -LiteralPath $GameDir -Directory -Filter '*_Data' -ErrorAction SilentlyContinue |
            Select-Object -First 1
    if (-not $data) { throw "No *_Data folder under: $GameDir" }

    $managed = Join-Path $data.FullName 'Managed'
    if (-not (Test-Path -LiteralPath $managed)) { throw "No Managed folder: $managed" }

    $exe = Get-ChildItem -LiteralPath $GameDir -Filter '*.exe' -ErrorAction SilentlyContinue |
           Where-Object { $_.Name -notmatch 'CrashHandler' } |
           Sort-Object Length -Descending | Select-Object -First 1

    return [pscustomobject]@{
        GameDir = $GameDir
        DataDir = $data.FullName
        Managed = $managed
        Exe     = if ($exe) { $exe.FullName } else { $null }
        ExeName = if ($exe) { $exe.Name } else { '<unknown>' }
    }
}

function Get-GameProcesses {
    param([Parameter(Mandatory)][string]$GameDir)
    Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($GameDir, [System.StringComparison]::OrdinalIgnoreCase)
    }
}
