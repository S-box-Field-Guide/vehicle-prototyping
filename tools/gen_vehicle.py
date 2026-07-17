"""Parameterized multi-part vehicle kit generator.

Headless Blender authors each vehicle as SEPARATE meshes, one OBJ per part, with
each part's pivot (origin) placed for its physics role (a sub-part origin
convention). A pure-python post-pass (runs inside the same Blender process, no
extra tools needed) copies OBJ+MTL into Assets/, authors a render-only .vmdl per
part + shared flat-colour .vmat(s), writes manifest.json (the contract the
VehicleFactory part-assembly path consumes), verifies the OBJs on disk, and
renders a contact-sheet preview.

Run:  "C:\\Program Files\\Blender Foundation\\Blender 5.1\\blender.exe" -b -P tools/gen_vehicle.py -- [kit ...]
      (no kit args = all kits; pass e.g. `-- pickup_kit` to regenerate one)

FRAME CONTRACT (EMPIRICALLY PROVEN 2026-07-11 — docs/part-kit-assembly.md §2):
  - Author in real-world METRES, +X forward / +Y left / +Z up.
  - Export Y-up OBJ (forward_axis='NEGATIVE_Z', up_axis='Y'):
        OBJ file  o = ( bX,  bZ, -bY )      [disk-verified: door-handle probe]
  - s&box OBJ import is the PLAIN Y-up->Z-up cyclic permutation (no sign flips):
        model     m = ( oZ,  oX,  oY )      [live-verified: facing screenshots]
  - Net author -> model-local:  m = (-bY, +bX, bZ)
    => chassis model-local frame: +X = vehicle RIGHT, +Y = vehicle FRONT
       (nose points local +Y), +Z = up; facing yaw on the kit-body GO is -90 deg.
  Manifests emitted by this script are schema "vp.partkit/3" = all frame fields
  (attach_local_m, local_bounds_*) computed with the PROVEN mapping (same as v2)
  PLUS an inert damage band (dent/loosen/detach impulses, stiffness, max_crush_m,
  zone — data only, no runtime deformation in this kit). PartKitManifest.TryLoad
  normalizes older manifests up at load (v1 bounds flip; v1/v2 damage-band defaults),
  so every schema assembles identically — never a compensating consumer.

Pipeline gotchas honoured:
  - uniform import_scale 39.37 in the vmdl (metres -> inches).
  - vmdl material remaps map BOTH "name" and "name.vmat".
  - export_materials=True keeps usemtl + a .mtl in the folder (we verify usemtl
    and 'vt ' are present after every export, per the export_materials=False trap).
  - Render-only vmdl (RenderMeshFile only) => ZERO engine collision. DELIBERATE:
    the part-kit assembler uses code-side BoxColliders on each part GameObject, so render-only is
    acceptable here. Recorded in docs/part-kit-pipeline.md.

MATERIAL ROUTE: per-part usemtl groups -> one flat .vmat per palette colour
(shared white.png + g_vColorTint). ~9 vmats shared across
all parts of a kit. See docs/part-kit-pipeline.md.
"""
import math
import os
import sys
import json
import struct
import zlib

try:
    import bpy
    HAVE_BPY = True
except ImportError:  # allows the post-pass helpers to be imported without Blender
    HAVE_BPY = False

# ---------------------------------------------------------------- paths
TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(TOOLS_DIR)
SCALE = 39.37  # metres -> s&box units (inches)

# ============================================================ vehicle specs
# Every dimension in METRES. Add new kits by adding a spec here + a plan in KITS.
# Custom part-kit art (replacing the placeholder Kenney assets): hatch_kit
# is the v2 hot-hatch design pass (ORANGE); coupe_kit + kart_kit are new. Wheelbase/
# track/wheel_radius MUST match the CarDefinition (assembler audits hubs at 1 cm);
# length/width/height are the VISUAL envelope (BodySize is the physics collider and
# may differ — pickup precedent).
SPECS = {
    "hatch_kit": {  # hot hatch v2 (brief 1): box flares, deep bumper, roof spoiler
        "length": 4.00,
        "width": 1.74,          # tire outer faces = the widest point; mirrors excluded
        "height": 1.45,
        "wheelbase": 2.55,
        "track": 1.50,
        "wheel_radius": 0.30,
        "wheel_width": 0.24,    # chunky low-profile (brief: dark, chunky)
        "body_color": "body_orange",
    },
    "pickup_kit": {  # full-size pickup proportions
        "length": 5.40,
        "width": 2.00,          # body width; mirrors overhang (excluded from envelope)
        "height": 1.90,
        "wheelbase": 3.40,
        "track": 1.70,
        "wheel_radius": 0.35,
        "wheel_width": 0.28,
        "body_color": "body_red",
    },
    "coupe_kit": {  # 80s mid-engine wedge supercar (brief 2) — retires race.vmdl
        "length": 4.40,
        "width": 1.90,          # flare outer edge (wide rear tires need 0.95);
                                # def BodySize.y stays 1.85 = the physics collider
        "height": 1.10,         # low wedge roofline (BodySize.z 1.25 is collider-only)
        "wheelbase": 2.70,      # def Coupe geometry — MUST match (hub audit)
        "track": 1.60,
        "wheel_radius": 0.33,
        "wheel_width": 0.25,    # front tire
        "wheel_width_r": 0.30,  # rear tire — visibly wider (brief: wide rears)
        "body_color": "body_signal_red",
    },
    "kart_kit": {  # real racing kart, exposed anatomy (brief 3) — retires kart-oobi
        "length": 1.90,
        "width": 1.32,          # rear-tire outer edges (wheels ARE the widest point)
        "height": 0.55,         # tall seat back top
        "wheelbase": 1.55,      # def Kart geometry — MUST match (hub audit)
        "track": 1.14,
        "wheel_radius": 0.16,
        "wheel_width": 0.12,    # front slick
        "wheel_width_r": 0.18,  # rear slick — visibly wider (brief)
        "body_color": "body_acid",
    },
}

# ---------------------------------------------------------------- palette
# flat colours shared across all parts of a kit (RGB 0..1).
# Colour scheme: every roster car gets its own
# signature colour — hatch ORANGE, coupe bright SIGNAL RED (clearly distinct from
# the pickup's dark brick body_red), kart designer's pick = ACID GREEN (distinct
# from orange / both reds / everything else on the roster).
PALETTE = {
    "body_blue": (0.16, 0.42, 0.66),      # retired hatch v1 colour (kept for old vmats)
    "body_red":  (0.55, 0.13, 0.11),      # pickup dark brick
    "body_orange": (0.93, 0.42, 0.03),    # hot hatch v2
    "body_signal_red": (0.80, 0.05, 0.07),  # coupe — bright pure signal red
    "body_acid": (0.58, 0.83, 0.07),      # kart — acid green
    "tire":      (0.09, 0.09, 0.10),
    "rim":       (0.72, 0.74, 0.78),
    "rim_dark":  (0.23, 0.24, 0.27),      # hatch v2 chunky dark wheels (brief)
    "glass":     (0.24, 0.34, 0.40),
    "trim":      (0.17, 0.18, 0.21),
    "light":     (0.86, 0.22, 0.16),   # rear/marker lamps (red)
    "light_f":   (0.93, 0.89, 0.72),   # headlights — distinct from rear, per the
                                       # facing-verification lesson (assembly doc 10)
    "light_amber": (0.94, 0.58, 0.10), # coupe taillight cluster amber + indicators
    "chrome":    (0.78, 0.80, 0.83),
}
ROUGH = {"glass": 0.15, "rim": 0.40, "rim_dark": 0.50, "tire": 0.85, "trim": 0.55,
         "chrome": 0.22, "light_f": 0.25, "light_amber": 0.30}

# ============================================================ Blender helpers
_mats = {}


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    _mats.clear()


def mat(name):
    if name not in _mats:
        m = bpy.data.materials.new(name)
        m.use_nodes = True
        bsdf = m.node_tree.nodes["Principled BSDF"]
        rgb = PALETTE[name]
        bsdf.inputs["Base Color"].default_value = (*rgb, 1.0)
        bsdf.inputs["Roughness"].default_value = ROUGH.get(name, 0.9)
        m.diffuse_color = (*rgb, 1.0)  # OBJ exporter writes this as Kd
        _mats[name] = m
    return _mats[name]


def cube(loc, size, m, rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    o = bpy.context.active_object
    o.scale = size
    o.rotation_euler = rot
    o.data.materials.append(mat(m))
    return o


def cyl(loc, radius, depth, m, axis='Z', verts=16, rot=None):
    bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=radius, depth=depth, location=loc)
    o = bpy.context.active_object
    if rot is not None:
        o.rotation_euler = rot
    elif axis == 'X':
        o.rotation_euler = (0, math.pi / 2, 0)
    elif axis == 'Y':
        o.rotation_euler = (math.pi / 2, 0, 0)
    o.data.materials.append(mat(m))
    return o


def rcube(loc, size, m, rot=(0, 0, 0), r=0.025, seg=2):
    """Beveled cube — the 'rounded' vocabulary (visual pass 2026-07-16). Scale is
    applied BEFORE the bevel so non-uniform sizes get uniform edge radii; width is
    clamped to a third of the thinnest dimension so thin slabs never degenerate.
    Deterministic (modifier apply, no RNG)."""
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    o = bpy.context.active_object
    o.scale = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    o.rotation_euler = rot
    mod = o.modifiers.new("bev", 'BEVEL')
    mod.width = min(r, min(size) / 3.0)
    mod.segments = seg
    mod.limit_method = 'ANGLE'
    mod.angle_limit = math.radians(40)
    bpy.ops.object.modifier_apply(modifier="bev")
    o.data.materials.append(mat(m))
    return o


def rcyl(loc, radius, depth, m, axis='Y', verts=24, shoulder=0.02):
    """Cylinder with beveled cap edges (rounded tire shoulders). verts stays a
    multiple of 4 so the bbox is vertex-aligned (exactly 2R on the radial axes —
    the assembler's self-correcting scale depends on it); the shoulder bevel cuts
    only the rim corner, so both the radial (side wall) and width (cap face)
    extents survive."""
    o = cyl(loc, radius, depth, m, axis=axis, verts=verts)
    mod = o.modifiers.new("bev", 'BEVEL')
    mod.width = shoulder
    mod.segments = 2
    mod.limit_method = 'ANGLE'
    mod.angle_limit = math.radians(40)
    bpy.ops.object.modifier_apply(modifier="bev")
    return o


def arch_band(loc, r_in, r_out, width, m, a0=-20.0, a1=200.0, segs=18):
    """Semi-annular wheel-arch flare band in the author XZ plane (visual pass
    2026-07-16 — replaces the box flares). loc = (x_hub, y_centerline, z_hub);
    the band is a quad strip between the two arcs, extruded width/2 both ways
    along Y, with end caps. Built vert-by-vert (deterministic, clean topology,
    no booleans). Angles in degrees: 0 = +X (forward), 90 = up."""
    import bmesh
    mesh = bpy.data.meshes.new("arch")
    o = bpy.data.objects.new("arch", mesh)
    bpy.context.collection.objects.link(o)
    bm = bmesh.new()
    w2 = width / 2.0
    rows = []
    for i in range(segs + 1):
        a = math.radians(a0 + (a1 - a0) * i / segs)
        ca, sa = math.cos(a), math.sin(a)
        rows.append((
            bm.verts.new((r_in * ca, -w2, r_in * sa)),
            bm.verts.new((r_out * ca, -w2, r_out * sa)),
            bm.verts.new((r_out * ca, w2, r_out * sa)),
            bm.verts.new((r_in * ca, w2, r_in * sa)),
        ))
    for i in range(segs):
        a_in0, a_out0, b_out0, b_in0 = rows[i]
        a_in1, a_out1, b_out1, b_in1 = rows[i + 1]
        bm.faces.new((a_out0, a_out1, b_out1, b_out0))   # outer surface
        bm.faces.new((b_in0, b_in1, a_in1, a_in0))       # inner surface
        bm.faces.new((a_in0, a_in1, a_out1, a_out0))     # -Y rim
        bm.faces.new((b_out0, b_out1, b_in1, b_in0))     # +Y rim
    bm.faces.new(rows[0])                                # start cap
    bm.faces.new(tuple(reversed(rows[-1])))              # end cap
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(mesh)
    bm.free()
    # flat-tint materials never sample UVs, but the exporter only writes 'vt'
    # lines for faces that HAVE a UV layer — and the on-disk verifier requires
    # them (the export_materials trap check). Default coords are fine.
    mesh.uv_layers.new(name="UVMap")
    o.location = loc
    o.data.materials.append(mat(m))
    return o


def export_part(out_dir, name, ground=False):
    """Join all objects in the scene and export a Y-up OBJ (+ MTL) for this part.
    The pivot is wherever the geometry sits relative to the Blender origin; we do
    NOT recentre (except optional ground=True which drops min-z to 0)."""
    bpy.ops.object.select_all(action='SELECT')
    bpy.context.view_layer.objects.active = bpy.data.objects[0]
    if len(bpy.data.objects) > 1:
        bpy.ops.object.join()
    obj = bpy.context.active_object
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    if ground:
        min_z = min((obj.matrix_world @ v.co).z for v in obj.data.vertices)
        obj.location.z -= min_z
        bpy.ops.object.transform_apply(location=True)
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, f"{name}.obj")
    bpy.ops.wm.obj_export(filepath=path, export_materials=True,
                          export_uv=True, export_normals=True,
                          forward_axis='NEGATIVE_Z', up_axis='Y')
    print(f"  exported {name}: {len(obj.data.vertices)} verts")
    return path


# ============================================================ hatch_kit geometry (v2)
# All geometry authored in the AUTHORING frame: +X forward, +Y left, +Z up,
# metres. Each part is built with its PIVOT at the Blender origin (0,0,0).
#
# hatch design pass:
# aggressive 3-door hot hatch, ORANGE. Box-flared arches with separate lip
# planes, slim corner-wrapping headlights, V-crease hood, deep bumper with a
# full-width black mesh intake + fog lamps + splitter, roof spoiler, stalked
# mirrors, black side skirts, rear quarter windows, tall taillights, black
# diffuser band, chunky dark wheels.
#
# Author-frame stations (m):  front face +2.00 (bumper) | chassis nose +1.83 |
# hood 0.70..1.72 (cowl hinge at 0.70, z 0.90) | A pillar / door hinge +0.55 |
# door rear -0.40 | C pillar -1.12 | roof rear / tailgate hinge -1.20 (z 1.40) |
# tail top -1.72 (z 0.98) | rear face -2.00.  Heights: skirt bottom 0.13 |
# floor 0.18 | beltline 0.92 | hood cowl 0.90 | roof top 1.45 (= spec height).
# Ground-clearance law: nothing but tires below z 0.10 (bottom-out margin —
# static susp length 0.089 m; the checker enforces min_part_ground).

