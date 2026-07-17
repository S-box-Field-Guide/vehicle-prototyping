"""Deterministic Blender generator for the city's COMMERCIAL fill assets.

Produces a small set of flat-shaded "mini high-rise" buildings + a traffic cone
prop, all in the same house style as the rest of this kit's custom art: shared
white.png + g_vColorTint flat vmats, low-poly boxy geometry, one render-only
vmdl per model (import_scale 39.37 metres->units). The C# world builder places
each with a single bounds-fit static BoxCollider (footprint x height), so these
are background city fill the car bumps but never climbs.

Pipeline mirrors tools/gen_vehicle.py exactly (cube/mat/export_part/write_*),
kept self-contained so it needs nothing from that module. All geometry is
authored in the AUTHORING frame: +X forward, +Y left, +Z up, metres, with each
model's pivot at footprint centre on the ground (min z = 0), so the C# side rests
it on the ground by lifting -Bounds.Mins.z.

DETERMINISM: pure geometry from the fixed SPECS below -- no RNG anywhere, so a
regen is byte-identical. Z-FIGHT DISCIPLINE: window bands sit PROUD of the wall
face (offset 0.05 m >> the 0.5 mm coplanar tolerance), and same-face stacked
bands are separated by spandrel gaps, so no two coplanar faces ever overlap.
Verify with:  python tools/check_coplanar.py --kit Assets/models/city

Run (authoring + contact sheet):
  "C:\\Program Files\\Blender Foundation\\Blender 5.1\\blender.exe" -b -P tools/gen_buildings.py
Regen a single model:  ... -P tools/gen_buildings.py -- highrise_c
"""
import math
import os
import sys
import struct
import zlib

try:
    import bpy
    HAVE_BPY = True
except ImportError:  # allows the writer helpers to import without Blender
    HAVE_BPY = False

# ---------------------------------------------------------------- paths
TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(TOOLS_DIR)
SCALE = 39.37  # metres -> s&box units (inches)
ASSET_REL = "models/city"                 # project-root-relative asset dir
ASSET_DIR = os.path.join(ROOT, "Assets", "models", "city")
MAT_DIR_REL = f"{ASSET_REL}/materials"
WHITE_REL = f"{ASSET_REL}/materials/white.png"

VMDL_HEADER = ('<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} '
               'format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->')

# ---------------------------------------------------------------- palette
# Flat commercial-district colours, tuned to sit beside the vendored village
# set (cream / ochre / red / slate / blue glass). RGB 0..1.
PALETTE = {
    "concrete_grey": (0.62, 0.62, 0.64),
    "stone_light":   (0.78, 0.76, 0.70),
    "stone_dark":    (0.44, 0.46, 0.50),
    "wall_ochre":    (0.80, 0.62, 0.34),
    "wall_red":      (0.55, 0.24, 0.19),
    "glass_blue":    (0.36, 0.55, 0.66),
    "glass_teal":    (0.32, 0.52, 0.50),
    "glass_amber":   (0.72, 0.58, 0.30),
    "accent_red":    (0.72, 0.16, 0.14),
    "roof_dark":     (0.28, 0.29, 0.33),
    "steel":         (0.55, 0.57, 0.60),
    # cone prop
    "cone_orange":   (0.93, 0.34, 0.06),
    "cone_white":    (0.90, 0.90, 0.88),
}
ROUGH = {"glass_blue": 0.18, "glass_teal": 0.18, "glass_amber": 0.20,
         "steel": 0.30}

