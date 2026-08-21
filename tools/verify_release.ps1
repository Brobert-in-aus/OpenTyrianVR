param([switch]$AllowDirtyArtifacts)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$version = (Get-Content -LiteralPath (Join-Path $repo 'VERSION') -Raw).Trim()
$commit = (& git -C $repo rev-parse HEAD).Trim()
$status = @(& git -C $repo status --porcelain)
if ($status.Count -and !$AllowDirtyArtifacts) {
    throw 'Release verification requires a clean working tree.'
}

$artifactDir = Join-Path $repo 'artifacts'
$quest = Join-Path $artifactDir "OpenTyrianVR-$version-quest.apk"
$pcvr = Join-Path $artifactDir "OpenTyrianVR-$version-pcvr-win-x64.zip"

function Test-Checksum([string]$Path) {
    $checksumPath = "$Path.sha256"
    if (!(Test-Path -LiteralPath $Path) -or !(Test-Path -LiteralPath $checksumPath)) {
        throw "Release artifact or checksum missing: $Path"
    }
    $expected = ((Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+')[0]
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw "SHA-256 mismatch: $Path" }
    Write-Host "SHA-256 verified: $([IO.Path]::GetFileName($Path))"
}

Test-Checksum $quest
Test-Checksum $pcvr

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($pcvr)
try {
    $entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    $root = "OpenTyrianVR-$version-pcvr/"
    foreach ($required in @(
        "${root}OpenTyrianVR.exe",
        "${root}OpenTyrianVR.pck",
        "${root}native/win-x64/opentyrian-core-x64-Release.dll",
        "${root}native/win-x64/SDL2.dll",
        "${root}tyrian21/tyrian1.lvl",
        "${root}COPYING",
        "${root}THIRD_PARTY_NOTICES.md",
        "${root}PLAYTESTING.md",
        "${root}BUILD.txt"
    )) {
        if ($entries -notcontains $required) { throw "PCVR package entry missing: $required" }
    }
    $buildEntry = $zip.Entries | Where-Object {
        $_.FullName.Replace('\', '/') -eq "${root}BUILD.txt"
    } | Select-Object -First 1
    $reader = [IO.StreamReader]::new($buildEntry.Open())
    try { $pcBuild = $reader.ReadToEnd() } finally { $reader.Dispose() }
} finally {
    $zip.Dispose()
}

$questBuildPath = "$quest.build.txt"
if (!(Test-Path -LiteralPath $questBuildPath)) { throw "Quest build record missing: $questBuildPath" }
$questBuild = Get-Content -LiteralPath $questBuildPath -Raw
foreach ($record in @($pcBuild, $questBuild)) {
    if ($record -notmatch "(?m)^commit=$([regex]::Escape($commit))$") {
        throw 'Artifact commit does not match HEAD.'
    }
    if (!$AllowDirtyArtifacts -and $record -notmatch '(?m)^dirty=false$') {
        throw 'Release artifact was built from a dirty working tree.'
    }
}
if ($questBuild -notmatch '(?m)^signing=release:') {
    throw 'Quest APK was not produced with release signing.'
}

$sdk = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) {
    $env:ANDROID_HOME
} else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$buildTools = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory |
    Sort-Object Name -Descending | Select-Object -First 1
if (!$buildTools) { throw 'Android build-tools not found.' }
$aapt = Join-Path $buildTools.FullName 'aapt.exe'
$apksigner = Join-Path $buildTools.FullName 'apksigner.bat'
$badging = (& $aapt dump badging $quest | Select-Object -First 1) -join ''
if ($badging -notmatch "name='com\.brobert\.opentyrianvr'" -or
    $badging -notmatch "versionName='$([regex]::Escape($version))'") {
    throw "Unexpected Quest package identity: $badging"
}
& $apksigner verify --verbose $quest | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Quest APK signature verification failed.' }

$apk = [IO.Compression.ZipFile]::OpenRead($quest)
try {
    $apkEntries = @($apk.Entries | ForEach-Object FullName)
    foreach ($required in @('assets/COPYING', 'assets/THIRD_PARTY_NOTICES.md',
        'assets/tyrian21/tyrian1.lvl', 'lib/arm64-v8a/libopentyrian_core.so')) {
        if ($apkEntries -notcontains $required) { throw "Quest package entry missing: $required" }
    }
} finally {
    $apk.Dispose()
}

Write-Host "Release artifacts verified for OpenTyrianVR $version at $commit"
