# Publishing BDNukes

The path is: GitHub repo → GitHub release with the zip → forum thread (optional SpaceDock) → CKAN.

## 1. Before a release
- Bump the version in `GameData/BDNukes/BDNukes.version` (the packaging script reads it and stamps the DLL).
- Add a section to `CHANGELOG.md`.
- Test in game: atmospheric burst, vacuum burst, Small Nuclear Warhead fuzes, and a Tsar Bomba drop.
  Check `KSP.log` for `[BDNukes]` errors.

## 2. Build the release zip
```
powershell -ExecutionPolicy Bypass -File tools/package.ps1
```
This writes `release/BDNukes-<version>.zip`, containing `GameData/BDNukes/` (DLL, configs, LICENSE, README, CHANGELOG).
CKAN finds the `BDNukes` folder by the mod identifier, and manual installers just drop `GameData` into KSP.

## 3. GitHub
```
git remote add origin https://github.com/CRsquared64/BDNukes.git
git push -u origin main
```
Create a release with tag `v<version>` (e.g. `v1.0.0`) and attach the zip as an asset. Use the CHANGELOG section as the notes.
CKAN's GitHub indexer reads the tag and asset from here.

Then replace the forum URL placeholder in `ckan/BDNukes.netkan`.
Optionally add `"URL"`, `"DOWNLOAD"` and `"GITHUB"` fields to `BDNukes.version` so KSP-AVC can notify users of updates.

## 4. Forum thread (recommended) and SpaceDock (optional)
- CKAN reviewers want `resources.homepage` to point at a forum thread. Post in
  *KSP1 Mod Releases* on forum.kerbalspaceprogram.com with screenshots, dependencies, license and the download link.
- SpaceDock (spacedock.info): uploading there and ticking **"Add to CKAN"** is the "express" route.
  The wranglers then create the netkan for you, with `$kref: '#/ckan/spacedock/<id>'`.

## 5. CKAN
CKAN indexes mods through small `.netkan` files in https://github.com/KSP-CKAN/NetKAN. Bots then
re-check your GitHub releases and publish each new version automatically.

**Option A: express.** Open an issue at https://github.com/KSP-CKAN/NetKAN/issues asking to add BDNukes, with the repo link.
Or use the metadata web tool at https://ksp-ckan.github.io/metadata-webtool/.

**Option B: pull request (fastest review).**
1. Fork KSP-CKAN/NetKAN and copy `ckan/BDNukes.netkan` to `NetKAN/BDNukes.netkan`.
2. Optional local check: `netkan.exe --verbose BDNukes.netkan`, then `ckan.exe install -c BDNukes-<version>.ckan`.
3. Open the PR. Automated validation runs on it; if it fails, fix the netkan and push again.

What the reviewers check:
- `license: MIT` matches `LICENSE` inside the zip. `MIT` is a valid CKAN license string.
- The `homepage` is the forum thread.
- The identifier (`BDNukes`) only uses letters, digits and dashes.
- The `.version` file is valid, because `$vref: '#/ckan/ksp-avc'` sets the KSP compatibility from it.
- Dependencies use `depends`, not bundled copies. BDNukes depends on `ModuleManager`, `Harmony2` and `BDArmory`
  (`BDArmory` is the virtual identifier that `BDArmoryContinued` provides). It suggests `BDArmoryExtended`.

After the first merge, every new GitHub release with a zip asset is picked up automatically, usually within an hour or so.

## References
- Adding a mod to CKAN: https://github.com/KSP-CKAN/CKAN/wiki/Adding-a-mod-to-the-CKAN
- CKAN FAQ: https://github.com/KSP-CKAN/CKAN/wiki/FAQ
- NetKAN repo: https://github.com/KSP-CKAN/NetKAN
- CKAN schema (license list): https://github.com/KSP-CKAN/CKAN/blob/master/CKAN.schema
- BDArmoryContinued netkan (dependency identifiers): https://github.com/KSP-CKAN/NetKAN/blob/master/NetKAN/BDArmoryContinued.netkan
