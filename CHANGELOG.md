# Changelog

## Unreleased

### Added
- **Air bursts get their own simulated fireball:** a free ball in open air rolling up into a ring, with a clearer shock shell around it. Ground bursts keep the ground simulation.
- **Smoother, more saturated fire:** both bakes are at higher resolution (112) and cover just the fireball stage. The importer smooths each frame, and flame-scale folds and stronger orange-yellow colour are added in the shader. The procedural cap is smoother too.
- **Reduced-resolution rendering** (default ½): the cloud is raymarched at ½ or ⅓ resolution and composited over the scene with a depth-aware upsample, so edges in front of it stay sharp. That's 4–9× fewer raymarched pixels, with no visible loss in the test renders. New setting: "Cloud resolution".
- **Cloud shadow on the ground:** the volumetric cloud darkens the ground and craft beneath it along the sun direction. It fades out under overcast. New settings: on/off and strength.
- **Light-bending shockwave:** the shock front is a refracting shell (a dome for ground bursts) that bends the scene behind it, strongest at its silhouette, and fades as it spreads. New setting: strength (0 = off).
- **Simulated fireball:** the fireball plays back a fluid simulation baked in Blender with its built-in Mantaflow solver: a full-size hot hemisphere on the ground that burns, rolls and lifts off, with real swirling flame and soot. The volume blends between baked frames and adds sub-voxel detail. The box is fitted to the procedural cap each frame, and it hands over to the procedural cloud once the fire has burned down.
  - `unity/Sim/bake_fireball.py` bakes it, and the bundle build imports it (about 6 MB more in the bundle).
  - New setting: "Simulated fireball".
- **Volumetric mushroom clouds:** the cap, stem and dust skirt are raymarched through a 3D billow-noise volume instead of drawn as sprites. They get cauliflower billows, self-shadowing from the sun, sky and ground light, haze and overcast lighting.
- **Fluid fireballs:** the fireball is part of the same volume. It is a white-hot ball whose turbulent incandescent filaments cool through yellow, orange and dull red, and its shell rolls into the toroidal cap with the cap's poloidal circulation.
  - Needs the shader bundle in `GameData/BDNukes/Shaders` (Windows / DirectX 11). Without it, the sprite cloud is used.
  - New settings: on/off and raymarch quality.
  - The shader source and the bundle build live in `unity/`, a Unity 2019.4.18f1 project.
- **Everything in one volume:** the Wilson cloud (a brief, patchy veil), the condensation rings (soft bands), the stem collars (drooping bells of mist), the dust skirt (a turbulent mound) and the base surge (a rolling dust ring) are all part of the raymarched volume. Their sprites are hidden when it's on.
- **Animation:**
  - The billows are a fixed physical size and are carried by flow fields: the cap's poloidal roll and expansion, the stem's updraft and twist, and the outflow of the skirt and surge.
  - Two crossfaded flow-map layers keep the cloud churning without zooming, smearing or pulsing.
  - The fireball starts as a smooth, white-hot ball and becomes turbulent within a second or two. Tongues of flame burn through dark soot while it cools.
- **Lighting:**
  - The billows shade each other (cauliflower relief), with multiple-scattering light, sky light blocked by the cloud above and ground bounce.
  - The cap, stem, dust and condensation each have their own colour.
  - A hue-preserving tone curve keeps the hot core from clipping to flat white.
- **Wind:**
  - The stem bends over, nearly upright in the weak low wind and leaning further higher up.
  - Wind shear leans the cap, and the cap stretches downwind as it drifts. Its downwind end thins into a ragged plume, and billows stream off the downwind side.
  - The dust skirt and base surge blow downwind.
- **Ageing:** big lopsided lobes make every cap a different shape. Late in its life the cloud frays, thins and fades, and the stem goes first.
- **Vapor:**
  - A visible Wilson cloud races out with the shock in humid air.
  - The condensation rings and stem collars are stronger.
  - A pileus veil (the "ice cap") drapes over the rising cap where it lifts moist air.
