param(
    [string]$Godot,
    [int]$Frames = 140,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$godotProject = Join-Path $repo 'godot'

if (!$Godot) {
    $bundled = Join-Path (Split-Path $repo -Parent) '_tools\godot'
    $Godot = Get-ChildItem $bundled -Recurse -Filter 'Godot*_console.exe' -ErrorAction SilentlyContinue |
        Where-Object Name -Match 'mono' |
        Select-Object -First 1 -ExpandProperty FullName
}
if (!$Godot -or !(Test-Path -LiteralPath $Godot)) {
    throw 'Godot 4.7 Mono console executable not found; pass -Godot.'
}
if ($Frames -lt 80) { throw '-Frames must be at least 80.' }

if (!$SkipBuild) {
    $msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
        -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe
    if (!$msbuild) { throw 'MSBuild not found.' }
    & $msbuild (Join-Path $repo 'visualc\opentyrian.sln') /p:Configuration=Release /p:Platform=x64 /m
    if ($LASTEXITCODE -ne 0) { throw "Native Release build failed ($LASTEXITCODE)." }

    $env:OTYR_MUTE = '1'
    $env:SDL_AUDIODRIVER = 'dummy'
    & dotnet build (Join-Path $godotProject 'OpenTyrianVR.csproj') --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Managed build failed ($LASTEXITCODE)." }
}

& python (Join-Path $repo 'tools\audit_phase4_coverage.py')
if ($LASTEXITCODE -ne 0) { throw 'Phase 4 coverage audit failed.' }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runRoot = Join-Path $repo "artifacts\presentation-regression-$stamp"
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

$saved = @{}
$envNames = @('OTYR_MUTE','SDL_AUDIODRIVER','OTYR_FLAT','OTYR_TOPDOWN','OTYR_PLAY_DEMO',
              'OTYR_TEST_FRAMES','OTYR_CAPTURE_AT','OTYR_CAPTURE_DIR','OTYR_FORCE_FLIP',
              'OTYR_FORCE_SMOOTHIE','OTYR_FORCE_SPECIAL_CODE','OTYR_START_EPISODE',
              'OTYR_START_SECTION')
foreach ($name in $envNames) {
    $item = Get-Item "Env:$name" -ErrorAction SilentlyContinue
    $saved[$name] = if ($null -ne $item) { $item.Value } else { $null }
}

function Invoke-PresentationCase {
    param(
        [string]$Name,
        [string]$CaptureAt,
        [string]$Smoothie = '',
        [string]$SpecialCode = '',
        [int]$StartEpisode = 0,
        [int]$StartSection = 0,
        [int]$CaseFrames = 0,
        [switch]$ForceFlip
    )
    $caseDir = Join-Path $runRoot $Name
    New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
    $stdout = Join-Path $caseDir 'stdout.log'
    $stderr = Join-Path $caseDir 'stderr.log'

    $env:OTYR_MUTE = '1'
    $env:SDL_AUDIODRIVER = 'dummy'
    $env:OTYR_FLAT = '1'
    $env:OTYR_TOPDOWN = '1'
    $env:OTYR_PLAY_DEMO = '1'
    $env:OTYR_TEST_FRAMES = if ($CaseFrames -gt 0) { "$CaseFrames" } else { "$Frames" }
    $env:OTYR_CAPTURE_AT = $CaptureAt
    $env:OTYR_CAPTURE_DIR = $caseDir
    if ($ForceFlip) { $env:OTYR_FORCE_FLIP = '1' } else { Remove-Item Env:OTYR_FORCE_FLIP -ErrorAction SilentlyContinue }
    if ($Smoothie) { $env:OTYR_FORCE_SMOOTHIE = $Smoothie } else { Remove-Item Env:OTYR_FORCE_SMOOTHIE -ErrorAction SilentlyContinue }
    if ($SpecialCode) { $env:OTYR_FORCE_SPECIAL_CODE = $SpecialCode } else { Remove-Item Env:OTYR_FORCE_SPECIAL_CODE -ErrorAction SilentlyContinue }
    if ($StartEpisode -gt 0) { $env:OTYR_START_EPISODE = "$StartEpisode" } else { Remove-Item Env:OTYR_START_EPISODE -ErrorAction SilentlyContinue }
    if ($StartSection -gt 0) { $env:OTYR_START_SECTION = "$StartSection" } else { Remove-Item Env:OTYR_START_SECTION -ErrorAction SilentlyContinue }

    $process = Start-Process -FilePath $Godot -PassThru `
        -ArgumentList @('--path', $godotProject, '--xr-mode', 'off', '--audio-driver', 'Dummy') `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = (Get-Date).AddSeconds(45)
    while (!$process.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    }
    if (!$process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "$Name presentation run timed out; see $stdout and $stderr"
    }
    $process.WaitForExit()
    # Windows PowerShell 5 can leave ExitCode null for Start-Process handles
    # with both streams redirected even after WaitForExit. The durable clean
    # exit gate below is the application's REGRESSION summary; still reject a
    # concrete nonzero code when PowerShell supplies one.
    if ($null -ne $process.ExitCode -and $process.ExitCode -ne 0) {
        throw "$Name presentation run failed ($($process.ExitCode)); see $stdout and $stderr"
    }
    $log = Get-Content -Raw $stdout
    if ($log -notmatch 'OpenTyrianVR: REGRESSION') { throw "$Name emitted no regression summary." }
    if ($log -match '(?im)^ERROR:|SCRIPT ERROR|Unhandled exception|shader.*error') {
        throw "$Name logged a runtime/shader error; see $stdout"
    }
    return @{ Directory = $caseDir; Log = $log }
}

try {
    $hybrid = Invoke-PresentationCase -Name 'hybrid' -CaptureAt '40,80,120' -ForceFlip
    if ($hybrid.Log -notmatch 'REGRESSION .*hybrid=1') { throw 'Hybrid run never entered hybrid 3D.' }
    if ($hybrid.Log -notmatch 'max_cast_shadows=([1-9][0-9]*)') { throw 'Hybrid run produced no cast shadows.' }
    if ($hybrid.Log -notmatch 'max_map_cast_shadows=([1-9][0-9]*)') { throw 'Hybrid run produced no elevated-map shadows.' }
    if ($hybrid.Log -notmatch 'max_receiver_layers=2') { throw 'Hybrid run did not expose both elevated receiver layers.' }
    if ($hybrid.Log -notmatch 'REGRESSION .*flip=1') { throw 'Hybrid run did not exercise the native card flip.' }

    $effects = Invoke-PresentationCase -Name 'native-effects' -CaptureAt '80' `
        -Smoothie '1,2,3,4,5' -SpecialCode '2'
    if ($effects.Log -notmatch 'REGRESSION .*hybrid=1.*storm=1.*effects=0x3F') {
        throw 'Native-effects run did not expose all six effects while retaining hybrid 3D.'
    }

    # At frame 80 the level-start "Good luck" card can still be active, before
    # the player (and therefore the searchlight cone) has entered. Capture once
    # gameplay is established so this checks the rendered darkness effect.
    $darkness = Invoke-PresentationCase -Name 'darkness' -CaptureAt '120' -SpecialCode '2'
    if ($darkness.Log -notmatch 'REGRESSION .*hybrid=1.*effects=0x20') {
        throw 'Darkness run did not retain hybrid 3D with the searchlight enabled.'
    }

    # Water used to switch native drawing to VGAScreen2 even though hybrid
    # mode skips the legacy filter.  The host then published the untouched
    # black primary framebuffer as its UI plane (SAVARA V regression).
    $water = Invoke-PresentationCase -Name 'water' -CaptureAt '80' -Smoothie '2'
    if ($water.Log -notmatch 'REGRESSION .*hybrid=1.*storm=1.*effects=0x02') {
        throw 'Water run did not retain hybrid 3D with the storm renderer enabled.'
    }

    $fallback = Invoke-PresentationCase -Name 'legacy-fallback' -CaptureAt '80' -SpecialCode '3'
    if ($fallback.Log -notmatch 'REGRESSION .*legacy=1') { throw 'Fallback run never entered complete legacy presentation.' }
    if ($fallback.Log -notmatch 'presentation -> complete legacy fallback') {
        throw 'Fallback transition was not logged.'
    }

    # TIME WAR uses a fully opaque layer-1 cover drawn after both ground enemy
    # bands. The hybrid renderer must preserve that native paint-order
    # occlusion instead of resurrecting covered structures in 3D.
    $opaqueCover = Invoke-PresentationCase -Name 'opaque-cover' -CaptureAt '350' `
        -StartEpisode 4 -StartSection 41 -CaseFrames 360
    if ($opaqueCover.Log -notmatch 'REGRESSION .*hybrid=1') {
        throw 'Opaque-cover run did not retain hybrid presentation.'
    }

    Add-Type -AssemblyName System.Drawing
    $captures = Get-ChildItem $runRoot -Recurse -Filter 'cap_at_*.png'
    if ($captures.Count -ne 8) { throw "Expected 8 captures, found $($captures.Count)." }
    foreach ($capture in $captures) {
        if ($capture.Length -lt 4096) { throw "Capture is unexpectedly small: $($capture.FullName)" }
        $image = [System.Drawing.Image]::FromFile($capture.FullName)
        try {
            if ($image.Width -lt 640 -or $image.Height -lt 360) {
                throw "Capture has unexpected dimensions: $($capture.FullName) ($($image.Width)x$($image.Height))"
            }
        } finally { $image.Dispose() }
    }

    # Guard the failure that motivated the dedicated darkness mask: the old
    # screen-copy quad captured before transparent terrain and produced an
    # almost entirely black playfield. Sample the left 80% (excluding HUD)
    # and require substantial visible scene content.
    $darkCapture = Get-ChildItem $darkness.Directory -Filter 'cap_at_*.png' | Select-Object -First 1
    $bitmap = [System.Drawing.Bitmap]::FromFile($darkCapture.FullName)
    try {
        $visible = 0
        $samples = 0
        for ($y = 0; $y -lt [int]($bitmap.Height * 0.90); $y += 8) {
            for ($x = 0; $x -lt [int]($bitmap.Width * 0.80); $x += 8) {
                $pixel = $bitmap.GetPixel($x, $y)
                if (($pixel.R + $pixel.G + $pixel.B) -gt 45) { ++$visible }
                ++$samples
            }
        }
        if ($visible -lt [int]($samples * 0.10)) {
            throw "Darkness capture is effectively black ($visible/$samples visible samples)."
        }
    } finally { $bitmap.Dispose() }

    # The water-only case must contain visible playfield content.  This is
    # deliberately separate from the combined-effects smoke test, whose
    # screen-copy filters exercise a different compositing path.
    $waterCapture = Get-ChildItem $water.Directory -Filter 'cap_at_*.png' | Select-Object -First 1
    $bitmap = [System.Drawing.Bitmap]::FromFile($waterCapture.FullName)
    try {
        $visible = 0
        $samples = 0
        for ($y = 0; $y -lt [int]($bitmap.Height * 0.90); $y += 8) {
            for ($x = 0; $x -lt [int]($bitmap.Width * 0.80); $x += 8) {
                $pixel = $bitmap.GetPixel($x, $y)
                if (($pixel.R + $pixel.G + $pixel.B) -gt 45) { ++$visible }
                ++$samples
            }
        }
        if ($visible -lt [int]($samples * 0.10)) {
            throw "Water capture is effectively black ($visible/$samples visible samples)."
        }
    } finally { $bitmap.Dispose() }

    Write-Host "Presentation regression PASS: $runRoot"
    Select-String -Path (Join-Path $hybrid.Directory 'stdout.log'),(Join-Path $effects.Directory 'stdout.log'),(Join-Path $darkness.Directory 'stdout.log'),(Join-Path $water.Directory 'stdout.log'),(Join-Path $fallback.Directory 'stdout.log'),(Join-Path $opaqueCover.Directory 'stdout.log') `
        -Pattern 'OpenTyrianVR: REGRESSION|presentation ->|PERF ' | ForEach-Object Line
} finally {
    foreach ($name in $envNames) {
        if ($null -eq $saved[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:$name" $saved[$name] }
    }
}
