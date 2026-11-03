# BDNukes roadmap: candidate nuclear weapons

Ten real-world systems that fit BDArmory gameplay, plus bonus ideas, with the specs to build them and
the effect each one gets. Specs come from public sources (see bottom). Numbers that disagree between
sources are given as ranges or marked "~"; check them again before modelling.

## How effects scale (applies to every addition automatically)

The cinematic engine (`NukeCinematic`) derives everything from **yield W (kt)** and the **burst
environment** at the moment of detonation. A new part only needs a `BDModuleNuke` with the right
`yield`. It gets a matching fireball, cap, stem, rings, sound and shake, and the right regime.

| Regime | Trigger at detonation | What you see |
|---|---|---|
| **Ground / low air burst** | height above ground < 3R | Fireball dome sitting on the ground. Fire-lit dust skirt, base surge racing outwards, dust stem sucked up from the ground into the cap. |
| **Air burst** | 3R – 6R | Fireball rises and rolls into a cap. A smoke stem trails beneath it; no ground dust column. The shock still raises a faint surge ring. |
| **High air burst** | > 6R, air density > 0.05 kg/m³ | A free-floating fireball → toroidal "smoke ring" cloud with no stem. Condensation rings if the air is dense enough. |
| **Vacuum** | air density < 0.05 kg/m³ | X-ray flash, filamentary debris shell, magnetic-field streamers, artificial aurora (if above an atmosphere). Damage is timed by the debris shell. No sound. |

R = fireball radius ≈ 70 m·W^0.4. Cloud top ≈ 5.8 km·W^0.26 × `cloudHeightScale` (0.6 for Kerbin).
Cap radius = max(0.28·top, 0.45·top·(W/1000)^0.15). Rise and lifetime scale by (W/20)^0.12.

Calculated effect sizes for every candidate yield (default settings):

| Weapon | Yield | Fireball R | Ground contact below | Cloud top | Cap radius | Rise time |
|---|---|---|---|---|---|---|
| M388 Davy Crockett (W54) | 0.01–0.02 kt | 10 m | 40 m | 1.3 km | 0.4 km | 90 s |
| AIM-47 Falcon (W42) | 0.25 kt | 40 m | 120 m | 2.4 km | 0.7 km | 90 s |
| MIM-14 Nike Hercules (W31) | 20 kt | 230 m | 0.7 km | 7.6 km | 2.1 km | 90 s |
| Fat Man (Mk 3) | 21 kt | 240 m | 0.7 km | 7.7 km | 2.2 km | 91 s |
| B61-12 (max setting) | 50 kt | 330 m | 1.0 km | 9.6 km | 2.8 km | 100 s |
| AGM-86B ALCM (W80) | 150 kt | 520 m | 1.6 km | 12.8 km | 4.3 km | 115 s |
| Minuteman III Mk21 RV (W87) | 300 kt | 690 m | 2.1 km | 15.3 km | 5.8 km | 125 s |
| R-36M2 MIRV RV | 550–800 kt | ~1.0 km | 3.0 km | 19.5 km | 8.4 km | 139 s |
| B83 | 1.2 Mt | 1.2 km | 3.6 km | 22.0 km | 10.2 km | 147 s |
| LIM-49 Spartan (W71) | 5 Mt | 2.1 km | 6.3 km | 31.9 km | 18.3 km | 175 s |
| R-36M2 single RV (Mod 6) | 20 Mt | 3.7 km | 11.0 km | 45.7 km | 32.2 km | 206 s |
| Tsar Bomba (shipped) | 50 Mt | 5.3 km | 15.9 km | 58.0 km | 46.9 km | 230 s |

## The ten candidates

### 1. Fat Man (Mk 3), the WWII implosion bomb
- **Real specs:** 21 kt (±10%), 3.25 m long, 1.52 m diameter, ~4,656 kg. Airburst at ~503 m over Nagasaki.
- **Build:** a free-fall bomb with the distinctive bulbous body and box tail ("California parachute" fins). Barometric airburst fuze defaulting to 500 m. A procedural model like the Tsar Bomba.
- **Effects:** at 500 m it's just inside the ground-contact limit (0.7 km), so you get the iconic picture: a towering stem drawn up from the ground into a 7.7 km cap. Surface burst: a dirtier, browner stem with a big base surge. Dropped in space: a small vacuum flash with a modest debris shell.
- **Pair option:** Little Boy (~15 kt, 3.0 m, ~4,400 kg) as a gun-type twin.

