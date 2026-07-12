# Stage B hover heights: first-pass classifier.
# Joins the static enemy data (captures\edat_dump.csv, from OTYR_DUMP_EDAT)
# with demo observations (captures\etype_observed.csv, from the harness sweep)
# and writes godot\hover_heights.json for the host.  The output is a FIRST
# PASS -- docs\HOVER_HEIGHTS.md describes the manual assignment workflow.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$edat = Import-Csv captures\edat_dump.csv | Where-Object { $_.episode -eq '1' }
$seen = @{}
if (Test-Path captures\etype_observed.csv) {
    Import-Csv captures\etype_observed.csv | ForEach-Object { $seen[$_.type] = $_ }
}

$types = [ordered]@{}
foreach ($e in $edat) {
    $class = $null
    if ($e.ground -eq '1') {
        $class = 'ground'          # explosiontype even: legacy ground unit
    } elseif ($e.armor -eq '0' -and [int]$e.value -gt 0) {
        $class = 'pickup'          # indestructible score item
    } elseif ([int]$e.xaccel -gt 0 -or [int]$e.yaccel -gt 0) {
        $class = 'air-low'         # player-seeker: near the player plane
    } elseif ($e.size -eq '1') {
        $class = 'air-high'        # 2x2 flyer: big silhouette, high band
    } else {
        $class = 'air-mid'
    }
    $entry = [ordered]@{ class = $class }
    if ($seen.ContainsKey($e.type)) {
        $o = $seen[$e.type]
        $entry.seen = "demo sheet=$($o.sheet) index=$($o.index0) ticks=$($o.ticks)"
    }
    $types[$e.type] = $entry
}

$out = [ordered]@{
    _comment = 'Stage B hover heights, first pass (episode 1). class: ground (rides the surface beneath, +offset), pickup, air-low, air-mid, air-high, or an explicit {height: <lane Z>}. Unlisted types keep the legacy category band. Edit freely; the host reloads at level start.'
    classes = [ordered]@{
        'ground'   = 0.004   # offset ABOVE the surface beneath (terrain or platform)
        'pickup'   = 0.040
        'air-low'  = 0.045
        'air-mid'  = 0.055
        'air-high' = 0.070
    }
    types = $types
}
$out | ConvertTo-Json -Depth 4 | Out-File godot\hover_heights.json -Encoding utf8
"wrote godot\hover_heights.json ($($types.Count) types, $($seen.Count) demo-observed)"