# ---------------------------------------------------------------- building specs
# Every dimension in METRES. A "storey" is ~3.3 m at vehicle scale. Each spec:
#   floors      : storey count (drives height + window-band count)
#   w, d        : ground footprint width (Y) x depth (X), metres
#   wall,glass  : palette keys
#   setback     : optional (from_floor, w, d) -> upper floors shrink to w x d
#   ground_accent: optional palette key -> a proud awning/storefront band at street level
#   roof        : list of rooftop box specs (dx, dy dimensions, h, palette key) + optional "antenna"
STOREY = 3.3
SPECS = {
    # 5-storey pale-stone office block, blue glass, single rooftop plant box
    "highrise_a": dict(floors=5, w=14.0, d=12.0, wall="stone_light", glass="glass_blue",
                       roof=[("box", 5.0, 4.0, 1.6, "roof_dark")]),
    # 6-storey ochre block with a red ground-floor awning + a rooftop water tank
    "highrise_b": dict(floors=6, w=12.0, d=12.0, wall="wall_ochre", glass="glass_teal",
                       ground_accent="accent_red",
                       roof=[("box", 4.0, 4.0, 1.4, "roof_dark"), ("tank", 2.2, 2.2, 2.4, "steel")]),
    # 8-storey grey tower, stepped setback for the top 3 floors, mech box + antenna
    "highrise_c": dict(floors=8, w=13.0, d=13.0, wall="concrete_grey", glass="glass_blue",
                       setback=(5, 9.0, 9.0),
                       roof=[("box", 4.5, 4.5, 1.8, "roof_dark"), ("antenna", 0.0, 0.0, 5.0, "steel")]),
    # 10-storey slim tower (the tallest), dark stone, corner piers + rooftop cluster
    "highrise_e": dict(floors=10, w=11.0, d=11.0, wall="stone_dark", glass="glass_blue",
                       piers=True,
                       roof=[("box", 4.0, 4.0, 2.0, "roof_dark"), ("antenna", 0.0, 0.0, 6.0, "steel")]),

    # -- SMALL COMMERCIAL FILL (1-3 storeys) -----------------------------------
    # These pack the downtown blocks AROUND the tall high-rises so a block reads
    # as a cluster of structures rather than one lonely tower on a bare pad. Same
    # flat-shaded palette + band/parapet discipline; footprints <=12 m so two sit
    # in adjacent block quadrants with a comfortable gap. build_highrise() handles
    # them unchanged (low floor count -> short building, parapet = flat retail roof).
    # 1-storey glass storefront, pale stone, amber glazing, parapet + AC box
    "shop_a": dict(floors=1, w=12.0, d=9.0, wall="stone_light", glass="glass_amber",
                   parapet=True, roof=[("box", 3.0, 2.4, 0.9, "roof_dark")]),
    # 2-storey grey office, blue glass, parapet + roof box
    "shop_b": dict(floors=2, w=10.0, d=10.0, wall="concrete_grey", glass="glass_blue",
                   parapet=True, roof=[("box", 3.2, 3.2, 1.0, "roof_dark")]),
    # 3-storey ochre mixed-use, teal glass, red storefront awning + roof box
    "shop_c": dict(floors=3, w=11.0, d=9.0, wall="wall_ochre", glass="glass_teal",
                   ground_accent="accent_red", roof=[("box", 3.0, 2.6, 1.0, "roof_dark")]),
    # 2-storey brick retail, amber glazing, red awning + parapet
    "shop_d": dict(floors=2, w=12.0, d=8.0, wall="wall_red", glass="glass_amber",
                   ground_accent="accent_red", parapet=True,
                   roof=[("box", 3.0, 2.2, 0.9, "roof_dark")]),
}

# ---------------------------------------------------------------- Blender helpers
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
    """Axis-aligned box; loc = CENTRE, size = FULL extents (metres)."""
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    o = bpy.context.active_object
    o.scale = size
    o.rotation_euler = rot
    o.data.materials.append(mat(m))
    return o


def frustum(z0, z1, r0, r1, m, verts=16, cx=0.0, cy=0.0):
    """Vertical cone frustum from z0 (radius r0) to z1 (radius r1). Used by the cone
    prop and rooftop tanks (cx/cy offset places it in a non-overlapping roof slot)."""
    depth = z1 - z0
    bpy.ops.mesh.primitive_cone_add(vertices=verts, radius1=r0, radius2=r1,
                                    depth=depth, location=(cx, cy, (z0 + z1) * 0.5))
    o = bpy.context.active_object
    o.data.materials.append(mat(m))
    return o