### 2. B61-12, modern variable-yield gravity bomb
- **Real specs:** ~3.6 m long, ~0.33 m diameter, ~320–375 kg. Variable yield, reported as 0.3 / 1.5 / 10 / 50 kt. Guided tail kit.
- **Build:** a slim bomb with a **yield selector in the right-click menu** (a new KSPField that sets `BDModuleNuke.yield` before launch). Use BDArmory's guided-bomb `MissileLauncher` (JDAM-style), so AI pilots can use it.
- **Effects:** the same bomb shows four very different explosions. 0.3 kt is a small rising fireball and a stubby cloud. 50 kt is a full 9.6 km mushroom. It shows off the yield scaling well.

### 3. B83, megaton-class laydown bomb
- **Real specs:** up to 1.2 Mt (variable down to low kt), 3.7 m long, 0.46 m diameter, ~1,100 kg. Can be parachute-retarded for **laydown** (it lands, then detonates after a delay).
- **Build:** reuse `ModuleNukeParachute` (a small chute) plus a new **laydown fuze**: detonate N seconds after touchdown, set in the menu.
- **Effects:** a true surface burst. The fireball dome rests on the ground, the dust stem is heaviest, the base surge reaches ~12 km, and the cloud tops out ~22 km. A good test case for the ground regime at megaton scale.

### 4. AIM-47 Falcon (W42), nuclear air-to-air missile
- **Real specs:** ~3.8 m, ~370 kg, Mach ~6, ~160 km range. Designed for the YF-12 / XF-108 interceptors. W42 warhead ~0.25 kt. Never deployed (verify these figures before modelling; few sources).
- **Note:** the only nuclear air-to-air missiles ever fielded were AIR-2 Genie and AIM-26 Falcon, and both are already in BDArmory / BDArmory Extended. AIM-47 is the natural third.
- **Build:** a BDArmory radar-guided `MissileLauncher` with a proximity fuze sized to the yield. Its weapon range should keep the launcher outside its own blast.
- **Effects:** usually a high air burst, giving a free-floating fireball and a small smoke ring with no stem. Fired at a low target, it gets a short smoke trail.

### 5. AGM-86B ALCM (W80), air-launched cruise missile
- **Real specs:** 6.3 m, ~0.62 m diameter, ~1,450 kg, selectable yield 5 / 150 kt (W80: 80 cm, 130 kg).
- **Build:** a BDArmory cruise-guidance `MissileLauncher` that flies terrain-following, with pop-out wings. Yield selector as for the B61.
- **Effects:** a low-level airburst, so the stem is drawn up from the ground, with a 12.8 km cloud at 150 kt. Set to 5 kt it's a mid-size mushroom.

### 6. MIM-14 Nike Hercules (W31), nuclear surface-to-air missile
- **Real specs:** ~12.5 m, ~4,860 kg two-stage (verify). W31 in 2 / 20 / 40 kt variants (20 kt for Nike).
- **Build:** a two-stage ground-launched SAM using BDArmory's surface-to-air guidance and a radar-proximity fuze. A launcher rail part.
- **Effects:** the high-air-burst regime. A fireball blooms in the sky among bomber formations and leaves a smoke ring with no stem. A **flak-style shock shell** kills a whole formation. Detonated low, it switches to the air-burst stem.

### 7. LIM-49 Spartan (W71), exo-atmospheric ABM
- **Real specs:** ~16.8 m, ~13,100 kg, three stages. W71 ~5 Mt, designed for X-ray kills of reentry vehicles *outside* the atmosphere.
- **Build:** a silo-launched interceptor with BDArmory guidance against high-altitude targets (reentry vehicles from candidates 9 and 10).
- **Effects:** the showcase for the **vacuum regime**. A blinding X-ray flash visible from the ground, a huge filamentary debris shell, plasma along the field lines, and auroras where the field lines meet the atmosphere. Also motivates the "high-altitude" engine work below.

### 8. M388 Davy Crockett (W54), recoilless-rifle nuke
- **Real specs:** W54 yield 10–20 **tons** (0.01–0.02 kt). Warhead ~23 kg, round ~34.5 kg. Fired from an M28/M29 recoilless rifle, range ~2–4 km.
- **Build:** a BDArmory `ModuleWeapon` (a small tripod gun) with a `BULLET` definition using `nuclear = true` (BDArmory already supports nuclear shells, and BDNukes already renders them).
- **Effects:** the low end of the scale. A ~10 m fireball, a small fast cloud and a short dusty stem. Nearly always a ground burst, so most of what you see is dust. Fun to compare with the Tsar.

### 9. LGM-30G Minuteman III with Mk21 RV (W87), ICBM
- **Real specs:** ~18.3 m, ~35,300 kg, three solid stages. W87 ~300 kt in a Mk21 reentry vehicle (or 3 × W78 335 kt in Mk12A MIRVs).
- **Build:** start with the **reentry vehicle** as a standalone part. It needs an ablative heat shield, `ModuleNukeFuze` with airburst and impact fuzes, and a plasma reentry trail. It can then be put on any launcher or used with BDArmory's modular missile guidance. Optional booster stages as parts.
- **Effects:** ground or air burst depending on the fuze setting, with a 15 km cloud. Suborbital tests over the KSC are great for watching the reentry trail and then the flash.

