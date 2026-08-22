# Publishing a GitHub prerelease

OpenTyrianVR uses tagged releases from `master`. Do not create a long-lived
release branch for the initial alpha. Prepare the release on a short-lived
`codex/release-0.1.0-alpha.1` (or equivalent) branch, merge it through a pull
request, and tag the resulting `master` commit. Create `release/0.1` only if
0.1 later needs maintained hotfixes while `master` has moved toward 0.2.

## 1. Prepare and review

1. Confirm `VERSION`, `CHANGELOG.md`, `RELEASE_NOTES.md`, the Android version,
   and Windows resource versions agree.
2. Disable player access to developer overlays. Release exports do this
   automatically; do not set `OTYR_DEV_TOOLS=1` in distributed PCVR builds.
3. Run the native harness and `tools\test_presentation.ps1`.
4. Open a pull request into `master`, let validation pass, and merge it.

## 2. Build from the exact release commit

Check out the merged commit and require a clean working tree:

```powershell
git switch master
git pull --ff-only origin master
git status --short
```

Configure the persistent Android release key as documented in
`BUILDING_QUEST.md`, then build both packages:

```powershell
powershell -ExecutionPolicy Bypass -File tools\build_pcvr.ps1
powershell -ExecutionPolicy Bypass -File tools\build_quest.ps1 -ReleaseSigning
```

Do a cold-start smoke test of these exact artifacts on Quest and PCVR. Verify
the checksums and inspect each `BUILD.txt`/`.build.txt`; `dirty` must be `false`
and `commit` must equal `git rev-parse HEAD`.

## 3. Tag and publish

Create and push an annotated tag only after the exact artifacts pass:

```powershell
git tag -a v0.1.0-alpha.1 -m 'OpenTyrianVR 0.1.0-alpha.1'
git push origin v0.1.0-alpha.1
```

Create a GitHub prerelease and attach binaries, checksums, and build records:

```powershell
gh release create v0.1.0-alpha.1 `
  artifacts\OpenTyrianVR-0.1.0-alpha.1-quest.apk `
  artifacts\OpenTyrianVR-0.1.0-alpha.1-quest.apk.sha256 `
  artifacts\OpenTyrianVR-0.1.0-alpha.1-quest.apk.build.txt `
  artifacts\OpenTyrianVR-0.1.0-alpha.1-pcvr-win-x64.zip `
  artifacts\OpenTyrianVR-0.1.0-alpha.1-pcvr-win-x64.zip.sha256 `
  --repo Brobert-in-aus/OpenTyrianVR `
  --verify-tag --prerelease `
  --title 'OpenTyrianVR 0.1.0-alpha.1' `
  --notes-file RELEASE_NOTES.md
```

Finally, download both public assets from GitHub, recheck their SHA-256 values,
and perform one launch of each downloaded copy. Keep the release marked as a
prerelease until the playtest is intentionally promoted.
