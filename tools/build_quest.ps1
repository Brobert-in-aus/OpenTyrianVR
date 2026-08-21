param(
    [string]$Godot,
    [string]$DataSource,
    [string]$VendorSource,
    [string]$Output,
    [switch]$ReleaseSigning,
    [string]$Keystore = $env:OTYR_ANDROID_KEYSTORE,
    [string]$KeyAlias = $env:OTYR_ANDROID_KEY_ALIAS,
    [string]$CredentialFile
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$godotProject = Join-Path $repo 'godot'
$version = (Get-Content -LiteralPath (Join-Path $repo 'VERSION') -Raw).Trim()
if (!$Output) { $Output = "artifacts\OpenTyrianVR-$version-quest.apk" }
$releasePassword = $env:OTYR_ANDROID_KEYSTORE_PASSWORD
if (!$CredentialFile) {
    $CredentialFile = Join-Path $env:USERPROFILE '.android\OpenTyrianVR-release-signing.xml'
}
if ($ReleaseSigning) {
    if (!$Keystore) { $Keystore = Join-Path $env:USERPROFILE '.android\OpenTyrianVR-release.jks' }
    if (((!$KeyAlias) -or (!$releasePassword)) -and (Test-Path -LiteralPath $CredentialFile)) {
        $storedSigningCredential = Import-Clixml -LiteralPath $CredentialFile
        if (!$KeyAlias) { $KeyAlias = $storedSigningCredential.UserName }
        if (!$releasePassword) {
            $releasePassword = $storedSigningCredential.GetNetworkCredential().Password
        }
    }
}
$gitDirty = @(& git -C $repo status --porcelain).Count -gt 0
$sdk = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$buildTools = Join-Path $sdk 'build-tools\35.0.0'

if (!$Godot) {
    $bundled = Join-Path (Split-Path $repo -Parent) '_tools\godot'
    $Godot = Get-ChildItem $bundled -Recurse -Filter 'Godot*_console.exe' -ErrorAction SilentlyContinue |
        Where-Object Name -Match 'mono' |
        Select-Object -First 1 -ExpandProperty FullName
}
if (!$Godot -or !(Test-Path $Godot)) { throw 'Godot 4.7 Mono console executable not found; pass -Godot.' }

if (!$DataSource) { $DataSource = Join-Path $repo 'tyrian21' }
if (!$VendorSource) { $VendorSource = Join-Path (Split-Path $repo -Parent) 'crimson\crimson-vr\godot\addons\godotopenxrvendors' }

$signingKeystore = if ($ReleaseSigning) { $Keystore } else { Join-Path $env:USERPROFILE '.android\debug.keystore' }
if ($ReleaseSigning) {
    if (!$signingKeystore -or !$KeyAlias -or !$releasePassword) {
        throw 'Release signing requires a keystore, key alias, and password via the local DPAPI credential or OTYR_ANDROID_* settings.'
    }
    if ($env:OTYR_ANDROID_KEY_PASSWORD -and
        $env:OTYR_ANDROID_KEY_PASSWORD -ne $releasePassword) {
        throw 'Godot Android release export requires the key and keystore passwords to match.'
    }
    if ($gitDirty) {
        throw 'Release signing requires a clean tracked working tree so BUILD metadata identifies the exact source.'
    }
}

$dataTarget = Join-Path $godotProject 'tyrian21'
$vendorTarget = Join-Path $godotProject 'addons\godotopenxrvendors'
$androidTemplate = Join-Path $godotProject 'android\build\build.gradle'
foreach ($required in @(
    (Join-Path $DataSource 'tyrian1.lvl'),
    (Join-Path $VendorSource 'plugin.gdextension'),
    $androidTemplate,
    (Join-Path $buildTools 'zipalign.exe'),
    (Join-Path $buildTools 'apksigner.bat'),
    $signingKeystore
)) {
    if (!(Test-Path $required)) { throw "Quest build prerequisite missing: $required" }
}

New-Item -ItemType Directory -Force -Path $dataTarget, $vendorTarget | Out-Null
Copy-Item -Path (Join-Path $DataSource '*') -Destination $dataTarget -Recurse -Force
Copy-Item -Path (Join-Path $VendorSource '*') -Destination $vendorTarget -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repo 'COPYING') -Destination (Join-Path $godotProject 'COPYING') -Force
Copy-Item -LiteralPath (Join-Path $repo 'THIRD_PARTY_NOTICES.md') `
    -Destination (Join-Path $godotProject 'THIRD_PARTY_NOTICES.md') -Force
Copy-Item -LiteralPath (Join-Path $repo 'android\GodotApp.java') `
    -Destination (Join-Path $godotProject 'android\build\src\main\java\com\godot\game\GodotApp.java') -Force
