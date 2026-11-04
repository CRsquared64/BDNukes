# Bakes a nuclear fireball with Blender's Mantaflow fire/smoke solver and exports the frames as raw voxel grids.
#
#   blender -b --factory-startup --python unity/Sim/bake_fireball.py -- <variant> <out_dir>
#
# variant: "ground" (contact burst: the fireball sits on the ground, the cap rolls up and a dust stem follows it)
#          "air"    (free air burst: a ball that rises and rolls into a torus)
#
# Units: 1 Blender unit = 1 fireball radius (R). Blender is z-up; the exporter keeps Blender's axes and the
# Unity importer swaps them. Each exported frame is float32 x/y/z-major grids of density (smoke), flame and
# temperature, plus meta.json with the resolution, the domain bounds (in R) and the frame times (in sim seconds).
import bpy, sys, os, json, struct, math, time

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
VARIANT = argv[0] if argv else "ground"
OUT = argv[1] if len(argv) > 1 else os.path.join(os.path.dirname(__file__), "out", VARIANT)
os.makedirs(OUT, exist_ok=True)
CACHE = os.path.join(OUT, "cache_v5")

ground = VARIANT == "ground"
RES = 112                 # voxels along the longest (vertical) axis
FRAMES = 96              # simulated frames at 24 fps (4 s: the burning fireball stage)
EXPORT_EVERY = 4         # -> 24 exported frames
HALF = 3.0               # domain half width (R): room for the vortex ring to spread
HEIGHT = 8.5             # domain height (R)

# ---- clean scene ----
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.frame_start = 1
scene.frame_end = FRAMES
scene.render.fps = 24

def cube(name, size, loc):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=loc)
    o = bpy.context.active_object
    o.name = name
    o.scale = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return o

# ---- domain ----
zmin = 0.0 if ground else -2.0
dom = cube("Domain", (2 * HALF, 2 * HALF, HEIGHT), (0, 0, zmin + HEIGHT / 2))
mod = dom.modifiers.new("Fluid", type='FLUID')
mod.fluid_type = 'DOMAIN'
ds = mod.domain_settings
ds.domain_type = 'GAS'
ds.resolution_max = RES
ds.use_adaptive_domain = False
ds.cache_directory = CACHE
ds.cache_type = 'ALL'
ds.cache_frame_start = 1
ds.cache_frame_end = FRAMES
ds.cache_data_format = 'OPENVDB'
ds.alpha = 0.3            # smoke density buoyancy: a little lift
ds.beta = 0.75             # heat buoyancy: the hot ball rises and rolls up into a vortex ring
ds.vorticity = 0.55       # vortex confinement: the swirling, folding detail
ds.use_dissolve_smoke = False
ds.burning_rate = 0.28
ds.flame_smoke = 1.6      # dense soot from the burning
ds.flame_vorticity = 0.9
ds.flame_ignition = 1.3
ds.flame_max_temp = 4.0
ds.time_scale = 1.0
# every side open except the ground
ds.use_collision_border_front = False
ds.use_collision_border_back = False
ds.use_collision_border_right = False
ds.use_collision_border_left = False
ds.use_collision_border_top = False
ds.use_collision_border_bottom = ground

# ---- the fireball: already full size (a real one reaches its radius within a second), a hot ball of burning
#      gas that the simulation lifts and rolls into the cap. Ground burst: a hemisphere sitting on the ground ----
src_z = 0.0
bpy.ops.mesh.primitive_uv_sphere_add(radius=1.0, location=(0, 0, src_z), segments=48, ring_count=24)
flow = bpy.context.active_object
flow.name = "Burst"
fm = flow.modifiers.new("Fluid", type='FLUID')
fm.fluid_type = 'FLOW'
fs = fm.flow_settings
fs.flow_type = 'BOTH'          # fire and smoke
fs.flow_behavior = 'INFLOW'
fs.flow_source = 'MESH'
fs.surface_distance = 0.0
fs.volume_density = 1.0
fs.fuel_amount = 2.4
fs.temperature = 3.2
fs.density = 0.85
fs.use_initial_velocity = True
fs.velocity_normal = 0.6        # a last gentle outward push
fs.velocity_random = 0.4
fs.use_plane_init = False
# burn for the first ~0.35 s only
fs.use_inflow = True
fs.keyframe_insert("use_inflow", frame=1)
fs.use_inflow = False
fs.keyframe_insert("use_inflow", frame=4)
# (boolean keyframes already hold their value; Blender 5's layered actions need no interpolation tweak)

if ground:
    # dust and smoke drawn up off the ground after the burst: the start of the stem
    bpy.ops.mesh.primitive_cylinder_add(radius=0.7, depth=0.25, location=(0, 0, 0.13))
    dust = bpy.context.active_object
    dust.name = "Dust"
    dm = dust.modifiers.new("Fluid", type='FLUID')
    dm.fluid_type = 'FLOW'
    d = dm.flow_settings
    d.flow_type = 'SMOKE'
    d.flow_behavior = 'INFLOW'
    d.flow_source = 'MESH'
    d.surface_distance = 0.3
    d.density = 0.45
    d.temperature = 0.45          # warm: drawn up into the rising column
    d.use_inflow = False
    d.keyframe_insert("use_inflow", frame=1)
    d.use_inflow = True
    d.keyframe_insert("use_inflow", frame=12)
    d.use_inflow = False
    d.keyframe_insert("use_inflow", frame=110)

# ---- bake ----
t0 = time.time()
bpy.context.view_layer.objects.active = dom
dom.select_set(True)
with bpy.context.temp_override(object=dom, active_object=dom, selected_objects=[dom]):
    bpy.ops.fluid.bake_data()
print("[bake] simulated %d frames in %.0f s" % (FRAMES, time.time() - t0), flush=True)

# ---- export ----
frames = []
dg = bpy.context.evaluated_depsgraph_get()
res = None
for f in range(EXPORT_EVERY, FRAMES + 1, EXPORT_EVERY):
    scene.frame_set(f)
    dg = bpy.context.evaluated_depsgraph_get()
    ev = dom.evaluated_get(dg)
    eds = ev.modifiers["Fluid"].domain_settings
    r = tuple(eds.domain_resolution)
    dens = eds.density_grid
    flame = eds.flame_grid
    temp = eds.temperature_grid
    n = r[0] * r[1] * r[2]
    if len(dens) != n:
        print("[bake] frame %d: grid size %d != %d, skipped" % (f, len(dens), n), flush=True)
        continue
    res = r
    path = os.path.join(OUT, "frame_%03d.raw" % len(frames))
    with open(path, "wb") as fh:
        for grid in (dens, flame, temp if len(temp) == n else [0.0] * n):
            fh.write(struct.pack("<%df" % n, *grid))
    frames.append((f - 1) / scene.render.fps)
    print("[bake] exported frame %d (%s)" % (f, path), flush=True)

meta = {
    "variant": VARIANT, "resolution": list(res) if res else None,
    "boundsMin": [-HALF, -HALF, zmin], "boundsMax": [HALF, HALF, zmin + HEIGHT],
    "times": frames, "axes": "blender z-up; grids are x fastest? see order",
}
with open(os.path.join(OUT, "meta.json"), "w") as fh:
    json.dump(meta, fh, indent=1)
print("[bake] done: %d frames, resolution %s" % (len(frames), res), flush=True)
