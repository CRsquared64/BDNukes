# Writes GameData/BDNukes/Parts/BombBays/*.cfg: the bomb bay parts (ModuleBombBay builds the model and the doors).
# Usage: python tools/make_bomb_bays.py
import os, math
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'GameData', 'BDNukes', 'Parts', 'BombBays')
os.makedirs(OUT, exist_ok=True)

# name, title, shape, length, width, height, corner, node size, bulkhead profile, tech, description of what it carries
BAYS = [
    ('bdnukesBayMk1S', 'Mk1 Bomb Bay, Short', 'round', 2.0, 1.25, 1.25, 0, 1, 'size1', 'advAerodynamics',
     'A short 1.25 m round bomb bay in the Mk1 fuselage\'s width: a Davy Crockett, an orbital interceptor or a few small stores.'),
    ('bdnukesBayMk1', 'Mk1 Bomb Bay', 'round', 4.0, 1.25, 1.25, 0, 1, 'size1', 'advAerodynamics',
     'A 1.25 m round bomb bay in the Mk1 fuselage\'s width, long enough for the B57, B61-12, B83, Little Boy, Genie and AIM-47.'),
    ('bdnukesBayRectS', 'Rectangular Bomb Bay, Short', 'rect', 2.04, 1.25, 1.36, 0.3, 1, 'mk1rect', 'advAerodynamics',
     'A short bomb bay in the rectangular 2 m fuselage\'s cross-section; it stacks with the rectangular fuselage parts.'),
    ('bdnukesBayRect', 'Rectangular Bomb Bay', 'rect', 4.08, 1.25, 1.36, 0.3, 1, 'mk1rect', 'advAerodynamics',
     'A bomb bay in the rectangular fuselage\'s cross-section, twice its length: room for the B61-12, B83 or Little Boy.'),
    ('bdnukesBayMk2', 'Mk2 Bomb Bay', 'rect', 4.0, 2.5, 1.35, 0.5, 1, 'mk2', 'highAltitudeFlight',
     'A wide, low bomb bay in the Mk2 fuselage\'s cross-section, with room for Fat Man and the lighter bombs side by side.'),
    ('bdnukesBayMk2Long', 'Mk2 Bomb Bay, Long', 'rect', 8.0, 2.5, 1.35, 0.5, 1, 'mk2', 'highAltitudeFlight',
     'The Mk2 bomb bay at twice the length.'),
    ('bdnukesBay25', '2.5 m Bomb Bay', 'round', 4.0, 2.5, 2.5, 0, 2, 'size2', 'heavyAerodynamics',
     'A 2.5 m round bomb bay for Fat Man and the heavier bombs.'),
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
	}}
	MODULE
	{{
		name = ModuleAnimateGeneric
		animationName = BombBayDoors
		startEventGUIName = Close Bay Doors
		endEventGUIName = Open Bay Doors
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

for (name, title, shape, length, width, height, corner, nodesize, bulkhead, tech, desc) in BAYS:
    perimeter = math.pi * width if shape == 'round' else 2 * (width + height)
    mass = round(0.02 * perimeter * length, 3)
    cost = int(150 + 1000 * mass)
    text = TEMPLATE.format(
        name=name, title=title, shape=shape, length=length, width=width, height=height, corner=corner,
        nodesize=nodesize, bulkhead=bulkhead, tech=tech, desc=desc, mass=mass, cost=cost, entry=cost * 8,
        top=length / 2, bottom=-length / 2, itop=length / 2 - 0.02, ibottom=-(length / 2 - 0.02),
        look=max(length / 2, width / 2) + 0.3)
    with open(os.path.join(OUT, name + '.cfg'), 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)
print(len(BAYS), 'bays written to', OUT)
