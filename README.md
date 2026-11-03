# BDNukes

Cinematic, physically grounded nuclear weapons for [BDArmory Continued](https://github.com/BrettRyland/BDArmory) in Kerbal Space Program 1.12.

BDNukes replaces BDArmory's nuclear visuals and sound with a full effect that scales with yield. It fixes nuclear warheads that never detonated, makes vacuum detonations physically correct, and adds the AN602 **Tsar Bomba**.
BDArmory's damage model is kept. Only the presentation changes, plus a few corrections to vacuum physics.

## Features

### Atmospheric detonations
- A blinding flash, then a white-hot fireball that cools through yellow, orange and red while a warm glow covers the screen.
- Stacked condensation rings and a brief Wilson cloud, lit orange by the fireball. A visible shock front expands at the speed of sound.
- The fireball rises and rolls into a churning toroidal mushroom cap built from hundreds of lit cauliflower billows, each turned to face the sun.
- A swirling dust stem flows up into the cap, a fire-lit dust skirt forms at the base and a base surge races across the ground.
- Sound and camera shake arrive with the shock: a crack and thump, then a long rolling rumble, muffled with distance.
- Sizes follow Glasstone & Dolan, *The Effects of Nuclear Weapons*: fireball radius ~70 m·W^0.4 and cloud top ~5.8 km·W^0.26, with wider caps and longer timelines for megaton yields.

### Vacuum detonations
- An X-ray flash that blinds from very far away, then a filamentary debris-plasma shell that cools white → violet → red.
- Plasma spirals out along the planet's (dipole) magnetic field. Bursts high above an atmosphere create red/green artificial auroras where the field lines enter the air, like Starfish Prime.
- No sound, unless the debris shell hits your own craft.
- **Physics:** hits arrive with the debris shell rather than BDArmory's sea-level blast speed, and the air "suck-back" pull is removed. Also fixed: pooled explosions kept the air density from their previous detonation.

### Fixes and additions
- **BDArmory Extended "Small Nuclear Warhead" now works.** `ModuleNukeFuze` adds Arm/Disarm, a Detonate button and action group, impact, proximity (up to 3 km) and airburst-altitude fuzes, and detonation when destroyed while armed.
- **AN602 Tsar Bomba (50 Mt).** A procedurally modelled 8 m × 2.1 m, 27 t casing. Release it by staging; a 1600 m² retarding chute opens after 2 s, and the barometric fuze airbursts it at 4 km.

## Requirements
- KSP 1.12.x
- BDArmory Continued (built against 1.12.x)
- Harmony (`000_Harmony`)
- Module Manager
- BDArmory Extended (optional; needed for the Small Nuclear Warhead fix)

## Install
Copy `GameData/BDNukes` into your KSP `GameData` folder. Tune everything in `GameData/BDNukes/Settings.cfg`.

## Building
```
cd src
dotnet build -c Release
```
The project expects the repo to sit inside your KSP folder (`<KSP>/BDNukes`). Otherwise pass `-p:KSPDir="C:\path\to\Kerbal Space Program"` or set the `KSPDIR` environment variable.
The build writes `BDNukes.dll` to `GameData/BDNukes/Plugins` and copies the mod folder into your KSP install.

To build a release zip: `powershell -ExecutionPolicy Bypass -File tools/package.ps1` → `release/BDNukes-<version>.zip`.

## Layout
```
src/                      C# plugin (fuze, parachute, Tsar model, cinematic FX, Harmony patches)
GameData/BDNukes/         distributable mod folder
  Settings.cfg            all tunables
  Patches/                Module Manager patches for existing BDArmory parts
  Parts/TsarBomba/        Tsar Bomba part
tools/package.ps1         release packaging
ckan/BDNukes.netkan       CKAN metadata draft
docs/PUBLISHING.md        GitHub release → forum → CKAN steps
docs/ROADMAP.md           researched candidate weapons and the engine work they need
```

## License
MIT. See [LICENSE](LICENSE). BDNukes contains no third-party assets: every mesh, texture and sound is generated in code.
