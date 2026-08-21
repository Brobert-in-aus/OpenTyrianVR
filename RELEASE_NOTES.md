# OpenTyrianVR 0.1.0-alpha.1

This is the first public playtest of OpenTyrianVR: the original Tyrian campaign
presented as a tilted stereoscopic diorama, with the original simulation kept
authoritative underneath.

This build is deliberately marked **alpha**. Please expect rough edges, report
comfort problems promptly, and back up saves before upgrading to later builds.

## Downloads

- **Meta Quest:** `OpenTyrianVR-0.1.0-alpha.1-quest.apk`
- **Windows PCVR:** `OpenTyrianVR-0.1.0-alpha.1-pcvr-win-x64.zip`
- Use each artifact's adjacent `.sha256` file to verify the download.

Quest 2, Quest 3/3S, and Quest Pro are the current standalone targets. Windows
PCVR uses the active OpenXR runtime and has primarily been tested with Virtual
Desktop/VDXR.

Read the [playtesting guide](https://github.com/Brobert-in-aus/OpenTyrianVR/blob/v0.1.0-alpha.1/PLAYTESTING.md)
before playing for installation, controls, known limitations, comfort guidance,
and bug-report information.

## Highlights

- Full campaign and original game rules/timing.
- Direct left-hand steering plus thumbstick controls.
- A first-run laser-pointer menu appears before the game starts, with recenter
  guidance and Start/Skip choices. Its safe practice level verifies full-range
  hand movement, main fire, sidekick fire, and item collection; missed practice
  pickups keep respawning. Steering aids fade after ten seconds in normal play
  without disabling hand control.
- Stereoscopic terrain, enemies, shots, platforms, clouds, and shadows.
- Stereo-safe storm, flip, lava, blur, ice, and searchlight effects.
- Working audio, pause/recenter, saves, menus, story, death, and end-level flows.

## Known limitations

- Menus, cinematics, and story screens remain flat legacy panels.
- Two-player/network play is not included.
- Broad headset and controller compatibility testing is still needed.
- Rare art may fall back to conservative or flat presentation.

The corresponding source is the `v0.1.0-alpha.1` tag. OpenTyrianVR is GPL-2.0-or-later;
third-party notices are included in the repository and binary packages.
