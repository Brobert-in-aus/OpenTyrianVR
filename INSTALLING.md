# Installing OpenTyrianVR 0.1

OpenTyrianVR is distributed as a standalone Meta Quest APK and a portable
Windows PCVR ZIP. Neither build needs a separate copy of Tyrian: the freeware
Tyrian 2.1 data is included.

This is an alpha playtest. Back up saves you care about, expect rough edges,
and read [PLAYTESTING.md](PLAYTESTING.md) before a long session.

## Download and verify

Download one of these packages and its adjacent `.sha256` file from the same
GitHub prerelease:

- `OpenTyrianVR-0.1.0-alpha.1-quest.apk` for standalone Quest
- `OpenTyrianVR-0.1.0-alpha.1-pcvr-win-x64.zip` for Windows PCVR

On Windows, verify a download in PowerShell:

```powershell
Get-FileHash -Algorithm SHA256 .\OpenTyrianVR-0.1.0-alpha.1-quest.apk
Get-Content .\OpenTyrianVR-0.1.0-alpha.1-quest.apk.sha256
```

The two hexadecimal values must match. Substitute the PCVR ZIP names when
checking that package. Do not install a file whose checksum differs.

## Meta Quest: SideQuest

This is the simplest installation route for most playtesters.

1. Enable Developer Mode for the headset and install SideQuest on the computer.
2. Connect the awake, unlocked headset by USB. In-headset, allow USB debugging
   for that computer if prompted.
3. Wait for SideQuest to show that the headset is connected.
4. Choose SideQuest's **Install APK file from folder** action and select the
   downloaded `OpenTyrianVR-0.1.0-alpha.1-quest.apk`.
5. Wait for the install-success notification, then disconnect the cable.
6. In the headset's App Library, open the unknown-sources/developer section and
   start **OpenTyrianVR**. Meta occasionally changes the name or location of
   this filter between Horizon OS versions.

SideQuest can install a later APK over this release without deleting saves as
long as it is signed by the same OpenTyrianVR release key.

## Meta Quest: manual ADB

Install the Android SDK Platform Tools, enable Developer Mode, connect the
headset, and accept its USB-debugging prompt. Then run:

```powershell
adb devices
adb install -r .\OpenTyrianVR-0.1.0-alpha.1-quest.apk
```

The headset should appear once in `adb devices` with status `device`, and the
install command should finish with `Success`. The repository also provides a
Windows helper which validates the package identity and installed version:

```powershell
powershell -ExecutionPolicy Bypass -File tools\install_quest.ps1 `
    -Apk .\artifacts\OpenTyrianVR-0.1.0-alpha.1-quest.apk
```

If ADB reports `unauthorized`, put on the headset and accept the debugging
prompt, then reconnect it. If it reports `INSTALL_FAILED_UPDATE_INCOMPATIBLE`,
the installed copy was signed with a different key. Uninstalling it would also
remove its local application data, so preserve anything important and prefer a
correctly signed upgrade instead.

## Windows PCVR

You need 64-bit Windows, an OpenXR-compatible headset and controller pair, and
the headset vendor's PC software or another working OpenXR runtime. OpenTyrianVR
does not require Steam itself, but SteamVR can provide the OpenXR runtime.

1. Extract `OpenTyrianVR-0.1.0-alpha.1-pcvr-win-x64.zip` to a normal writable
   folder. Do not run the executable from inside the ZIP.
2. Select the software driving your headset as the active OpenXR runtime. For
   example, choose the OpenXR-runtime option in Meta Quest Link, SteamVR, or the
   Virtual Desktop Streamer/VDXR settings. The exact label can vary by version.
3. Start and connect the headset through that software.
4. Run `OpenTyrianVR.exe` from the extracted folder.

Keep the whole folder together. In particular, `native/`, `tyrian21/`, the
`.pck` file, and `OpenTyrianVR.exe` are all part of the application. There is no
installer and the alpha executable is not code-signed, so Windows may display
an unfamiliar-app warning.

To update, extract the new release to a fresh folder and run that copy. Windows
saves live outside the package under Godot's OpenTyrianVR application-data
directory, so replacing the extracted package does not normally remove them.

## First launch and recentering

The first launch opens a VR menu before the main game. If it is not comfortably
centred, use the headset/runtime recenter gesture first. Point either controller
laser at **Start Tutorial** or **Skip** and press that controller's trigger.

The tutorial covers hand steering, main fire, sidekick fire, and collecting an
item. During normal levels, the blue hand-steering guide fades after about ten
seconds; hand steering remains active.

## Troubleshooting

- **PC window opens but nothing appears in the headset:** close the game, make
  sure the desired OpenXR runtime is active and the headset is connected, then
  start the game again.
- **Wrong PCVR runtime starts:** change the active OpenXR runtime in the headset
  software before launching OpenTyrianVR.
- **Quest app is missing:** look in the App Library's unknown-sources/developer
  filter and confirm the APK installation reported success.
- **Board position is uncomfortable:** use the left controller menu button to
  pause and recenter.
- **Need logs:** see [PLAYTESTING.md](PLAYTESTING.md#reporting-feedback) for the
  Windows log location, Quest logcat command, and useful report details.

For controls, comfort guidance, known limitations, and feedback instructions,
continue with [PLAYTESTING.md](PLAYTESTING.md).
