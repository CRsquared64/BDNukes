# Changelog

## 1.3.0 - the ICBM update

### Added
- **New weapons:**
  - **AIR-2 Genie** (W25, 1.5 kt): an unguided air-to-air rocket with a proximity burst and a 12 s time fuze.
  - **Mk 45 ASTOR** (W34, 11 kt): a nuclear torpedo, steered by the launching boat's sonar or radar lock.
  - **B57** (5–20 kt): a nuclear depth bomb that sinks and fires at a set depth (or on a shallower sea floor); over land it is an ordinary tactical bomb. `ModuleNukeFuze` has a new optional depth fuze for it.
  - **UGM-133 Trident II D5** (8x W76, 100 kt): fired from under water it is ejected first and lights its motor only once clear of the surface (new `ModuleColdLaunch`).
  - **MGM-31C Pershing II** (W85, 5–80 kt) and **RSD-10 Pioneer / SS-20** (3x 150 kt MIRV): road-mobile intermediate-range missiles.
  - **Starfish Prime test package** (W49, 1.4 Mt): a stack warhead for your own rocket with a burst-altitude fuze that goes up to 1,000 km.
- **Strike planner** (BDNukes toolbar button in flight): a window that lists the strike weapons on the active vessel, which are BDArmory GPS ballistic missiles plus the strike vehicles below.
  - Pick a body, then a target on it: Kerbal Konstructs towns, cities and bases (optional, read without a hard dependency), the stock launch sites, a click in map view or on the ground, typed coordinates, or the point under a satellite. The target is marked in flight and in map view.
  - The window checks the weapon can reach the target before it lets you launch.
  - After launch the camera can follow the weapon in, keeping it loaded and steering, and a button takes you back to the launcher.
  - BDArmory ballistic missiles fired from the planner fly round-planet ballistic guidance and hand back to BDArmory's own terminal guidance 80 km out.
- **Fictional strike vehicles** (in Missiles):
  - **OSV Orbital Strike Vehicle** (10 kt): launched from a craft in orbit, it burns its internal propellant to deorbit onto a point on the body it orbits or one of its moons, drops its burn stage and re-enters.
  - **OKV Orbital Interceptor** (0.1 kt): a radar-cued space interceptor flying BDArmory's orbital guidance with RCS divert thrusters. The booster drops at burnout. The carrier needs a BDArmory radar and weapon manager.
  - **Starlance Interplanetary Vehicle** (10 × 5 Mt): a three-stage rocket that flies itself from the pad to a parking orbit, staging as it goes. A light-drive bus then crosses to another planet at 0.9× `waveSpeed`, even unloaded, and releases ten re-entry vehicles in a centre-and-ring pattern around the target, each bursting 3 km up. Each RV keeps its planned arc, saved with the game, and is put back on it if it drifts more than 2 km or 5 m/s. It needs a designator: another craft with a probe core or antenna already orbiting the target body. Launched from orbit, it skips the ascent.