def export_obj(out_dir, name):
    """Join everything + export a Y-up OBJ (+MTL) with the pivot left where the
    geometry sits relative to the Blender origin (footprint centre at z=0)."""
    bpy.ops.object.select_all(action='SELECT')
    bpy.context.view_layer.objects.active = bpy.data.objects[0]
    if len(bpy.data.objects) > 1:
        bpy.ops.object.join()
    obj = bpy.context.active_object
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, f"{name}.obj")
    bpy.ops.wm.obj_export(filepath=path, export_materials=True,
                          export_uv=True, export_normals=True,
                          forward_axis='NEGATIVE_Z', up_axis='Y')
    print(f"  exported {name}: {len(obj.data.vertices)} verts")
    return path


# ---------------------------------------------------------------- geometry
# Window-band discipline: a glass band is a thin box PROUD of the wall face by
# BAND_PROUD (front face sits BAND_PROUD past the wall plane) and inset INSET
# from the wall edges. Same-face stacked bands are separated by the spandrel gap
# between floors, so no two coplanar faces overlap. See module docstring.
BAND_PROUD = 0.05
INSET = 0.6          # band inset from each wall edge (metres)
BAND_FRAC = 0.62     # fraction of a storey that is glazing (rest = spandrel)


def build_block(w, d, z_base, z_top, wall, glass, skip_ground=False):
    """A solid wall box z_base..z_top with proud glass window bands on all 4 faces.
    skip_ground drops the floor-0 band so a ground-floor accent/storefront band can
    take that slot without a coplanar overlap."""
    halfW, halfD = w * 0.5, d * 0.5
    h = z_top - z_base
    cube((0.0, 0.0, (z_base + z_top) * 0.5), (d, w, h), wall)  # solid core (X=d, Y=w, Z=h)

    floors = max(1, int(round(h / STOREY)))
    band_h = STOREY * BAND_FRAC
    for f in range(floors):
        if skip_ground and f == 0:
            continue
        cz = z_base + (f + 0.5) * STOREY
        if cz + band_h * 0.5 > z_top - 0.15:
            continue  # keep a solid parapet lip at the very top
        # bands on the two X-facing walls (proud in +/-X, span most of Y)
        for sx in (1.0, -1.0):
            cube((sx * (halfD + BAND_PROUD * 0.5), 0.0, cz),
                 (BAND_PROUD, w - 2 * INSET, band_h), glass)
        # bands on the two Y-facing walls (proud in +/-Y, span most of X)
        for sy in (1.0, -1.0):
            cube((0.0, sy * (halfW + BAND_PROUD * 0.5), cz),
                 (d - 2 * INSET, BAND_PROUD, band_h), glass)


