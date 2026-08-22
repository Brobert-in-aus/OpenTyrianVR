param(
    [string]$Apk,
    [string]$Device
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$version = (Get-Content -LiteralPath (Join-Path $repo 'VERSION') -Raw).Trim()
if (!$Apk) { $Apk = "artifacts\OpenTyrianVR-$version-quest.apk" }
$sdk = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$adb = Join-Path $sdk 'platform-tools\adb.exe'
$buildTools = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory |
    Sort-Object Name -Descending | Select-Object -First 1
$aapt = if ($buildTools) { Join-Path $buildTools.FullName 'aapt.exe' } else { $null }

if (!(Test-Path $adb)) { throw "ADB not found: $adb" }
if (!$aapt -or !(Test-Path $aapt)) { throw 'Android aapt.exe not found.' }

$apkPath = if ([IO.Path]::IsPathRooted($Apk)) { $Apk } else { Join-Path $repo $Apk }
if (!(Test-Path $apkPath)) { throw "Quest APK not found: $apkPath" }
$apkPath = (Resolve-Path $apkPath).Path

$badgingOutput = & $aapt dump badging $apkPath
$badgingExit = $LASTEXITCODE
if ($badgingExit -ne 0) { throw "Cannot read APK identity: $apkPath" }
$badging = ($badgingOutput | Select-Object -First 1) -join ''
if ($badging -notmatch "name='com\.brobert\.opentyrianvr'") {
    throw "Refusing to install unexpected package: $badging"
}
$versionCode = if ($badging -match "versionCode='([^']+)'") { $Matches[1] } else { '?' }
$versionName = if ($badging -match "versionName='([^']+)'") { $Matches[1] } else { '?' }

if (!$Device) {
    $connected = @(& $adb devices | Select-Object -Skip 1 |
        Where-Object { $_ -match '^\S+\s+device$' } |
        ForEach-Object { ($_ -split '\s+')[0] })
    if ($connected.Count -ne 1) {
        throw "Expected exactly one connected ADB device; found $($connected.Count). Pass -Device explicitly."
    }
    $Device = $connected[0]
}

& $adb -s $Device get-state | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ADB device is unavailable: $Device" }
& $adb -s $Device install -r $apkPath
if ($LASTEXITCODE -ne 0) { throw "Quest install failed ($LASTEXITCODE)" }

$installed = & $adb -s $Device shell dumpsys package com.brobert.opentyrianvr |
    Select-String -Pattern 'versionCode=|versionName=' | Select-Object -First 2
Write-Host "Installed OpenTyrianVR $versionName (code $versionCode) on $Device."
$installed | ForEach-Object { Write-Host $_.Line.Trim() }
Write-Host 'The app was not launched.'