$sdlJavaSource = Join-Path $repo 'deps\SDL2-source-2.32.10\android-project\app\src\main\java\org\libsdl\app\SDLAudioManager.java'
$sdlJavaTarget = Join-Path $godotProject 'android\build\src\main\java\org\libsdl\app'
New-Item -ItemType Directory -Force -Path $sdlJavaTarget | Out-Null
$sdlAudioJava = [IO.File]::ReadAllText($sdlJavaSource)
$sdlAudioFixes = [ordered]@{
    'addAudioDevice(deviceInfo.isSink(),' = 'addAudioDevice(!deviceInfo.isSink(),'
    'removeAudioDevice(deviceInfo.isSink(),' = 'removeAudioDevice(!deviceInfo.isSink(),'
    'mAudioRecord.setPreferredDevice(getOutputAudioDeviceInfo(deviceId))' = 'mAudioRecord.setPreferredDevice(getInputAudioDeviceInfo(deviceId))'
    'mAudioTrack.setPreferredDevice(getInputAudioDeviceInfo(deviceId))' = 'mAudioTrack.setPreferredDevice(getOutputAudioDeviceInfo(deviceId))'
}
foreach ($old in $sdlAudioFixes.Keys) {
    if (!$sdlAudioJava.Contains($old)) { throw "SDL audio routing source changed; missing expected text: $old" }
    $sdlAudioJava = $sdlAudioJava.Replace($old, $sdlAudioFixes[$old])
}
[IO.File]::WriteAllText((Join-Path $sdlJavaTarget 'SDLAudioManager.java'), $sdlAudioJava,
    [Text.UTF8Encoding]::new($false))

& (Join-Path $PSScriptRoot 'build_android_native.ps1')
if ($LASTEXITCODE -ne 0) { throw "Android native build failed ($LASTEXITCODE)" }

foreach ($variant in @('debug', 'release')) {
    $gradleLibs = Join-Path $godotProject "android\build\libs\$variant\arm64-v8a"
    New-Item -ItemType Directory -Force -Path $gradleLibs | Out-Null
    Copy-Item (Join-Path $godotProject 'native\android-arm64\libopentyrian_core.so') $gradleLibs -Force
    Copy-Item (Join-Path $godotProject 'native\android-arm64\libSDL2.so') $gradleLibs -Force
}

$artifactDir = Join-Path $repo 'artifacts'
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
$final = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $repo $Output }
$artifactStem = [IO.Path]::GetFileNameWithoutExtension($final)
$raw = Join-Path $artifactDir "$artifactStem.raw.apk"
$aligned = Join-Path $artifactDir "$artifactStem.aligned.apk"
$exportStdout = Join-Path $artifactDir "$artifactStem.export.stdout.log"
$exportStderr = Join-Path $artifactDir "$artifactStem.export.stderr.log"