def build_highrise(spec, out_dir, name):
    reset()
    w, d = spec["w"], spec["d"]
    floors = spec["floors"]
    top = floors * STOREY
    wall, glass = spec["wall"], spec["glass"]

    skip_ground = bool(spec.get("ground_accent"))
    setback = spec.get("setback")
    if setback:
        sb_floor, sw, sd = setback
        base_top = sb_floor * STOREY
        build_block(w, d, 0.0, base_top, wall, glass, skip_ground=skip_ground)   # wide base
        build_block(sw, sd, base_top, top, wall, glass)                          # narrower upper
        # the setback step reads on its own (base top cap vs upper base cap are
        # back-to-back/benign); a full-slab ledge here would coplanar-fight the upper base.
        roof_w, roof_d, roof_z = sw, sd, top
    else:
        build_block(w, d, 0.0, top, wall, glass, skip_ground=skip_ground)
        roof_w, roof_d, roof_z = w, d, top

    # optional full-height corner piers (proud MORE than the bands, so their
    # outer faces never share a plane with a window band -> no coplanar overlap)
    if spec.get("piers"):
        pier = 0.9
        pp = 0.12
        for sx in (1.0, -1.0):
            for sy in (1.0, -1.0):
                cube((sx * (d * 0.5 - pier * 0.5 + pp), sy * (w * 0.5 - pier * 0.5 + pp), top * 0.5),
                     (pier, pier, top), spec["wall"])

    # optional parapet cap (a proud rim box at the roofline)
    if spec.get("parapet"):
        cube((0.0, 0.0, top + 0.25), (d + 0.3, w + 0.3, 0.5), "roof_dark")

    # ground-floor accent band (awning / storefront), proud, wrapping the base
    if spec.get("ground_accent"):
        col = spec["ground_accent"]
        gz = STOREY * 0.5
        for sx in (1.0, -1.0):
            cube((sx * (d * 0.5 + 0.12 * 0.5), 0.0, gz), (0.12, w + 0.4, 0.5), col)
        for sy in (1.0, -1.0):
            cube((0.0, sy * (w * 0.5 + 0.12 * 0.5), gz), (d + 0.4, 0.12, 0.5), col)

    # rooftop clutter. Boxes/tanks are spread into non-overlapping slots along the
    # roof depth so no two share a footprint (their down-facing bases would fight);
    # each box base vs the building roof top is back-to-back/benign.
    roof_items = spec.get("roof", [])
    slots = [it for it in roof_items if it[0] in ("box", "tank")]
    nslot = max(1, len(slots))
    slot = 0
    for item in roof_items:
        kind, dx, dy, hh, col = item
        if kind == "antenna":
            cube((roof_d * 0.30, roof_w * 0.30, roof_z + hh * 0.5), (0.16, 0.16, hh), col)
            continue
        ox = (slot - (nslot - 1) / 2.0) * (roof_d * 0.34)
        slot += 1
        if kind == "box":
            cube((ox, 0.0, roof_z + hh * 0.5), (dx, dy, hh), col)
        elif kind == "tank":
            frustum(roof_z, roof_z + hh, dx * 0.5, dx * 0.5, col, verts=12, cx=ox)

    return export_obj(out_dir, name)


def build_cone(out_dir, name):
    """Custom flat-shaded traffic cone (replaces the retired Kenney cone). Stacked
    frustums: orange base skirt, orange body, white reflective band, orange tip.
    Authored ~0.75 m tall; the C# Cone() helper bounds-scales it to 0.7 m."""
    reset()
    # square base skirt
    cube((0.0, 0.0, 0.03), (0.34, 0.34, 0.06), "cone_orange")
    # body frustums (radius tapers with height); a white band in the middle
    frustum(0.06, 0.34, 0.155, 0.115, "cone_orange")   # lower body
    frustum(0.34, 0.46, 0.115, 0.098, "cone_white")    # white band
    frustum(0.46, 0.74, 0.098, 0.020, "cone_orange")   # upper body -> tip
    return export_obj(out_dir, name)


# ---------------------------------------------------------------- writers (pure, no bpy)
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


def write_flat_vmat(path, rgb, rough):
    r, g, b = rgb
    txt = f"""// generated by tools/gen_buildings.py
Layer0
{{
\tshader "shaders/complex.shader"

\tTextureColor "{WHITE_REL}"
\tg_vColorTint "[{r:.4f} {g:.4f} {b:.4f} 0.0000]"
\tg_flModelTintAmount "1.000"
\tTextureRoughness "[{rough:.4f} {rough:.4f} {rough:.4f} 1.0000]"
\tg_flMetalness "0.000"
}}
"""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", newline="\n") as f:
        f.write(txt)


def write_vmdl(path, obj_rel, materials):
    remaps = []
    for m in materials:
        vmat = f"{MAT_DIR_REL}/{m}.vmat"
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


def obj_materials(obj_path):
    """The usemtl names actually written into the OBJ, in first-seen order."""
    seen = []
    with open(obj_path) as f:
        for line in f:
            if line.startswith("usemtl"):
                nm = line.split(None, 1)[1].strip()
                if nm not in seen:
                    seen.append(nm)
    return seen


