# Headless Blender generator for the playground kickball prop.
# Run: blender --background --python tools/gen_kickball.py
#
# Produces (relative to Assets/models/props/):
#   kickball.obj        low-poly UV sphere, triangulated, meters-authored
#   kickball.mtl        basic material stub (s&box uses the vmdl remap, not this)
#   materials/kickball_color.png   512px red-rubber diffuse with molded seam lines
#
# Scale contract: the game's DynamicBall scales the model by (diamM * M / 100),
# assuming "100 u dia at scale 1". With vmdl import_scale = 39.37 (inches/metre),
# a 2.54 m (radius 1.27 m) authored sphere imports to exactly 100 units diameter.
# So the mesh is authored at radius 1.27 m.

import bpy
import os
import math
import numpy as np

# ----------------------------------------------------------------- paths
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(SCRIPT_DIR)
PROPS_DIR = os.path.join(REPO_ROOT, "Assets", "models", "props")
MAT_DIR = os.path.join(PROPS_DIR, "materials")
os.makedirs(MAT_DIR, exist_ok=True)

OBJ_PATH = os.path.join(PROPS_DIR, "kickball.obj")
PNG_PATH = os.path.join(MAT_DIR, "kickball_color.png")

# ----------------------------------------------------------------- params
RADIUS_M = 1.27          # 2.54 m dia -> 100 units at import_scale 39.37
SEGMENTS = 20            # azimuth divisions
RINGS = 14               # latitude divisions
TEX = 512                # texture size (px)

# classic red playground-ball palette (target sRGB, 0..1)
BASE_RED = (0.74, 0.11, 0.10)
SEAM_RED = (0.42, 0.06, 0.06)   # darker molded seam
POLE_RED = (0.66, 0.09, 0.09)   # very subtle pole tone
SEAM_W = 0.050           # half-width of a seam band in normalized object space
SEAM_FEATHER = 0.022     # soft edge over this extra width

# ----------------------------------------------------------------- clean scene
bpy.ops.wm.read_factory_settings(use_empty=True)

# ----------------------------------------------------------------- mesh
bpy.ops.mesh.primitive_uv_sphere_add(
    segments=SEGMENTS, ring_count=RINGS, radius=RADIUS_M, location=(0, 0, 0)
)
ball = bpy.context.active_object
ball.name = "Kickball"

# smooth shading for a round rubber look
for p in ball.data.polygons:
    p.use_smooth = True

# triangulate so the exported OBJ is pure tris and the count is exact
import bmesh
bm = bmesh.new()
bm.from_mesh(ball.data)
bmesh.ops.triangulate(bm, faces=bm.faces[:])
bm.to_mesh(ball.data)
bm.free()
tri_count = len(ball.data.polygons)
print("KICKBALL_TRIS=%d" % tri_count)

# ----------------------------------------------------------------- texture (procedural, seams aligned to the sphere's own UVs)
# UV sphere UV convention: u in [0,1] -> azimuth 0..2pi, v in [0,1] -> bottom..top pole
u = (np.arange(TEX) + 0.5) / TEX
v = (np.arange(TEX) + 0.5) / TEX
uu, vv = np.meshgrid(u, v)                      # uu: x/columns, vv: y/rows
theta = uu * 2.0 * math.pi
phi = (vv - 0.5) * math.pi                       # -pi/2 (bottom) .. +pi/2 (top)
cx = np.cos(phi) * np.cos(theta)
cy = np.cos(phi) * np.sin(theta)
cz = np.sin(phi)

def band(coord):
    # 1.0 inside |coord| < SEAM_W, ramps to 0 over SEAM_FEATHER
    a = (np.abs(coord) - SEAM_W) / SEAM_FEATHER
    return np.clip(1.0 - a, 0.0, 1.0)

# three great-circle molded seams: equator (z=0) + two meridians (x=0, y=0)
seam = np.maximum(np.maximum(band(cz), band(cx)), band(cy))

# subtle pole darkening (optional star/pentagon stand-in) near |z| ~ 1
pole = np.clip((np.abs(cz) - 0.92) / 0.08, 0.0, 1.0) * 0.6

base = np.array(BASE_RED)
seamc = np.array(SEAM_RED)
polec = np.array(POLE_RED)

col = base[None, None, :] * (1 - pole[..., None]) + polec[None, None, :] * pole[..., None]
col = col * (1 - seam[..., None]) + seamc[None, None, :] * seam[..., None]
col = np.clip(col, 0.0, 1.0)

# Blender's image.save() on this build writes the float buffer straight to 8-bit
# with no sRGB transfer, so feed sRGB-target values directly (byte = round(v*255)).
rgba = np.ones((TEX, TEX, 4), dtype=np.float64)
rgba[..., 0:3] = col
img = bpy.data.images.new("kickball_color", width=TEX, height=TEX, alpha=False)
img.colorspace_settings.name = "sRGB"
img.pixels = rgba.reshape(-1).tolist()
img.file_format = "PNG"
img.filepath_raw = PNG_PATH
img.save()
print("KICKBALL_PNG_SAVED=%s" % PNG_PATH)

# ----------------------------------------------------------------- material stub (named so the OBJ carries usemtl "kickball")
mat = bpy.data.materials.new(name="kickball")
mat.use_nodes = True
bsdf = mat.node_tree.nodes.get("Principled BSDF")
if bsdf:
    bsdf.inputs["Base Color"].default_value = (BASE_RED[0], BASE_RED[1], BASE_RED[2], 1.0)
    if "Roughness" in bsdf.inputs:
        bsdf.inputs["Roughness"].default_value = 0.9
ball.data.materials.clear()
ball.data.materials.append(mat)

# ----------------------------------------------------------------- export OBJ
bpy.ops.object.select_all(action="DESELECT")
ball.select_set(True)
bpy.context.view_layer.objects.active = ball
bpy.ops.wm.obj_export(
    filepath=OBJ_PATH,
    export_selected_objects=True,
    export_materials=True,
    export_uv=True,
    export_normals=True,
    export_triangulated_mesh=True,
    apply_modifiers=True,
    forward_axis="NEGATIVE_Z",
    up_axis="Y",
    path_mode="RELATIVE",
)
print("KICKBALL_OBJ_SAVED=%s" % OBJ_PATH)
print("KICKBALL_DONE")
