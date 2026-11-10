# BDNukes

Cinematic, physically grounded nuclear weapons for [BDArmory Plus (BDA+)](https://github.com/BrettRyland/BDArmory) in Kerbal Space Program 1.12.

BDNukes adds 20 real-world nuclear weapon types (21 with Little Boy and Fat Man counted separately), from the 20-ton Davy Crockett to the 50-megaton Tsar Bomba. It replaces BDArmory's nuclear visuals and sound with a full effect whose size *and* character follow yield and burst environment, and it fixes several BDArmory nuclear bugs.
BDArmory's blast and thermal damage model is kept; BDNukes corrects its vacuum timing and EMP.

![Volumetric mushroom clouds](docs/screenshots/11-volumetric-fire.jpg)

| | |
|---|---|
| ![Salvo off an island](docs/screenshots/12-island-salvo.jpg) | ![Tsar Bomba from altitude](docs/screenshots/16-tsar-bomba.jpg) |
| ![Burst over the Space Center](docs/screenshots/17-ksc-burst.jpg) | ![Fireball over the launch stand](docs/screenshots/18-launch-stand-fireball.jpg) |
| ![Water burst and base surge](docs/screenshots/14-water-burst-surge.jpg) | ![Salvo seen from altitude](docs/screenshots/13-salvo-from-altitude.jpg) |
| ![Ocean mushroom](docs/screenshots/15-ocean-mushroom.jpg) | ![MIRV reentry](docs/screenshots/19-mirv-reentry.jpg) |
| ![A kerbal watching a salvo from orbit](docs/screenshots/20-kerbal-watching-from-orbit.jpg) | ![Orbital missile station over the salvo](docs/screenshots/21-orbital-missile-station.jpg) |
| ![Orbital missile platform](docs/screenshots/22-orbital-missile-platform.jpg) | |

<details><summary>1.0 sprite clouds (still used without the volumetric shaders)</summary>

![Ground burst over the Space Center](docs/screenshots/01-ground-burst-mushroom.jpg)

| | |
|---|---|
| ![Fireball and base surge](docs/screenshots/02-ground-burst-fireball-surge.jpg) | ![Megaton cap](docs/screenshots/03-megaton-cap.jpg) |
| ![Megaton pancake cap](docs/screenshots/04-megaton-pancake.jpg) | ![Burst on another world](docs/screenshots/05-other-worlds.jpg) |
| ![Desert burst](docs/screenshots/06-desert-burst.jpg) | ![Vacuum burst](docs/screenshots/08-vacuum-burst.jpg) |

</details>

## Weapons
All weapons are on their own **BDNukes** page in the VAB/SPH part list, sorted into Bombs, Missiles, ICBMs & Reentry Vehicles, Nuclear Artillery and Warheads. The page also collects BDArmory's own nuclear parts.

| Weapon | Type | Yield |
|---|---|---|
| W54 SADM | backpack demolition charge: place on EVA, set a timer, arm | 0.01–1 kt |
| M29 Davy Crockett (W54) | recoilless rifle, one-press manual fire | 20 t |
| AIM-47 Falcon (W42) | radar air-to-air missile | 0.25 kt |
| AIR-2 Genie (W25) | unguided air-to-air rocket, proximity and time fuze | 1.5 kt |
| B57 | depth bomb with a depth fuze, selectable yield | 5–20 kt |
| Mk 45 ASTOR (W34) | nuclear torpedo | 11 kt |
| Little Boy / Fat Man | WWII free-fall bombs, airburst fuze | 15 / 21 kt |
| MIM-14 Nike Hercules (W31) | surface-to-air missile | 20 kt |
| B61-12 | guided bomb, selectable yield | 0.3–50 kt |
| AGM-86B ALCM (W80) | cruise missile, selectable yield | 5 / 150 kt |
| MGM-31C Pershing II (W85) | road-mobile intermediate-range missile, selectable yield | 5–80 kt |
| UGM-133 Trident II D5 | submarine-launched ICBM, cold launch from under water | 8 × 100 kt |
| RSD-10 Pioneer (SS-20) | road-mobile intermediate-range missile with 3 MIRVs | 3 × 150 kt |
| Minuteman III + Mk21 RV (W87) | ICBM, plus a standalone reentry vehicle | 300 kt |
| R-36M2 Voevoda | ICBM with 10 MIRVs | 10 × 750 kt |
| B83 | parachute-retarded laydown bomb, selectable yield | 10 kt–1.2 Mt |
| Starfish Prime package (W49) | stack warhead for your own rocket, burst altitude up to 1,000 km | 1.4 Mt |
| LIM-49 Spartan (W71) | exo-atmospheric ABM | 5 Mt |
| R-36M2 Mod 6 | ICBM, single warhead | 20 Mt |
| AN602 Tsar Bomba | free-fall bomb with retarding chute | 50 Mt |

Fictional strike vehicles, flown from the strike planner (below):

| Vehicle | Type | Yield |
|---|---|---|
| OKV Orbital Interceptor | radar-cued space interceptor with RCS divert | 0.1 kt |
| OSV Orbital Strike Vehicle | orbit-to-ground strike on the current body or its moons | 10 kt |
| Starlance | interplanetary: self-flown ascent, light-drive bus, 10 re-entry vehicles | 10 × 5 Mt |

The **Project Orion** nuclear pulse drive (10 m pusher plate, burning Nuclear Pulse Units) is in Engines.

Real dimensions, masses and burn times. Minimum launch ranges keep AI launchers outside their own blast. Ballistic missiles cut their motors once the trajectory reaches the target.

### Strike planner
Open it from its toolbar button in flight. It lists the strike weapons on the active vessel: BDArmory GPS ballistic missiles and the vehicles above.
1. Pick a body, then a target. You can use Kerbal Konstructs sites (if installed), the stock launch sites, a click in map view or on the ground, typed coordinates, or the point under a satellite.
2. Check that the weapon can reach the target, then launch.
3. Optionally follow the weapon in with the camera, then use the button to go back to the launcher.

Starlance needs a designator: another craft with a probe core or antenna already orbiting the target body.

## Effects
- **Volumetric clouds (Windows / DirectX 11):** the fireball, cap, stem, skirt, surge and condensation are one raymarched volume of self-shadowing billows that flow, roll and drift with the wind. Ground bursts up to 150 kt play a baked Mantaflow simulation of the cloud forming. The fireball lights terrain and craft, the cloud casts its shadow, and the shock front bends the light behind it.
- **Craters:** surface and low bursts dig a persistent crater into the terrain (works with Parallax), leaving the space centre and other built-up sites alone. With EVE, blasts punch healing holes in the cloud decks.
- **Persistent clouds:** switch vessels, visit the space centre or reload, and a cloud carries on from the age it reached.
- **Yield-scaled:** fireball radius ~70 m·W^0.4 and cloud top ~5.8 km·W^0.26 (Glasstone & Dolan), with wider caps and longer timelines for megatons.
- **Cloud character, not just size:** styles (auto, desert, oceanic, tower, tropical megaton, Tsar, high altitude) shape the cap, stem, collars and colour. *Auto* reads the burst: ground or air, sea or land, biome, humidity, and whether the cap reaches the tropopause and flattens into a pancake.
- **Atmosphere:** flash, incandescent fireball, condensation rings and Wilson cloud, a visible shock front, a rolling toroidal cap of sun-lit cauliflower billows, a flowing dust stem, a fire-lit skirt and a base surge. Sound and camera shake arrive with the shock, muffled with distance.
- **Water bursts:** a water column, spray dome, white base surge and ring waves.
- **High altitude and vacuum:** a big red rising fireball and airglow, blending into the vacuum look. In vacuum: an X-ray flash, a filamentary debris shell, plasma along the magnetic field, and artificial auroras where field lines meet the air.
- **Aftermath:** a wind-drifted radioactive fallout plume, a scorched and bleached ground scar glowing at night, lingering fires and smoke, and lightning inside megaton clouds.
- **Radiation (optional):** kerbals in fallout or near ground zero take a dose that decays with time. Over 1 Sv brings sickness warnings and 6 Sv is fatal. Probe cores lose charge. Doses are saved with the game. There's a dosimeter and a Geiger crackle.
- **Multiple warheads:** MIRVs spread across a footprint. Many bursts at once automatically use fewer particles.
- **Early time:** rope-trick spikes under tower and low bursts, a Mach stem dust ring under air bursts, and EVE cloud decks lit by the fireball.
- **World effects:** forests flattened out to about 5 psi (with Parallax), a downwind fallout footprint that fades over game days, and firestorms after big bursts over land.
- **Space:** bursts above the atmosphere make artificial radiation belts that drain panels and probes and dose kerbals, high-altitude EMP over the horizon circle below, and a radiation front that crosses the whole system, drawn in map view, at a slower-than-light `waveSpeed` so you can watch it arrive.
- **Detonation alert:** any burst in the system sounds an alarm and opens a window with the yield, the range and the front's countdown to you and each planet. Burst sites, footprints, belts and fronts are drawn in map view and the tracking station.
- **Time warp:** clouds, fireballs and fallout age at the warped rate, including on-rails warp.

## Tools and hotkeys
| Key | What it does |
|---|---|
| Alt+F6 | Cinematic camera on/off (frames and orbits detonations near you; off by default, `cinematicCamera`) |
| Alt+F7 | Photo mode: slows or freezes the effects without pausing the game |
| Alt+F8 | Effects-range overlay: fireball, 20/5/1 psi and thermal rings around your nuke's predicted impact point |
| Alt+F10 | Replay the last detonation or salvo (visuals and sound only) |

The keys can be changed in `Settings.cfg` (`cinematicCameraKey`, `rangeOverlayKey`, `replayKey`).

## Fixes to BDArmory behaviour
- BDArmory Extended's **Small Nuclear Warhead** could never detonate. It now has a full fuze: arm/disarm, a detonate button and action group, impact, proximity, airburst and laydown fuzes.
- **Nuclear EMP did nothing** (a zero-length shielding raycast). It now works: hard EMP inside the thermal radius, recoverable EMP beyond it.
- **Vacuum bursts** timed damage at the sea-level blast speed and applied an air "suck-back" pull. They now use the debris-shell timing and no pull. Pooled explosions also no longer reuse stale air density.
- Bombs carrying a fuze no longer fall off their aircraft at spawn.

## Settings
Open the in-game window from the **BDNukes** toolbar button in flight. Pick a quality preset (Low / Medium / High / Ultra / Custom) or tune individual effects, then save. Saved values go to `GameData/BDNukes/PluginData/Settings.cfg`; the defaults and comments are in `GameData/BDNukes/Settings.cfg`.

## Requirements
- KSP 1.12.x
- BDArmory Plus (BDA+) 1.12 or later, with Physics Range Extender. The old BDArmory Continued 1.3.x line is not supported
- Harmony (`000_Harmony`)
- Module Manager
- BDArmory Extended (optional; needed for the Small Nuclear Warhead fix)
- EVE and Parallax are optional; BDNukes works with them. The volumetric cloud needs Windows (DirectX 11); elsewhere the sprite cloud is used.

## Install
Copy `GameData/BDNukes` into your KSP `GameData` folder.

## Building
```
cd src
dotnet build -c Release
```
The project expects the repo to sit inside your KSP folder (`<KSP>/BDNukes`). Otherwise pass `-p:KSPDir="C:\path\to\Kerbal Space Program"` or set the `KSPDIR` environment variable. Add `-p:SkipInstall=true` while KSP is running.
The build writes `BDNukes.dll` to `GameData/BDNukes/Plugins` and copies the mod folder into your KSP install.

To check the strike and interplanetary orbit maths without KSP: `dotnet run -c Release --project tests/StrikeMath`.

To build a release zip: `powershell -ExecutionPolicy Bypass -File tools/package.ps1` → `release/BDNukes-<version>.zip`.

## Layout
```
src/                      C# plugin (fuzes, parachute, procedural models, effects, Harmony patches)
GameData/BDNukes/         distributable mod folder
  Settings.cfg            all tunables
  Parts/                  the weapons
  BulletDefs/             nuclear round and its ammo
  Patches/                Module Manager patches for BDArmory's own nuclear parts
tests/StrikeMath/         standalone checks of the strike orbit maths
tools/package.ps1         release packaging
ckan/BDNukes.netkan       CKAN metadata
docs/                     design contract, roadmap, publishing guide
```

## License
MIT. See [LICENSE](LICENSE). BDNukes contains no third-party assets: every mesh, texture and sound is generated in code.