# ---------------------------------------------------------------- contact sheet
def render_contact_sheet(names):
    """Line the buildings up on the ground and render one 3/4 workbench view to
    docs/images/highrise_contact.png (visual QA deliverable). A Track-To constraint
    aims the camera at the row centre so framing is robust to the row length."""
    try:
        reset()
        step = 26.0
        x = 0.0
        for nm in names:
            obj_path = os.path.join(ASSET_DIR, f"{nm}.obj")
            if not os.path.exists(obj_path):
                continue
            bpy.ops.wm.obj_import(filepath=obj_path,
                                  forward_axis='NEGATIVE_Z', up_axis='Y')
            for o in bpy.context.selected_objects:
                o.location.x += x
            x += step
        row_w = max(x - step, step)
        cx = row_w * 0.5

        # aim target at the row centre, ~mid height
        tgt = bpy.data.objects.new("target", None)
        tgt.location = (cx, 0.0, 11.0)
        bpy.context.scene.collection.objects.link(tgt)

        # pull back + widen so the whole row (towers + short shops) stays in frame
        cam_data = bpy.data.cameras.new("cam")
        cam = bpy.data.objects.new("cam", cam_data)
        cam.location = (cx - row_w * 0.08, -row_w * 1.55, row_w * 0.42)
        cam_data.lens = 30
        bpy.context.scene.collection.objects.link(cam)
        con = cam.constraints.new('TRACK_TO')
        con.target = tgt
        con.track_axis = 'TRACK_NEGATIVE_Z'
        con.up_axis = 'UP_Y'
        bpy.context.scene.camera = cam

        sun_d = bpy.data.lights.new("sun", 'SUN')
        sun_d.energy = 3.2
        sun = bpy.data.objects.new("sun", sun_d)
        sun.rotation_euler = (math.radians(52), math.radians(16), math.radians(-40))
        bpy.context.scene.collection.objects.link(sun)

        scn = bpy.context.scene
        scn.render.engine = 'BLENDER_WORKBENCH'
        scn.render.resolution_x = 1800
        scn.render.resolution_y = 720
        scn.render.film_transparent = False
        try:
            scn.display.shading.light = 'STUDIO'
            scn.display.shading.color_type = 'MATERIAL'
            scn.display.shading.show_shadows = True
        except Exception:
            pass
        out = os.path.join(ROOT, "docs", "images", "highrise_contact.png")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        scn.render.filepath = out
        bpy.ops.render.render(write_still=True)
        print(f"[gen_buildings] contact sheet -> {out}")
    except Exception as e:
        print(f"[gen_buildings] contact sheet SKIPPED: {e}")


# ---------------------------------------------------------------- driver
def generate(names):
    os.makedirs(ASSET_DIR, exist_ok=True)
    write_white_png(os.path.join(ASSET_DIR, "materials", "white.png"))

    built = []
    for nm in names:
        if nm == "cone":
            obj_path = build_cone(ASSET_DIR, nm)
        else:
            obj_path = build_highrise(SPECS[nm], ASSET_DIR, nm)
        mats = obj_materials(obj_path)
        write_vmdl(os.path.join(ASSET_DIR, f"{nm}.vmdl"), f"{ASSET_REL}/{nm}.obj", mats)
        built.append(nm)

    # write every palette colour as a shared flat vmat (union across models)
    for c in sorted(PALETTE):
        write_flat_vmat(os.path.join(ASSET_DIR, "materials", f"{c}.vmat"),
                        PALETTE[c], ROUGH.get(c, 0.9))
    print(f"[gen_buildings] wrote {len(PALETTE)} flat vmats + white.png; models: {built}")
    return built


def main():
    if not HAVE_BPY:
        print("gen_buildings.py must run inside Blender (-b -P). See module docstring.")
        sys.exit(2)
    argv = sys.argv
    req = argv[argv.index("--") + 1:] if "--" in argv else []
    all_names = list(SPECS.keys()) + ["cone"]
    names = req if req else all_names
    built = generate(names)
    # the cone prop is ~40x smaller than the towers; keep it out of the lineup sheet.
    render_contact_sheet([n for n in SPECS if n in built])
    print("[gen_buildings] done.")


if __name__ == "__main__":
    main()