- **Blasts clear EVE clouds:**
  - The shock blows a hole in EVE's volumetric cloud decks within reach. The cloud it pushes out piles up in a ring at the edge.
  - The hole drifts with the clouds and heals over several minutes, then EVE's own coverage map goes back.
  - New setting: "Blasts clear EVE clouds".
- **Seen from orbit:** the planet itself now hides the volumetric cloud (behind the horizon and limb), since the scaled-space view leaves no depth buffer.
- **Cost:** a cloud that's small on screen, or one of a salvo, raymarches with fewer steps.
- `unity/run-unity.ps1 RenderTests` renders contact sheets of a detonation's life, its motion, the fireball, the weather and a megaton. They are drawn through the same parameter code the plugin uses (`unity/Assets/Shared`).
- **Terrain craters:** surface and low bursts on land dig a persistent crater (bowl, ragged rim, ejecta blanket) into the terrain, about 52 m x yield^0.294 in radius. Megaton airbursts press a wide, shallow depression in from up to about 2.2 fireball radii. Craters are bigger on low-gravity bodies, are saved with the game and come back on load or revert. The scorch mark drapes into the crater, and landed craft and colliders follow the new ground.
- Only the terrain near a new crater is rebuilt (a few ms), and it works with Parallax. A full terrain rebuild is available as a setting, but it freezes the game for several seconds.
- **Distance haze:** the cloud washes out toward the sky colour through the air between it and the camera. The amount depends on distance and altitude, and the haze colour follows the time of day.
- **Overcast lighting:** with EVE volumetric clouds, the cloud below an overcast deck loses direct sun (flatter, greyer, no silver lining). A cap that rises above the deck stays sunlit.
- New settings for craters (on/off, size scale, rebuild mode, fill in all craters) and for haze and weather.

### Fixed
- The volumetric cloud showed through terrain in front of it where the terrain shader writes no depth (Parallax). Rays now also stop at the ground's sphere at ground zero.
- Reduced-resolution clouds looked blocky and speckled. The upsample is now a 3×3 depth-aware filter that averages away the raymarch jitter, and the cloud takes more steps at reduced resolution.
- Blasts never cleared EVE clouds: EVE's `GetEquirectangularUV` is a private static method, and the lookup only searched instance methods. The log now names any EVE member that can't be found.

### Changed
- **Nuclear blasts push craft much less** (new `nukeImpulseScale`, default 0.15): BDArmory's blast impulse from nukes alone is scaled, and its global explosion-impulse setting is restored straight after each nuclear hit. Unscaled, a nuke could throw a light craft at near-orbital speed, taking the camera with it. Damage is unchanged.
- **Mushroom shape and fire:**
  - The cap is a rugged rolling torus, wider than it is tall, with smoke lobes curling down its outer edge. Dusty clouds are dark grey-brown smoke rather than pale cotton.
  - A burning ring inside the cap's vortex outlives the fireball, glowing orange through gaps in the underside even for small yields such as the Davy Crockett.
  - The stem is a rough, lumpy column instead of a flared trumpet.
  - A tall, billowing dust mound rises around its foot.
- **Fluid motion:** animated domain warping makes the billows curl, fold and roll into each other, and the billow layers flow at different speeds so they boil against each other.
- **Camera shake** is applied to the cameras only while they render, and their exact rotation is put back afterwards. KSP's camera controller never sees it, so a salvo can't spin the camera off to random angles or stop you centring on a blast.
- **Volumetric performance:**
  - The step count shrinks as the cloud fills the screen (a budget of pixels × steps), so getting close no longer tanks the frame rate.
  - Empty space is jumped across using the shapes' distance fields.
  - The shadow march takes one billowed step, then cheap ones.
  - Sky occlusion takes one sample.
  - The default is 64 steps.
  - The holed EVE coverage maps are redrawn 5 times a second instead of every frame.
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
