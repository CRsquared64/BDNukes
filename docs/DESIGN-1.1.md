# BDNukes 1.1 design: shared interfaces

This is the contract between the 1.1 work streams. Module names, config fields and file ownership here are fixed.
If you need something that isn't here, add it in **your own files** and note it in your final report. Don't rename anything below.

## Build and environment
- KSP install: `D:\Games\steamapps\common\Kerbal Space Program` (KSP 1.12, Unity 2019.4.18, BDArmory 1.12.x).
- Build from a worktree: `dotnet build src/BDNukes.csproj -c Release -p:KSPDir="D:\Games\steamapps\common\Kerbal Space Program" -p:SkipInstall=true`
  (`SkipInstall=true` stops parallel builds from overwriting the live game install).
- BDArmory source for reference: https://github.com/BrettRyland/BDArmory
  (e.g. `BDArmory/Weapons/Missiles/MissileLauncher.cs`, `BDArmory/Weapons/ModuleWeapon.cs`, `BDArmory/Bullets/BulletInfo.cs`).
- BDArmory part configs to copy guidance/weapon parameters from: `D:\Games\steamapps\common\Kerbal Space Program\GameData\BDArmory\Parts\`
  and `...\GameData\BDArmoryExtended\Parts\` (e.g. `aim-120`, `AGM86-17`, `jdamMk83`, `mk82BombBrake`, `pac-3`, `m102Howitzer`, `browninganm2`).
- BDArmory conventions: missiles and bombs have their **nose along +Z** and attach by their **+Y side**
  (`node_attach = 0, <radius>, 0, 0, 1, 0, 1`). `homingType` strings are: aam, aamlead, aampure, aamloft, agm,
  agmballistic, cruise, weave, sts, orbital/rcs, beamriding, slw, pronav, augpronav, kappa, clos, closthree, closlead.
  Unknown strings (e.g. "none" for dumb bombs) mean no guidance; the missile still goes Drop → Boost → Cruise.
- `BDModuleNuke.Detonate()` refuses while a MissileLauncher is in the Idle or Drop state, so keep `dropTime` small (≤ 0.5 s) on nuclear missiles and bombs.
- BULLET nodes are read from any GameData folder (`GameDatabase.GetConfigs("BULLET")`); `nuclear = true` makes BDArmory call `NukeFX.CreateExplosion` with `tntMass` as the yield in kt.

## Yield-derived BDArmory numbers (use these in every nuke part)
- `yield` = kilotons
- `thermalRadius` = round(650 · W^(1/3)) metres, minimum 100 (20 kt → 1764, 1.2 Mt → 6900, 50 Mt → 23950)
- `fluence` = W
- `isEMP = true`

## Placeholder model
Every procedurally modelled part uses
```
MODEL
{
	model = BDArmory/Parts/ArmorPlate/Panel
	scale = 0.01, 0.01, 0.01
}
```
`ModuleProceduralModel` strips the placeholder's renderers and colliders at load, then builds the real mesh.

## Modules

### ModuleProceduralModel (part framework stream)
Config-driven replacement for `ModuleTsarBombaModel`. Builds a lathed body, fin sets, painted bands and empty named transforms on the part prefab in `OnLoad`, so editor icons, drag cubes and spawned copies all get the mesh.
```
MODULE
{
	name = ModuleProceduralModel
	axis = Z                    // Z: nose +Z, hangs from +Y (missiles/bombs).  Y: nose +Y (vertical/stack parts)
	length = 3.25               // m. The model is centred: nose at +length/2 along the axis, tail at -length/2
	bodyColor = 0.80, 0.72, 0.20
	shininess = 0.3
	panelLines = true
	seamSpacing = 1.0           // m between circumferential seams (0 = none)
	collider = capsule          // capsule | box | none
	PROFILE
	{
		point = 0.00, 0.00      // distanceFromNose (m), radius (m); ordered nose to tail; repeat "point" lines
		point = 0.20, 0.45
		point = 3.25, 0.00
	}
	BAND                        // painted stripe; any number of BAND nodes
	{
		from = 0.4              // distance from nose (m)
		to = 0.5
		color = 1, 0.85, 0.1
	}
	FINS                        // any number of fin sets
	{
		count = 4
		roll = 45               // degrees of the first fin around the axis
		rootFront = 2.6         // distance from nose at the fin root (leading edge)
		rootBack = 3.2
		tipFront = 2.9
		tipBack = 3.25
		span = 0.8              // tip distance from the axis (m)
		thickness = 0.03
		color = 0.3, 0.3, 0.3   // optional; default bodyColor * 0.8
		shroud = none           // none | box | ring : band joining the fin tips (Fat Man box tail = box)
		shroudChord = 0.35      // shroud length along the axis (m)
	}
	TRANSFORM                   // empty transform for BDArmory modules that look one up by name
	{
		name = exhaustTransform
		position = 0, 0, -1.6   // part-local metres
		forward = 0, 0, -1      // direction the transform's +Z faces
	}
}
```
The Tsar Bomba moves to this module, and `ModuleTsarBombaModel` is removed.

### ModuleNukeYieldSelector (part framework stream)
```
MODULE
{
	name = ModuleNukeYieldSelector
	yields = 0.3, 1.5, 10, 50   // kt; chosen in the editor, or in flight before launch
	defaultIndex = 3
	thermalRadiusAtMax = 2400   // thermalRadius at the largest yield; scaled by (y/ymax)^(1/3); fluence = y
}
```
It writes `yield`, `thermalRadius` and `fluence` into the part's `BDModuleNuke`.

### ModuleNukeFuze additions (part framework stream)
New fields on the existing module:
- `laydownDelay` (s, persistent, slider 0–120, 0 = off). Once armed and released, touching down
  (landed, splashed, or a contact below `laydownMaxSpeed`) starts a countdown, then it detonates. While laydown is active,
  the impact fuze only fires above `laydownMaxSpeed`.
- `laydownMaxSpeed` (m/s, default 60).

### ModuleReentryVehicle (part framework stream)
```
MODULE
{
	name = ModuleReentryVehicle
	plasmaMinSpeed = 1500       // m/s: plasma sheath and trail visible above this speed, in atmosphere
	plasmaScale = 1             // size multiplier
	spinRate = 0                // deg/s spin stabilisation after release (0 = off)
}
```
Use it together with stock `ModuleAblator` and an `Ablator` RESOURCE for heating.

### ModuleNukeCloudProfile (cloud FX stream)
Shapes the cloud's *character*, not just its size. It's read from the detonating part (`nukePart`) in the CreateExplosion hook.
```
MODULE
{
	name = ModuleNukeCloudProfile
	style = auto        // auto | desert | oceanic | tower | tropicalMegaton | tsar | highAltitude
	dustiness = -1      // 0 white condensation .. 1 heavy brown/red dust   (-1 = from style/environment)
	capAspect = -1      // cap height/width: ~0.3 flat pancake .. ~1.0 tall cauliflower
	capSpread = -1      // multiplier on cap radius
	stemWidth = -1      // multiplier on stem radius
	stemCollars = -1    // condensation collars around the stem (integer)
	skirtScale = -1     // multiplier on the base dust skirt
	billowScale = -1    // puff size multiplier (smaller = finer cauliflower detail)
	glowScale = -1      // incandescence duration multiplier
	tint =              // optional "r,g,b" dust tint override
}
```
`style = auto` works the look out from the environment: ground vs air burst, ocean vs land, biome
(desert/badlands → red-brown, ice/tundra → white-grey, water → white condensation), humidity near sea
level, and whether the cloud top passes the tropopause (pancake spreading).

### ModuleMIRVBus (cloud FX stream)
```
MODULE
{
	name = ModuleMIRVBus
	warheads = 10       // number of RVs; each detonates with the part's BDModuleNuke yield
	spread = 6000       // m, radius of the impact footprint
	interval = 0.6      // s between successive detonations (±50% random)
}
```
When a part with this module detonates, the CreateExplosion hook adds (warheads − 1) extra BDArmory nuclear
explosions spread across the footprint, at the same height above terrain, with staggered delays.

## File ownership
| Stream | Owns (only edit these) |
|---|---|
| Cloud FX | `src/NukeCinematic.cs`, `src/NukeAssets.cs`, `src/NukeFXManager.cs`, `src/NukeSettings.cs`, `src/BDNukesLoader.cs`, `src/NukeVacuumPhysics.cs`, new `src/ModuleNukeCloudProfile.cs`, new `src/ModuleMIRVBus.cs`, new `src/Nuke*Regime*.cs` if split out, `GameData/BDNukes/Settings.cfg`, new `GameData/BDNukes/Patches/CloudProfiles.cfg` |
| Part framework | new `src/ModuleProceduralModel.cs`, new `src/ModuleNukeYieldSelector.cs`, new `src/ModuleReentryVehicle.cs`, `src/ModuleNukeFuze.cs`, `src/ModuleNukeParachute.cs`, delete `src/ModuleTsarBombaModel.cs`, `GameData/BDNukes/Parts/TsarBomba/`, `GameData/BDNukes/Patches/NuclearWarheadFuze.cfg` |
| Weapons A | `GameData/BDNukes/Parts/FatMan/`, `B61/`, `B83/`, `AIM47/`, `AGM86B/` |
| Weapons B | `GameData/BDNukes/Parts/NikeHercules/`, `Spartan/`, `DavyCrockett/`, `MinutemanIII/`, `R36M2/`, `GameData/BDNukes/BulletDefs/` |

`CHANGELOG.md`, `README.md` and `docs/` are updated by the integrator after merging.
