# Height-editor launcher.
#   tools\editor.ps1                 -> title screen (pick anything manually)
#   tools\editor.ps1 -Section 6      -> boot straight into script section 6
#   tools\editor.ps1 -Section 11 -Episode 2
#   tools\editor.ps1 -ListSections   -> print the episode's section targets
# Episode 1 sections: 4 TYRIAN, 6 ASTEROID1, 7 ASTEROID2, 11 SAVARA,
# 14 MINES, 17 BUBBLES, 20 DELIANI, 22 ASTEROID?, 24 MINEMAZE, 26 BONUS,
# 29 HOLES, 30 SAVARA, 32 SOH JIN, 34 WINDY, 37 ASSASSIN, 39 SAVARA V,
# 42 ** ALE **
param(
    [int]$Section = 0,
    [int]$Episode = 1,
    [switch]$ListSections
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

if ($ListSections) {
    $env:OTYR_DUMP_SECTIONS = '1'
    $env:OTYR_START_EPISODE = "$Episode"
    $env:OTYR_START_SECTION = '9999'  # forces episode init, then falls back
    $p = Start-Process -FilePath "$repo\opentyrian-x64-Release.exe" `
        -ArgumentList "--data=tyrian21","--no-sound" -WindowStyle Minimized -PassThru `
        -RedirectStandardOutput "$env:TEMP\otyr_sections.log"
    Start-Sleep 8
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Get-Content "$env:TEMP\otyr_sections.log" | Select-String "section|episode"
    exit
}

$env:OTYR_FLAT = '1'
$env:OTYR_HEIGHT_EDITOR = '1'
$env:OTYR_INVULN = '1'
$env:OTYR_LINEAR = '1'   # completing a level advances to the NEXT level in
                         # script order (bonuses/secrets included), no
                         # pickups or difficulty branches needed
if ($Section -gt 0) {
    $env:OTYR_START_SECTION = "$Section"
    $env:OTYR_START_EPISODE = "$Episode"
} else {
    Remove-Item Env:OTYR_START_SECTION -ErrorAction SilentlyContinue
    Remove-Item Env:OTYR_START_EPISODE -ErrorAction SilentlyContinue
}
& "D:\Projects\CrimsonVR\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe" `
    --path "$repo\godot" --xr-mode off
