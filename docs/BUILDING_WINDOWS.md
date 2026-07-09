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

Determinism gate: play the same demo twice with `--hash-log` and diff the
logs; they must be identical over the common prefix. The title screen
auto-plays demos after 30 seconds idle, so this can be done unattended.

Reference capture: `captures/demorec-ep1-tyrian.0` (episode 1, level TYRIAN).
