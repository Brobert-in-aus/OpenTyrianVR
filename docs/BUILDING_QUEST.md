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
The exporter runs with redirected logs and a five-minute bound. Godot 4.7 can
remain alive after Gradle has closed a complete APK; the helper verifies the
ZIP central directory and managed payload before stopping only that idle export
process and continuing with alignment and signing.

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

## Device status and current validation gate

Quest packaging, startup, data extraction, OpenXR session creation, controller
input, automatic centering, and stereo multiview presentation are confirmed on
device. Per-eye diagnostics reported two 1680x1760 views, mirrored asymmetric
projections, and a 62.8 mm IPD. Keeping the Quest viewport at the
runtime-recommended 1.0 render scale fixed the shifted eye boundary and
head-motion distortion. A sustained 0.1.5 pass validated pause/resume,
in-game-menu transitions, platform-rider stability, 4x MSAA, and ample
performance headroom; review-marker halos are editor-only.

Version 0.1.21 (code 22) is the current validation build. It requests 90 Hz,
crops terrain/backing/map shadows to the reachable 0..264 surface while
retaining the ship-safe -25..288 sprite envelope, keeps the
vertical crop at 0..184, render-interpolates all terrain layers, stabilizes
linked composite enemies across authored transparent gaps, and keeps known
cloud layers translucent when level events change their draw order. Clouds
retain their elevated plane across those order changes and paint after ground
shadows but before aerial platforms; surface objects select ground versus
platform per instance; connected boss components share one surface while
retaining their authored stack offsets. ABI v27 also carries episode identity
so Episode 1 type heights cannot leak into Episodes 2-4; conservative
episode-local semantics cover only exact or validated close-family matches.
Dynamic type-zero spawns use conservatively validated graphic semantics.
Entity shadows now mask each fragment against the live elevated receiver art,
clipping unsupported portions at cloud/platform holes; the deterministic
desktop presentation suite covers all six native effects together and the
unknown-effect legacy safety path.
The in-headset checklist is the authoritative pass/fail gate:

- the ship reaches both cropped horizontal edges;
- enemies cross the hard vertical playfield edges cleanly, with no fade;
- terrain below floating platforms scrolls without judder;
- fast stacks and the small level-1 boss remain welded;
- clouds remain translucent and below level-1 aerial platforms;
- ground/platform objects and flying enemies occupy the intended planes;
- the stacked tank boss keeps its body/turret offsets on one shared surface;
- Episodes 2-4 show no Episode 1 height leakage, and classified later-episode
  objects occupy the intended surface/air planes;
- height-driven entity shadows move farther from higher casters, and elevated
  clouds/platforms cast stable silhouettes without floating across transparent
  holes;
- storm, flip, lava, blur, iced, and searchlight effects remain stereo-correct
  in the hybrid 3D scene;
- HUD, pause, death, end-level, and story/lifecycle screens remain complete and
  readable;
- motion remains smooth at the selected 90 Hz refresh rate.

The check control cycles each entry through pass, fail, and unchecked; the
navigation control advances separately. Unchecked therefore means untested,
not failed. Quest automation remains install-and-report only: do not launch the
application from build or deployment scripts.
