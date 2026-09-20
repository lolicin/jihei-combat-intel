# One-command research-artifact refresh for a game update.
#
# What it does, in order:
#   1. Hash-checks game DLLs vs mod\libs (drift report).
#   2. Runs build.ps1 unless -SkipBuild (it refreshes libs and rebuilds; compile errors from
#      renamed fields show up here, which is the intended early-warning).
#   3. Re-decompiles Assembly-CSharp into decompiled\AssemblyCSharp (project mode, verified
#      non-empty; old output is kept as AssemblyCSharp.old-<stamp> then replaced).
#   4. Regenerates types_all.txt (sorted unique "Kind FullName" lines).
#   5. Regenerates component_inventory.txt by parsing the decompiled sources.
#   6. Prints a type-level diff vs the previous types_all.txt.
#
# Pure ASCII on purpose: PS 5.1 reads BOM-less .ps1 as ANSI; the game path has CJK chars and is
# discovered via _common.ps1 (gamepath.txt / -GameDir), never hardcoded here.
param(
    [string]$GameDir,
    [switch]$SkipBuild,
    [switch]$SkipDecompile
)

$ErrorActionPreference = 'Stop'
$ToolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ModRoot  = Split-Path -Parent $ToolsDir
$RepoRoot = Split-Path -Parent $ModRoot
. (Join-Path $ModRoot '_common.ps1')

$resolved = Resolve-GameDir -Explicit $GameDir -ModRoot $ModRoot
$layout   = Get-GameLayout -GameDir $resolved
$srcAsm   = Join-Path $layout.Managed 'Assembly-CSharp.dll'
if (-not (Test-Path -LiteralPath $srcAsm)) { throw "Assembly-CSharp.dll not found: $srcAsm" }
$libsAsm  = Join-Path $ModRoot 'libs\Assembly-CSharp.dll'

# ---------------------------------------------------------------- 1) drift check
Write-Step 'Drift check: game DLL vs mod\libs'
if (Test-Path -LiteralPath $libsAsm) {
    $h1 = (Get-FileHash -LiteralPath $srcAsm -Algorithm SHA256).Hash
    $h2 = (Get-FileHash -LiteralPath $libsAsm -Algorithm SHA256).Hash
    if ($h1 -eq $h2) { Write-Ok 'libs are identical to the game (nothing to do on the DLL front)' }
    else {
        Write-Warn2 "Assembly-CSharp.dll differs. game: $($h1.Substring(0,12))...  libs: $($h2.Substring(0,12))..."
        Write-Warn2 'A build (step 2) will refresh libs.'
    }
}
else { Write-Warn2 'libs\Assembly-CSharp.dll missing; step 2 will copy it' }

# ---------------------------------------------------------------- 2) build
if (-not $SkipBuild) {
    Write-Step 'Build (refreshes reference DLLs, compiles both loader variants)'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ModRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'build failed - fix the compile errors above (they are the renamed/changed fields)' }
}
else { Write-Warn2 'SkipBuild set; libs may be stale' }

# ---------------------------------------------------------------- 3) decompile
$decDir = Join-Path $RepoRoot 'decompiled\AssemblyCSharp'
if (-not $SkipDecompile) {
    Write-Step 'Decompile Assembly-CSharp (project mode)'
    $tmp = Join-Path $RepoRoot ('decompiled\_new_' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    & ilspycmd --disable-updatecheck -p -o $tmp $libsAsm | Out-Null
    $cs = @(Get-ChildItem $tmp -Filter *.cs -Recurse -ErrorAction SilentlyContinue)
    if ($cs.Count -eq 0) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue; throw 'decompile produced 0 .cs files' }
    Write-Ok ("decompiled: " + $cs.Count + ' .cs files')

    if (Test-Path $decDir) {
        $stamp = 'old-' + (Get-Date -Format 'MMdd-HHmm')
        $bak = Join-Path $RepoRoot ('decompiled\AssemblyCSharp.' + $stamp)
        Rename-Item $decDir $bak
        Write-Ok "previous decompile moved aside: $bak"
    }
    Move-Item $tmp $decDir
}

# ---------------------------------------------------------------- 4) types_all.txt
Write-Step 'Regenerate types_all.txt'
$kinds = @('c', 's', 'e', 'd', 'i')
$all = New-Object System.Collections.Generic.List[string]
foreach ($k in $kinds) {
    $r = & ilspycmd --disable-updatecheck -l $k $libsAsm 2>$null
    foreach ($x in $r) {
        $s = "$x".Trim()
        if ($s -match '^(Class|Struct|Enum|Delegate|Interface)\s+\S') { $all.Add($s) }
    }
}
$typesFile = Join-Path $RepoRoot 'types_all.txt'
$oldTypes = @()
if (Test-Path -LiteralPath $typesFile) { $oldTypes = @(Get-Content -LiteralPath $typesFile) }
$all | Sort-Object -Unique | Set-Content -LiteralPath $typesFile -Encoding UTF8
Write-Ok ("types_all.txt: " + $all.Count + ' entries')