- **Radiation fronts across the system:** a burst in space sends its radiation out as a sphere across the whole system, drawn growing in map view and the tracking station. It travels at `waveSpeed` (20,000 km/s by default, slower than light so you can watch it). Every craft it passes gets a short pulse of dose falling off with distance; planets shade their far side and an atmosphere shields the ground. Time warp drops to 1x as a front reaches you, and the dosimeter clicks as it passes. Settings `radiationWaves`, `showRadiationWaves`, `waveSpeed`, `wavePulse`, `waveDoseRate`, `waveSurfaceShielding`, `waveStopWarp`.
- **Nuclear detonation alert:** every burst anywhere in the system sounds an alarm and opens a warning window. It shows the yield, where the burst was and how far from you, and the radiation front's countdown to you and to each planet, with "Show on map" and "Previous" buttons. Salvos and MIRVs count as one alert. Settings `blastAlert`, `blastAlertSound`.
- **Radiation overlays in map view and the tracking station:** burst sites are marked with pulsing markers and their yield, and fallout footprints are outlined on the ground with their hot core. An alert turns every radiation overlay on (bursts, footprints, belts and fronts). Settings `showRadiationOverlays`, `blastMarkerDays`.
- **W54 SADM "backpack nuke"** (0.01–1 kt dial-a-yield): carry it in an inventory, place it on EVA, set a 10 s – 10 min timer and arm it from the part menu, the EVA kerbal's menu or an action group. The countdown is shown in the menu and on screen and survives save/load.
- **Project Orion nuclear pulse drive:** a 10 m pusher-plate engine that burns Nuclear Pulse Units (a new resource). The throttle sets the pulse rate. Each pulse gives a cushioned kick with a bright flash, a plasma disc in vacuum and a thump in air.
- **Rope tricks:** tower and low bursts under about 100 kt shoot thin, bright spikes down the guy wires from the fireball's underside for the first fraction of a second. Setting `ropeTricks`.
- **Mach stem dust ring:** air bursts 0.5–8 fireball radii up raise a thin, bright-edged dust ring where the reflected shock runs out along the ground, ahead of the base surge, fading over about 15 s. Drawn in the volumetric cloud, with sprites in the fallback. Setting `machStemDust`.
- **Cloud-deck flash:** EVE cloud decks above or below a burst light up with the fireball, white with the flash and then orange, fading with distance over the first seconds. Setting `cloudDeckFlash`.
- **Forests are flattened** when Parallax (Continued) is installed: trees and bushes out to about 5 psi, and every scatter over the crater, kept in the save. Setting `flattenTrees`.
- **Fallout footprint:** ground and low bursts leave a persistent downwind hot zone, a faint ground tint that fades over game days and gives a slow radiation dose inside it. Settings `falloutFootprint`, `footprintDays`, `footprintDose`.
- **Firestorms:** after big bursts over land, fires spread through the thermal radius and merge, feeding smoke columns that drift downwind for several minutes. Settings `firestorm`, `firestormMinYield`, `firestormDuration`.
- **Artificial radiation belts (Starfish Prime):** bursts above the atmosphere of Kerbin, Jool or Eve (configurable) trap fission electrons in a belt along the field shell through the burst. It decays over Kerbin days (`beltHalfLifeDays`), stacks over several bursts, and is saved with the game.
  - Craft in a belt take a dose, loaded or not. Solar panels lose output, uncrewed probe cores eventually fail, and kerbals take a shielded share toward radiation sickness. Probe cores and panels show the dose in their part menu.
  - The belts are drawn in map view and the tracking station, coloured by intensity (`showRadiationBelts`).
- **High-altitude EMP:** bursts at 30–400 km over an atmosphere hit loaded craft below 20 km anywhere within the horizon circle with an EMP, strongest under the burst. Setting `hemp`.
- **Effects-range overlay** (Alt+F8 or the settings window): NUKEMAP-style rings on the terrain for the fireball, 20/5/1 psi and the thermal radius around your nuke's predicted impact point, with labels and the yield. Nuke parts also list these ranges in km in the part menu and editor tooltip.
- **Cinematic camera** (`cinematicCamera`, Alt+F6, off by default): frames and slowly orbits detonations near you. Any camera input gives control back.
- **Replay last detonation** (settings window or Alt+F10): plays the last detonation or salvo again from the start, visuals and sound only, with no damage, crater, scorch or radiation.
- **Waves on the sea.** Underwater and surface bursts send a train of waves out across the ocean. The first crest is the highest, about 28 m at 300 m for a 23 kt charge at 27 m depth, as at Crossroads Baker. Wave height falls off with distance and the waves run faster for bigger yields. They follow the planet's curve, steepen and break white where the sea gets shallow, and stop behind islands. Boats and anything else floating are carried and tipped as the waves pass. Low air bursts make small waves. Air bursts over the sea also sweep a pale, roughened shock ring across the water. Setting `waterWaves`.
- **New rocket plumes** (`ModuleNukePlume`, shader `BDNukes/Plume`): a hot core, shock diamonds at low altitude that fade as the air thins, and a wide, faint plume in vacuum, with flicker. Plumes follow the thrust of BDArmory missiles and stock engines and go out with a dropped stage.
- **New sounds** in `GameData/BDNukes/Sounds` (light-drive charge and jump, warhead release, a heavy motor loop, an RCS divert hiss), generated by `tools/make_sounds.py`.
- `ModuleProceduralModel`: a TRANSFORM can now be nested under another (`parent`) and can carry its own slice of the body (`detach = from, to`) so it drops away as a stage; FINS can ride on a TRANSFORM too (`parent`).
- **Works alongside Volumetric Explosions.** With that mod installed, nuke parts and parts destroyed inside a fresh nuclear fireball no longer get its explosion on top of the BDNukes effect. Without it, nothing changes.