def build_chassis(spec, out_dir):
    """Body minus doors/hood/tailgate/bumpers/mirrors/spoiler. Pivot = footprint
    centre at ground (z=0)."""
    reset()
    B = spec["body_color"]
    wb2 = spec["wheelbase"] / 2.0          # 1.275 — axle stations
    halfW = spec["width"] / 2.0            # 0.875 — flare-lip outer edge
    roof_top = spec["height"]              # 1.45
    # floor / lower body (sides at +-0.79, inboard of the flares) — soft edges
    rcube((0, 0, 0.38), (3.58, 1.54, 0.40), B, r=0.045)   # x -1.79..1.79, z 0.18..0.58
    # black side skirts between the arches (brief: black side skirts)
    for sy in (0.78, -0.78):
        rcube((0.0, sy, 0.185), (1.55, 0.055, 0.11), "trim", r=0.015)   # z 0.13..0.24
    # beltline band: front fenders / cowl / nose / rear quarters / tail panel
    for sy in (0.605, -0.605):
        rcube((1.30, sy, 0.75), (1.06, 0.33, 0.34), B, r=0.035)   # front fender x 0.77..1.83
        rcube((-1.12, sy, 0.75), (1.42, 0.33, 0.34), B, r=0.035)  # rear quarter x -1.83..-0.41
    cube((0.73, 0, 0.75), (0.10, 1.48, 0.34), B)          # cowl wall (hood rear seat)
    rcube((1.77, 0, 0.75), (0.12, 1.48, 0.34), B, r=0.03)  # nose panel above bumper
    rcube((-1.77, 0, 0.80), (0.12, 1.48, 0.44), B, r=0.03)  # tail panel z 0.58..1.02
    # greenhouse: roof top exactly at spec height; windshield leans top-REARWARD
    # from the cowl top (0.68, 0.92) to the roof front edge (0.30, 1.415).
    # Rotation law (R_y): rot +t tips a slab's top toward +X — glass panels that
    # lean back need NEGATIVE y-rotation (the v1 kit had this inverted: the
    # 'floating hatch lid' quirk in docs/part-kit-assembly.md 10).
    rcube((-0.45, 0, roof_top - 0.035), (1.50, 1.24, 0.07), B, r=0.022)   # roof x -1.20..0.30
    cube((0.49, 0, 1.17), (0.10, 1.20, 0.62), "glass", rot=(0, math.radians(-38), 0))  # windshield
    # A-pillar frames: inner edge 2.5 mm OUTBOARD of the windshield glass edge
    # (y 0.60) so the body frame and the glass pane don't share the raked
    # windshield plane (that coplanar overlap z-fought the windshield sides —
    # the coupe's proven A-frame law, docs/part-kit-pipeline.md). Center 0.6275,
    # half-width 0.025 -> inner edge 0.6025.
    for sy in (0.6275, -0.6275):
        cube((0.49, sy, 1.17), (0.09, 0.05, 0.62), B, rot=(0, math.radians(-38), 0))  # A pillar
    for sy in (0.60, -0.60):
        cube((-0.42, sy, 1.16), (0.08, 0.07, 0.50), B)    # B pillar
        cube((-1.12, sy, 1.16), (0.14, 0.07, 0.50), B)    # C pillar (thick, hot-hatch)
        # small rear quarter window behind the door (brief), flush B->C pillar
        cube((-0.76, sy, 1.17), (0.60, 0.03, 0.49), "glass")
    # ROUNDED arch flares (visual pass 2026-07-16 — replaces the box flares):
    # a semi-annular body-colour band following the wheel, hub at (±wb2, z=R),
    # inner radius R+0.06 (visible tire gap), 10 cm flare depth. Band outer face
    # y 0.865 stays 2.5 mm INBOARD of the 0.87 tire face (wheel-arch law: never
    # bury the wheel; no coplanar z-fight with the sidewall). Band inner face
    # y 0.775 clears the 0.77 floor side by 5 mm (no coplanar).
    R = spec["wheel_radius"]
    for fx in (wb2, -wb2):
        for s in (1.0, -1.0):
            arch_band((fx, s * 0.82, R), R + 0.06, R + 0.16, 0.09, B)
            # thin dark inner well band under the flare (reads as the arch
            # opening instead of raw body side poking through)
            arch_band((fx, s * 0.755, R), R + 0.045, R + 0.075, 0.035, "trim",
                      a0=-15.0, a1=195.0, segs=14)
    # slim angular headlights wrapping the corners (front face + side return)
    for s in (1.0, -1.0):
        cube((1.835, s * 0.55, 0.79), (0.03, 0.38, 0.09), "light_f", rot=(0, 0, -s * math.radians(8)))
        cube((1.72, s * 0.78, 0.79), (0.16, 0.02, 0.08), "light_f")      # corner wrap
    # tall simple taillights on the tail panel (brief)
    for s in (1.0, -1.0):
        cube((-1.835, s * 0.60, 0.82), (0.03, 0.20, 0.38), "light")
    return export_part(out_dir, "chassis_shell", ground=False)


def build_wheel(spec, out_dir):
    """Chunky dark hot-hatch wheel (brief: dark-gray/black, low profile): wide
    tire, large dark rim disc + hub. Pivot AT HUB CENTRE; axle along author Y
    => spin axis is part-local X after import. 20 verts = vertex-aligned bbox
    (exactly 2R on the radial axes — the 18-vert pickup tire taught us the
    face-aligned bbox trips the assembler's self-correcting scale)."""
    reset()
    R = spec["wheel_radius"]
    Wd = spec["wheel_width"]
    # 24 verts (still %4==0 => vertex-aligned bbox, exactly 2R radial) + beveled
    # tire shoulders — the rounded-look pass; radial + width extents survive the
    # shoulder bevel (side wall keeps R, cap face keeps ±Wd/2).
    rcyl((0, 0, 0), R, Wd, "tire", axis='Y', verts=24, shoulder=0.025)      # tire (widest)
    cyl((0, 0, 0), R * 0.62, Wd + 0.01, "rim_dark", axis='Y', verts=16)     # low-profile rim
    cyl((0, 0, 0), R * 0.20, Wd + 0.02, "trim", axis='Y', verts=10)         # centre hub
    return export_part(out_dir, "wheel", ground=False)


def build_door(spec, side, out_dir):
    """Long 3-door hot-hatch door; pivot on the FRONT (A-pillar) hinge line at
    (0.55, +-0.795, 0.62). Panel hangs rearward to the B pillar (-0.40). Swing
    axis = author Z => part-local Z. Geometry properly MIRRORED per side. The
    window pane is offset INBOARD to the pillar line (pickup lesson: it reads
    as greenhouse but swings with the door; no fixed strip to z-fight)."""
    reset()
    B = spec["body_color"]
    s = 1.0 if side == "l" else -1.0
    rcube((-0.475, 0, 0.0), (0.95, 0.05, 0.62), B, r=0.016)       # skin z 0.31..0.93
    cube((-0.495, -s * 0.19, 0.55), (0.87, 0.04, 0.50), "glass")   # window at pillar line
    cube((-0.82, s * 0.032, 0.10), (0.14, 0.02, 0.045), "trim")    # handle (outboard)
    cube((-0.475, s * 0.028, -0.245), (0.90, 0.015, 0.10), "trim")  # black lower strip
    name = "door_l" if side == "l" else "door_r"
    return export_part(out_dir, name, ground=False)


def build_hood(spec, out_dir):
    """Hood with V-crease (brief); pivot on the REAR hinge line at the cowl
    (0.70, 0, 0.90). Extends forward FLUSH with the fender tops (surface 0.93
    vs fenders 0.92 — proud, never sunken: a raked hood between flat fender
    boxes read as an open trough from the side). Swing axis = author Y =>
    part-local X."""
    reset()
    B = spec["body_color"]
    rcube((0.51, 0, 0.005), (1.02, 0.86, 0.05), B, r=0.016)
    # V-crease: two thin ridges converging toward the nose, proud of the panel
    for s in (1.0, -1.0):
        cube((0.50, s * 0.17, 0.042), (0.72, 0.045, 0.018), B,
             rot=(0, 0, -s * math.radians(9)))
    return export_part(out_dir, "hood", ground=False)


def build_trunk(spec, out_dir):
    """Tailgate (hatch lid + glass); pivot on the FORWARD hinge line at roof
    rear (-1.20, 0, 1.40). Slants rearward+down to the tail top (-1.72, 0.98).
    Swing axis = author Y => part-local X."""
    reset()
    B = spec["body_color"]
    a = math.radians(-40)     # top edge at the pivot, panel descends down-BACK
    cube((-0.22, 0, -0.185), (0.55, 1.08, 0.05), "glass", rot=(0, a, 0))  # hatch glass
    rcube((-0.46, 0, -0.39), (0.24, 1.14, 0.05), B, rot=(0, a, 0), r=0.016)  # lower lid
    # plate recess nudged 4 mm DOWN-tailgate along the rake tangent (-0.766,0,-0.643)
    # so its top edge clears the hatch-glass bottom edge. The two panels lay on the
    # SAME raked lid plane and grazed at the seam (a ~0.3 mm coplanar sliver z-fight).
    # Moving along the panel plane keeps the plate flush on the lid (a plane-normal
    # offset would instead collide with the lid's own top/underside faces).
    cube((-0.4931, 0, -0.4176), (0.16, 0.36, 0.055), "trim", rot=(0, a, 0))  # plate recess
    return export_part(out_dir, "trunk", ground=False)


def build_bumper(spec, end, out_dir):
    """Deep hot-hatch bumpers; pivot at the MOUNT-PLANE centre (author +-1.83,
    z 0.42). Front: body-colour bar, full-width black mesh intake, square fog
    lamps in the corners, splitter lip. Rear: body-colour bar + black diffuser
    band + twin exhaust. Rigid bolt-ons (no rotation axis)."""
    reset()
    B = spec["body_color"]
    if end == "f":
        rcube((0.0825, 0, 0.06), (0.165, 1.56, 0.28), B, r=0.035)  # main bar z 0.34..0.62
        rcube((0.15, 0, -0.02), (0.04, 1.30, 0.18), "trim", r=0.012)  # mesh intake (full width)
        for s in (1.0, -1.0):
            # fog lens front pushed 2.5 mm PROUD of the mesh-intake front plane
            # (author x 2.00) it used to share — that coplanar overlap was a
            # trim(mesh)/light_f(lens) z-fight on both front corners. Proud (not
            # recessed): the mesh is a solid box front-to-0.17, so a recessed lens
            # would vanish behind it; a lens bulging 2.5 mm out of the grille reads fine.
            cube((0.1575, s * 0.60, -0.02), (0.03, 0.14, 0.10), "light_f")  # fog lamps
        cube((0.05, 0, -0.235), (0.22, 1.50, 0.05), "trim")        # splitter lip z 0.16..0.21
        name = "bumper_f"
    else:
        rcube((-0.085, 0, 0.06), (0.17, 1.56, 0.28), B, r=0.035)   # main bar
        rcube((-0.10, 0, -0.19), (0.15, 1.40, 0.16), "trim", r=0.012)  # diffuser band z 0.15..0.31
        for s in (1.0, -1.0):
            cyl((-0.14, s * 0.32, -0.15), 0.035, 0.08, "chrome", axis='X', verts=8)  # tips proud
        name = "bumper_r"
    return export_part(out_dir, name, ground=False)


def build_hatch_mirror(spec, side, out_dir):
    """Stalked side mirror (brief); pivot at the door-top front corner
    (0.48, +-0.82, 0.98). Stalk reaches outboard, glass faces rearward."""
    reset()
    s = 1.0 if side == "l" else -1.0
    cube((0, s * 0.06, 0.02), (0.04, 0.12, 0.03), "trim")            # stalk
    cube((0, s * 0.15, 0.05), (0.06, 0.075, 0.13), "trim")           # head
    cube((-0.033, s * 0.15, 0.05), (0.012, 0.065, 0.11), "glass")    # face (-X)
    name = "mirror_l" if side == "l" else "mirror_r"
    return export_part(out_dir, name, ground=False)


def build_hatch_spoiler(spec, out_dir):
    """Roof spoiler over the tailgate (brief); pivot at the roof-rear mount
    (-1.22, 0, 1.36). Body-colour blade with black end caps; top stays at
    z 1.44 <= spec height."""
    reset()
    B = spec["body_color"]
    rcube((-0.18, 0, 0.045), (0.44, 1.26, 0.04), B, rot=(0, math.radians(6), 0), r=0.013)
    for s in (1.0, -1.0):
        cube((-0.20, s * 0.62, 0.035), (0.34, 0.025, 0.075), "trim")  # end plates
    return export_part(out_dir, "spoiler", ground=False)


def build_hatch_parts(spec, kit_dir):
    build_chassis(spec, kit_dir)
    build_wheel(spec, kit_dir)
    build_door(spec, "l", kit_dir)
    build_door(spec, "r", kit_dir)
    build_hood(spec, kit_dir)
    build_trunk(spec, kit_dir)
    build_bumper(spec, "f", kit_dir)
    build_bumper(spec, "r", kit_dir)
    build_hatch_mirror(spec, "l", kit_dir)
    build_hatch_mirror(spec, "r", kit_dir)
    build_hatch_spoiler(spec, kit_dir)


def attach_table_hatch(spec):
    """Attach point = where each part's PIVOT sits in the chassis AUTHORING frame
    (metres, +X fwd/+Y left/+Z up)."""
    wb2 = spec["wheelbase"] / 2.0
    tr2 = spec["track"] / 2.0
    R = spec["wheel_radius"]
    front_x = spec["length"] / 2.0 - 0.17   # front bumper mount plane (author X 1.83)
    rear_x = -(spec["length"] / 2.0 - 0.17)
    return {
        # part        (bX,     bY,    bZ)
        "chassis_shell": (0.0, 0.0, 0.0),
        "wheel_fl":  (wb2,  tr2,  R),
        "wheel_fr":  (wb2, -tr2,  R),
        "wheel_rl": (-wb2,  tr2,  R),
        "wheel_rr": (-wb2, -tr2,  R),
        "door_l":    (0.55,  0.795, 0.62),
        "door_r":    (0.55, -0.795, 0.62),
        "hood":      (0.70,  0.0,  0.90),
        "trunk":    (-1.20,  0.0,  1.40),
        "bumper_f":  (front_x, 0.0, 0.42),
        "bumper_r":  (rear_x,  0.0, 0.42),
        "mirror_l":  (0.48,  0.82, 0.98),
        "mirror_r":  (0.48, -0.82, 0.98),
        "spoiler":  (-1.22,  0.0,  1.36),
    }


# which OBJ file backs each hatch part, spin/hinge role, mass fraction
HATCH_ROLES = {
    "chassis_shell": dict(obj="chassis_shell", pivot="footprint-centre@ground",
                          axis=None, mass=0.65),
    "wheel_fl": dict(obj="wheel", pivot="hub-centre", axis="X", mass=0.045,
                     kind="wheel", steer=True, mirror=False),
    "wheel_fr": dict(obj="wheel", pivot="hub-centre", axis="X", mass=0.045,
                     kind="wheel", steer=True, mirror=True),
    "wheel_rl": dict(obj="wheel", pivot="hub-centre", axis="X", mass=0.045,
                     kind="wheel", steer=False, mirror=False),
    "wheel_rr": dict(obj="wheel", pivot="hub-centre", axis="X", mass=0.045,
                     kind="wheel", steer=False, mirror=True),
    "door_l": dict(obj="door_l", pivot="front-hinge-line", axis="Z", mass=0.03,
                   kind="door", open_sign=1),
    "door_r": dict(obj="door_r", pivot="front-hinge-line", axis="Z", mass=0.03,
                   kind="door", open_sign=-1),
    "hood": dict(obj="hood", pivot="rear-hinge-line", axis="X", mass=0.03,
                 kind="hood", open_sign=-1),
    "trunk": dict(obj="trunk", pivot="forward-hinge-line", axis="X", mass=0.03,
                  kind="trunk", open_sign=1),
    "bumper_f": dict(obj="bumper_f", pivot="mount-plane", axis=None, mass=0.02,
                     kind="bolton", mount_normal="-Y"),
    "bumper_r": dict(obj="bumper_r", pivot="mount-plane", axis=None, mass=0.02,
                     kind="bolton", mount_normal="+Y"),
    # mirrors break outboard: author +Y (left) => model-local -X, and vice versa
    "mirror_l": dict(obj="mirror_l", pivot="door-mount", axis=None, mass=0.002,
                     kind="mirror", mount_normal="-X"),
    "mirror_r": dict(obj="mirror_r", pivot="door-mount", axis=None, mass=0.002,
                     kind="mirror", mount_normal="+X"),
    "spoiler": dict(obj="spoiler", pivot="roof-rear-mount", axis=None, mass=0.006,
                    kind="accessory", mount_normal="+Z"),
}


