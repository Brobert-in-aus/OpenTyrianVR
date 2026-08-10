# Building on Windows (reproducible baseline)

This is the Phase 0 reference build (see [VR_CONVERSION_PLAN.md](VR_CONVERSION_PLAN.md)).

## Prerequisites

- Visual Studio 2026 (v18) Community or later with the C++ desktop workload
- PowerShell 5.1+
- Tyrian 2.1 data files in `tyrian21/` at the repo root (freeware; not in git)

## Steps

```powershell
# 1. Fetch pinned SDL2 dev libraries (SDL2 2.32.10, SDL2_net 2.2.0) into deps/
#    and generate visualc/*.props:
powershell -ExecutionPolicy Bypass -File visualc\fetch-deps.ps1

# 2. Build x64 Release:
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe
& $msbuild visualc\opentyrian.sln /p:Configuration=Release /p:Platform=x64 /m

# 3. Run:
.\opentyrian-x64-Release.exe --data=tyrian21
```

The build outputs `opentyrian-x64-Release.exe` plus `SDL2.dll`/`SDL2_net.dll`
at the repo root.

## Verification instrumentation

Fork-specific additions used by the Phase 0 replay/determinism gates:

- `--record` — records keyboard input per level to `demorec.N` in the working
  directory (stock OpenTyrian feature). Playable via the title-screen Demo
  menu after copying over `demo.N` in the data directory.
- `--hash-log=FILE` — writes one line per gameplay tick:
  `<tick> <state hash> <frame hash>`, where the state hash covers players,
  enemies, shots, level-event progress, and RNG state (`src/statehash.c`),
  and the frame hash covers the 320x200 legacy framebuffer.
- Demo recording/playback and hash-logged runs reseed the gameplay RNG with a
  fixed seed at level start (the same mechanism lockstep network games always
  used), making replays fully deterministic. Stock OpenTyrian demos are
  input-deterministic only — enemy random acceleration diverges between runs.

- `--turbo` — removes all frame-pacing delays. The simulation is wall-clock
  independent, so results are bit-identical, roughly 50x faster.
- `--play-demo` — skips logos/title and plays demos immediately (cycling
  demo.1-5).
- `OTYR_START_SECTION=<n>` with optional `OTYR_START_EPISODE=<e>` boots the
  height-editor/test path at a script section. Release builds skip intro logos
  when this hook is active, so targeted unattended checks do not spend their
  timeout outside gameplay.

Determinism gate: run `--turbo --play-demo --hash-log=FILE` and diff against
the checked baseline; identical over the common prefix. A full demo verifies
in a few seconds. (Without the flags, the title screen auto-plays demos after
30 seconds idle, so it also works unattended in real time.)

Reference capture: `captures/demorec-ep1-tyrian.0` (episode 1, level TYRIAN).

## Silent presentation regression

`tools/test_presentation.ps1` builds the native and managed hosts, audits all
campaign effect events, then runs three self-terminating Godot attract-mode
cases with dummy audio: hybrid 3D plus card flip, native water storm, and a
complete legacy-filter fallback. It writes five addressed frame captures and
logs beneath the ignored `artifacts/presentation-regression-*` directory and
fails on shader/runtime errors, missing captures, absent entity/map shadows,
missing receiver layers, or an incorrect presentation transition.

```powershell
powershell -ExecutionPolicy Bypass -File tools\test_presentation.ps1
```

Pass `-SkipBuild` only after both native and managed outputs are current.
