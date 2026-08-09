# Building for Meta Quest

The Quest build is a Godot 4.7 Mono/OpenXR Android application targeting
64-bit ARM. It currently supports Quest 2, Quest 3/3S, and Quest Pro.

## Prerequisites

- Godot 4.7 stable Mono plus its matching Android build template
- JDK 17
- Android SDK with build-tools 35.0.0 and NDK 27.0.12077973
- Godot OpenXR Vendors 5.1.0 in `godot/addons/godotopenxrvendors/`
- SDL 2.32.10 source in `deps/SDL2-source-2.32.10/`
- Tyrian 2.1 data files in `tyrian21/`
- The standard Android debug keystore at `%USERPROFILE%\.android\debug.keystore`

The generated Android template, vendor plugin, SDL source, staged data, and
native build outputs are intentionally ignored by git.

## Build

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File tools\build_quest.ps1
```

The script builds SDL and the OpenTyrian core for `arm64-v8a`, exports the
Godot C# project, applies 16 KiB ELF/APK alignment, signs with the debug
keystore, and verifies the required managed, native, OpenXR, and data payloads.
It sets `OTYR_MUTE=1` for Godot tooling and never launches or installs the game.

Output: `artifacts/OpenTyrianVR.quest.apk`

## Install (never launch)

The deployment helper verifies the package identity, installs it, and reports
the installed version. It intentionally has no launch path and does not change
headset volume; the tester starts the app manually when ready.

```powershell
powershell -ExecutionPolicy Bypass -File tools\install_quest.ps1 `
    -Device 192.168.8.100:5555
```

An alternate APK can be selected with `-Apk`. If `-Device` is omitted, the
helper proceeds only when exactly one ADB device is connected.

## Device status and remaining gate

Quest packaging, startup, data extraction, OpenXR session creation, controller
input, automatic centering, and stereo multiview presentation are confirmed on
device as of version 0.1.3. Per-eye diagnostics reported two 1680x1760 views,
mirrored asymmetric projections, and a 62.8 mm IPD. Keeping the Quest viewport
at the runtime-recommended 1.0 render scale fixed the shifted eye boundary and
head-motion distortion.

The next installed development build exposes the remaining checks on the
in-headset panel to the player's left: pause/resume and in-game-menu transitions,
elevated platform-rider stability, and the pulsing green review markers used for
ambiguous hover-height types.