# ============================================================ pickup_kit geometry
# Design doc: docs/pickup-kit.md 1 (part list + author-frame layout written first).
# Key author-frame stations: front bumper face +2.70 | grille mount +2.56 |
# front axle +1.70 | cowl +1.02 | door hinge +0.58 | cab rear wall -0.56 |
# bed front -0.70 (0.10 m visible cab-bed GAP) | rear axle -1.70 |
# tailgate hinge -2.55 | rear face -2.70.  Heights: frame rails 0.34-0.46,
# hood top 1.08, bed rail 1.10, beltline 1.20, roof 1.90.

def build_pickup_cab(spec, out_dir):
    """Cab + front clip + full-length frame rails, minus doors/hood/grille.
    Pivot = footprint centre at ground (z=0), like the hatch chassis."""
    reset()
    B = spec["body_color"]
    wb2 = spec["wheelbase"] / 2.0          # 1.70 — front axle
    halfW = spec["width"] / 2.0            # 1.00 — flare outer edge
    roof_top = spec["height"]              # 1.90
    # visible frame rails (full vehicle length — body-on-frame signature)
    for sy in (0.55, -0.55):
        cube((0, sy, 0.40), (4.90, 0.12, 0.12), "trim")
    for cx in (wb2, -wb2):                                  # crossmembers at the axles
        cube((cx, 0, 0.40), (0.14, 1.00, 0.10), "trim")
    # cab floor/rockers + lower body to beltline (x -0.60..1.05) — soft edges
    rcube((0.22, 0, 0.52), (1.66, 1.80, 0.20), B, r=0.035)  # z 0.42-0.62
    rcube((0.22, 0, 0.91), (1.66, 1.80, 0.58), B, r=0.04)   # z 0.62-1.20 (beltline)
    for sy in (0.93, -0.93):                                # side steps
        # rear end shortened 2.5 mm (x-min -0.4275 vs the door's rear shut plane at
        # -0.43) so the trim step and the body door skin don't share that plane (a
        # cross-part z-fight at the door's rear shut-line).
        cube((0.2213, sy, 0.44), (1.295, 0.10, 0.06), "trim")
    # cowl wall (hood rear seat)
    cube((1.02, 0, 1.02), (0.08, 1.60, 0.34), B)            # z 0.85-1.19
    # front clip: fenders flanking the engine bay + core support + bay floor
    for sy in (0.68, -0.68):
        rcube((1.80, sy, 0.82), (1.50, 0.44, 0.52), B, r=0.04)   # z 0.56-1.08, y 0.46-0.90
    rcube((2.44, 0, 0.80), (0.22, 1.44, 0.56), B, r=0.03)        # core support, z 0.52-1.08
    cube((1.75, 0, 0.55), (1.30, 0.88, 0.18), "trim")       # bay floor hint
    # greenhouse: roof + pillars + glass (upright truck greenhouse)
    rcube((0.20, 0, roof_top - 0.04), (1.50, 1.62, 0.08), B, r=0.022)  # top = 1.90
    for sy in (0.78, -0.78):
        cube((0.88, sy, 1.53), (0.10, 0.09, 0.62), B)       # A pillar
        cube((-0.50, sy, 1.53), (0.12, 0.09, 0.62), B)      # C pillar
    # windshield + backlight: VERTICAL panes (upright truck greenhouse) so each pane sits
    # WITHIN its window opening on the vertical A/C pillars. The old panes were RAKED
    # (windshield +26 top-forward, backlight -8) — a raked pane crosses the vertical pillar
    # plane and its top/bottom edges poke fore/aft PAST the pillar (the reported "windows not
    # matching the bounds"). Fit law per pane: y-edges +-0.72 sit 15 mm inboard of the pillar
    # inner face (0.735); the 0.06-0.09 x-thickness sits ~5 mm inside the pillar depth (no
    # coplanar contact, glass proud/inset); z tucks 2 cm behind the beltline top (1.20) /
    # back-wall top (1.30) and under the roof underside (1.82) so no seam-gap slit shows
    # (the tucked edges are buried inside the solid body, never coplanar with a frame face).
    cube((0.88, 0, 1.505), (0.09, 1.44, 0.65), "glass")   # windshield, abs z 1.18-1.83; x 0.835-0.925 within A-pillar 0.83-0.93
    cube((-0.50, 0, 1.555), (0.06, 1.44, 0.55), "glass")  # backlight,  abs z 1.28-1.83; x -0.53..-0.47 within C-pillar -0.56..-0.44
    # NOTE: no fixed side-glass strips — the door parts carry the side windows
    # (inboard-offset panes aligned with the pillar line, so they swing with the
    # door at Stage C and never z-fight a fixed pane)
    # cab back wall (faces the bed across the gap)
    rcube((-0.56, 0, 0.95), (0.08, 1.80, 0.70), B, r=0.022)  # z 0.60-1.30
    cube((-0.515, 0, 1.80), (0.04, 0.30, 0.05), "light")    # third brake light — rear
    # face pulled 1.5 cm forward of the roof rear plane (x=-0.55) it used to share (z-fight)
    # FRONT wheel-arch flares (rear arches belong to the BED part). Outer edge sits
    # 2.5 mm INBOARD of the tire outer face (0.99) so the wheel reads proud, per the
    # arch law (docs/part-kit-pipeline.md). Pre-fix these sat at y=1.00, ~1 cm OUTBOARD
    # of the tire, so the red fender occluded the wheel and (with the coplanar rim)
    # read as a red disc plastered on the fender.
    tire_face = spec["track"] / 2.0 + spec["wheel_width"] / 2.0   # 0.99
    # ROUNDED arch flare (visual pass 2026-07-16, matches the hatch): semi-annular
    # band, hub at (wb2, z=R); outer face stays 2.5 mm inboard of the tire face.
    R = spec["wheel_radius"]
    flare_cy = (tire_face - 0.0025) - 0.05                        # centre; band width 0.10
    for sy in (flare_cy, -flare_cy):
        arch_band((wb2, sy, R), R + 0.06, R + 0.16, 0.10, B)
    # underbody detail: tow hooks (below the bumper bar, so they stay visible) + exhaust
    for sy in (0.35, -0.35):
        cube((2.58, sy, 0.26), (0.14, 0.06, 0.08), "trim")
    cyl((-2.20, 0.42, 0.33), 0.045, 0.60, "chrome", axis='X', verts=8)
    return export_part(out_dir, "cab", ground=False)


def build_pickup_bed(spec, out_dir):
    """Cargo box: floor, front/side walls, rail caps, inner wheel humps, REAR
    arch flares + taillights. SEPARATE from cab (the pickup signature; also the
    Stage-C bed-swap seam). Pivot = bed-front frame mount: front-wall plane,
    centred in Y, at frame-rail top (author (-0.70, 0, 0.46)). No rear wall —
    the tailgate is its own part."""
    reset()
    B = spec["body_color"]
    # PANEL-GAP LAW (de-Kenney inset discipline): where a trim panel and a body panel
    # share a plane, inset the SUBORDINATE (internal/covered) panel 2.5 mm so the two
    # coplanar faces don't z-fight — the shimmer is only visible where the two coincident
    # faces differ in MATERIAL, so every trim/body seam here gets a 2.5 mm gap.
    # floor, abs z 0.53-0.61 (internal trim):
    #  - REAR edge inset 2.5 mm (x-max -1.8475 vs walls' -1.85): the shared x=-1.85 plane
    #    was the observed rear-of-bed z-fight, both sides (A2, 2026-07-13).
    #  - FRONT edge inset 2.5 mm (x-max -0.0025 vs walls' 0.0): the floor's trim front face
    #    was coplanar with the body-colour front & side walls at x=0, z-fighting in the
    #    cab-bed gap. Floor is covered by the walls, so both insets are invisible.
    cube((-0.925, 0, 0.11), (1.845, 1.76, 0.08), "trim")
    # front & side walls: TOP dropped 2.5 mm (abs top 1.0975 vs the trim rail caps' 1.10)
    # so the trim RAIL CAPS alone own the z=0.64 rail-top plane. That body-vs-trim coplanar
    # top was the reported flicker on the back two bed sides, seen from above/behind
    # (the rail caps fully cover the dropped wall tops, so the 2.5 mm is invisible; A3,
    # 2026-07-14 — the numeric coplanar census tools/check_coplanar.py flags it).
    rcube((-0.05, 0, 0.33875), (0.10, 1.80, 0.5975), B, r=0.02)  # front wall, abs z 0.50-1.0975
    for sy in (0.85, -0.85):
        rcube((-0.925, sy, 0.33875), (1.85, 0.10, 0.5975), B, r=0.02)  # side walls, top dropped 2.5mm
        cube((-0.925, sy, 0.615), (1.89, 0.14, 0.05), "trim")  # rail caps, abs top 1.10
    for sy in (0.72, -0.72):
        cube((-1.00, sy, 0.25), (0.90, 0.26, 0.30), "trim")  # wheel humps (travel clearance)
    # rear arch flares — outer edge 2.5 mm inboard of the tire face (0.99), matching
    # the FRONT arches (build_pickup_cab) and the arch law. Pre-fix y=0.94 → outer 1.00,
    # ~1 cm outboard of the tire, so the flare buried the rear wheels.
    # ROUNDED rear arch (visual pass 2026-07-16): bed part local frame — pivot at
    # author (-0.70, 0, 0.46), rear hub author (-1.70, y, R=0.35) => local (-1.00, y, R-0.46).
    R = spec["wheel_radius"]
    rear_flare_cy = (spec["track"] / 2.0 + spec["wheel_width"] / 2.0 - 0.0025) - 0.05
    for sy in (rear_flare_cy, -rear_flare_cy):
        arch_band((-1.00, sy, R - 0.46), R + 0.06, R + 0.16, 0.10, B)
    for sy in (0.83, -0.83):
        cube((-1.83, sy, 0.36), (0.05, 0.20, 0.32), "light")  # taillights
    cube((-1.82, 0, 0.06), (0.07, 1.60, 0.10), "trim")      # rear sill under tailgate
    return export_part(out_dir, "bed", ground=False)


def build_pickup_tailgate(spec, out_dir):
    """Hinged at the BOTTOM edge (drops open rearward-down). Pivot on the bottom
    hinge line at bed-floor height (author (-2.55, 0, 0.53)); panel extends UP.
    Swing axis = author Y => part-local X."""
    reset()
    B = spec["body_color"]
    rcube((-0.02, 0, 0.28), (0.09, 1.58, 0.54), B, r=0.016)  # panel, abs z 0.54-1.08
    cube((0.035, 0, 0.30), (0.02, 1.30, 0.38), "trim")      # inner (bed-side) liner
    cube((-0.075, 0, 0.50), (0.015, 1.20, 0.10), "chrome")  # outer trim band
    return export_part(out_dir, "tailgate", ground=False)


def build_pickup_hood(spec, out_dir):
    """Long flat truck hood; pivot on the REAR hinge line at the cowl
    (author (1.05, 0, 1.08)); extends forward. Swing axis = author Y => local X."""
    reset()
    B = spec["body_color"]
    rcube((0.72, 0, -0.02), (1.44, 1.40, 0.06), B, r=0.018)
    rcube((0.62, 0, 0.035), (0.92, 0.76, 0.05), B, r=0.015)  # power dome
    return export_part(out_dir, "hood", ground=False)


def build_pickup_door(spec, side, out_dir):
    """Tall truck door; pivot on the FRONT hinge line at the A pillar
    (author (0.80, +-0.92, 0.80)); panel hangs rearward to the C pillar. Swing
    axis = author Z => part-local Z. Geometry is properly MIRRORED per side
    (handle/trim outboard on both doors). The door WINDOW is offset INBOARD to
    the pillar line (author +-0.79) so the pane reads as part of the greenhouse
    yet swings with the door at Stage C."""
    reset()
    B = spec["body_color"]
    s = 1.0 if side == "l" else -1.0
    rcube((-0.62, 0, 0.02), (1.22, 0.06, 0.78), B, r=0.018)   # skin, abs z 0.43-1.21
    cube((-0.53, -s * 0.13, 0.66), (0.94, 0.045, 0.52), "glass")  # window, abs z 1.20-1.72
    cube((-1.10, s * 0.035, 0.28), (0.14, 0.02, 0.05), "chrome")  # handle (outboard)
    cube((-0.62, s * 0.032, -0.22), (1.15, 0.015, 0.08), "trim")  # lower trim strip
    name = "door_l" if side == "l" else "door_r"
    return export_part(out_dir, name, ground=False)


def build_pickup_bumper(spec, end, out_dir):
    """Chunky chrome truck bumpers; pivot at the mount plane. Front adds a lower
    valance; rear adds a step pad + hitch stub."""
    reset()
    if end == "f":
        rcube((0.10, 0, 0), (0.20, 1.90, 0.34), "chrome", r=0.035)
        rcube((0.08, 0, -0.16), (0.12, 1.40, 0.10), "trim", r=0.012)
        name = "bumper_f"
    else:
        rcube((-0.05, 0, 0), (0.10, 1.95, 0.32), "chrome", r=0.03)
        cube((-0.06, 0, 0.175), (0.06, 0.80, 0.03), "trim")     # step pad
        cube((-0.0725, 0, -0.14), (0.05, 0.10, 0.10), "trim")   # hitch stub — rear face
        # 2.5 mm FORWARD of the bumper rear face (was flush = chrome/trim z-fight);
        # still never passes author -2.70 = length/2.
        name = "bumper_r"
    return export_part(out_dir, name, ground=False)


def build_pickup_grille(spec, out_dir):
    """Front fascia as ONE cosmetic bolt-on: chrome surround, grille bars,
    headlights (light_f) + turn signals. Pivot at the mount plane on the core
    support (author (2.56, 0, 0.85)); extends forward."""
    reset()
    cube((0.035, 0, 0), (0.07, 1.50, 0.44), "chrome")       # surround, abs z 0.63-1.07
    for gz in (-0.10, 0.0, 0.10):
        cube((0.075, 0, gz), (0.02, 1.26, 0.06), "trim")    # horizontal bars
    for sy in (0.60, -0.60):
        # headlight raised 2.5 mm so its bottom face clears the centre grille bar's
        # z=-0.03 plane (that shared plane was a trim/light_f z-fight).
        cube((0.075, sy, 0.0625), (0.03, 0.26, 0.18), "light_f")  # headlights
        cube((0.075, sy, -0.14), (0.025, 0.20, 0.08), "light")   # turn/marker
    return export_part(out_dir, "grille", ground=False)


def build_pickup_mirror(spec, side, out_dir):
    """Side mirror: arm + head + glass. Cheap tris, big silhouette. Pivot at the
    door top front corner (author (0.72, +-0.94, 1.18)); arm reaches outboard,
    base embedded in the door top edge. Glass faces rearward (author -X).
    Head outer edge lands at author |y| ~1.19 — mirrors overhang body width like
    a real truck's, hence the envelope_exclude entry."""
    reset()
    s = 1.0 if side == "l" else -1.0
    cube((0, s * 0.10, 0.02), (0.05, 0.20, 0.05), "trim")           # arm
    cube((0, s * 0.205, 0.075), (0.07, 0.09, 0.18), "trim")         # head
    cube((-0.038, s * 0.205, 0.075), (0.012, 0.075, 0.15), "glass")  # mirror face (-X)
    name = "mirror_l" if side == "l" else "mirror_r"
    return export_part(out_dir, name, ground=False)


def build_pickup_rollbar(spec, out_dir):
    """Optional accessory (customization demo): roll bar over the bed front with
    two light pods. Pivot at the bed-front-wall top mount (author (-0.85, 0, 1.10))."""
    reset()
    for sy in (0.70, -0.70):
        cube((0, sy, 0.29), (0.08, 0.08, 0.58), "trim")     # uprights, abs z 1.10-1.68
    cube((0, 0, 0.545), (0.08, 1.48, 0.07), "trim")         # crossbar
    for sy in (0.32, -0.32):
        cube((0.055, sy, 0.60), (0.06, 0.11, 0.09), "light_f")  # light pods
    return export_part(out_dir, "rollbar", ground=False)


