# Changelog

## Unreleased

### Added
- **Terrain craters:** surface and low bursts on land dig a persistent crater (bowl, ragged rim, ejecta blanket) into the terrain, about 52 m x yield^0.294 in radius. Megaton airbursts press a wide, shallow depression in from up to about 2.2 fireball radii. Craters are bigger on low-gravity bodies, are saved with the game and come back on load or revert. The scorch mark drapes into the crater, and landed craft and colliders follow the new ground.
- Only the terrain near a new crater is rebuilt (a few ms), and it works with Parallax. A full terrain rebuild is available as a setting, but it freezes the game for several seconds.
- **Distance haze:** the cloud washes out toward the sky colour through the air between it and the camera. The amount depends on distance and altitude, and the haze colour follows the time of day.
- **Overcast lighting:** with EVE volumetric clouds, the cloud below an overcast deck loses direct sun (flatter, greyer, no silver lining). A cap that rises above the deck stays sunlit.
- New settings for craters (on/off, size scale, rebuild mode, fill in all craters) and for haze and weather.

### Changed
- **Flash:** the double flash now scales with yield. A millisecond first pulse is followed by the thermal pulse, which peaks at 0.042 x W^0.44 s (about 0.16 s at 20 kt, 0.9 s at 1 Mt, 5 s at 50 Mt). The screen whiteout eases off as the eye adapts.
- **Sound:**
  - The crack is an N-wave with a ground-reflected echo.
  - Bigger yields sound deeper and longer.
  - Far off, the rumble dominates over the crack.
  - Close to ground zero, a roaring blast wind and afterwind follow the shock.
- **Wind on small clouds:** a cloud only feels the wind at the height it actually reaches, and the rising fireball picks up the wind gradually. Low-yield clouds such as the Davy Crockett's now stand up instead of smearing sideways.
- Far away, the smoke puffs merge into one mass instead of reading as separate balls.

## 1.0.1

### Fixed
- The M29 Davy Crockett could not fire. Its nuclear round never loaded, because BDArmory reads bullet definitions before Module Manager processes `:NEEDS` tags.
- The Davy Crockett's "Fire" button was blocked by BDArmory while the mouse was over the part menu. If a shot can't go, the button now says exactly why (reload countdown, out of rounds, barrel blocked, time warp).

## 1.0.0

First release.

### Weapons
- **15 nuclear weapons** with procedurally generated models at real dimensions and masses:
  - **Bombs:** M29 Davy Crockett (recoilless rifle, one-press manual fire), Little Boy, Fat Man, B61-12, B83, AN602 Tsar Bomba.
  - **Missiles:** AIM-47 Falcon, AGM-86B ALCM, MIM-14 Nike Hercules, LIM-49 Spartan.
  - **ICBMs:** Minuteman III with a standalone Mk21 RV, R-36M2 MIRV and Mod 6.
- **BDNukes part page** in the VAB/SPH, sorted by weapon type. It includes BDArmory's own nuclear parts.
- **Modules:**
  - `ModuleNukeFuze`: arm/disarm, detonate button and action group, impact, proximity, airburst-altitude and laydown fuzes.
  - `ModuleNukeYieldSelector`: dial-a-yield on the B61-12, B83 and AGM-86B.
  - `ModuleNukeParachute`: retarding chutes with real drag.
  - `ModuleReentryVehicle`: plasma sheath and trail.
  - `ModuleMIRVBus`: multiple warheads across a footprint.
  - `ModuleNukeGunTrigger`: one-press fire for nuclear guns.
  - `ModuleNukeDecouple`: a decoupler for fuzed bombs.
- **Ballistic missile motor cutoff:** thrust is cut once the drag-free trajectory reaches the target.

### Effects
- **Cinematic detonations for all BDArmory nukes:** yield-scaled flash, fireball, condensation rings and collars, Wilson cloud, shock front, rolling toroidal cap, flowing dust stem, skirt, base surge, delayed sound and camera shake, and screen glow.
- **Cloud character profiles** (`ModuleNukeCloudProfile`): auto, desert, oceanic, tower, tropical megaton, Tsar and high altitude.
- **Burst regimes:** water surface and underwater bursts, a blended high-altitude regime, low-yield tuning, and vacuum bursts (debris shell, magnetic streamers, artificial aurora).
- **Aftermath:** wind-drifted fallout plume, scorch scar and night afterglow, lingering fires and smoke, lightning in megaton clouds.
- **Optional radiation hazard:** a dose model with t^-1.2 decay, sickness and lethal thresholds, probe-core drain, dosimeter and Geiger counter. Doses are saved per game.
- **Graphics:** quality presets (Low/Medium/High/Ultra/Custom) and an in-game settings window. Particle and sound LOD apply when many detonations are active.

### Fixed (BDArmory behaviour)
- BDArmory Extended's "Small Nuclear Warhead" could never detonate.
- Nuclear EMP never affected anything (zero-length shielding raycast).
- Vacuum bursts used air-blast timing and an air "suck-back" pull.
- Pooled nuke explosions kept the air density from the previous detonation.
- A stray BDArmory shell model and point light were visible at ground zero under the new effects.
- Fuzed bombs with a decoupler dropped off their carrier at spawn.

### Robustness
- Each Harmony patch is applied separately, so a BDArmory update that renames one method only disables that one feature.
- The fuze stays live if BDArmory refuses a detonation, and it can't airburst while still on its carrier.
