# OpenTyrianVR 0.1 playtest

This is a very early alpha intended to find crashes, unreadable scenes,
controller problems, VR discomfort, and presentation mistakes across Tyrian's
levels. Saves may not remain compatible with later builds. Back up anything you
care about before upgrading.

## Install and start

Detailed checksum, SideQuest, manual ADB, PCVR runtime, upgrade, and
troubleshooting instructions are in [INSTALLING.md](INSTALLING.md).

### Meta Quest

Install the supplied `OpenTyrianVR-0.1.0-alpha.1-quest.apk` with SideQuest or the
repository's `tools\install_quest.ps1`, then start **OpenTyrianVR** from the
headset's app library. Quest 2, Quest 3/3S, and Quest Pro are the current test
targets.

### Windows PCVR

Unzip `OpenTyrianVR-0.1.0-alpha.1-pcvr-win-x64.zip`, make sure the desired headset
software is the active OpenXR runtime, and run `OpenTyrianVR.exe`. Keep the
package directory intact; its `native` and `tyrian21` subdirectories are
required. Development has primarily used Virtual Desktop/VDXR, so reports from
SteamVR, Meta Link/Air Link, and Windows Mixed Reality-compatible runtimes are
especially useful.

Compare the package against its adjacent `.sha256` file before installing.

## Controls

- Before the game starts for the first time, an in-headset menu explains the
  training and how to recenter. Point either controller laser at **Start
  Tutorial** or **Skip**, then squeeze that controller's trigger. Completion or
  skipping is remembered. Set `OTYR_TUTORIAL=1` before a PCVR launch to replay
  it for testing.
- Training runs in a safe practice scene. Move the left hand to all four corners
  of the blue rectangle while the real player ship visibly catches up to the
  blue target, then demonstrate main fire, sidekick fire, and collect the
  falling powerup. The practice scene uses original Tyrian ship, powerup, and
  terrain art. A missed item respawns from the top until collected; sleeping
  controller tracking pauses progression rather than failing the step.
- Move the left hand inside the floating blue rectangle to steer directly.
  The rectangle, hand marker, and lane reticle fade between 8 and 10 seconds
  into each level; hand steering remains active after they disappear.
- Either trigger fires.
- Left and right grip activate the corresponding sidekick.
- Right A confirms; right B cancels.
- Left X advances screens/acts as Space; left Y changes fire mode.
- The left controller menu button pauses and recenters the board.
- On a PC keyboard: arrows move, Space fires, Enter confirms, Escape cancels,
  Ctrl/Alt activate sidekicks, and P pauses.

## Known 0.1 limitations

- The original low-resolution menus, cinematics, and story screens remain flat
  panels; gameplay is the focus of the 3D conversion.
- Two-player and network play are outside this playtest scope.
- PCVR runtime and controller coverage is still limited.
- Some rare sprites or level effects may use conservative flat/fallback
  presentation rather than an authored height.
- The presentation is tuned around a forward-facing seated pose. Use the menu
  button to recenter if the board appears offset.

## Reporting feedback

Include the exact version, the commit from `BUILD.txt` or the Quest `.build.txt`
file, headset, OpenXR runtime, and whether the issue reproduces. For visual bugs,
name the episode/level and attach a screenshot or short clip. For comfort
feedback, describe seated/standing play, session length, and the specific motion
or scene that caused discomfort.

Windows logs are normally under
`%APPDATA%\Godot\app_userdata\OpenTyrianVR\logs\godot.log`. Quest developers can
capture a focused log with:

```powershell
adb logcat -d | Select-String 'OpenTyrianVR|Godot|opentyrian'
```

Submit reports through the project's issue tracker, or through the private
channel that supplied the playtest build. Do not include personal information
or unrelated device logs.