def build_pickup_wheel(spec, out_dir):
    """Chunky offroad wheel: wider tire, smaller rim fraction (deep sidewall),
    10 sidewall tread lugs. Lugs stay INSIDE the tire radius (rotated-cube
    corners max out at r 0.343 < R 0.35) so the manifest diameter — which the
    assembler's self-correcting visual scale trusts — remains exactly 2R.
    Pivot at hub centre; axle along author Y => part-local X after import."""
    reset()
    R = spec["wheel_radius"]
    Wd = spec["wheel_width"]
    # 20-vert tire = vertex-aligned bbox (exactly 2R on the radial axes) so the
    # assembler's self-correcting visual scale is a no-op — the 18-vert face-aligned
    # bbox measured 0.345 m and forced a runtime rescale (the coupe/hatch tires learned
    # this first). Lug corners still clear the tire radius (rc 0.30 + 0.042 = 0.342 < R).
    rcyl((0, 0, 0), R, Wd, "tire", axis='Y', verts=24, shoulder=0.028)
    # rim + hub RECESSED inboard of the tire face (deep-dish steelie look) so neither
    # gray disc is coplanar with the black tire outer face — that coincidence was the
    # long-standing pickup rim/tire z-fight (both faces sat at Wd/2, different material).
    cyl((0, 0, 0), R * 0.52, Wd - 0.05, "rim", axis='Y', verts=14)   # dish face inset 2.5 cm/side
    cyl((0, 0, 0), R * 0.18, Wd - 0.02, "rim", axis='Y', verts=8)    # hub proud of dish, still inboard
    # centre hub-cap disc — PROUD of the tyre face by 2.5 mm (owner-taste 2026-07-16).
    # The rim/hub above sit recessed to kill the sidewall z-fight, which left the wheel
    # reading as a plain black donut side-on. This grey cap gives it a deliberate metal
    # centre: depth Wd+0.005 => each cap face at ±(Wd/2 + 2.5 mm), never sharing the
    # tyre's outer-face plane (2.5 mm > the 0.5 mm coplanar tol, so no z-fight). Radius
    # R*0.36 stays well inside the rim (R*0.52) and far inside the lug ring (R-0.05) so
    # it reads as a hub, not a spinner. rcyl = the tyre's rounded-shoulder vocabulary.
    rcyl((0, 0, 0), R * 0.36, Wd + 0.005, "rim", axis='Y', verts=16, shoulder=0.012)
    rc = R - 0.05                                # lug centre radius
    for i in range(10):
        a = 2 * math.pi * i / 10
        # lug ends poke 2 cm out of each sidewall (visible shoulder-lug ring);
        # radially the rotated-cube corners stay under the tire surface
        cube((rc * math.cos(a), 0, rc * math.sin(a)),
             (0.06, Wd + 0.04, 0.06), "tire", rot=(0, -a, 0))
    return export_part(out_dir, "wheel", ground=False)


def build_pickup_parts(spec, kit_dir):
    build_pickup_cab(spec, kit_dir)
    build_pickup_bed(spec, kit_dir)
    build_pickup_tailgate(spec, kit_dir)
    build_pickup_hood(spec, kit_dir)
    build_pickup_door(spec, "l", kit_dir)
    build_pickup_door(spec, "r", kit_dir)
    build_pickup_bumper(spec, "f", kit_dir)
    build_pickup_bumper(spec, "r", kit_dir)
    build_pickup_grille(spec, kit_dir)
    build_pickup_mirror(spec, "l", kit_dir)
    build_pickup_mirror(spec, "r", kit_dir)
    build_pickup_rollbar(spec, kit_dir)
    build_pickup_wheel(spec, kit_dir)


def attach_table_pickup(spec):
    wb2 = spec["wheelbase"] / 2.0           # 1.70
    tr2 = spec["track"] / 2.0               # 0.85
    R = spec["wheel_radius"]                # 0.35
    front_x = spec["length"] / 2.0 - 0.20   # 2.50 — front bumper mount plane
    rear_x = -(spec["length"] / 2.0 - 0.10)  # -2.60 — rear bumper mount plane
    return {
        # part       (bX,     bY,    bZ)
        "cab":       (0.0,    0.0,   0.0),
        "wheel_fl":  (wb2,    tr2,   R),
        "wheel_fr":  (wb2,   -tr2,   R),
        "wheel_rl":  (-wb2,   tr2,   R),
        "wheel_rr":  (-wb2,  -tr2,   R),
        "door_l":    (0.80,   0.92,  0.80),
        "door_r":    (0.80,  -0.92,  0.80),
        "hood":      (1.05,   0.0,   1.08),
        "bed":       (-0.70,  0.0,   0.46),
        "tailgate":  (-2.55,  0.0,   0.53),
        "bumper_f":  (front_x, 0.0,  0.48),
        "bumper_r":  (rear_x,  0.0,  0.48),
        "grille":    (2.56,   0.0,   0.85),
        "mirror_l":  (0.72,   0.94,  1.18),
        "mirror_r":  (0.72,  -0.94,  1.18),
        "rollbar":   (-0.85,  0.0,   1.10),
    }


PICKUP_ROLES = {
    "cab": dict(obj="cab", pivot="footprint-centre@ground", axis=None, mass=0.52),
    "wheel_fl": dict(obj="wheel", pivot="hub-centre", axis="X", mass=0.04,
                     kind="wheel", steer=True, mirror=False),
    "wheel_fr": dict(obj="wheel", pivot="hub-centre", axis="X", mass=0.04,
                     kind="wheel", steer=True, mirror=True),
    "wheel_rl": dict(obj="wheel", pivot="hub-centre", axis="X", mass=0.04,
                     kind="wheel", steer=False, mirror=False),
    "wheel_rr": dict(obj="wheel", pivot="hub-centre", axis="X", mass=0.04,
                     kind="wheel", steer=False, mirror=True),
    "door_l": dict(obj="door_l", pivot="front-hinge-line", axis="Z", mass=0.025,
                   kind="door", open_sign=1),
    "door_r": dict(obj="door_r", pivot="front-hinge-line", axis="Z", mass=0.025,
                   kind="door", open_sign=-1),
    "hood": dict(obj="hood", pivot="rear-hinge-line", axis="X", mass=0.03,
                 kind="hood", open_sign=-1),
    # bed: the swap/break-off seam; mounts DOWN onto the frame => break direction up
    "bed": dict(obj="bed", pivot="bed-front-frame-mount", axis=None, mass=0.16,
                kind="bed", mount_normal="+Z"),
    # tailgate: bottom hinge, drops open rearward-down (opposite sense to the
    # hatch trunk's up-back). Sign to be VISUALLY verified when Stage C animates
    # (frame lesson: manifest axis letters trustworthy, swing signs verified live).
    "tailgate": dict(obj="tailgate", pivot="bottom-hinge-line", axis="X", mass=0.02,
                     kind="tailgate", open_sign=-1),
    "bumper_f": dict(obj="bumper_f", pivot="mount-plane", axis=None, mass=0.015,
                     kind="bolton", mount_normal="-Y"),
    "bumper_r": dict(obj="bumper_r", pivot="mount-plane", axis=None, mass=0.015,
                     kind="bolton", mount_normal="+Y"),
    "grille": dict(obj="grille", pivot="mount-plane", axis=None, mass=0.01,
                   kind="fascia", mount_normal="-Y"),
    # mirrors break outboard: author +Y (left) => model-local -X, and vice versa
    "mirror_l": dict(obj="mirror_l", pivot="door-mount", axis=None, mass=0.002,
                     kind="mirror", mount_normal="-X"),
    "mirror_r": dict(obj="mirror_r", pivot="door-mount", axis=None, mass=0.002,
                     kind="mirror", mount_normal="+X"),
    "rollbar": dict(obj="rollbar", pivot="bed-rail-mount", axis=None, mass=0.01,
                    kind="accessory", mount_normal="+Z"),
}


# ============================================================ coupe_kit geometry
# 1980s mid-engine WEDGE supercar — NOT an open-wheel
# racer. Low pointed nose (pop-up-lamp era flat front), steeply raked windshield,
# cab set well forward, long flat rear deck with a louver strip, big wing on twin
# struts, continuous BLACK lower cladding band, side intakes, hex red+amber tail
# clusters, twin exhaust, 5-spoke silver alloys with wide rears.
#
# Author-frame stations (m): front face +2.20 (bumper band) | nose cap 1.87..2.17 |
# front lid (hood) 1.05..1.90 | front axle +1.35 | windshield base +0.72 |
# roof 0.30..-0.60 (z 1.10 = spec height) | engine-lid hinge -0.64 (z 1.06) |
# deck -0.95..-1.90 (z 0.78) | tail panel -1.96 | rear axle -1.35 | rear face -2.20.
# Cladding band z 0.20..0.40 all round (rockers + bumpers). Nothing below z 0.10
# except tires (bottom-out drop 0.097 m; lowest lip authored 0.135).

def build_coupe_chassis(spec, out_dir):
    """Wedge body minus hood/doors/engine lid/bumpers/wing/mirrors. Pivot =
    footprint centre at ground (z=0)."""
    reset()
    B = spec["body_color"]
    roof_top = spec["height"]              # 1.10
    # floor + black rocker cladding (continuous lower band, brief)
    rcube((0, 0, 0.46), (3.90, 1.66, 0.24), B, r=0.04)      # floor x -1.95..1.95, z 0.34..0.58
    for sy in (0.86, -0.86):
        rcube((0.0, sy, 0.30), (2.60, 0.06, 0.20), "trim", r=0.014)  # rockers z 0.20..0.40
    # nose: raked fender slabs flanking the front lid + pointed nose cap
    rake = math.radians(5)     # +rot drops the +X (nose) end: rises toward the cowl
    for sy in (0.665, -0.665):
        rcube((1.36, sy, 0.585), (1.28, 0.37, 0.16), B, rot=(0, rake, 0), r=0.03)  # x 0.72..2.00
    rcube((2.02, 0, 0.52), (0.30, 1.40, 0.18), B, rot=(0, rake, 0), r=0.035)       # nose cap
    for s in (1.0, -1.0):                                    # swept nose corners
        cube((1.94, s * 0.70, 0.52), (0.34, 0.34, 0.17), B,
             rot=(0, rake, -s * math.radians(28)))
        cube((1.80, s * 0.50, 0.665), (0.30, 0.26, 0.05), B, rot=(0, rake, 0))  # pop-up lamp bulges
    cube((2.155, 0, 0.50), (0.04, 1.30, 0.10), "light_f")    # flat lamp/marker strip on the front face
    # cowl + steeply raked windshield with thin frame strips
    cube((0.70, 0, 0.66), (0.14, 1.46, 0.12), B)             # cowl wall
    cube((0.505, 0, 0.885), (0.07, 1.26, 0.52), "glass", rot=(0, math.radians(-51), 0))
    for s in (1.0, -1.0):
        # A-pillar frames: inner edge 2.5 mm OUTBOARD of the glass edge (y 0.63) so
        # the body frame and the windshield pane don't share the raked plane (that
        # coplanar overlap was a pre-existing z-fight on the windshield sides).
        cube((0.505, s * 0.6575, 0.885), (0.07, 0.05, 0.52), B, rot=(0, math.radians(-51), 0))  # A frames
    # roof + B pillars + steep backlight (louvers cover it from the engine lid)
    rcube((-0.21, 0, roof_top - 0.0275), (1.06, 1.30, 0.055), B, r=0.016)   # roof x -0.74..0.32
    for s in (1.0, -1.0):
        cube((-0.62, s * 0.64, 0.89), (0.08, 0.06, 0.38), B)      # B pillar
    # rear glass under the louvers: roof rear edge (-0.72,1.03) down to the deck
    # (-0.95,0.80); top leans FORWARD of its base => POSITIVE rotation
    cube((-0.835, 0, 0.915), (0.05, 1.10, 0.33), "glass", rot=(0, math.radians(45), 0))
    for s in (1.0, -1.0):   # sail panels: close the side notch, Esprit buttress look
        cube((-0.835, s * 0.60, 0.915), (0.06, 0.10, 0.33), B, rot=(0, math.radians(45), 0))
    # rear quarters: deck side rails + side intake vents behind the doors (brief)
    for sy in (0.75, -0.75):
        rcube((-1.28, sy, 0.55), (1.35, 0.30, 0.26), B, r=0.03)  # quarter mass x -1.95..-0.60
        rcube((-1.28, sy, 0.74), (1.35, 0.30, 0.12), B, r=0.025)  # rail up to deck height 0.80
    for s in (1.0, -1.0):
        cube((-0.72, s * 0.885, 0.56), (0.34, 0.045, 0.16), "trim")    # intake vent inset
        cube((-0.54, s * 0.90, 0.56), (0.06, 0.06, 0.20), B)           # vent leading edge
    # tail panel with hex-ish red + amber clusters (brief)
    rcube((-1.96, 0, 0.60), (0.10, 1.60, 0.40), B, r=0.025)  # tail z 0.40..0.80
    for s in (1.0, -1.0):
        cube((-2.015, s * 0.55, 0.62), (0.03, 0.26, 0.15), "light")
        cube((-2.015, s * 0.32, 0.62), (0.03, 0.12, 0.15), "light_amber")
    # wheel arch flares. LAW (matches the hatch, docs/part-kit-pipeline.md): the
    # flare outer edge sits 2.5 mm INBOARD of THIS axle's tire outer face, so the
    # wheel always reads proud of the arch. The front tire is NARROWER than the
    # rear (wheel_width 0.25 vs wheel_width_r 0.30), so the front flare must tuck
    # further inboard than the rear — a shared outer edge (the pre-fix bug) sat
    # 2.5 mm inboard of the wide rear tire but ~2 cm OUTBOARD of the front tire,
    # burying the front wheels behind their own flare.
    # ROUNDED arch flares (visual pass 2026-07-16, matches the hatch): per-axle
    # outer face 2.5 mm inboard of THAT axle's tire face (front narrower than
    # rear — the buried-front-wheel lesson). Shallow band (wedge car): hub z=R.
    tr2 = spec["track"] / 2.0                                   # 0.80 — hub y
    R = spec["wheel_radius"]
    flare_w = 0.09
    axle_face = {1.35:  tr2 + spec["wheel_width"] / 2.0,        # front tire face 0.925
                 -1.35: tr2 + spec["wheel_width_r"] / 2.0}      # rear  tire face 0.95
    for fx in (1.35, -1.35):
        cy = (axle_face[fx] - 0.0025) - flare_w / 2.0           # centre so outer = face-2.5mm
        for s in (1.0, -1.0):
            arch_band((fx, s * cy, R), R + 0.045, R + 0.115, flare_w, B)
    return export_part(out_dir, "chassis_shell", ground=False)


def build_coupe_wheel(spec, rear, out_dir):
    """5-spoke silver alloy (brief); rears visibly wider. Tire + dark dish +
    5 silver spokes + chrome hub. Pivot at hub centre; 20-vert tire = exact-2R
    bbox (assembler scale contract)."""
    reset()
    R = spec["wheel_radius"]
    Wd = spec["wheel_width_r"] if rear else spec["wheel_width"]
    rcyl((0, 0, 0), R, Wd, "tire", axis='Y', verts=24, shoulder=0.022)
    cyl((0, 0, 0), R * 0.62, Wd - 0.05, "trim", axis='Y', verts=16)   # recessed dark dish
    for i in range(5):
        a = 2 * math.pi * i / 5 + math.pi / 2
        rc = R * 0.34
        cube((rc * math.cos(a), 0, rc * math.sin(a)),
             (R * 0.36, Wd - 0.02, R * 0.13), "rim", rot=(0, -a, 0))  # spokes
    cyl((0, 0, 0), R * 0.14, Wd + 0.01, "chrome", axis='Y', verts=8)  # hub
    name = "wheel_r" if rear else "wheel_f"
    return export_part(out_dir, name, ground=False)


