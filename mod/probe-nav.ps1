# Waits for the game to come back up, then samples the navigation diagnostics repeatedly so the
# raycast question gets answered without a manual prompt. ASCII-only; path comes from gamepath.txt.
param(
    [string]$GameDir,
    [int]$WaitUpSeconds = 1200,
    [int]$SampleSeconds = 150
)

$ErrorActionPreference = 'Continue'
$ModRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $ModRoot '_common.ps1')

$resolved = Resolve-GameDir -Explicit $GameDir -ModRoot $ModRoot
$url = 'http://127.0.0.1:8790/state'

function Get-State {
    try {
        $wc = New-Object System.Net.WebClient
        $wc.Encoding = [System.Text.Encoding]::UTF8
        $raw = $wc.DownloadString($url)
        return ($raw | ConvertFrom-Json)
    } catch { return $null }
}

# 1) wait for the mod endpoint
$deadline = (Get-Date).AddSeconds($WaitUpSeconds)
$up = $false
while ((Get-Date) -lt $deadline) {
    $s = Get-State
    if ($null -ne $s) { $up = $true; break }
    Start-Sleep -Seconds 3
}
if (-not $up) { "TIMEOUT: mod endpoint never came up within $WaitUpSeconds s"; exit 0 }
"endpoint up at $(Get-Date -Format 'HH:mm:ss')"

# 2) wait until actually in a run with enemies
$deadline = (Get-Date).AddSeconds(180)
$inRun = $false
while ((Get-Date) -lt $deadline) {
    $s = Get-State
    if ($null -ne $s -and $s.enemies.Count -gt 0 -and $s.advice -and $s.advice.navAvailable) { $inRun = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $inRun) { "NOT IN A RUN YET (or nav unavailable) - reporting whatever we have"; }

# 3) sample repeatedly
$seen = @{}
$deadline = (Get-Date).AddSeconds($SampleSeconds)
while ((Get-Date) -lt $deadline) {
    $s = Get-State
    if ($null -ne $s) {
        $key = "$($s.navInfo)"
        if (-not $seen.ContainsKey($key)) {
            $seen[$key] = 1
            $t = Get-Date -Format 'HH:mm:ss'
            "--- $t  enemies=$($s.enemies.Count)"
            "    navInfo : $($s.navInfo)"
            if ($s.advice) {
                "    墙距=$([math]::Round($s.advice.wallDistance,2)) 通畅=$([math]::Round($s.advice.pathClearance,2)) 避墙修正=$($s.advice.moveAdjustedForWalls) 被困=$($s.advice.trapped) 走位=$($s.advice.moveLabel)"
            }
            "    autoAim : $($s.autoAimInfo)"
        } else { $seen[$key]++ }
    }
    Start-Sleep -Milliseconds 1500
}
"`n=== distinct navInfo values seen: $($seen.Count) ==="
$seen.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 6 | ForEach-Object { "  x$($_.Value)  $($_.Key)" }
