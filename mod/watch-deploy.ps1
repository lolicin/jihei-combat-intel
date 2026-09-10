# Deploy watcher: as soon as the game process exits, copy the newest build from dist into the
# plugin folder. Retries on copy failure (in case you relaunch immediately), then writes a marker.
#
# Pure ASCII on purpose: the game path has CJK characters and a full-width colon, and Windows
# PowerShell 5.1 reads BOM-less .ps1 as ANSI. The path comes from gamepath.txt via _common.ps1.
param(
    [string]$GameDir,
    [int]$MaxSeconds = 21600
)

$ErrorActionPreference = 'Continue'

$ModRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $ModRoot '_common.ps1')

$resolved  = Resolve-GameDir -Explicit $GameDir -ModRoot $ModRoot
$src       = Join-Path $ModRoot 'dist\bepinex5\CombatInspector.dll'
$dst       = Join-Path $resolved 'BepInEx\plugins\CombatInspector\CombatInspector.dll'
$marker    = Join-Path $ModRoot 'out\_deploy_watch5.txt'

function GameProcs {
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and
        $_.Path.StartsWith($resolved, [StringComparison]::OrdinalIgnoreCase) -and
        $_.ProcessName -notmatch 'Crash'
    })
}

if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }

$wasRunning = ((GameProcs).Count -gt 0)
for ($i = 0; $i -lt $MaxSeconds; $i++) {
    $now = ((GameProcs).Count -gt 0)
    if ($wasRunning -and -not $now) {
        $ok = $false
        for ($k = 0; $k -lt 600 -and -not $ok; $k++) {
            try { Copy-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop; $ok = $true }
            catch { Start-Sleep -Milliseconds 500 }
        }
        $len = -1
        try { $len = (Get-Item -LiteralPath $dst).Length } catch { }
        "deployed=$ok at=$(Get-Date -Format 'HH:mm:ss') len=$len" | Out-File -Encoding utf8 $marker
        exit 0
    }
    $wasRunning = $now
    Start-Sleep -Seconds 1
}

"timeout (no close detected within $MaxSeconds s)" | Out-File -Encoding utf8 $marker