def build_coupe_door(spec, side, out_dir):
    """Low wedge door; pivot on the FRONT hinge line (0.60, +-0.90, 0.52).
    Window pane offset inboard under the roof edge; flush black handle."""
    reset()
    B = spec["body_color"]
    s = 1.0 if side == "l" else -1.0
    rcube((-0.60, 0, 0.0), (1.20, 0.05, 0.36), B, r=0.014)         # skin z 0.34..0.70
    cube((-0.60, -s * 0.245, 0.35), (0.96, 0.04, 0.36), "glass")    # window at pillar line
    cube((-1.02, s * 0.032, 0.09), (0.12, 0.02, 0.04), "trim")      # flush handle
    name = "door_l" if side == "l" else "door_r"
    return export_part(out_dir, name, ground=False)


def build_coupe_hood(spec, out_dir):
    """Front luggage lid on the nose; pivot on the REAR hinge line
    (1.05, 0, 0.70); extends forward between the fenders, raked with the nose."""
    reset()
    B = spec["body_color"]
    rcube((0.42, 0, -0.015), (0.85, 0.88, 0.05), B, rot=(0, math.radians(5), 0), r=0.014)
    return export_part(out_dir, "hood", ground=False)


def build_coupe_engine_lid(spec, out_dir):
    """Louvered engine cover + flat rear deck (the wedge signature); pivot on
    the FORWARD hinge line at roof rear (-0.64, 0, 1.06). Louver slats step
    down the backlight slope; flat deck runs to the tail. Swing axis = author
    Y => part-local X (opens up-forward like a clamshell)."""
    reset()
    B = spec["body_color"]
    for i in range(5):                                    # louver strip (brief)
        t = 0.10 + 0.20 * i
        cube((-0.31 * t, 0, -0.26 * t), (0.055, 1.12, 0.022), "trim",
             rot=(0, math.radians(-15), 0))
    rcube((-0.785, 0, -0.285), (0.95, 1.16, 0.05), B, r=0.016)  # flat deck, top z 0.775
    # lid shoulder front edge retracted 2 cm (author x -0.96 -> -0.98, hidden under
    # the louver strip) so the shoulder box no longer overlaps the backlight glass
    # base at the shut-line — clears the last cross-part z-fight there (was 0.9 cm^2).
    cube((-0.43, 0, -0.24), (0.18, 1.10, 0.03), B)        # lid shoulder at slope foot
    return export_part(out_dir, "engine_lid", ground=False)


def build_coupe_wing(spec, out_dir):
    """Large rear wing on twin struts (brief); pivot at the deck mount
    (-1.80, 0, 0.78). Blade spans wider than the deck; top z 1.04 < roof."""
    reset()
    B = spec["body_color"]
    for s in (1.0, -1.0):
        cube((-0.05, s * 0.40, 0.10), (0.06, 0.05, 0.20), "trim")   # struts
    rcube((-0.10, 0, 0.235), (0.34, 1.55, 0.045), B, rot=(0, math.radians(8), 0), r=0.012)
    for s in (1.0, -1.0):
        # end-plate outer face 2.5 mm INBOARD of the blade tip (y 0.775) so the trim
        # winglet and the body blade don't share the tip plane (pre-existing z-fight).
        cube((-0.10, s * 0.7625, 0.235), (0.30, 0.02, 0.09), "trim")  # end plates
    return export_part(out_dir, "wing", ground=False)


def build_coupe_bumper(spec, end, out_dir):
    """BLACK bumper band — part of the continuous lower cladding ring (brief).
    Pivot at the mount plane (author +-2.00, z 0.30). Front adds a lower lip +
    amber indicators; rear adds the black valance + twin exhaust tips."""
    reset()
    if end == "f":
        rcube((0.085, 0, 0.0), (0.21, 1.78, 0.20), "trim", r=0.028)  # band z 0.20..0.40
        rcube((0.06, 0, -0.13), (0.16, 1.60, 0.07), "trim", r=0.012)  # lip z 0.135..0.205
        for s in (1.0, -1.0):
            cube((0.19, s * 0.72, 0.02), (0.02, 0.12, 0.06), "light_amber")
        name = "bumper_f"
    else:
        rcube((-0.085, 0, 0.0), (0.21, 1.78, 0.20), "trim", r=0.028)  # band
        rcube((-0.05, 0, -0.125), (0.14, 1.55, 0.08), "trim", r=0.012)  # valance
        for s in (1.0, -1.0):
            cyl((-0.155, s * 0.28, -0.10), 0.045, 0.10, "chrome", axis='X', verts=8)  # tips proud
        name = "bumper_r"
    return export_part(out_dir, name, ground=False)


def build_coupe_mirror(spec, side, out_dir):
    """Wedge door mirror; pivot at the door-top front corner (0.46, +-0.90, 0.72)."""
    reset()
    s = 1.0 if side == "l" else -1.0
    cube((0, s * 0.055, 0.02), (0.04, 0.11, 0.03), "trim")
    cube((0, s * 0.145, 0.05), (0.06, 0.075, 0.12), "trim")
    cube((-0.033, s * 0.145, 0.05), (0.012, 0.065, 0.10), "glass")
    name = "mirror_l" if side == "l" else "mirror_r"
    return export_part(out_dir, name, ground=False)


def build_coupe_parts(spec, kit_dir):
    build_coupe_chassis(spec, kit_dir)
    build_coupe_wheel(spec, False, kit_dir)
    build_coupe_wheel(spec, True, kit_dir)
    build_coupe_door(spec, "l", kit_dir)
    build_coupe_door(spec, "r", kit_dir)
    build_coupe_hood(spec, kit_dir)
    build_coupe_engine_lid(spec, kit_dir)
    build_coupe_wing(spec, kit_dir)
    build_coupe_bumper(spec, "f", kit_dir)
    build_coupe_bumper(spec, "r", kit_dir)
    build_coupe_mirror(spec, "l", kit_dir)
    build_coupe_mirror(spec, "r", kit_dir)


def attach_table_coupe(spec):
    wb2 = spec["wheelbase"] / 2.0           # 1.35
    tr2 = spec["track"] / 2.0               # 0.80
    R = spec["wheel_radius"]                # 0.33
    front_x = spec["length"] / 2.0 - 0.20   # 2.00 — front bumper mount plane
    rear_x = -(spec["length"] / 2.0 - 0.20)
    return {
        # part        (bX,     bY,    bZ)
        "chassis_shell": (0.0, 0.0, 0.0),
        "wheel_fl":  (wb2,  tr2,  R),
        "wheel_fr":  (wb2, -tr2,  R),
        "wheel_rl": (-wb2,  tr2,  R),
        "wheel_rr": (-wb2, -tr2,  R),
        "door_l":    (0.60,  0.90, 0.52),
        "door_r":    (0.60, -0.90, 0.52),
        "hood":      (1.05,  0.0,  0.70),
        "engine_lid": (-0.64, 0.0, 1.06),
        "wing":     (-1.80,  0.0,  0.78),
        "bumper_f":  (front_x, 0.0, 0.30),
        "bumper_r":  (rear_x,  0.0, 0.30),
        "mirror_l":  (0.46,  0.90, 0.72),
        "mirror_r":  (0.46, -0.90, 0.72),
    }


COUPE_ROLES = {
    "chassis_shell": dict(obj="chassis_shell", pivot="footprint-centre@ground",
                          axis=None, mass=0.64),
    "wheel_fl": dict(obj="wheel_f", pivot="hub-centre", axis="X", mass=0.05,
                     kind="wheel", steer=True, mirror=False),
    "wheel_fr": dict(obj="wheel_f", pivot="hub-centre", axis="X", mass=0.05,
                     kind="wheel", steer=True, mirror=True),
    "wheel_rl": dict(obj="wheel_r", pivot="hub-centre", axis="X", mass=0.05,
                     kind="wheel", steer=False, mirror=False),
    "wheel_rr": dict(obj="wheel_r", pivot="hub-centre", axis="X", mass=0.05,
                     kind="wheel", steer=False, mirror=True),
    "door_l": dict(obj="door_l", pivot="front-hinge-line", axis="Z", mass=0.028,
                   kind="door", open_sign=1),
    "door_r": dict(obj="door_r", pivot="front-hinge-line", axis="Z", mass=0.028,
                   kind="door", open_sign=-1),
    "hood": dict(obj="hood", pivot="rear-hinge-line", axis="X", mass=0.022,
                 kind="hood", open_sign=-1),
    # louvered clamshell over the engine bay: forward hinge at the roof rear,
    # opens up-forward like the hatch tailgate (kind trunk = the same contract)
    "engine_lid": dict(obj="engine_lid", pivot="forward-hinge-line", axis="X",
                       mass=0.035, kind="trunk", open_sign=1),
    "wing": dict(obj="wing", pivot="deck-mount", axis=None, mass=0.008,
                 kind="accessory", mount_normal="+Z"),
    "bumper_f": dict(obj="bumper_f", pivot="mount-plane", axis=None, mass=0.018,
                     kind="bolton", mount_normal="-Y"),
    "bumper_r": dict(obj="bumper_r", pivot="mount-plane", axis=None, mass=0.018,
                     kind="bolton", mount_normal="+Y"),
    "mirror_l": dict(obj="mirror_l", pivot="door-mount", axis=None, mass=0.002,
                     kind="mirror", mount_normal="-X"),
    "mirror_r": dict(obj="mirror_r", pivot="door-mount", axis=None, mass=0.002,
                     kind="mirror", mount_normal="+X"),
}


# ============================================================ kart_kit geometry
# A REAL racing kart, exposed anatomy: flat floor
# pan + visible perimeter tube frame, nose cone with bumper hoops, side pods,
# rear bumper bar, tall-backed bucket seat, steering column + wheel, engine
# block beside the seat, black tie rods/axle. Slicks: rears visibly wider;
# ACID-GREEN spoked rims matching the frame; black hardware everywhere else.
# Driver = the engine citizen via VehicleFactory.AddDriver (directive
# 2026-07-13: NEVER model a custom driver figure) — the manifest carries
# driver_seat_author_m, the citizen sit point in this authoring frame.
#
# Stations (m): nose front +0.95 | front axle +0.775 | pedal tray 0.40..0.70 |
# steering column base +0.30 | seat centre -0.32 | engine (right) -0.45 |
# rear axle -0.775 | bumper bar face -0.95.  Heights: floor 0.10..0.13 |
# frame tubes ~0.145 | seat pan top 0.19 | seat back top 0.55 (= spec height) |
# wheel hubs 0.16. Ground law: nothing below z 0.10 (bottom-out drop 0.091 m).

def build_kart_frame(spec, out_dir):
    """Floor pan + perimeter tube frame + tie rods + rear axle. The structural
    part: NEVER dents (rigid_damage — brief: frame/floor not dentable). Pivot =
    footprint centre at ground (z=0). Frame tubes in body colour (painted
    chromoly), hardware black."""
    reset()
    B = spec["body_color"]
    rcube((0.10, 0, 0.115), (0.80, 0.58, 0.03), "trim", r=0.02)   # floor pan z 0.10..0.13 (soft edges)
    rcube((0.55, 0, 0.115), (0.30, 0.30, 0.03), "trim", r=0.02)   # pedal tray extension
    for sy in (0.28, -0.28):                                  # perimeter side rails
        cyl((-0.05, sy, 0.145), 0.022, 1.40, B, axis='X', verts=8)
    cyl((0.62, 0, 0.145), 0.022, 0.52, B, axis='Y', verts=8)  # front cross tube
    cyl((-0.70, 0, 0.145), 0.022, 0.56, B, axis='Y', verts=8)  # rear cross tube
    cyl((-0.15, 0, 0.145), 0.020, 0.56, B, axis='Y', verts=8)  # seat cross tube
    # steering/suspension hardware (black): tie rod + spindles + rear axle
    cyl((0.70, 0, 0.15), 0.012, 1.00, "trim", axis='Y', verts=6)
    for sy in (0.50, -0.50):
        cyl((0.775, sy, 0.16), 0.018, 0.14, "trim", axis='Y', verts=6)
    cyl((-0.775, 0, 0.16), 0.025, 1.05, "trim", axis='Y', verts=8)  # live rear axle
    return export_part(out_dir, "frame", ground=False)


def build_kart_wheel(spec, rear, out_dir):
    """Slick + coloured spoked rim (brief: rims match the body colour, rears
    visibly wider). Pivot at hub centre; 20-vert tire = exact-2R bbox. Beveled
    tire shoulders (rounded pass 2026-07-16 — the same rcyl language the
    hatch/coupe/pickup slicks use); shoulder cuts only the rim corner, so the
    radial (2R) and width (Wd) extents survive the assembler's exact-2R contract."""
    reset()
    R = spec["wheel_radius"]
    Wd = spec["wheel_width_r"] if rear else spec["wheel_width"]
    rcyl((0, 0, 0), R, Wd, "tire", axis='Y', verts=20, shoulder=0.018)     # slick (rounded shoulders)
    cyl((0, 0, 0), R * 0.60, Wd + 0.012, spec["body_color"], axis='Y', verts=10)  # coloured rim
    cyl((0, 0, 0), R * 0.18, Wd + 0.022, "trim", axis='Y', verts=6)        # hub nut
    name = "wheel_r" if rear else "wheel_f"
    return export_part(out_dir, name, ground=False)


def build_kart_nose(spec, out_dir):
    """Nose cone/fairing + front bumper hoop (brief). Fascia bolt-on; pivot at
    the frame mount plane (0.62, 0, 0.16); extends forward to +0.95."""
    reset()
    B = spec["body_color"]
    rcube((0.14, 0, 0.02), (0.30, 0.34, 0.10), B, rot=(0, math.radians(8), 0), r=0.03)  # cone
    rcube((0.02, 0, 0.0), (0.10, 0.44, 0.08), B, r=0.025)       # cone base fairing
    for sy in (0.10, -0.10):                                    # hoop prongs
        cube((0.28, sy, -0.01), (0.10, 0.028, 0.028), "trim")
    cube((0.315, 0, -0.01), (0.03, 0.30, 0.05), "trim")         # hoop cross bar
    cube((0.245, 0, 0.065), (0.02, 0.16, 0.10), "light_f",
         rot=(0, math.radians(-15), 0))                         # number plate panel
    return export_part(out_dir, "nose", ground=False)


def build_kart_pod(spec, side, out_dir):
    """Side pod (brief); bolt-on; pivot at the side-rail mount
    (-0.05, +-0.30, 0.15). Body-colour box with a black outboard cap."""
    reset()
    B = spec["body_color"]
    s = 1.0 if side == "l" else -1.0
    rcube((0, s * 0.075, 0.015), (0.55, 0.15, 0.11), B, r=0.025)  # pod, outer y 0.45
    cube((0, s * 0.155, 0.015), (0.50, 0.02, 0.09), "trim")     # outboard cap
    name = "pod_l" if side == "l" else "pod_r"
    return export_part(out_dir, name, ground=False)


def build_kart_rear_bumper(spec, out_dir):
    """Rear bumper bar (brief); bolt-on; pivot at the rear-frame mount
    (-0.85, 0, 0.16); bar face at author -0.95 = length/2."""
    reset()
    rcube((-0.075, 0, 0.0), (0.05, 0.90, 0.05), "trim", r=0.02)  # bar (rounded tube)
    for sy in (0.30, -0.30):
        cube((-0.03, sy, -0.005), (0.08, 0.028, 0.04), "trim")  # uprights
    return export_part(out_dir, "bumper_r", ground=False)


def build_kart_seat(spec, out_dir):
    """Tall-backed bucket seat (brief), sized for the citizen's seated pose
    (directive: stock citizen, no custom driver). Pivot at the seat-base
    frame mount (-0.32, 0, 0.14). Pan top z 0.19, back top exactly at spec
    height 0.55; bolsters clear the citizen's hips (pan is 0.36 wide)."""
    reset()
    rcube((0.0, 0, 0.05), (0.36, 0.36, 0.06), "trim", r=0.025)   # pan, top author 0.22
    rcube((-0.185, 0, 0.205), (0.07, 0.38, 0.41), "trim", r=0.025)  # tall back, top author 0.55
    for sy in (0.165, -0.165):
        rcube((-0.02, sy, 0.10), (0.28, 0.05, 0.10), "trim", r=0.02)  # side bolsters
    return export_part(out_dir, "seat", ground=False)


