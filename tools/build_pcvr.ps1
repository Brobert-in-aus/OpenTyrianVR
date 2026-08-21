param(
    [string]$Godot,
    [string]$DataSource,
    [string]$OutputDirectory,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$godotProject = Join-Path $repo 'godot'
$artifactRoot = Join-Path $repo 'artifacts'
$version = (Get-Content -LiteralPath (Join-Path $repo 'VERSION') -Raw).Trim()
if (!$OutputDirectory) { $OutputDirectory = "artifacts\OpenTyrianVR-$version-pcvr" }
$gitDirty = @(& git -C $repo status --porcelain).Count -gt 0
if ($gitDirty -and !$AllowDirty) {
    throw 'PCVR packaging requires a clean working tree. Use -AllowDirty only for local validation builds.'
}

if (!$Godot) {
    $bundled = Join-Path (Split-Path $repo -Parent) '_tools\godot'
    $Godot = Get-ChildItem $bundled -Recurse -Filter 'Godot*_console.exe' -ErrorAction SilentlyContinue |
        Where-Object Name -Match 'mono' |
        Select-Object -First 1 -ExpandProperty FullName
}
if (!$Godot -or !(Test-Path -LiteralPath $Godot)) {
    throw 'Godot 4.7 Mono console executable not found; pass -Godot.'
}

if (!$DataSource) { $DataSource = Join-Path $repo 'tyrian21' }
foreach ($required in @(
    (Join-Path $DataSource 'tyrian1.lvl'),
    (Join-Path $repo 'COPYING')
)) {
    if (!(Test-Path -LiteralPath $required)) { throw "PCVR build prerequisite missing: $required" }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw "Visual Studio locator not found: $vswhere" }
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
    Select-Object -First 1
if (!$msbuild) { throw 'MSBuild was not found.' }

& $msbuild (Join-Path $repo 'visualc\opentyrian.sln') /p:Configuration=Release /p:Platform=x64 /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Native Windows build failed ($LASTEXITCODE)" }
dotnet build (Join-Path $godotProject 'OpenTyrianVR.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Managed Windows build failed ($LASTEXITCODE)" }

$outputDir = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
}
$artifactPrefix = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'
if (!$outputDir.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must remain inside $artifactRoot"
}
if (Test-Path -LiteralPath $outputDir) {
    Remove-Item -LiteralPath $outputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $outputDir | Out-Null

$exportStdout = Join-Path $artifactRoot 'OpenTyrianVR.pcvr.export.stdout.log'
$exportStderr = Join-Path $artifactRoot 'OpenTyrianVR.pcvr.export.stderr.log'
Remove-Item -LiteralPath $exportStdout, $exportStderr -Force -ErrorAction SilentlyContinue
$exe = Join-Path $outputDir 'OpenTyrianVR.exe'
$export = Start-Process -FilePath $Godot -WindowStyle Hidden -PassThru `
    -ArgumentList @('--headless', '--path', "`"$godotProject`"", '--export-release',
        '"Windows Desktop"', "`"$exe`"") `
    -RedirectStandardOutput $exportStdout -RedirectStandardError $exportStderr
if (!$export.WaitForExit(300000)) {
    Stop-Process -Id $export.Id -Force -ErrorAction SilentlyContinue
    throw "Godot Windows export timed out; see $exportStdout and $exportStderr"
}
$export.WaitForExit()
$export.Refresh()
$exportExitCode = $export.ExitCode
if (![string]::IsNullOrEmpty("$exportExitCode") -and $exportExitCode -ne 0) {
    throw "Godot Windows export failed ($($export.ExitCode)); see $exportStdout and $exportStderr"
}
if (!(Test-Path -LiteralPath $exe)) { throw "Godot did not produce $exe" }

$nativeDir = Join-Path $outputDir 'native\win-x64'
New-Item -ItemType Directory -Path $nativeDir | Out-Null
foreach ($native in @('opentyrian-core-x64-Release.dll', 'SDL2.dll', 'SDL2_net.dll')) {
    $source = Join-Path $repo $native
    if (!(Test-Path -LiteralPath $source)) { throw "Native runtime missing after build: $source" }
    Copy-Item -LiteralPath $source -Destination $nativeDir
}
Copy-Item -LiteralPath $DataSource -Destination (Join-Path $outputDir 'tyrian21') -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'COPYING') -Destination $outputDir
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $outputDir
Copy-Item -LiteralPath (Join-Path $repo 'PLAYTESTING.md') -Destination $outputDir
Copy-Item -LiteralPath (Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination $outputDir
$commit = (& git -C $repo rev-parse HEAD).Trim()
[IO.File]::WriteAllText((Join-Path $outputDir 'BUILD.txt'),
    "OpenTyrianVR $version`ncommit=$commit`ndirty=$($gitDirty.ToString().ToLowerInvariant())`nbuilt_utc=$([DateTime]::UtcNow.ToString('o'))`n",
    [Text.UTF8Encoding]::new($false))

$zip = Join-Path $artifactRoot "OpenTyrianVR-$version-pcvr-win-x64.zip"
$checksum = "$zip.sha256"
Remove-Item -LiteralPath $zip, $checksum -Force -ErrorAction SilentlyContinue
Compress-Archive -LiteralPath $outputDir -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($checksum, "$hash  $([IO.Path]::GetFileName($zip))`n", [Text.UTF8Encoding]::new($false))

Write-Host "PCVR package ready: $zip"
Write-Host "SHA-256: $hash"