Add-Type -AssemblyName System.IO.Compression.FileSystem
function Test-CompleteGodotApk([string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) { return $false }
    $zip = $null
    try {
        $zip = [IO.Compression.ZipFile]::OpenRead($Path)
        return @($zip.Entries | ForEach-Object FullName) -contains `
            'assets/.godot/mono/publish/arm64/OpenTyrianVR.dll'
    } catch {
        return $false
    } finally {
        if ($null -ne $zip) { $zip.Dispose() }
    }
}

# Godot can initialize audio even during command-line tooling. Keep all build/export
# invocations muted; this script never launches or installs the game.
$previousMute = $env:OTYR_MUTE
$previousGodotReleasePath = $env:GODOT_ANDROID_KEYSTORE_RELEASE_PATH
$previousGodotReleaseUser = $env:GODOT_ANDROID_KEYSTORE_RELEASE_USER
$previousGodotReleasePassword = $env:GODOT_ANDROID_KEYSTORE_RELEASE_PASSWORD
$env:OTYR_MUTE = '1'
if ($ReleaseSigning) {
    $env:OTYR_ANDROID_KEYSTORE_PASSWORD = $releasePassword
    $env:GODOT_ANDROID_KEYSTORE_RELEASE_PATH = $signingKeystore
    $env:GODOT_ANDROID_KEYSTORE_RELEASE_USER = $KeyAlias
    $env:GODOT_ANDROID_KEYSTORE_RELEASE_PASSWORD = $releasePassword
}
try {
    Remove-Item -LiteralPath $raw -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $exportStdout, $exportStderr -Force -ErrorAction SilentlyContinue
    $exportMode = if ($ReleaseSigning) { '--export-release' } else { '--export-debug' }
    $export = Start-Process -FilePath $Godot -WindowStyle Hidden -PassThru `
        -ArgumentList @('--headless', '--path', $godotProject, $exportMode, 'Android', $raw) `
        -RedirectStandardOutput $exportStdout -RedirectStandardError $exportStderr
    $deadline = (Get-Date).AddMinutes(5)
    $archiveReady = $false
    while (!$export.HasExited -and (Get-Date) -lt $deadline) {
        if (Test-CompleteGodotApk $raw) {
            # Godot 4.7 can remain alive after Gradle has returned and closed a
            # complete APK. Once the ZIP central directory and managed payload
            # are readable, only exporter shutdown remains; do not let that
            # wedge unattended Quest builds indefinitely.
            $archiveReady = $true
            Stop-Process -Id $export.Id -Force
            $export.WaitForExit()
            Write-Host 'Godot exporter remained alive after completing the APK; stopped its idle process.'
            break
        }
        Start-Sleep -Milliseconds 500
        $export.Refresh()
    }
    if (!$export.HasExited) {
        Stop-Process -Id $export.Id -Force -ErrorAction SilentlyContinue
        throw "Godot Android export timed out; see $exportStdout and $exportStderr"
    }
    if (!$archiveReady -and $export.ExitCode -ne 0) {
        throw "Godot Android export failed ($($export.ExitCode)); see $exportStdout and $exportStderr"
    }
} finally {
    $env:OTYR_MUTE = $previousMute
    $env:GODOT_ANDROID_KEYSTORE_RELEASE_PATH = $previousGodotReleasePath
    $env:GODOT_ANDROID_KEYSTORE_RELEASE_USER = $previousGodotReleaseUser
    $env:GODOT_ANDROID_KEYSTORE_RELEASE_PASSWORD = $previousGodotReleasePassword
}
if (!(Test-Path $raw)) { throw "Godot did not produce $raw" }

& (Join-Path $buildTools 'zipalign.exe') -P 16 -f 4 $raw $aligned
if ($LASTEXITCODE -ne 0) { throw "zipalign failed ($LASTEXITCODE)" }

New-Item -ItemType Directory -Force -Path (Split-Path $final -Parent) | Out-Null
$signArgs = @('sign', '--ks', $signingKeystore)
if ($ReleaseSigning) {
    $signArgs += @('--ks-pass', 'env:OTYR_ANDROID_KEYSTORE_PASSWORD', '--ks-key-alias', $KeyAlias)
    $signArgs += @('--key-pass', 'env:OTYR_ANDROID_KEYSTORE_PASSWORD')
} else {
    $signArgs += @('--ks-pass', 'pass:android', '--ks-key-alias', 'androiddebugkey', '--key-pass', 'pass:android')
}
$signArgs += @('--v1-signing-enabled', 'false', '--v2-signing-enabled', 'true',
    '--v3-signing-enabled', 'true', '--out', $final, $aligned)
& (Join-Path $buildTools 'apksigner.bat') @signArgs
if ($LASTEXITCODE -ne 0) { throw "APK signing failed ($LASTEXITCODE)" }

& (Join-Path $buildTools 'zipalign.exe') -c -P 16 4 $final
if ($LASTEXITCODE -ne 0) { throw "APK alignment verification failed ($LASTEXITCODE)" }
& (Join-Path $buildTools 'apksigner.bat') verify --verbose $final
if ($LASTEXITCODE -ne 0) { throw "APK signature verification failed ($LASTEXITCODE)" }

$archive = [IO.Compression.ZipFile]::OpenRead($final)
try {
    $entries = @($archive.Entries | ForEach-Object FullName)
    $requiredEntries = @(
        'assets/.godot/mono/publish/arm64/OpenTyrianVR.dll',
        'lib/arm64-v8a/libopentyrian_core.so',
        'lib/arm64-v8a/libSDL2.so',
        'lib/arm64-v8a/libopenxr_loader.so',
        'lib/arm64-v8a/libgodotopenxrvendors.so',
        'assets/tyrian21/tyrian1.lvl',
        'assets/COPYING',
        'assets/THIRD_PARTY_NOTICES.md'
    )
    foreach ($entry in $requiredEntries) {
        if ($entries -notcontains $entry) { throw "Quest APK payload missing: $entry" }
    }
} finally {
    $archive.Dispose()
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $final).Hash.ToLowerInvariant()
$checksum = "$final.sha256"
[IO.File]::WriteAllText($checksum, "$hash  $([IO.Path]::GetFileName($final))`n", [Text.UTF8Encoding]::new($false))
$commit = (& git -C $repo rev-parse HEAD).Trim()
$signingKind = if ($ReleaseSigning) { "release:$KeyAlias" } else { 'debug' }
$buildInfo = @(
    "OpenTyrianVR $version (code 28)",
    "commit=$commit",
    "dirty=$($gitDirty.ToString().ToLowerInvariant())",
    "sha256=$hash",
    "signing=$signingKind",
    "built_utc=$([DateTime]::UtcNow.ToString('o'))"
) -join "`n"
[IO.File]::WriteAllText("$final.build.txt", "$buildInfo`n", [Text.UTF8Encoding]::new($false))

Write-Host "Quest APK ready: $final"
Write-Host "SHA-256: $hash"