def build_kart_engine(spec, out_dir):
    """Engine block + exhaust beside/behind the seat, right side (brief).
    Accessory; pivot at the frame mount (-0.45, -0.34, 0.14)."""
    reset()
    rcube((0.0, 0, 0.06), (0.24, 0.20, 0.16), "trim", r=0.02)   # block, author z 0.12..0.28
    cube((0.05, 0, 0.155), (0.12, 0.14, 0.05), "chrome")        # head/cam cover
    cyl((-0.17, 0.02, 0.02), 0.024, 0.26, "chrome", axis='X', verts=8)  # exhaust, rear-facing
    # carb/airbox nub pulled 2.5 mm INBOARD (local y -0.06 -> -0.0575) so its outer
    # face (was author y -0.45) clears the right side-pod's outer skin plane (also
    # author y -0.45) it used to share — a cross-part trim/body_acid z-fight at the
    # pod front edge (they overlapped only a ~5 mm x-sliver, but coplanar = flicker).
    cube((0.10, -0.0575, 0.02), (0.06, 0.10, 0.08), "trim")     # carb/airbox nub
    return export_part(out_dir, "engine", ground=False)


def build_kart_steering(spec, out_dir):
    """Steering column + actual steering wheel (brief). Accessory; pivot at the
    column floor mount (0.30, 0, 0.14). Column leans back toward the driver;
    wheel top author z ~0.43 (clears the citizen's knees at the pedal tray)."""
    reset()
    lean = math.radians(-35)                                    # top toward the seat (-X)
    cyl((-0.06, 0, 0.13), 0.015, 0.34, "trim", rot=(0, lean, 0))     # column (base at floor)
    cyl((-0.155, 0, 0.27), 0.10, 0.03, "trim", rot=(0, lean, 0))     # wheel rim disc
    cube((-0.155, 0, 0.27), (0.05, 0.16, 0.026), "chrome", rot=(0, lean, 0))  # spoke bar
    return export_part(out_dir, "steering", ground=False)


def build_kart_parts(spec, kit_dir):
    build_kart_frame(spec, kit_dir)
    build_kart_wheel(spec, False, kit_dir)
    build_kart_wheel(spec, True, kit_dir)
    build_kart_nose(spec, kit_dir)
    build_kart_pod(spec, "l", kit_dir)
    build_kart_pod(spec, "r", kit_dir)
    build_kart_rear_bumper(spec, kit_dir)
    build_kart_seat(spec, kit_dir)
    build_kart_engine(spec, kit_dir)
    build_kart_steering(spec, kit_dir)


def attach_table_kart(spec):
    wb2 = spec["wheelbase"] / 2.0           # 0.775
    tr2 = spec["track"] / 2.0               # 0.57
    R = spec["wheel_radius"]                # 0.16
    return {
        # part       (bX,    bY,    bZ)
        "frame":     (0.0,   0.0,   0.0),
        "wheel_fl":  (wb2,   tr2,   R),
        "wheel_fr":  (wb2,  -tr2,   R),
        "wheel_rl": (-wb2,   tr2,   R),
        "wheel_rr": (-wb2,  -tr2,   R),
        "nose":      (0.62,  0.0,   0.16),
        "pod_l":     (-0.05, 0.30,  0.15),
        "pod_r":     (-0.05, -0.30, 0.15),
        "bumper_r":  (-0.85, 0.0,   0.16),
        "seat":      (-0.32, 0.0,   0.14),
        "engine":    (-0.45, -0.34, 0.14),
        "steering":  (0.30,  0.0,   0.14),
    }


KART_ROLES = {
    # the frame/floor NEVER dents (brief): rigid_damage emits the never-dent
    # sentinel band (same profile as wheels) while staying kind=chassis
    "frame": dict(obj="frame", pivot="footprint-centre@ground", axis=None,
                  mass=0.30, rigid_damage=True),
    "wheel_fl": dict(obj="wheel_f", pivot="hub-centre", axis="X", mass=0.055,
                     kind="wheel", steer=True, mirror=False),
    "wheel_fr": dict(obj="wheel_f", pivot="hub-centre", axis="X", mass=0.055,
                     kind="wheel", steer=True, mirror=True),
    "wheel_rl": dict(obj="wheel_r", pivot="hub-centre", axis="X", mass=0.055,
                     kind="wheel", steer=False, mirror=False),
    "wheel_rr": dict(obj="wheel_r", pivot="hub-centre", axis="X", mass=0.055,
                     kind="wheel", steer=False, mirror=True),
    "nose": dict(obj="nose", pivot="mount-plane", axis=None, mass=0.05,
                 kind="fascia", mount_normal="-Y"),
    # side pods are lateral crash pads, not rear items: zone override (the
    # default bolton rule would tag pod_r "rear" off the '_r' suffix)
    "pod_l": dict(obj="pod_l", pivot="side-rail-mount", axis=None, mass=0.045,
                  kind="bolton", mount_normal="-X", zone="door"),
    "pod_r": dict(obj="pod_r", pivot="side-rail-mount", axis=None, mass=0.045,
                  kind="bolton", mount_normal="+X", zone="door"),
    "bumper_r": dict(obj="bumper_r", pivot="mount-plane", axis=None, mass=0.04,
                     kind="bolton", mount_normal="+Y"),
    # the seat is cosmetic-kind but visually load-bearing (the citizen sits in
    # it): explicit required=True so a missing seat vmdl falls back whole-kit
    "seat": dict(obj="seat", pivot="frame-mount", axis=None, mass=0.07,
                 kind="accessory", mount_normal="+Z", zone="cabin", required=True),
    "engine": dict(obj="engine", pivot="frame-mount", axis=None, mass=0.20,
                   kind="accessory", mount_normal="+Z"),
    "steering": dict(obj="steering", pivot="floor-mount", axis=None, mass=0.03,
                     kind="accessory", mount_normal="+Z", zone="cabin"),
}


# ============================================================ kit registry
KITS = {
    "hatch_kit": dict(
        build=build_hatch_parts,
        objs=("chassis_shell", "wheel", "door_l", "door_r", "hood", "trunk",
              "bumper_f", "bumper_r", "mirror_l", "mirror_r", "spoiler"),
        roles=HATCH_ROLES,
        attach=attach_table_hatch,
        envelope_exclude=("mirror_l", "mirror_r"),   # stalked mirrors overhang body width
        max_parts=16, max_tris=8000,
        min_part_ground=0.095,   # bottom-out guard: static susp length 0.089 m
        contact=dict(
            offs={"chassis_shell": (0, 0), "wheel": (0, 3.2), "door_l": (3.6, 3.0),
                  "door_r": (3.6, 1.2), "hood": (3.6, -1.2), "trunk": (-3.4, 3.0),
                  "bumper_f": (-3.4, 0.6), "bumper_r": (-3.4, -1.6),
                  "mirror_l": (3.6, -2.6), "mirror_r": (3.6, -3.4),
                  "spoiler": (-3.4, -3.4)},
            cam_loc=(2.0, -6.5, 7.5), cam_rot=(45, 0, 18), lens=42),
        views=dict(spacing=5.0, cam_loc=(0.0, -20.0, 4.0), cam_rot=(79, 0, 0), lens=35),
    ),
    "pickup_kit": dict(
        build=build_pickup_parts,
        objs=("cab", "bed", "tailgate", "hood", "door_l", "door_r",
              "bumper_f", "bumper_r", "grille", "mirror_l", "mirror_r",
              "rollbar", "wheel"),
        roles=PICKUP_ROLES,
        attach=attach_table_pickup,
        envelope_exclude=("mirror_l", "mirror_r"),   # real-truck mirrors overhang body width
        max_parts=16, max_tris=8000,
        min_part_ground=0.095,
        contact=dict(
            offs={"cab": (0, 0), "bed": (0, 3.8), "wheel": (0, -3.6),
                  "hood": (4.4, 2.6), "door_l": (4.4, 0.8), "door_r": (4.4, -0.8),
                  "tailgate": (4.4, -2.6), "grille": (-4.4, 3.0),
                  "bumper_f": (-4.4, 1.6), "bumper_r": (-4.4, 0.2),
                  "mirror_l": (-4.4, -1.0), "mirror_r": (-4.4, -1.9),
                  "rollbar": (-4.4, -3.2)},
            cam_loc=(1.2, -12.0, 13.5), cam_rot=(45, 0, 6), lens=33),
        views=dict(spacing=6.6, cam_loc=(0.0, -26.0, 4.8), cam_rot=(79, 0, 0), lens=35),
    ),
    "coupe_kit": dict(
        build=build_coupe_parts,
        objs=("chassis_shell", "wheel_f", "wheel_r", "door_l", "door_r", "hood",
              "engine_lid", "wing", "bumper_f", "bumper_r", "mirror_l", "mirror_r"),
        roles=COUPE_ROLES,
        attach=attach_table_coupe,
        envelope_exclude=("mirror_l", "mirror_r"),
        max_parts=16, max_tris=8000,
        min_part_ground=0.095,   # static susp length 0.097 m; lowest lip 0.135
        contact=dict(
            offs={"chassis_shell": (0, 0), "wheel_f": (0, 3.4), "wheel_r": (0, -3.4),
                  "hood": (4.0, 3.0), "door_l": (4.0, 1.4), "door_r": (4.0, -0.2),
                  "engine_lid": (4.0, -1.8), "wing": (4.0, -3.4),
                  "bumper_f": (-4.0, 2.6), "bumper_r": (-4.0, 1.0),
                  "mirror_l": (-4.0, -0.4), "mirror_r": (-4.0, -1.3)},
            cam_loc=(1.6, -7.5, 8.5), cam_rot=(45, 0, 12), lens=40),
        views=dict(spacing=5.5, cam_loc=(0.0, -21.0, 3.4), cam_rot=(80, 0, 0), lens=35),
    ),
    "kart_kit": dict(
        build=build_kart_parts,
        objs=("frame", "wheel_f", "wheel_r", "nose", "pod_l", "pod_r",
              "bumper_r", "seat", "engine", "steering"),
        roles=KART_ROLES,
        attach=attach_table_kart,
        envelope_exclude=(),      # wheels ARE the envelope (open-wheel kart)
        max_parts=16, max_tris=8000,
        min_part_ground=0.095,    # static susp length 0.091 m; floor pan at 0.10
        # citizen sit point (authoring frame, metres): where VehicleFactory puts
        # the driver GO root (citizen feet-origin, animgraph chair pose sits the
        # pelvis behind/above it). Emitted as manifest driver_seat_author_m;
        # tuned so the pose lands in the bucket seat (Phase-2 visual verify),
        # RE-tuned 2026-07-13 for the recumbent sit=4 pose —
        # this tuple is the single source of truth; the manifest is generated.
        driver_seat=(-0.20, 0.0, 0.12),
        contact=dict(
            offs={"frame": (0, 0), "wheel_f": (0, 1.8), "wheel_r": (0, -1.6),
                  "nose": (2.0, 1.6), "pod_l": (2.0, 0.4), "pod_r": (2.0, -0.8),
                  "bumper_r": (2.0, -1.9), "seat": (-2.0, 1.4),
                  "engine": (-2.0, 0.0), "steering": (-2.0, -1.2)},
            cam_loc=(0.8, -4.2, 4.6), cam_rot=(45, 0, 10), lens=42),
        views=dict(spacing=2.8, cam_loc=(0.0, -10.5, 1.5), cam_rot=(84, 0, 0), lens=35,
                   driver_dummy=True),   # seated citizen-proportion proxy (fit check)
    ),
}


# ============================================================ frame mapping (PROVEN)
def author_to_local(b):
    """Author (X fwd, Y left, Z up) -> chassis model-local engine metres.
    PROVEN mapping m = (-bY, +bX, bZ) — see module docstring. (The pre-fix
    emission used (-bY, -bX, bZ), det -1, a mirror: disproven in-engine.)"""
    bx, by, bz = b
    return (-by, bx, bz)


# ============================================================ damage band (schema v3, D1)
# Per-kind damage-band defaults. LOCKSTEP with Code/Vehicle/Parts/PartKitManifest.cs
# DamageDefaults.For / .Zone AND tools/test_partkit.py — change one, change all three.
# Impulses are N*s (mass*|dv| across a contact tick); stiffness = metres of dent per N*s over the
# dent threshold; max_crush_m = metres ceiling. Values are D1 starting points (live tuning = a later D1 pass).
NO_DETACH = 1_000_000_000.0
# kind -> (dentImpulse, loosenMul, detachMul[None=non-detachable -> sentinel], stiffness, crushFrac)
_KIND_DAMAGE = {
    "chassis":   (2600.0, 2.5, None, 3.0e-5, 0.18),
    "bed":       (2200.0, 2.2, 6.0,  3.5e-5, 0.16),
    "door":      (1500.0, 2.0, 4.5,  5.0e-5, 0.30),
    "hood":      (1100.0, 2.4, 4.0,  7.0e-5, 0.30),
    "trunk":     (1200.0, 2.2, 4.0,  6.5e-5, 0.30),
    "tailgate":  (1200.0, 2.0, 3.5,  6.5e-5, 0.30),
    "bolton":    (800.0,  2.2, 3.2,  8.0e-5, 0.35),
    "fascia":    (750.0,  2.0, 3.0,  9.0e-5, 0.30),
    "mirror":    (250.0,  1.6, 2.2,  1.2e-4, 0.30),
    "accessory": (400.0,  1.8, 2.5,  1.0e-4, 0.30),
}


def default_zone(kind, part):
    """Zone tag: front-corner steering parts vs hood vs rear vs door vs cabin vs wheel.
    Bolt-ons split front/rear by part name. Mirrors DamageDefaults.Zone in C#."""
    p = part or ""
    if kind == "wheel":
        return "wheel"
    if kind == "chassis":
        return "cabin"
    if kind == "hood":
        return "hood"
    if kind in ("door", "mirror"):
        return "door"
    if kind in ("trunk", "tailgate", "bed", "accessory"):
        return "rear"
    if kind == "fascia":
        return "front"
    if kind == "bolton":
        return "rear" if ("_r" in p or "rear" in p) else "front"
    return "cabin"


def damage_defaults(kind, part, dims_m):
    """Per-part schema-v3 damage band. Mirrors DamageDefaults.For (+ .Zone) in C#."""
    if kind == "wheel":  # wheels never dent (physics contract)
        return dict(dent_impulse=NO_DETACH, loosen_impulse=NO_DETACH,
                    detach_impulse=NO_DETACH, stiffness=0.0, max_crush_m=0.0,
                    zone="wheel")
    dent, loosen_mul, detach_mul, stiff, crush_frac = _KIND_DAMAGE.get(
        kind, (1500.0, 2.2, 4.0, 5.0e-5, 0.25))
    min_dim = min(dims_m) if dims_m else 0.5
    if min_dim <= 0:
        min_dim = 0.5
    crush = max(0.05, min(0.30, crush_frac * min_dim))
    detach = NO_DETACH if detach_mul is None else dent * detach_mul
    return dict(
        dent_impulse=round(dent, 4),
        loosen_impulse=round(dent * loosen_mul, 4),
        detach_impulse=round(detach, 4),
        stiffness=stiff,
        max_crush_m=round(crush, 4),
        zone=default_zone(kind, part),
    )


def local_bounds(mn, mx):
    """OBJ-file (Y-up, o=(bX,bZ,-bY)) bounds -> TRUE model-local engine frame
    via the proven import m=(oZ,oX,oY): plain cyclic permutation, no sign flips.
    Returns (size, centre, min, max) in model-local metres."""
    lmn = (mn[2], mn[0], mn[1])
    lmx = (mx[2], mx[0], mx[1])
    size = tuple(b - a for a, b in zip(lmn, lmx))
    ctr = tuple((a + b) / 2 for a, b in zip(lmn, lmx))
    return size, ctr, lmn, lmx


