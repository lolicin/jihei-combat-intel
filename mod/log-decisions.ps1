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
$okHttp = 0
$parseFail = 0
$noAdvice = 0
$nullEnemies = 0
$firstHttpErr = ''
$firstParseErr = ''
$maxTracker = 0
$maxMeas = 0
$srcTally = @{}
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
    try { $json = $wc.DownloadString('http://127.0.0.1:8790/state'); $failStreak = 0; $okHttp++ }
    catch {
        $failStreak++
        if (-not $firstHttpErr) { $firstHttpErr = $_.Exception.GetType().Name + ': ' + $_.Exception.Message }
        if ($failStreak -ge $FailTolerance) { "endpoint unreachable ${FailTolerance}x in a row - stopping" ; break }
        Start-Sleep -Milliseconds $IntervalMs
        continue
    }

    $s = $null
    try { $s = $json | ConvertFrom-Json }
    catch {
        $parseFail++
        if (-not $firstParseErr) { $firstParseErr = $_.Exception.GetType().Name + ': ' + $_.Exception.Message }
        Start-Sleep -Milliseconds $IntervalMs
        continue
    }
    if (-not $s -or -not $s.advice) {
        $noAdvice++
        Start-Sleep -Milliseconds $IntervalMs
        continue
    }
    if (-not $s.enemies) { $nullEnemies++ }

    $frames++
    $adv = $s.advice

    # who actually took damage this frame, and how much.
    # NOTE: damageTakenThisFrame is a per-frame buffer the game clears before our
    # Update() runs, so it is always empty - kept only as a cross-check. The real
    # measured signal is avgHitDamage (HP-delta based) and the source label.
    $victims = @()
    $measCount = 0
    $measSum = 0.0
    foreach ($e in $s.enemies) {
        if ($e.damageTakenThisFrame -and $e.damageTakenThisFrame.Count -gt 0) {
            $sum = 0.0
            foreach ($d in $e.damageTakenThisFrame) { $sum += [double]$d }
            if ($sum -gt 0) { $victims += [pscustomobject]@{ idx = $e.entityIndex; dmg = $sum; killable = [bool]$e.willDieFromNextHit } }
        }
        if ([double]$e.avgHitDamage -gt 0) { $measCount++; $measSum += [double]$e.avgHitDamage }
    }
    $tgt = $null
    foreach ($e in $s.enemies) { if ($e.entityIndex -eq $adv.targetEntityIndex) { $tgt = $e; break } }

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
        tracker   = $s.damageTrackerEntries
        measCount = $measCount
        measAvg   = if ($measCount -gt 0) { [math]::Round($measSum / $measCount, 1) } else { 0 }
        tgtSrc    = if ($tgt) { $tgt.predictedDamageSource } else { '' }
        tgtMeas   = if ($tgt) { [math]::Round([double]$tgt.avgHitDamage, 1) } else { 0 }
        victims   = @($victims | ForEach-Object { $_.idx })
        victimsDmg = @($victims | ForEach-Object { [math]::Round($_.dmg, 1) })
        ranking   = @($adv.ranking | ForEach-Object { $_.entityIndex })
    }
    ($rec | ConvertTo-Json -Compress) | Add-Content -LiteralPath $OutFile -Encoding UTF8

    if ([int]$rec.tracker -gt $maxTracker) { $maxTracker = [int]$rec.tracker }
    if ([int]$rec.measCount -gt $maxMeas) { $maxMeas = [int]$rec.measCount }
    if ($rec.tgtSrc) {
        $sk = [string]$rec.tgtSrc
        if (-not $srcTally.ContainsKey($sk)) { $srcTally[$sk] = 0 }
        $srcTally[$sk]++
    }

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
"`n--- pipeline (read this when samples=0; the three causes no longer look alike) ---"
"http ok            : $okHttp"
"json parse failed  : $parseFail"
"advice missing     : $noAdvice"
"enemies missing    : $nullEnemies"
"first http error   : $firstHttpErr"
"first parse error  : $firstParseErr"
$verdict = if ($frames -gt 0) { 'OK - got samples' }
elseif ($okHttp -eq 0) { 'ENDPOINT DOWN - game not running or Http/Enabled=false' }
elseif ($parseFail -gt 0) { 'PARSE FAILED - got bytes but could not deserialise' }
elseif ($noAdvice -gt 0) { 'NO ADVICE - Advisor/Enabled off, or not in a run' }
else { 'UNKNOWN - no samples and no counter explains it' }
"VERDICT            : $verdict"

"`n--- measured damage path (the actual question) ---"
"trackerEntries max : $maxTracker"
"measCount max      : $maxMeas   (enemies with avgHitDamage>0)"
"target source tags : " + (($srcTally.GetEnumerator() | ForEach-Object { "$($_.Key) x$($_.Value)" }) -join '   ')
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
