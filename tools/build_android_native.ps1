param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$NdkVersion = '27.0.12077973',
    [int]$AndroidApi = 32
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$sdk = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$ndk = Join-Path $sdk "ndk\$NdkVersion"
$toolchain = Join-Path $ndk 'build\cmake\android.toolchain.cmake'
$cmake = Join-Path $sdk 'cmake\3.22.1\bin\cmake.exe'
$ninja = Join-Path $sdk 'cmake\3.22.1\bin\ninja.exe'
if (!(Test-Path $cmake)) {
    $vsCmakeRoot = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake'
    $cmake = Join-Path $vsCmakeRoot 'CMake\bin\cmake.exe'
    $ninja = Join-Path $vsCmakeRoot 'Ninja\ninja.exe'
}
if (!(Test-Path $cmake)) { $cmake = (Get-Command cmake -ErrorAction Stop).Source }
if (!(Test-Path $ninja)) { $ninja = (Get-Command ninja -ErrorAction Stop).Source }

$sdl = Join-Path $repo 'deps\SDL2-source-2.32.10'
$build = Join-Path $repo "obj\android-arm64\$Configuration"
$stage = Join-Path $repo 'godot\native\android-arm64'
$cmakeToolchain = $toolchain.Replace('\', '/')
$cmakeNinja = $ninja.Replace('\', '/')
$cmakeSdl = $sdl.Replace('\', '/')

foreach ($required in @($toolchain, (Join-Path $sdl 'CMakeLists.txt'))) {
    if (!(Test-Path $required)) { throw "Android native prerequisite missing: $required" }
}

$sdlVmMarker = 'SDL_AndroidSetJavaVMForForeignActivity'
$sdlAndroid = Join-Path $sdl 'src\core\android\SDL_android.c'
$sdlPatch = Join-Path $repo 'android\sdl2-foreign-activity-javavm.patch'
if (!(Select-String -LiteralPath $sdlAndroid -SimpleMatch $sdlVmMarker -Quiet)) {
    & git -C $sdl apply $sdlPatch
    if ($LASTEXITCODE -ne 0) { throw "Failed to apply SDL2 Godot JavaVM bridge patch ($LASTEXITCODE)" }
}

& $cmake --fresh -S (Join-Path $repo 'android') -B $build -G Ninja `
    "-DCMAKE_TOOLCHAIN_FILE=$cmakeToolchain" `
    '-DANDROID_ABI=arm64-v8a' `
    "-DANDROID_PLATFORM=android-$AndroidApi" `
    "-DCMAKE_BUILD_TYPE=$Configuration" `
    "-DCMAKE_MAKE_PROGRAM=$cmakeNinja" `
    "-DSDL2_SOURCE_DIR=$cmakeSdl"
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed ($LASTEXITCODE)" }

& $cmake --build $build --config $Configuration --target opentyrian_core SDL2
if ($LASTEXITCODE -ne 0) { throw "Android native build failed ($LASTEXITCODE)" }

$core = Join-Path $build 'out\libopentyrian_core.so'
$sdlSo = Get-ChildItem $build -Recurse -Filter 'libSDL2.so' | Select-Object -First 1 -ExpandProperty FullName
if (!(Test-Path $core)) { throw "Android core output missing: $core" }
if (!$sdlSo -or !(Test-Path $sdlSo)) { throw 'Android SDL2 output missing' }

New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -LiteralPath $core -Destination (Join-Path $stage 'libopentyrian_core.so') -Force
Copy-Item -LiteralPath $sdlSo -Destination (Join-Path $stage 'libSDL2.so') -Force
Write-Host "Android arm64 native libraries staged in $stage"