### Changed
- **Ballistic missiles allow for drag when they cut the motor.** The cut-off used to stop the motor once the drag-free trajectory reached 5% past the target. At long range that left the warhead tens of km long: a Trident II fired 730 km came down about 25 km past the target. The missile's drag is now measured in flight (KSP's drag, which dwarfs BDArmory's own missile drag), and the cut-off flies the coast with it through the atmosphere a few times a second, cutting once the warhead would come down on the target. The cut is logged with the drag loss and the drag area. New `ModuleNukeCloudProfile` fields `ballisticDrag` and `ballisticDragScale`; `ballisticCutoffMargin` now defaults to 1.
- **BDArmory-fired ICBMs fly the planner's guidance.** A BDNukes ballistic missile that BDArmory or the AI fires at a target more than 30 km away now flies the round-planet pitch program and steering assist, following BDArmory's target if it moves.
- **Salvos share one cloud.** A burst that goes off inside a cloud still forming (a MIRV's warheads, or a salvo) gets its flash, fireball and shock but no cloud, firestorm, cloud-deck hole or sea waves of its own; the first burst's cloud stands for them all. Its crater, scorch, flattened trees, fallout footprint and damage are unchanged. An 8-warhead MIRV used to build eight volumetric clouds over each other. Settings `salvoMerge`, `salvoMergeWindow`, `salvoMergeRadius`.
- **Less exhaust smoke from the ICBMs.** BDArmory's large exhaust piled up into a huge cloud round a slow, heavy missile leaving the pad. The Minuteman III, R-36M2 (both), SS-20, Pershing II, Trident II, Spartan and Nike Hercules now emit about a third of the smoke, in smaller, shorter-lived puffs. New `ModuleNukeCloudProfile` field `exhaustSmoke`.
- Planner strikes log their flight every 4 s under power (height, speed, the pitch of the velocity, nose and aim, and the predicted range), so a shot that goes wrong can be read back from the log.
- **Sharper, solid-looking clouds.** Caps are now optically thick, scaled to their billows: you see the first rounded lumps, not several translucent layers of them blurred into fuzz. This was worst on megaton caps seen from above. Billows have crisper edges, with a finer layer of lumps on top of them. The folds between lumps are shaded darker and the lump tops lighter, and each lump shades its own far side from the sun. The ray takes finer steps inside the cloud, and a new per-pixel jitter removes the crosshatch pattern. Megaton caps get bigger, fewer billows.
- **Bursts can be seen from far away.** A burst on the Mun, or anywhere the flight scene isn't drawing its cloud, now shows from Kerbin, from orbit, from the space centre and in map view: a blinding flash, the fireball cooling to a red glow, then a sunlit cloud spreading and fading over minutes. Its light travels with the radiation front (`waveSpeed`), so a burst a planet away appears when its radiation arrives, minutes later. Bigger bursts are bigger, brighter points of light. A burst out in space shows its flash, a fading debris shell and a slowly reddening core instead of a cloud. Planets in front hide it, and a cloud in another body's shadow goes dark. Bursts are saved with the game, so a quickload or another save only shows the ones that happened there, and a salvo counts as one burst. Every burst is now recorded and marked on the map even with the alarm (`blastAlert`) off. Setting `distantView`.
- **The blast pushes EVE clouds aside.** The hole in the cloud deck now drags the cloud outward with the shock into a dense rim that travels with the front, and relaxes back as the hole heals. It reaches well past the cap, so it shows around big bursts, and the clouds are redrawn more often while the front is moving.
- **Ground dust and the velum are lit properly.** The base surge, the dust skirt and the velum now shade themselves and are shaded by the rest of the cloud, so they no longer show as flat glowing white discs. The dust is darker at the bottom of the layer and in the folds between its lumps, and the glow from the fireball is blocked by cloud in between. The velum is a thinner veil that fades out toward its rim instead of ending in a hard edge. Its colour and the ambient light are less blue, the ember glow inside the cap is weaker and shorter, and the sunlight on the cloud now follows the game's sun colour.
- Explosion effects follow time warp, including on-rails warp. Clouds, fireballs, EVE cloud holes and debris age at the warped rate.
- **More detailed weapon models.** Every BDNukes part has a finer profile with sharp edges at steps and joints, more painted bands and seams, recessed nozzles, and new detail: cable raceways down the rocket stages, suspension lugs on the bombs, fuzes and box-tail struts on Fat Man and Little Boy, propellers on ASTOR, divert and RCS thrusters on the OKV and OSV, the Davy Crockett's clamps, sight and spotting rifle, and stringers on the Orion magazine. Raceways and fins on a dropped stage fall away with it.
- The ICBMs' boost and cruise motors use the heavier `bdnukes_heavy_motor_loop` sound.
- The Starlance light drive shows a glow you can see in vacuum: a pulsing blue-white ball round the bus while it charges, and a white bloom at the jump.

### Fixed
- **Heavy ICBMs went into space** instead of to the target. BDArmory steers a missile only within its maxAoA of the airflow and with a slowly ramping torque, which couldn't pitch a heavy missile over: it climbed straight up until it was too fast to turn. A steering assist now turns the nose onto the guidance aim at up to 6°/s, held within 15° of the airflow in dense air and freer as the air thins, and the pitch-over now goes by speed as well as height.
- **Frame rate collapsed after a burst under EVE clouds** when EVE changed the shader on a cloud layer's material: the cloud-hole code asked it for its coverage map every frame, and each time Unity logged an error. Such a layer is now let go.
- **Missile plumes pointed sideways** (issue #1). BDArmory's exhaust effects stream along the exhaust transform's +Y axis, which on BDNukes missiles pointed out of the side. They now point straight back.
- **ICBM stages didn't separate** (issue #1). The first stage of Minuteman III, R-36M2 (both), Trident II, Pershing II, SS-20, Spartan and Nike Hercules now visibly falls away at boost burnout, with its nozzles and fins, and the upper stage fires from its own tail.
- **Small bursts had a square dust sheet.** The low dust skirt round a sub-kiloton column ran out past its bounding box and was cut off square. The sheet is smaller, its rim fades out round before the cut-off, and the box (and its downwind stretch) reaches past it.
- The Pershing II's MaRV vanes and stage 1 fins were buried inside the body.
- **Blasts didn't clear EVE / StockVolumetricClouds cloud decks** ("coverage is missing" in the log). EVE's on-demand loader destroys coverage textures on scene and SOI changes and can leave the cloud material holding a dead one. BDNukes now finds the map again, reloading it through EVE's loader if needed, and releases it when the scene ends.
- Fixed a likely crash, and an error logged every frame, when another mod renders a flight camera into a depth or shadow texture after a detonation. BDNukes no longer runs its drawing and frame copy on those renders.
- Parallax scatters no longer float above or sink into fresh craters.
- Effects kept a single-precision clock, so after a few hours of game time clouds stepped and jittered instead of moving smoothly.
- Dropped bombs and released warheads were unloaded once the carrier flew 2–22 km away, taking their laydown, airburst and depth fuzes with them. They now stay in physics until they fire.
- Laydown countdowns ran on physics frames, so time warp and reloads stretched them. They now end at a fixed game time.
- Bumps and hard landings on the carrier no longer trip a carried weapon's impact fuze.
- A SADM put back in an inventory while armed went off as soon as it was placed again. It now comes out safe, and never fires while unloaded.
- The yield lock and the ballistic motor cutoff were forgotten on reload, so a launched weapon's yield could be changed again or its motor relit.
- The B57 broke up on hitting the water before its depth fuze could work.
- The B83 laydown speed was above its crash tolerance, so laydown drops broke the bomb instead of starting the timer.
- The Davy Crockett was silent without BDArmory Extended installed.
- Kerbals killed by radiation were reported dead twice.
- Radiation doses ignored time warp on loaded craft, and radiation fronts missed unloaded craft and other scenes.
- High-altitude EMP could cancel a stronger EMP already hitting a craft.
- A failure inside the vacuum-burst patch could leave BDArmory's air density changed for every later explosion.
- One error while drawing belts, fronts or alerts in map view turned the overlay off for good.
- Restored clouds that had already run out were rebuilt every frame after loading, and restores used the crater floor as the ground.
- Landed craft near a new crater were launched or buried when the terrain was rebuilt under them.
- Photo mode changed the blast sound's pitch for good, and missed the roar.
- Cloud-deck flash meshes leaked, as did flattened-scatter holders for evicted sites.
- Old replays and over-limit craters or flattened sites came back after loading another save.
- Every old crater refreshed nearby scatters on each rebuild, not just the new one.
- Condensation collars could be clipped by the cloud's bounding box.
- EVE cubemap or very large coverage textures broke the blast holes in the cloud deck.
- A malformed fluid-sim bake could be played.
- The cinematic camera didn't give back the player's camera mode when the vessel it was following died.
- The range overlay searched for a weapon every frame on craft without one, and re-traced its rings constantly while flying.
- The Orion pulse drive was listed among the nuclear weapons in the editor.
- Out-of-range values for cloud height, screen flash, tint, camera shake, volume and billow rotation in Settings.cfg are now clamped.
- The packaging script now leaves `PluginData` (your own settings) out of release zips.
- The baked fluid simulation of the fireball never played in game: its frames were looked up by names the shader bundle doesn't use.
- When a blast clears no EVE clouds, the log now says why (no layers on the body, deck out of reach, or an unsupported coverage map).

### Known limitations
- Craters are rebuilt per vertex across the whole terrain quad, so many craters on one body cost load time.
- A fuze delay set on a weapon isn't restored with a cloud after loading.
- In photo mode the fallout cloud and the fallout footprint can both dose the same kerbal.

## 1.1.0

### Added
- **Photo mode:** Alt+F7 cycles the nuclear effects between normal speed, slow motion (`photoModeSlowRate`, default 0.25x) and frozen, without pausing the game, so you can line up screenshots. Blast sounds slow down and pause with them.
- **Thermal scorch on craft:** the flash chars the side of loaded vessels that faces the fireball, out to about 1.2 km x sqrt(yield in kt). Terrain blocks it and parts in the way shade what is behind them. Cosmetic only, and it lasts until the vessel unloads. Setting `craftScorch`.
- **Debris smoke trails:** surface and near-surface bursts on land throw out 12 to 40 chunks of debris on ballistic arcs, two to six fireball radii out, each trailing dark grey-brown smoke that billows and fades, with a glowing orange-white tip for the first second or two. New setting `debrisTrails`.
- **Cloud quality presets and adaptive quality:** a Low / Medium / High / Ultra / Custom preset for the volumetric cloud sets its raymarch steps, resolution and ground shadow in one click; editing any of them by hand switches to Custom. Adaptive quality watches the frame rate while a cloud is on screen and, if it stays under the target (default 50 fps), trims raymarch steps and then the resolution, bringing them back once there is headroom. The settings window shows the current runtime scale. New settings `volumetricQuality`, `adaptivePerformance` and `targetFrameRate`.
- **The fireball lights the world:** terrain and craft now catch the fireball's light. Its light is stronger and reaches further for bigger yields. It takes the fireball's blackbody colour, white in the flash and then yellow, orange and dull red, and fades as the fireball stops glowing. A full-screen pass also adds warm light to everything near the fireball, falling off with distance. Surfaces facing it catch the most, and the sky is left alone. It is capped so a close burst doesn't wash out the screen. New setting `fireballLighting`.
- **Baked mushroom simulation:** small and medium surface bursts (up to 150 kt) play a full Mantaflow bake of the whole cloud forming, not just the fireball: a hot thermal on the ground rolling up into a lumpy column and head, with ground fire and the afterwinds drawing dust up the stem. It plays through most of the climb, then hands over to the procedural cloud. Bake it with `blender -b --factory-startup --python unity/Sim/bake_fireball.py -- mushroom <out_dir>`.
- **Persistent clouds:** every detonation is kept in the save with its universal time. Switch vessels, visit the space centre or reload, and the cloud is rebuilt at the age it has reached on that body. Only the visuals come back: the blast, crater and sound are not replayed. New setting `persistentClouds`.
- **Ground fire:** under a low burst over land, the ground burns for the first tens of seconds, with flame tongues licking up into the stem's foot. New setting `groundFire`.
- **Thermal precursor:** a hot, churning dust layer boils off the ground and races just ahead of the shock along the surface. New setting `precursorDust`.
- **Fallout plume:** for tens of kilotons and up, dust falls out of the cap's underside and drifts downwind in long, soft rain-out streaks.
- **Shock flash through EVE clouds:** while the shock crosses a cloud deck, a bright condensation ring races across it ahead of the clearing.
- **Water bursts:** over the sea the skirt, surge and column are white spray and condensation, not dust.
- **Velum:** in humid air a vast, translucent umbrella of condensation spreads over the rising fireball, like the great white veil in film of the biggest tests.
  - It is two to three and a half cap widths across, wider for big yields, and curves down at its fraying rim.
  - Its underside catches the fire's light. It is stronger under an EVE overcast.
  - New setting: "Condensation veil in humid air".
- **Varied mushroom shapes:** each detonation picks a cap between the wide rolling torus and a tight, tall, bulbous head on a slimmer stem. Small yields lean bulbous.
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
- **The cloud drew over parts of the ship** whose shaders skip Unity's depth texture (mod PBR shaders, transparent parts). The loaded vessels are now drawn into BDNukes' own depth each frame and the cloud stops at them.
- **Craters buried the space centre:** craters now fade out over the KSC and other built-up sites (PQS cities, Kerbal Konstructs statics), so buildings stay on their own ground and can be repaired. Existing craters are masked the next time the terrain loads. New setting `protectSites`.
- **Flat-topped caps:** the baked sim box cut the procedural cap off flat where it rose past the box. Sim and procedural now blend over the box's outer margin. The mushroom bake is smoothed a little more on import, against vertical streaks.
- **The velum** no longer spreads into a solid, glowing slab with a hard edge over big yields. It is capped at 14 km, much fainter, torn into patches with sky through the gaps, and its underside glow is toned down.
- **The stem was clipped to a smooth tube:** an early-out radius cut its billows off. It now leaves room for them.
- **A Tsar Bomba could still throw the camera's craft into space**, at 37,000 km/s: near a huge yield BDArmory's impulse grows without bound, and scaling it down wasn't enough. The speed one nuclear hit can give any part is now capped (`nukeMaxDeltaV`, default 120 m/s, still a violent tumble), and non-finite forces are dropped. Damage is unchanged.
- **Crash with other visual mods:** the volumetric cloud, its shadow and the shockwave were ordinary scene renderers, so other mods' cameras drew them too. When a depth or shadow-map camera reached the shockwave's GrabPass, Unity logged "Invalid surface for grabbing pixels" and could crash with an access violation. All three are now drawn only by the flight cameras' command buffer, and the shockwave reads an explicit copy of the frame instead of a GrabPass.
- The volumetric cloud showed through terrain in front of it where the terrain shader writes no depth (Parallax). Rays now also stop at the ground's sphere at ground zero.
- Reduced-resolution clouds looked blocky and speckled. The upsample is now a 3×3 depth-aware filter that averages away the raymarch jitter, and the cloud takes more steps at reduced resolution.
- Blasts never cleared EVE clouds: EVE's `GetEquirectangularUV` is a private static method, and the lookup only searched instance methods. The log now names any EVE member that can't be found.

### Changed
- **Davy Crockett and other sub-kiloton bursts** rise as a tall, lumpy column of billows nearly as wide as their head, not a mushroom on a thin stalk.
- **The stem** boils with cauliflower lumps sized to the stem itself, so a thin stem is as lumpy as a thick one. Its foot is churned-up dirt rolling in and up.
- **The dust skirt** is a heap of rolling dust billows, not a smooth mound.
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