# ============================================================ post-pass (python)
def parse_obj(path):
    """Return (min[3], max[3], has_usemtl, has_vt, mtllib, materials, tris, submeshes)
    from OBJ. tris counts fan-triangulated faces: an n-gon face line contributes n-2.

    `submeshes` is an ordered list of (material_name, index_count) — one entry per
    `usemtl` GROUP in DECLARATION ORDER, index_count = tris*3. This matches how the
    s&box compiler lays out the single compiled mesh's index buffer (draw-ranges grouped
    by material, in usemtl/Materials order — EMPIRICALLY PROVEN 2026-07-13, A1 probe:
    cab range zCentroids trim/body/glass/light/chrome = 15.5/42/60/71/13, contiguous &
    material-coherent). PartMeshRebuilder consumes the flattened `submesh_index_counts`
    to split a dent rebuild per submesh so a multi-material part keeps every material
    (fixes the "dented pickup cab renders black" bug — Materials[0]=trim was painted over
    the whole flattened mesh)."""
    mn = [9e9] * 3
    mx = [-9e9] * 3
    has_usemtl = has_vt = False
    mats = []
    mtllib = None
    tris = 0
    submeshes = []          # [(material, index_count)] in usemtl declaration order
    cur_mat = None
    cur_tris = 0

    def flush():
        if cur_mat is not None:
            submeshes.append((cur_mat, cur_tris * 3))

    with open(path, encoding="utf8", errors="ignore") as f:
        for line in f:
            if line.startswith("v "):
                p = [float(v) for v in line.split()[1:4]]
                for i in range(3):
                    mn[i] = min(mn[i], p[i])
                    mx[i] = max(mx[i], p[i])
            elif line.startswith("vt "):
                has_vt = True
            elif line.startswith("usemtl "):
                has_usemtl = True
                flush()
                cur_mat = line.split()[1]
                cur_tris = 0
                mats.append(cur_mat)
            elif line.startswith("mtllib "):
                mtllib = line.split(None, 1)[1].strip()
            elif line.startswith("f "):
                n = max(len(line.split()) - 3, 1)
                tris += n
                cur_tris += n
    flush()
    return mn, mx, has_usemtl, has_vt, mtllib, sorted(set(mats)), tris, submeshes


VMDL_HEADER = ('<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} '
               'format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->')


def write_white_png(path):
    px = [[(255, 255, 255)] * 8 for _ in range(8)]
    raw = b"".join(b"\x00" + bytes(v for p in row for v in p) for row in px)

    def chunk(t, d):
        c = t + d
        return struct.pack(">I", len(d)) + c + struct.pack(">I", zlib.crc32(c))
    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", 8, 8, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 9))
           + chunk(b"IEND", b""))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(png)


def write_flat_vmat(path, rgb, white_rel, rough):
    r, g, b = rgb
    txt = f"""// generated by tools/gen_vehicle.py
Layer0
{{
\tshader "shaders/complex.shader"

\tTextureColor "{white_rel}"
\tg_vColorTint "[{r:.4f} {g:.4f} {b:.4f} 0.0000]"
\tg_flModelTintAmount "1.000"
\tTextureRoughness "[{rough:.4f} {rough:.4f} {rough:.4f} 1.0000]"
\tg_flMetalness "0.000"
}}
"""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", newline="\n") as f:
        f.write(txt)


def write_vmdl(path, obj_rel, materials, mat_dir_rel):
    remaps = []
    for m in materials:
        vmat = f"{mat_dir_rel}/{m}.vmat"
        remaps.append((m, vmat))
        remaps.append((f"{m}.vmat", vmat))
    remap_lines = "\n".join(
        f'\t\t\t\t\t\t{{\n\t\t\t\t\t\t\tfrom = "{a}"\n\t\t\t\t\t\t\tto = "{b}"\n\t\t\t\t\t\t}},'
        for a, b in remaps)
    vmdl = f"""{VMDL_HEADER}
{{
\trootNode =
\t{{
\t\t_class = "RootNode"
\t\tchildren =
\t\t[
\t\t\t{{
\t\t\t\t_class = "MaterialGroupList"
\t\t\t\tchildren =
\t\t\t\t[
\t\t\t\t\t{{
\t\t\t\t\t\t_class = "DefaultMaterialGroup"
\t\t\t\t\t\tname = "default"
\t\t\t\t\t\tremaps =
\t\t\t\t\t\t[
{remap_lines}
\t\t\t\t\t\t]
\t\t\t\t\t\tuse_global_default = false
\t\t\t\t\t\tglobal_default_material = ""
\t\t\t\t\t}},
\t\t\t\t]
\t\t\t}},
\t\t\t{{
\t\t\t\t_class = "RenderMeshList"
\t\t\t\tchildren =
\t\t\t\t[
\t\t\t\t\t{{
\t\t\t\t\t\t_class = "RenderMeshFile"
\t\t\t\t\t\tname = "mesh"
\t\t\t\t\t\tfilename = "{obj_rel}"
\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]
\t\t\t\t\t\timport_rotation = [ 0.0, 0.0, 0.0 ]
\t\t\t\t\t\timport_scale = {SCALE}
\t\t\t\t\t\talign_origin_x_type = "None"
\t\t\t\t\t\talign_origin_y_type = "None"
\t\t\t\t\t\talign_origin_z_type = "None"
\t\t\t\t\t\tparent_bone = ""
\t\t\t\t\t}},
\t\t\t\t]
\t\t\t}},
\t\t]
\t}}
}}
"""
    with open(path, "w", newline="\n") as f:
        f.write(vmdl)