# ---------------------------------------------------------------- 5) component_inventory.txt
Write-Step 'Regenerate component_inventory.txt'
$typeRx = [regex]'^\s*public\s+(?:sealed\s+|static\s+|partial\s+|readonly\s+)*(class|struct|enum)\s+([A-Za-z_][\w]*)\s*(?::\s*([^\{]+))?\s*$'
$fieldRx = [regex]'^\s*public\s+(?!class\b|struct\b|enum\b|interface\b|delegate\b|const\b|static\b|void\b|event\b|[\w\.]+\s+\w+\s*\()[\w\.<>\[\],]+\s+[A-Za-z_]\w*\s*;'
$gen = '<|\$|^__|IFE_'

$entries = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem $decDir -Filter *.cs -Recurse) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $m = $typeRx.Match($lines[$i])
        if (-not $m.Success) { continue }
        $name = $m.Groups[2].Value
        if ($name -match $gen) { continue }
        $bases = $m.Groups[3].Value
        $marker = 'Other'
        if ($m.Groups[1].Value -eq 'enum') { $marker = 'Other' }
        elseif ($bases -match 'IBufferElementData')          { $marker = 'IBufferElementData' }
        elseif ($bases -match 'IComponentData')              { $marker = 'IComponentData' }
        elseif ($bases -match '\bISystem\b')                 { $marker = 'ISystem' }
        elseif ($bases -match 'SystemBase|ComponentSystemBase') { $marker = 'SystemBase' }
        elseif ($bases -match 'MonoBehaviour')               { $marker = 'MonoBehaviour' }

        # brace-count to the end of the type body, collecting only fields that sit at the
        # type's own brace depth (nested structs and method bodies are deeper, so excluded)
        $fields = New-Object System.Collections.Generic.List[string]
        $depth = 0
        $inBody = $false
        $bodyDepth = 0
        for ($j = $i; $j -lt $lines.Count; $j++) {
            $ln = $lines[$j]
            $opens  = ([regex]::Matches($ln, '\{')).Count
            $closes = ([regex]::Matches($ln, '\}')).Count
            if (-not $inBody) {
                if ($opens -gt 0) { $inBody = $true; $depth = $opens - $closes; $bodyDepth = $depth }
                continue
            }
            if ($closes -gt 0 -and $opens -eq 0) {
                $depth -= $closes
                if ($depth -lt $bodyDepth) { break }
                continue
            }
            $depth += $opens - $closes
            if ($depth -lt $bodyDepth) { break }
            if ($depth -eq $bodyDepth -and $opens -eq 0) {
                $fm = $fieldRx.Match($ln)
                if ($fm.Success) {
                    $t = ($fm.Value.Trim() -replace ';$', '' -replace '^public\s+(?:readonly\s+|volatile\s+)?', '')
                    if ($t -notmatch '\s__') { $fields.Add($t) }
                }
            }
        }
        $entries.Add([pscustomobject]@{ Marker = $marker; Name = $name; Fields = $fields })
    }
}

$inv = New-Object System.Collections.Generic.List[string]
foreach ($e in ($entries | Sort-Object Name)) {
    $inv.Add(('[' + $e.Marker + '] ' + $e.Name + '  (fields=' + $e.Fields.Count + ')'))
    if ($e.Fields.Count -gt 0) { $inv.Add('    ' + ($e.Fields -join ' | ')) }
}
$invFile = Join-Path $RepoRoot 'component_inventory.txt'
$inv | Set-Content -LiteralPath $invFile -Encoding UTF8
Write-Ok ("component_inventory.txt: " + $entries.Count + ' types')

# ---------------------------------------------------------------- 6) diff vs previous
if ($oldTypes.Count -gt 0) {
    Write-Step 'Type diff vs previous types_all.txt'
    $noise = '<|\$BurstDirectCall|__codegen__|__JobReflection|__UnmanagedPostProcessor|\$PostfixBurstDelegate|IFE_\d+|^Class \$'
    $o = @($oldTypes | Where-Object { $_ -match '^(Class|Struct|Enum|Delegate|Interface)\s+\S' -and $_ -notmatch $noise } | Sort-Object -Unique)
    $n = @($all | Where-Object { $_ -notmatch $noise } | Sort-Object -Unique)
    $d = Compare-Object $o $n
    $added   = @($d | Where-Object { $_.SideIndicator -eq '=>' })
    $removed = @($d | Where-Object { $_.SideIndicator -eq '<=' })
    Write-Host ("   old: " + $o.Count + "   new: " + $n.Count + "   added: " + $added.Count + "   removed: " + $removed.Count)
    if ($removed.Count -gt 0) {
        Write-Warn2 'removed types (these can break the mod):'
        $removed | Select-Object -First 40 | ForEach-Object { Write-Host ('   - ' + $_.InputObject) }
    }
}

Write-Step 'Done. Remaining manual step: launch the game and sanity-check /state once.'
