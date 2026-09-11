# Passive decision logger. Read-only: polls the mod's /state endpoint and records, for every
# capture, which enemy the advisor recommended vs which enemy actually took damage.
#
# The interesting metric is agreement: when you dealt damage, was it to the recommended target?
# That answers "is its target selection any good" with numbers instead of impressions.
#
# ASCII only: Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI.
param(
    [int]$Seconds = 900,
    [int]$IntervalMs = 250,
    [int]$FailTolerance = 2400,
    [string]$OutFile
)

$ErrorActionPreference = 'Continue'

$ModRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutFile) { $OutFile = Join-Path $ModRoot 'out\decision_log.jsonl' }
$null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile)

$wc = New-Object System.Net.WebClient
$wc.Encoding = [System.Text.Encoding]::UTF8

$frames = 0
$dmgFrames = 0
$agree = 0
$miss = 0
$killableTaken = 0
$byVictim = @{}
$rankSeen = @{}

"logging to $OutFile for ${Seconds}s ..."
$deadline = (Get-Date).AddSeconds($Seconds)
$failStreak = 0

while ((Get-Date) -lt $deadline) {
    $json = $null
    try { $json = $wc.DownloadString('http://127.0.0.1:8790/state'); $failStreak = 0 }
    catch {
        $failStreak++
        if ($failStreak -ge $FailTolerance) { "endpoint unreachable ${FailTolerance}x in a row - stopping"; break }
        Start-Sleep -Milliseconds $IntervalMs
        continue
    }

    try { $s = $json | ConvertFrom-Json } catch { Start-Sleep -Milliseconds $IntervalMs; continue }
    if (-not $s.advice) { Start-Sleep -Milliseconds $IntervalMs; continue }

    $frames++
    $adv = $s.advice

    # who actually took damage this frame, and how much
    $victims = @()
    foreach ($e in $s.enemies) {
        if ($e.damageTakenThisFrame -and $e.damageTakenThisFrame.Count -gt 0) {
            $sum = 0.0
            foreach ($d in $e.damageTakenThisFrame) { $sum += [double]$d }
            if ($sum -gt 0) { $victims += [pscustomobject]@{ idx = $e.entityIndex; dmg = $sum; killable = [bool]$e.willDieFromNextHit } }
        }
    }

    $rec = [ordered]@{
        t         = (Get-Date -Format 'HH:mm:ss.fff')
        active    = [bool]$adv.active
        target    = $adv.targetEntityIndex
        tgtName   = $adv.targetName
        tgtDist   = [math]::Round([double]$adv.targetDistance, 2)
        hits      = $adv.targetHitsToKill
        killable  = [bool]$adv.targetKillableNow
        danger    = [math]::Round([double]$adv.dangerScore, 3)
        move      = "$([math]::Round([double]$adv.moveX,2)),$([math]::Round([double]$adv.moveY,2)) $($adv.moveLabel)"
        wallDist  = [math]::Round([double]$adv.wallDistance, 2)
        trapped   = [bool]$adv.trapped
        aimDiff   = [math]::Round([double]$adv.aimDiff, 3)
        victims   = @($victims | ForEach-Object { $_.idx })
        victimsDmg = @($victims | ForEach-Object { [math]::Round($_.dmg, 1) })
        ranking   = @($adv.ranking | ForEach-Object { $_.entityIndex })
    }
    ($rec | ConvertTo-Json -Compress) | Add-Content -LiteralPath $OutFile -Encoding UTF8

    if ($adv.active -and $adv.ranking) {
        foreach ($c in $adv.ranking) {
            $k = "$($c.entityIndex)|$($c.name)|$($c.aiTags)"
            if (-not $rankSeen.ContainsKey($k)) { $rankSeen[$k] = 0 }
            $rankSeen[$k]++
        }
    }

    if ($victims.Count -gt 0) {
        $dmgFrames++
        $hitTarget = $false
        foreach ($v in $victims) {
            if ($v.idx -eq $adv.targetEntityIndex) { $hitTarget = $true }
            $key = "$($v.idx)"
            if (-not $byVictim.ContainsKey($key)) { $byVictim[$key] = 0.0 }
            $byVictim[$key] += $v.dmg
            if ($v.killable) { $killableTaken++ }
        }
        if ($hitTarget) { $agree++ } else { $miss++ }
    }

    Start-Sleep -Milliseconds $IntervalMs
}

"`n=== decision log summary ==="
"samples            : $frames"
"frames w/ damage   : $dmgFrames"
"agreed w/ advice   : $agree"
"disagreed          : $miss"
if ($dmgFrames -gt 0) {
    "AGREEMENT RATE     : {0:P1}" -f ($agree / $dmgFrames)
}
"killable-victim hits: $killableTaken"
"`ntop damaged entities (idx = total damage taken):"
$byVictim.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 8 | ForEach-Object { "  E$($_.Key)  $([math]::Round($_.Value,0))" }
"`nmost-seen ranking entries (idx|name|ai = frames ranked):"
$rankSeen.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 10 | ForEach-Object { "  x$($_.Value)  $($_.Key)" }
