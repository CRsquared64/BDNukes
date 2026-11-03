# BDNukes

Cinematic, physically grounded nuclear weapons for [BDArmory Continued](https://github.com/BrettRyland/BDArmory) in Kerbal Space Program 1.12.

BDNukes adds 15 real-world nuclear weapons, from the 20-ton Davy Crockett to the 50-megaton Tsar Bomba. It replaces BDArmory's nuclear visuals and sound with a full effect whose size *and* character follow yield and burst environment, and it fixes several BDArmory nuclear bugs.
BDArmory's blast and thermal damage model is kept; BDNukes corrects its vacuum timing and EMP.

![Ground burst over the Space Center](docs/screenshots/01-ground-burst-mushroom.jpg)

| | |
|---|---|
| ![Fireball and base surge](docs/screenshots/02-ground-burst-fireball-surge.jpg) | ![Megaton cap](docs/screenshots/03-megaton-cap.jpg) |
| ![Megaton pancake cap](docs/screenshots/04-megaton-pancake.jpg) | ![Burst on another world](docs/screenshots/05-other-worlds.jpg) |
| ![Desert burst](docs/screenshots/06-desert-burst.jpg) | ![Vacuum burst](docs/screenshots/08-vacuum-burst.jpg) |

## Weapons
All weapons are on their own **BDNukes** page in the VAB/SPH part list, sorted into Bombs, Missiles, ICBMs & Reentry Vehicles, Nuclear Artillery and Warheads. The page also collects BDArmory's own nuclear parts.

| Weapon | Type | Yield |
|---|---|---|
| M29 Davy Crockett (W54) | recoilless rifle, one-press manual fire | 20 t |
| AIM-47 Falcon (W42) | radar air-to-air missile | 0.25 kt |
| Little Boy / Fat Man | WWII free-fall bombs, airburst fuze | 15 / 21 kt |
| MIM-14 Nike Hercules (W31) | surface-to-air missile | 20 kt |
| B61-12 | guided bomb, selectable yield | 0.3–50 kt |
| AGM-86B ALCM (W80) | cruise missile, selectable yield | 5 / 150 kt |
| Minuteman III + Mk21 RV (W87) | ICBM, plus a standalone reentry vehicle | 300 kt |
| R-36M2 Voevoda | ICBM with 10 MIRVs | 10 × 750 kt |
| B83 | parachute-retarded laydown bomb, selectable yield | 10 kt–1.2 Mt |
| LIM-49 Spartan (W71) | exo-atmospheric ABM | 5 Mt |
| R-36M2 Mod 6 | ICBM, single warhead | 20 Mt |
| AN602 Tsar Bomba | free-fall bomb with retarding chute | 50 Mt |

Real dimensions, masses and burn times. Minimum launch ranges keep AI launchers outside their own blast. Ballistic missiles cut their motors once the trajectory reaches the target.

## Effects
- **Yield-scaled:** fireball radius ~70 m·W^0.4 and cloud top ~5.8 km·W^0.26 (Glasstone & Dolan), with wider caps and longer timelines for megatons.
- **Cloud character, not just size:** styles (auto, desert, oceanic, tower, tropical megaton, Tsar, high altitude) shape the cap, stem, collars and colour. *Auto* reads the burst: ground or air, sea or land, biome, humidity, and whether the cap reaches the tropopause and flattens into a pancake.
- **Atmosphere:** flash, incandescent fireball, condensation rings and Wilson cloud, a visible shock front, a rolling toroidal cap of sun-lit cauliflower billows, a flowing dust stem, a fire-lit skirt and a base surge. Sound and camera shake arrive with the shock, muffled with distance.
- **Water bursts:** a water column, spray dome, white base surge and ring waves.
- **High altitude and vacuum:** a big red rising fireball and airglow, blending into the vacuum look. In vacuum: an X-ray flash, a filamentary debris shell, plasma along the magnetic field, and artificial auroras where field lines meet the air.
- **Aftermath:** a wind-drifted radioactive fallout plume, a scorched and bleached ground scar glowing at night, lingering fires and smoke, and lightning inside megaton clouds.
- **Radiation (optional):** kerbals in fallout or near ground zero take a dose that decays with time. Over 1 Sv brings sickness warnings and 6 Sv is fatal. Probe cores lose charge. Doses are saved with the game. There's a dosimeter and a Geiger crackle.
- **Multiple warheads:** MIRVs spread across a footprint. Many bursts at once automatically use fewer particles.

## Fixes to BDArmory behaviour
- BDArmory Extended's **Small Nuclear Warhead** could never detonate. It now has a full fuze: arm/disarm, a detonate button and action group, impact, proximity, airburst and laydown fuzes.
- **Nuclear EMP did nothing** (a zero-length shielding raycast). It now works: hard EMP inside the thermal radius, recoverable EMP beyond it.
- **Vacuum bursts** timed damage at the sea-level blast speed and applied an air "suck-back" pull. They now use the debris-shell timing and no pull. Pooled explosions also no longer reuse stale air density.
- Bombs carrying a fuze no longer fall off their aircraft at spawn.

## Settings
Open the in-game window from the **BDNukes** toolbar button in flight. Pick a quality preset (Low / Medium / High / Ultra / Custom) or tune individual effects, then save. Saved values go to `GameData/BDNukes/PluginData/Settings.cfg`; the defaults and comments are in `GameData/BDNukes/Settings.cfg`.

## Requirements
- KSP 1.12.x
- BDArmory Continued (1.12.x) and its Physics Range Extender
- Harmony (`000_Harmony`)
- Module Manager
- BDArmory Extended (optional; needed for the Small Nuclear Warhead fix)

## Install
Copy `GameData/BDNukes` into your KSP `GameData` folder.

## Building
```
cd src
dotnet build -c Release
```
The project expects the repo to sit inside your KSP folder (`<KSP>/BDNukes`). Otherwise pass `-p:KSPDir="C:\path\to\Kerbal Space Program"` or set the `KSPDIR` environment variable. Add `-p:SkipInstall=true` while KSP is running.
The build writes `BDNukes.dll` to `GameData/BDNukes/Plugins` and copies the mod folder into your KSP install.

To build a release zip: `powershell -ExecutionPolicy Bypass -File tools/package.ps1` → `release/BDNukes-<version>.zip`.

## Layout
```
src/                      C# plugin (fuzes, parachute, procedural models, effects, Harmony patches)
GameData/BDNukes/         distributable mod folder
  Settings.cfg            all tunables
  Parts/                  the weapons
  BulletDefs/             nuclear round and its ammo
  Patches/                Module Manager patches for BDArmory's own nuclear parts
tools/package.ps1         release packaging
ckan/BDNukes.netkan       CKAN metadata
docs/                     design contract, roadmap, publishing guide
```

## License
MIT. See [LICENSE](LICENSE). BDNukes contains no third-party assets: every mesh, texture and sound is generated in code.