### 10. R-36M2 "Voevoda" (SS-18 "Satan"), heavy MIRV ICBM
- **Real specs:** ~34.3 m, ~211,000 kg. 10 × ~550–800 kt MIRVs (sources differ), or a single ~20 Mt RV (Mod 6).
- **Build:** a **MIRV bus** part that releases N RV parts with timed separation and its own guidance to spread the aim points. Also a single 20 Mt variant.
- **Effects:** 10 simultaneous ~800 kt bursts make a field of mushroom clouds across a city-sized area. The 20 Mt RV approaches Tsar scale (46 km cloud). This needs the **MIRV performance** engine work below.

### Bonus ideas
- **Status-6 "Poseidon":** a nuclear-powered autonomous torpedo, reported at up to ~2 Mt (figures are speculative). Needs the **water burst** regime.
- **UGM-133 Trident II D5:** an SLBM with W76 (~100 kt) or W88 (~475 kt) MIRVs, up to 8–14. Needs a submerged launch.
- **Little Boy:** ~15 kt, the gun-type partner to Fat Man.
- **Thor / W49 "Starfish Prime":** 1.45 Mt at 400 km. A pure vacuum and aurora spectacle.
- **RUR-5 ASROC / UUM-44 SUBROC:** W44 / W55 nuclear depth bombs. Water burst regime.

## Engine work some of these need

1. **Water surface and underwater burst regime** (Poseidon, ASROC, SUBROC, B83 over sea).
   Modelled on Operation Crossroads "Baker": a hollow water column (chimney) collapsing into a dense
   white **base surge** of spray, a Wilson cloud dome, a cauliflower top of spray rather than dust,
   and a ring of waves. Triggered by burst depth and `FlightGlobals.getAltitudeAtPos < 0` over an ocean.
2. **High-altitude transitional regime** (roughly 0.005–0.05 kg/m³; ~25–45 km on Kerbin): Spartan / Nike at altitude.
   The fireball grows much larger and redder (as in the 1958 Teak and Orange tests) and rises fast with no mushroom.
   A large red airglow ring plus aurora. Blend between the air and vacuum effects on density instead of switching hard.
3. **MIRV and many-burst level of detail:** when more than ~3 detonations are active, scale particle counts by
   1/√N, merge sounds, and cap total screen flash, to keep framerate up during Voevoda salvos.
4. **Yield selector / dial-a-yield:** a shared PartModule that edits `BDModuleNuke.yield`, `thermalRadius`
   and `fluence` together from one menu slider (B61, ALCM, B83).
5. **Laydown fuze:** detonate N seconds after touchdown (B83).
6. **Reentry vehicle visuals:** a plasma sheath trail and an ablative shield (ICBM RVs).
7. **Low-yield tuning:** below ~0.1 kt, skip the condensation rings and shorten the stem. A Davy Crockett cloud
   should look like a fat dusty puff, not a miniature Tsar.

## Sources
- B61: https://en.wikipedia.org/wiki/B61_nuclear_bomb · https://www.airandspaceforces.com/weapons-platforms/b61/
- B83: https://en.wikipedia.org/wiki/B83_nuclear_bomb
- W71 / Spartan: https://en.wikipedia.org/wiki/W71 · https://en.wikipedia.org/wiki/LIM-49_Spartan
- R-36M2: https://en.wikipedia.org/wiki/R-36_(missile) · https://www.globalsecurity.org/wmd/world/russia/r-36m-specs.htm · https://missilethreat.csis.org/missile/ss-18/
- ICBM comparison (Minuteman III, Trident II masses / MIRVs): https://en.wikipedia.org/wiki/Comparison_of_ICBMs
- W31 / Nike Hercules: https://en.wikipedia.org/wiki/W31 · https://en.wikipedia.org/wiki/MIM-14_Nike_Hercules
- W54 / Davy Crockett: https://en.wikipedia.org/wiki/W54 · https://www.airandspaceforces.com/article/0910nukes/
- AGM-86 / W80: https://en.wikipedia.org/wiki/AGM-86_ALCM · https://en.wikipedia.org/wiki/W80_(nuclear_warhead)
- Fat Man / Little Boy: https://en.wikipedia.org/wiki/Little_Boy · https://NuclearWeaponArchive.org/Usa/Med/Lbfm.html
- Scaling laws: Glasstone & Dolan, *The Effects of Nuclear Weapons* (1977).