# ============================================================ driver
def generate_kit(kit):
    spec = SPECS[kit]
    plan = KITS[kit]
    kit_dir = os.path.join(ROOT, "Assets", "models", "vehicles", kit)
    os.makedirs(kit_dir, exist_ok=True)
    root_rel = f"models/vehicles/{kit}"          # project-root-relative
    mat_dir_rel = f"{root_rel}/materials"
    white_rel = f"{root_rel}/white.png"

    # ---- 1. author + export OBJs (Blender) -----------------------------
    print(f"[gen_vehicle] authoring {kit} parts ...")
    plan["build"](spec, kit_dir)

    # ---- 2. shared materials ------------------------------------------
    write_white_png(os.path.join(kit_dir, "white.png"))
    used_colors = set(PALETTE)   # author all palette colours as vmats (shared)
    for c in sorted(used_colors):
        write_flat_vmat(os.path.join(kit_dir, "materials", f"{c}.vmat"),
                        PALETTE[c], white_rel, ROUGH.get(c, 0.9))
    print(f"[gen_vehicle] wrote {len(used_colors)} flat vmats + white.png")

    # ---- 3. per-part vmdl + gather bounds ------------------------------
    obj_info = {}   # obj_name -> parsed
    for obj_name in plan["objs"]:
        obj_path = os.path.join(kit_dir, f"{obj_name}.obj")
        mn, mx, has_usemtl, has_vt, mtllib, mats, tris, submeshes = parse_obj(obj_path)
        obj_info[obj_name] = dict(mn=mn, mx=mx, usemtl=has_usemtl, vt=has_vt,
                                  mats=mats, tris=tris, submeshes=submeshes)
        write_vmdl(os.path.join(kit_dir, f"{obj_name}.vmdl"),
                   f"{root_rel}/{obj_name}.obj", mats, mat_dir_rel)

    # ---- 4. manifest (schema v2 = PROVEN frame mapping) -----------------
    attach = plan["attach"](spec)
    parts_out = []
    total_tris = 0
    for part, role in plan["roles"].items():
        oi = obj_info[role["obj"]]
        size, ctr, lmn, lmx = local_bounds(oi["mn"], oi["mx"])
        aloc = author_to_local(attach[part])
        total_tris += oi["tris"]
        entry = {
            "part": part,
            "obj": f"{role['obj']}.obj",
            "vmdl": f"{root_rel}/{role['obj']}.vmdl",
            "kind": role.get("kind", "chassis"),
            "pivot_semantics": role["pivot"],
            "rotation_axis_local": role["axis"],   # part-local engine axis (None=rigid)
            "dims_m": [round(v, 4) for v in size],
            "local_bounds_min_m": [round(v, 4) for v in lmn],
            "local_bounds_max_m": [round(v, 4) for v in lmx],
            "attach_local_m": [round(v, 4) for v in aloc],
            "attach_author_m": list(attach[part]),
            "mass_fraction": role["mass"],
            "tris": oi["tris"],
            # Per-submesh index counts in usemtl/Materials order (A1 2026-07-13). Lets
            # PartMeshRebuilder split a dent rebuild per material so a multi-material part
            # (cab/bed/chassis) keeps every material instead of flattening to Materials[0].
            # Single-material parts get a one-element list; consumed only as a rebuild hint
            # (runtime-guarded on sum == index count), so it needs no schema bump.
            "submesh_index_counts": [c for _, c in oi["submeshes"]],
        }
        for k in ("steer", "mirror", "open_sign", "mount_normal", "required"):
            if k in role:
                entry[k] = role[k]
        # schema v3 damage band — dims are model-local size, frame-agnostic
        entry.update(damage_defaults(entry["kind"], part, entry["dims_m"]))
        # role-level overrides:
        #  - rigid_damage: the never-dent sentinel band (kart frame — brief says the
        #    frame/floor never dents; the same profile wheels get, zone kept)
        #  - zone: explicit zone tag where the kind default reads wrong (kart side
        #    pods would be tagged 'rear' by the bolton '_r' name rule)
        if role.get("rigid_damage"):
            entry.update(dent_impulse=NO_DETACH, loosen_impulse=NO_DETACH,
                         detach_impulse=NO_DETACH, stiffness=0.0, max_crush_m=0.0)
        if "zone" in role:
            entry["zone"] = role["zone"]
        parts_out.append(entry)

    manifest = {
        "schema": "vp.partkit/3",
        "generator": "tools/gen_vehicle.py",
        "kit": kit,
        "spec_m": spec,
        "units": "metres (multiply by 39.37 for s&box units at the engine boundary)",
        "frames": {
            "authoring": "+X forward, +Y left, +Z up (Blender)",
            "chassis_local": "+X right, +Y FRONT, +Z up (nose points local +Y; -90 deg yaw faces root +X) — EMPIRICALLY PROVEN 2026-07-11",
            "author_to_local": "(mx,my,mz) = (-bY, +bX, bZ)",
            "obj_import": "model = (objZ, objX, objY) — plain Y-up->Z-up cyclic permutation, no sign flips",
        },
        "material_route": "per-part usemtl -> shared flat vmats (white.png + g_vColorTint)",
        "collision": "render-only vmdls; the assembler attaches code-side BoxColliders per part",
        "mass_fraction_note": "suggestion only; the assembler assigns absolute masses from CarDefinition total",
        "total_tris": total_tris,
        "parts": parts_out,
    }
    # optional citizen sit point (kart): VehicleFactory.AddDriver consumes this
    # via PartKitManifest (authoring frame; factory converts z by -SeatHeightM)
    if "driver_seat" in plan:
        manifest["driver_seat_author_m"] = [round(v, 4) for v in plan["driver_seat"]]
    with open(os.path.join(kit_dir, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=2)
    print(f"[gen_vehicle] wrote manifest.json ({len(parts_out)} parts, {total_tris} tris, schema vp.partkit/3)")

    return spec, kit_dir, obj_info, manifest


# ============================================================ verification
def pivot_checks_hatch(parts, check):
    # Corrected frame: author +X (fwd) -> model-local +Y.
    # hood: pivot on REAR hinge line, panel extends forward => local Y in [0, +len]
    hood_lo = parts["hood"]["local_bounds_min_m"]
    hood_hi = parts["hood"]["local_bounds_max_m"]
    check(abs(hood_lo[1]) < 0.02 and hood_hi[1] > 0.5,
          f"hood pivot on rear hinge line (local Y min {hood_lo[1]:.3f}, max {hood_hi[1]:.3f})")
    # door: pivot on FRONT hinge line, panel hangs rearward => local Y in [-len, 0]
    door_lo = parts["door_l"]["local_bounds_min_m"]
    door_hi = parts["door_l"]["local_bounds_max_m"]
    check(abs(door_hi[1]) < 0.02 and door_lo[1] < -0.5,
          f"door_l pivot on front hinge line (local Y max {door_hi[1]:.3f}, min {door_lo[1]:.3f})")
    # v2 brief items: mirrors outboard, spoiler over the tailgate under the roofline
    ml_lo = parts["mirror_l"]["local_bounds_min_m"]
    mr_hi = parts["mirror_r"]["local_bounds_max_m"]
    check(ml_lo[0] < -0.12 and mr_hi[0] > 0.12,
          f"mirrors extend outboard (mirror_l local X min {ml_lo[0]:.3f}, mirror_r max {mr_hi[0]:.3f})")
    sp_top = parts["spoiler"]["attach_author_m"][2] + parts["spoiler"]["local_bounds_max_m"][2]
    check(1.38 < sp_top <= 1.45 + 0.001,
          f"spoiler blade tops out under the roofline (author z {sp_top:.3f} <= 1.45)")
    check(parts["spoiler"]["local_bounds_min_m"][1] < -0.25,
          f"spoiler overhangs the tailgate (local Y min {parts['spoiler']['local_bounds_min_m'][1]:.3f})")


def pivot_checks_coupe(parts, check):
    # hood (front lid) extends forward of its rear hinge
    hood_lo = parts["hood"]["local_bounds_min_m"]
    hood_hi = parts["hood"]["local_bounds_max_m"]
    check(abs(hood_lo[1]) < 0.03 and hood_hi[1] > 0.6,
          f"hood pivot on rear hinge line (local Y min {hood_lo[1]:.3f}, max {hood_hi[1]:.3f})")
    # door hangs rearward of the front hinge
    door_lo = parts["door_l"]["local_bounds_min_m"]
    door_hi = parts["door_l"]["local_bounds_max_m"]
    check(abs(door_hi[1]) < 0.03 and door_lo[1] < -1.0,
          f"door_l pivot on front hinge line (local Y max {door_hi[1]:.3f}, min {door_lo[1]:.3f})")
    # engine lid: FORWARD hinge at roof rear, louvers+deck extend rearward
    el_lo = parts["engine_lid"]["local_bounds_min_m"]
    el_hi = parts["engine_lid"]["local_bounds_max_m"]
    check(el_hi[1] < 0.05 and el_lo[1] < -1.1,
          f"engine_lid pivot on forward hinge line (local Y max {el_hi[1]:.3f}, min {el_lo[1]:.3f})")
    # wing blade rides above its deck mount, below the roof (1.10)
    wing_top = parts["wing"]["attach_author_m"][2] + parts["wing"]["local_bounds_max_m"][2]
    check(0.95 < wing_top < 1.10,
          f"wing blade above deck, below roofline (author z {wing_top:.3f})")
    # brief: rears visibly wider than fronts (dims X = width in model frame)
    wf = parts["wheel_fl"]["dims_m"][0]
    wr = parts["wheel_rl"]["dims_m"][0]
    check(wr > wf + 0.03, f"rear tires wider than front ({wr:.3f} vs {wf:.3f})")


def pivot_checks_kart(parts, check):
    # nose cone extends forward of its frame mount
    nose_hi = parts["nose"]["local_bounds_max_m"]
    check(nose_hi[1] > 0.25, f"nose extends forward of mount (local Y max {nose_hi[1]:.3f})")
    # tall seat back tops out at the spec height
    seat_top = parts["seat"]["attach_author_m"][2] + parts["seat"]["local_bounds_max_m"][2]
    check(0.50 < seat_top < 0.58, f"seat back top ~ spec height (author z {seat_top:.3f})")
    # brief: rear slicks visibly wider than fronts
    wf = parts["wheel_fl"]["dims_m"][0]
    wr = parts["wheel_rl"]["dims_m"][0]
    check(wr > wf + 0.04, f"rear slicks wider than front ({wr:.3f} vs {wf:.3f})")
    # brief: the frame/floor NEVER dents (rigid sentinel band)
    check(parts["frame"]["dent_impulse"] >= 1e8,
          f"frame is never-dent (dent_impulse {parts['frame']['dent_impulse']:.0f})")
    # side pods zone-tagged as lateral, not rear (role override applied)
    check(parts["pod_r"]["zone"] == "door" and parts["pod_l"]["zone"] == "door",
          f"side pods zone=door (got {parts['pod_l']['zone']}/{parts['pod_r']['zone']})")


def pivot_checks_pickup(parts, check):
    # hood forward of rear hinge (author +X -> local +Y)
    hood_lo = parts["hood"]["local_bounds_min_m"]
    hood_hi = parts["hood"]["local_bounds_max_m"]
    check(abs(hood_lo[1]) < 0.02 and hood_hi[1] > 0.8,
          f"hood pivot on rear hinge line (local Y min {hood_lo[1]:.3f}, max {hood_hi[1]:.3f})")
    # door hangs rearward of front hinge
    door_lo = parts["door_l"]["local_bounds_min_m"]
    door_hi = parts["door_l"]["local_bounds_max_m"]
    check(abs(door_hi[1]) < 0.03 and door_lo[1] < -0.9,
          f"door_l pivot on front hinge line (local Y max {door_hi[1]:.3f}, min {door_lo[1]:.3f})")
    # tailgate: bottom hinge, panel extends UP from pivot
    tg_lo = parts["tailgate"]["local_bounds_min_m"]
    tg_hi = parts["tailgate"]["local_bounds_max_m"]
    check(tg_lo[2] > -0.03 and tg_hi[2] > 0.4,
          f"tailgate pivot on bottom hinge line (local Z min {tg_lo[2]:.3f}, max {tg_hi[2]:.3f})")
    # bed: pivot at FRONT mount plane, box extends rearward (author -X -> local -Y)
    bed_lo = parts["bed"]["local_bounds_min_m"]
    bed_hi = parts["bed"]["local_bounds_max_m"]
    check(bed_hi[1] < 0.05 and bed_lo[1] < -1.7,
          f"bed pivot at front mount plane (local Y max {bed_hi[1]:.3f}, min {bed_lo[1]:.3f})")
    # cab-bed gap: bed front wall must start behind the cab back wall (which ends
    # at author X -0.60; the cab OBJ's own min-X is the frame rails, so compare stations)
    bed_front_author = parts["bed"]["attach_author_m"][0] + bed_hi[1]  # author X of bed's front face
    check(bed_front_author < -0.62,
          f"cab-bed gap present (bed front face at author X {bed_front_author:.3f}, cab wall ends -0.60)")
    # mirrors extend outboard: mirror_l outboard = author +Y = local -X
    ml_lo = parts["mirror_l"]["local_bounds_min_m"]
    mr_hi = parts["mirror_r"]["local_bounds_max_m"]
    check(ml_lo[0] < -0.15 and mr_hi[0] > 0.15,
          f"mirrors extend outboard (mirror_l local X min {ml_lo[0]:.3f}, mirror_r max {mr_hi[0]:.3f})")
    # grille extends forward of its mount plane
    gr_lo = parts["grille"]["local_bounds_min_m"]
    gr_hi = parts["grille"]["local_bounds_max_m"]
    check(gr_lo[1] > -0.02 and gr_hi[1] < 0.15,
          f"grille is a thin forward-facing fascia (local Y {gr_lo[1]:.3f}..{gr_hi[1]:.3f})")


PIVOT_CHECKS = {"hatch_kit": pivot_checks_hatch, "pickup_kit": pivot_checks_pickup,
                "coupe_kit": pivot_checks_coupe, "kart_kit": pivot_checks_kart}


def verify(kit, spec, kit_dir, obj_info, manifest):
    print(f"\n[gen_vehicle] VERIFICATION {kit} -----------------------------------")
    plan = KITS[kit]
    ok = True
    R = spec["wheel_radius"]

    def check(cond, msg):
        nonlocal ok
        print(f"  [{'PASS' if cond else 'FAIL'}] {msg}")
        if not cond:
            ok = False

    # per-OBJ usemtl + vt present
    for name, oi in obj_info.items():
        check(oi["usemtl"], f"{name}.obj has usemtl ({','.join(oi['mats'])})")
        check(oi["vt"], f"{name}.obj has vt (UV) lines")

    parts = {p["part"]: p for p in manifest["parts"]}

    # budgets: part count + tri census (guardrails)
    check(len(parts) <= plan["max_parts"],
          f"part count {len(parts)} <= {plan['max_parts']}")
    total = manifest["total_tris"]
    check(total <= plan["max_tris"], f"total tris {total} <= {plan['max_tris']}")
    print("  tri census: " + ", ".join(
        f"{n}={oi['tris']}" for n, oi in sorted(obj_info.items())))

    # wheels: diameter ~= 2R on the two radial axes (model-local Y,Z) for BOTH
    # meshes (front/rear may differ — coupe/kart); width per axle vs spec
    diam = 2 * R
    ww_f = spec["wheel_width"]
    ww_r = spec.get("wheel_width_r", ww_f)
    for pname, ww in (("wheel_fl", ww_f), ("wheel_rl", ww_r)):
        wd = parts[pname]["dims_m"]
        check(abs(wd[1] - diam) < 0.02 and abs(wd[2] - diam) < 0.02,
              f"{pname} diameter ~= {diam:.2f} m (got Y={wd[1]:.3f} Z={wd[2]:.3f})")
        check(abs(wd[0] - ww) < 0.06,
              f"{pname} width ~= {ww:.2f} m (got X={wd[0]:.3f})")

    # assembled envelope from parts (attach_local + local bounds, TRUE frame)
    amn = [9e9] * 3
    amx = [-9e9] * 3
    for p in manifest["parts"]:
        if p["part"] in plan["envelope_exclude"]:
            continue
        a = p["attach_local_m"]
        lo = p["local_bounds_min_m"]
        hi = p["local_bounds_max_m"]
        for i in range(3):
            amn[i] = min(amn[i], a[i] + lo[i])
            amx[i] = max(amx[i], a[i] + hi[i])
    env = [amx[i] - amn[i] for i in range(3)]
    # model-local: X=right(width), Y=fwd/back(length), Z=up(height)
    check(abs(env[0] - spec["width"]) < 0.03,
          f"assembled width ~= {spec['width']} m (got {env[0]:.3f})")
    check(abs(env[1] - spec["length"]) < 0.03,
          f"assembled length ~= {spec['length']} m (got {env[1]:.3f})")
    check(abs(env[2] - spec["height"]) < 0.03,
          f"assembled height ~= {spec['height']} m (got {env[2]:.3f})")

    # wheelbase / track from wheel attach points (model-local: X=right, Y=fwd)
    fl = parts["wheel_fl"]["attach_local_m"]
    rl = parts["wheel_rl"]["attach_local_m"]
    fr = parts["wheel_fr"]["attach_local_m"]
    check(abs(abs(fl[1] - rl[1]) - spec["wheelbase"]) < 0.001,
          f"wheelbase ~= {spec['wheelbase']} m (got {abs(fl[1]-rl[1]):.3f})")
    check(abs(abs(fl[0] - fr[0]) - spec["track"]) < 0.001,
          f"track ~= {spec['track']} m (got {abs(fl[0]-fr[0]):.3f})")

    # attach_local consistency vs the proven mapping applied to attach_author
    for p in manifest["parts"]:
        b = p["attach_author_m"]
        expect = author_to_local(b)
        got = p["attach_local_m"]
        if any(abs(e - g) > 0.001 for e, g in zip(expect, got)):
            check(False, f"{p['part']} attach_local {got} != author_to_local {expect}")
            break
    else:
        check(True, "attach_local_m == author_to_local(attach_author_m) for all parts")

    # wheel pivot at hub centre (bounds centred) — every distinct wheel mesh
    wheel_objs = sorted({r["obj"] for r in plan["roles"].values()
                         if r.get("kind") == "wheel"})
    for wo in wheel_objs:
        _, ctr, _, _ = local_bounds(obj_info[wo]["mn"], obj_info[wo]["mx"])
        check(all(abs(c) < 0.01 for c in ctr),
              f"{wo} pivot at hub centre (bounds centre {tuple(round(c, 3) for c in ctr)})")

    # ground-clearance law: no body part's lowest point under min_part_ground
    # (author z; tires excluded). Guards the bottom-out margin: part colliders
    # must never become an early bump-stop vs the fused twin (equivalence gate).
    floor_min = plan.get("min_part_ground")
    if floor_min is not None:
        worst = None
        for p in manifest["parts"]:
            if p["kind"] == "wheel":
                continue
            bot = p["attach_author_m"][2] + p["local_bounds_min_m"][2]
            if worst is None or bot < worst[1]:
                worst = (p["part"], bot)
        check(worst[1] >= floor_min,
              f"lowest body-part point {worst[1]:.3f} m ({worst[0]}) >= {floor_min} m")

    # per-kit pivot semantics
    PIVOT_CHECKS[kit](parts, check)

    print(f"[gen_vehicle] VERIFICATION {kit} {'ALL PASS' if ok else 'HAD FAILURES'}")
    return ok


# ============================================================ contact sheet
def render_contact_sheet(kit, spec):
    """Lay every part out flat and render one ortho-ish camera to
    docs/images/<kit>_contact.png. Best-effort; guarded."""
    try:
        reset()
        plan = KITS[kit]
        kit_dir = os.path.join(ROOT, "Assets", "models", "vehicles", kit)
        contact = plan["contact"]
        for name in plan["objs"]:
            p = os.path.join(kit_dir, f"{name}.obj")
            bpy.ops.wm.obj_import(filepath=p, forward_axis='NEGATIVE_Z', up_axis='Y')
            o = bpy.context.selected_objects[0]
            ox, oy = contact["offs"][name]
            o.location.x += ox
            o.location.y += oy
        # camera (top-down-ish 3/4)
        cam_data = bpy.data.cameras.new("cam")
        cam = bpy.data.objects.new("cam", cam_data)
        bpy.context.scene.collection.objects.link(cam)
        cam.location = contact["cam_loc"]
        cam.rotation_euler = tuple(math.radians(a) for a in contact["cam_rot"])
        cam_data.lens = contact["lens"]
        bpy.context.scene.camera = cam
        # a sun
        sd = bpy.data.lights.new("sun", 'SUN')
        sd.energy = 3.0
        su = bpy.data.objects.new("sun", sd)
        bpy.context.scene.collection.objects.link(su)
        su.rotation_euler = (math.radians(50), math.radians(20), 0)
        scn = bpy.context.scene
        scn.render.engine = 'BLENDER_WORKBENCH'
        scn.render.resolution_x = 1280
        scn.render.resolution_y = 800
        scn.render.film_transparent = False
        try:
            scn.display.shading.color_type = 'MATERIAL'
        except Exception:
            pass
        out = os.path.join(ROOT, "docs", "images", f"{kit}_contact.png")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        scn.render.filepath = out
        bpy.ops.render.render(write_still=True)
        print(f"[gen_vehicle] contact sheet -> {out}")
        return True
    except Exception as e:
        print(f"[gen_vehicle] contact sheet SKIPPED: {e}")
        return False


# ============================================================ assembled views sheet
def _build_driver_dummy(sit):
    """Citizen-PROPORTIONED seated mannequin (grey) at the manifest sit point —
    a Blender-side FIT CHECK only (the real driver is the engine citizen via
    VehicleFactory.AddDriver; directive: never model a custom driver).
    Returns the created objects so the caller can parent them."""
    objs = []
    sx, sy, sz = sit

    def d(loc, size, rot=(0, 0, 0)):
        o = cube((sx + loc[0], sy + loc[1], sz + loc[2]), size, "rim", rot=rot)
        objs.append(o)
        return o

    d((-0.04, 0, 0.10), (0.26, 0.30, 0.20))                       # pelvis
    d((-0.08, 0, 0.36), (0.22, 0.34, 0.40), rot=(0, math.radians(-10), 0))  # torso
    d((-0.06, 0, 0.66), (0.16, 0.16, 0.20))                       # head
    for s in (1.0, -1.0):
        d((0.18, s * 0.09, 0.09), (0.44, 0.12, 0.12))             # thigh
        d((0.53, s * 0.09, 0.01), (0.38, 0.10, 0.10), rot=(0, math.radians(25), 0))  # shin
        d((0.71, s * 0.09, -0.07), (0.16, 0.09, 0.06))            # foot
        d((0.10, s * 0.15, 0.36), (0.42, 0.09, 0.09),
          rot=(0, math.radians(35), -s * math.radians(8)))        # arm to the wheel
    return objs


def render_assembled_views(kit, spec):
    """Render the ASSEMBLED vehicle in four poses (front / 3-quarter / side /
    rear) in one image -> docs/images/<kit>_views.png — the first-look
    contact sheet (visual QA deliverable). Best-effort; guarded."""
    try:
        reset()
        plan = KITS[kit]
        vw = plan["views"]
        kit_dir = os.path.join(ROOT, "Assets", "models", "vehicles", kit)
        attach = plan["attach"](spec)
        obj_of = {part: role["obj"] for part, role in plan["roles"].items()}
        sp = vw["spacing"]
        poses = [("front", -90.0), ("threequarter", -55.0),
                 ("side", 0.0), ("rear", 90.0)]
        for vi, (label, yaw) in enumerate(poses):
            pivot = bpy.data.objects.new(f"pose_{label}", None)
            bpy.context.scene.collection.objects.link(pivot)
            children = []
            for part, obj_name in obj_of.items():
                p = os.path.join(kit_dir, f"{obj_name}.obj")
                bpy.ops.wm.obj_import(filepath=p, forward_axis='NEGATIVE_Z', up_axis='Y')
                o = bpy.context.selected_objects[0]
                o.location = attach[part]
                children.append(o)
            if vw.get("driver_dummy") and "driver_seat" in plan:
                children += _build_driver_dummy(plan["driver_seat"])
            for o in children:
                o.parent = pivot
            pivot.rotation_euler = (0, 0, math.radians(yaw))
            pivot.location = ((vi - 1.5) * sp, 0, 0)
        # camera + sun (low elevation so front/side/rear read as elevations)
        cam_data = bpy.data.cameras.new("cam")
        cam = bpy.data.objects.new("cam", cam_data)
        bpy.context.scene.collection.objects.link(cam)
        cam.location = vw["cam_loc"]
        cam.rotation_euler = tuple(math.radians(a) for a in vw["cam_rot"])
        cam_data.lens = vw["lens"]
        bpy.context.scene.camera = cam
        sd = bpy.data.lights.new("sun", 'SUN')
        sd.energy = 3.0
        su = bpy.data.objects.new("sun", sd)
        bpy.context.scene.collection.objects.link(su)
        su.rotation_euler = (math.radians(55), math.radians(15), 0)
        scn = bpy.context.scene
        scn.render.engine = 'BLENDER_WORKBENCH'
        scn.render.resolution_x = 1600
        scn.render.resolution_y = 560
        scn.render.film_transparent = False
        try:
            scn.display.shading.color_type = 'MATERIAL'
        except Exception:
            pass
        out = os.path.join(ROOT, "docs", "images", f"{kit}_views.png")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        scn.render.filepath = out
        bpy.ops.render.render(write_still=True)
        print(f"[gen_vehicle] assembled views -> {out}")
        return True
    except Exception as e:
        print(f"[gen_vehicle] assembled views SKIPPED: {e}")
        return False


def main():
    if not HAVE_BPY:
        raise SystemExit("run under Blender: blender -b -P tools/gen_vehicle.py -- [kit ...]")
    # kits after the blender `--` separator; default = all
    argv = sys.argv
    kits = argv[argv.index("--") + 1:] if "--" in argv else []
    kits = [k for k in kits if k in KITS] or list(KITS)
    all_ok = True
    for kit in kits:
        spec, kit_dir, obj_info, manifest = generate_kit(kit)
        all_ok &= verify(kit, spec, kit_dir, obj_info, manifest)
        render_contact_sheet(kit, spec)
        render_assembled_views(kit, spec)
    print(f"[gen_vehicle] done ({'ALL PASS' if all_ok else 'FAILURES PRESENT'}).")


if __name__ == "__main__":
    main()
