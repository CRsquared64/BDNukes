# Writes GameData/BDNukes/Parts/BombBays/*.cfg: the bomb bay parts (ModuleBombBay builds the model and the doors).
# Usage: python tools/make_bomb_bays.py
import os, math
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'GameData', 'BDNukes', 'Parts', 'BombBays')
os.makedirs(OUT, exist_ok=True)

# name, title, shape, length, width, height, corner, node size, bulkhead profile, tech, description of what it carries
# Each bay is skinned from its counterpart's own texture: a patch of it (x, y, w, h in pixels from the top left)
# tiled over the bay at `density` pixels per metre. The inside uses a second patch where the counterpart has an
# interior (the Mk2 cargo bay's ribs, the rectangular fuselage's hex plating), else the outside's patch darkened.
SKINS = {
    # Mk1 fuselage (Squad)
    'mk1': dict(texture='Squad/Parts/Structural/mk1Parts/Mk1Structural', texturePatch='520, 440, 490, 320', textureDensity=125),
    # the rectangular 2 m fuselage (RetroFuture's rect2m)
    'rect': dict(texture='RetroFuture/StockScheme/rectFuselage_DIF', texturePatch='240, 160, 360, 395', textureDensity=180,
                 innerTexture='RetroFuture/StockScheme/rectFuselage_DIF', innerPatch='5, 5, 222, 710', innerDensity=200),
    # the Mk2 fuselage, with the Mk2 cargo bay's own ribbed interior
    'mk2': dict(texture='Squad/Parts/FuelTank/mk2FuselageLong/mk2Fuselage', texturePatch='300, 380, 135, 600', textureDensity=165,
                innerTexture='Squad/Parts/Utility/mk2CargoBay/mk2CargoBay', innerPatch='305, 235, 280, 780', innerDensity=160),
    # the 2.5 m Rockomax tank
    'size2': dict(texture='Squad/Parts/FuelTank/RockomaxTanks/Assets/rockomax_16 [AlbedoM] BW', texturePatch='1050, 70, 830, 440', textureDensity=300,
                  innerTexture='Squad/Parts/FuelTank/RockomaxTanks/Assets/rockomax_16 [AlbedoM] BW', innerPatch='1050, 1480, 250, 480', innerDensity=300),
}
SKIN_OF = {'bdnukesBayMk1S': 'mk1', 'bdnukesBayMk1': 'mk1', 'bdnukesBayRectS': 'rect', 'bdnukesBayRect': 'rect',
           'bdnukesBayMk2': 'mk2', 'bdnukesBayMk2Long': 'mk2', 'bdnukesBay25': 'size2', 'bdnukesBay25Long': 'size2'}

BAYS = [
    ('bdnukesBayMk1S', 'Mk1 Bomb Bay, Short', 'round', 3.3, 1.25, 1.25, 0, 1, 'size1', 'advAerodynamics',
     'A short 1.25 m round bomb bay in the Mk1 fuselage\'s width: one B57, Little Boy or Genie.'),
    ('bdnukesBayMk1', 'Mk1 Bomb Bay', 'round', 6.6, 1.25, 1.25, 0, 1, 'size1', 'advAerodynamics',
     'A long 1.25 m round bomb bay in the Mk1 fuselage\'s width: two bombs end to end.'),
    ('bdnukesBayRectS', 'Rectangular Bomb Bay, Short', 'rect', 3.3, 1.25, 1.36, 0.3, 1, 'mk1rect', 'advAerodynamics',
     'A short bomb bay in the rectangular 2 m fuselage\'s cross-section, for one B57, Little Boy or Genie; it stacks with the rectangular fuselage parts.'),
    ('bdnukesBayRect', 'Rectangular Bomb Bay', 'rect', 6.12, 1.25, 1.36, 0.3, 1, 'mk1rect', 'advAerodynamics',
     'A long bomb bay in the rectangular fuselage\'s cross-section, three of its 2.04 m lengths: room for two bombs.'),
    ('bdnukesBayMk2', 'Mk2 Bomb Bay', 'rect', 4.0, 2.5, 1.35, 0.5, 1, 'mk2', 'highAltitudeFlight',
     'A wide, low bomb bay in the Mk2 fuselage\'s cross-section, for the B57, B61-12, B83 and Little Boy.'),
    ('bdnukesBayMk2Long', 'Mk2 Bomb Bay, Long', 'rect', 8.0, 2.5, 1.35, 0.5, 1, 'mk2', 'highAltitudeFlight',
     'The Mk2 bomb bay at twice the length.'),
    ('bdnukesBay25', '2.5 m Bomb Bay', 'round', 4.0, 2.5, 2.5, 0, 2, 'size2', 'heavyAerodynamics',
     'A 2.5 m round bomb bay for Fat Man and every lighter bomb.'),
    ('bdnukesBay25Long', '2.5 m Bomb Bay, Long', 'round', 8.5, 2.5, 2.5, 0, 2, 'size2', 'heavyAerodynamics',
     'A long 2.5 m round bomb bay: long enough for the AN602 Tsar Bomba.'),
]

