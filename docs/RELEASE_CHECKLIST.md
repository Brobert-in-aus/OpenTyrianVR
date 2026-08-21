# 0.1 playtest release checklist

Prepare the release on a short-lived branch and merge it to `master`; the tag is
cut from the merged commit. A maintained `release/0.1` branch is unnecessary
unless 0.1 later needs hotfixes while `master` has moved toward 0.2. Exported
release builds automatically hide the developer debug/level-warp menu,
diagnostics, and in-headset validation checklist.

## Candidate gate

- Working tree is clean and the version in `godot/export_presets.cfg` matches
  the release name.
- `tests\build_harness.ps1` followed by `tests\otyr_host_harness.exe` passes.
- `tools\test_presentation.ps1` passes.
- `tools\build_pcvr.ps1` produces a ZIP, commit-bearing `BUILD.txt`, and SHA-256.
- `tools\build_quest.ps1 -ReleaseSigning` produces an APK, build record, and
  SHA-256 using the persistent project signing key.
- Both checksum files match the uploaded artifacts.
- The candidate commit is tagged only after the packages are built from it.

## Headset smoke gate

Test the exact packaged artifacts, not an editor build:

- Quest cold start reaches the title screen without a managed exception.
- PCVR starts with the supported OpenXR runtime and renders both eyes correctly.
- Start a new game, steer by hand, fire, use both sidekicks, pause/recenter,
  resume, die/continue, and quit cleanly.
- Complete at least one representative level on both targets.
- Verify music and sound effects, readable menus/HUD, stable 90 Hz Quest motion,
  and no obvious eye mismatch during storm/searchlight/flip effects.
- Upgrade-install over the previous candidate and confirm saves still load, or
  disclose the incompatibility in `PLAYTESTING.md`.

Record headset/runtime, pass/fail result, tester, date, artifact SHA-256, and any
accepted known issues with the release notes.
