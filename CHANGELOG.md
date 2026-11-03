# Changelog

## 1.0.0 (unreleased)

First release.

### Added
- Cinematic nuclear detonations for all BDArmory nukes (missiles, warheads, nuclear shells, reactor cores),
  scaled by yield. Covers the flash, fireball, condensation rings, Wilson cloud, shock front, rolling
  toroidal cap, flowing dust stem, dust skirt, base surge, delayed sound and camera shake, and screen glow.
- Vacuum detonations: X-ray flash, filamentary debris-plasma shell, magnetic-field streamers, artificial aurora.
- AN602 Tsar Bomba (50 Mt): a procedurally modelled casing, a 1600 m² retarding parachute and a 4 km barometric airburst fuze.
- `ModuleNukeFuze`: arm/disarm, detonate button and action group, impact, proximity and airburst-altitude fuzes.
- `ModuleNukeParachute`: real drag retarding chute with a generated canopy.
- `Settings.cfg` with all tunables.

### Fixed
- BDArmory Extended "Small Nuclear Warhead" could never detonate.
- Vacuum bursts used BDArmory's air-blast timing and an air "suck-back" pull. They now use debris-shell timing and no pull.
- Pooled BDArmory nuke explosions kept the air density from the previous detonation.