TEMPLATE = '''// {title}. Part of the BDNukes bomb bays (tools/make_bomb_bays.py writes this file).
// A hollow fuselage section with belly doors that works like a cargo bay: attach a weapon to an inner node or
// surface-attach it to the inside of the roof, then open the doors to drop it. ModuleBombBay builds the model and the
// door animation in code; MODEL below is only a placeholder that is stripped at load. Doors start open (like the stock
// bays) so a weapon can be placed inside; Close them in flight to shield what is inside.
PART:NEEDS[BDArmory]
{{
	name = {name}
	module = Part
	author = BDNukes

	MODEL
	{{
		model = BDArmory/Parts/ArmorPlate/Panel
		scale = 0.01, 0.01, 0.01
	}}
	rescaleFactor = 1

	node_stack_top = 0.0, {top:.3f}, 0.0, 0.0, 1.0, 0.0, {nodesize}
	node_stack_bottom = 0.0, {bottom:.3f}, 0.0, 0.0, -1.0, 0.0, {nodesize}
	node_stack_top2 = 0.0, {itop:.3f}, 0.0, 0.0, -1.0, 0.0, {nodesize}
	node_stack_bottom2 = 0.0, {ibottom:.3f}, 0.0, 0.0, 1.0, 0.0, {nodesize}

	TechRequired = {tech}
	entryCost = {entry}
	cost = {cost}
	category = Payload
	subcategory = 0
	title = {title}
	manufacturer = BDNukes
	description = {desc} Its doors open downwards. Weapons go inside on the inner nodes, or surface-attached to the inside of the roof.
	tags = bomb bay cargo hold payload weapon BDNukes
	attachRules = 1,0,1,1,1
	mass = {mass:.3f}
	dragModelType = default
	maximum_drag = 0.2
	minimum_drag = 0.3
	angularDrag = 1
	crashTolerance = 40
	breakingForce = 100
	breakingTorque = 100
	maxTemp = 2500
	emissiveConstant = 0.8
	fuelCrossFeed = True
	bulkheadProfiles = {bulkhead}

	MODULE
	{{
		name = ModuleBombBay
		shape = {shape}
		length = {length}
		width = {width}
		height = {height}
		cornerRadius = {corner}
		animationName = BombBayDoors
{skin}	}}
	MODULE
	{{
		name = ModuleAnimateGeneric
		animationName = BombBayDoors
		startEventGUIName = Close
		endEventGUIName = Open
		actionGUIName = Toggle Bay Doors
		allowDeployLimit = true
		revClampDirection = false
		revClampSpeed = true
		revClampPercent = true
	}}
	MODULE
	{{
		name = ModuleCargoBay
		DeployModuleIndex = 1
		closedPosition = 1
		lookupRadius = {look:.2f}
		partTypeName = Bomb bay
		nodeOuterForeID = top
		nodeOuterAftID = bottom
		nodeInnerForeID = top2
		nodeInnerAftID = bottom2
	}}
}}
'''

BOMBS = [('B57', 3.0, 0.19), ('Little Boy', 3.0, 0.36), ('B61-12', 3.6, 0.17), ('B83', 3.7, 0.23),
         ('Fat Man', 3.25, 0.76), ('AN602 Tsar Bomba', 8.0, 1.06)]
WALL, RING, CLEAR = 0.06, 0.05, 0.1


def fits(shape, length, width, height):
    inner_len = length - 2 * RING - CLEAR
    half_w, half_h = width / 2 - WALL, (width if shape == 'round' else height) / 2 - WALL
    out = []
    for n, l, r in BOMBS:
        if l <= inner_len and r <= min(half_w, half_h):
            out.append(n)
    return out


for (name, title, shape, length, width, height, corner, nodesize, bulkhead, tech, desc) in BAYS:
    perimeter = math.pi * width if shape == 'round' else 2 * (width + height)
    mass = round(0.02 * perimeter * length, 3)
    cost = int(150 + 1000 * mass)
    skin = ''.join('\t\t%s = %s\n' % (k, v) for k, v in SKINS[SKIN_OF[name]].items())
    text = TEMPLATE.format(
        skin=skin,
        name=name, title=title, shape=shape, length=length, width=width, height=height, corner=corner,
        nodesize=nodesize, bulkhead=bulkhead, tech=tech, desc=desc, mass=mass, cost=cost, entry=cost * 8,
        top=length / 2, bottom=-length / 2, itop=length / 2 - 0.02, ibottom=-(length / 2 - 0.02),
        look=max(length / 2, width / 2) + 0.3)
    f = fits(shape, length, width, height)
    assert f, name + ' carries no bomb'
    print('%-18s %5.2f m  carries: %s' % (name, length, ', '.join(f)))
    with open(os.path.join(OUT, name + '.cfg'), 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)
print(len(BAYS), 'bays written to', OUT)
