# Part-kit vehicle pipeline

Status: **Stage A groundwork** · 2026-07-11

The parameterized Blender generator that authors a vehicle as **separate physics-aware
parts** (chassis, wheels, doors, hood, trunk, bumpers), each exported with its pivot at
its joint so the `VehicleFactory` can mount, spin, steer, compress, and (later) detach
them. This doc is the contract for the part-assembly path.

> **Verification status: everything below is verified OUT-OF-ENGINE only** (geometry,
> bounds, pivots, usemtl/UV presence, manifest math — all asserted in-script). **Nothing
> here has been loaded in s&box yet.** Engine verification (vmdl compiles, materials
> resolve, parts assemble at the right offsets, wheels track suspension) happens at engine
> integration. See the status table at the bottom.

---

## How to run

```
"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" -b -P tools/gen_vehicle.py
```

One invocation does everything (headless): authors + exports the OBJs, writes the shared
materials, authors a `.vmdl` per part, emits `manifest.json`, runs the in-script
verification battery (prints `[PASS]/[FAIL]` lines + `VERIFICATION ALL PASS`), and renders
a contact-sheet preview to `docs/images/hatch_kit_contact.png`. It writes only into
`tools/`, `Assets/models/vehicles/**`, and `docs/images/`.

Outputs (per kit) under `Assets/models/vehicles/<kit>/`:

```
chassis_shell.obj/.mtl/.vmdl   wheel.obj/.mtl/.vmdl
door_l/.obj door_r  hood  trunk  bumper_f  bumper_r   (each .obj/.mtl/.vmdl)
white.png                       materials/<color>.vmat   (one flat vmat per palette colour)
manifest.json                   (the assembly contract)
```

---

## Spec-dict schema (add a kit here)

Kits are entries in `SPECS` in `tools/gen_vehicle.py`. All dimensions in **metres**:

| key | meaning |
|---|---|
| `length`, `width`, `height` | overall assembled envelope (m) |
| `wheelbase` | front-axle to rear-axle distance (m) |
| `track` | left-hub to right-hub distance (m) |
| `wheel_radius`, `wheel_width` | tyre radius / width (m) |
| `body_color` | palette key used for the body panels |

`PALETTE` (RGB 0..1) and `ROUGH` (per-colour roughness) are shared across kits. Adding a
new kit = add a `SPECS` entry (+ any new palette colours) and re-run. The geometry
builders currently hard-code the hatchback silhouette proportionally from the spec; a new
body *shape* needs new `build_*` geometry, but a re-proportioned hatch (different
size/wheelbase/track) works from spec alone.

### Current kit: `hatch_kit`
~4.0 m × 1.75 m × 1.45 m, wheelbase 2.55 m, track 1.5 m, wheel r=0.30 m. Stylized
low-poly (box-derived + rounded-edge pass, see below), Kenney-adjacent flat colours.

---

## Rounded panel vocabulary (`rcube` / `rcyl` / `arch_band`)

The visual pass (2026-07-16) replaced raw box geometry with a small rounded-edge
vocabulary, now used by **all four kits** (hatch, coupe, pickup, and kart — the kart
was brought in line on 2026-07-16):

- **`rcube(loc, size, mat, rot, r, seg)`** — beveled cube for body panels. Scale is
  applied *before* the bevel so non-uniform slabs get a uniform edge radius, and the
  bevel width auto-clamps to `min(size)/3` so thin slabs never degenerate. The bevel
  cuts only the corners — **every face-plane (and thus the bounding box) is preserved**,
  so panel-gap insets, pivot bounds, envelope/wheelbase checks, and coplanar
  relationships all survive the conversion unchanged. Typical radii: big masses
  `0.03–0.045`, doors/skins/seat `0.014–0.025`, thin trim/bars `0.012–0.02`.
- **`rcyl(loc, radius, depth, mat, axis, verts, shoulder)`** — cylinder with beveled
  cap edges (rounded tyre shoulders). `verts` stays a multiple of 4 so the radial
  bbox is exactly `2R`; the shoulder bevel cuts only the rim corner, so the radial
  (`2R`) and width (`Wd`) extents survive the assembler's exact-2R contract. Used for
  every tyre slick.
- **`arch_band(loc, r_in, r_out, width, mat, …)`** — semi-annular wheel-arch flare
  band (replaces the old box flares) on the closed-body kits. **Not used on the kart**,
  which is open-wheel: the wheels are the outermost surface and carry no bodywork over
  them, so the arch-clearance law below is trivially satisfied.

Because bevels only pull corners *inward*, the rounded pass can never newly bury a
wheel or introduce a coplanar overlap — both offline gates (`--assembled`, `--arches`)
stayed green through every kit's conversion.

---

## Coordinate frames (READ THIS before consuming attach points)

Authoring is in Blender, **+X forward, +Y left, +Z up, metres**. The house OBJ pipeline
(`forward_axis='NEGATIVE_Z', up_axis='Y'` + `import_scale 39.37`) maps authoring axes to
the compiled s&box model/world frame as:

```
author +X (forward) -> world/model-local -Y      (model faces world -Y; +90° yaw -> +X)
author +Y (left)    -> world/model-local -X
author +Z (up)      -> world/model-local +Z
=> (mx, my, mz) = (-bY, -bX, bZ)
```

So the **chassis model-local frame** the assembly factory works in is **+X = vehicle RIGHT,
+Y = vehicle REAR, +Z = UP**, with the nose pointing local **−Y**. Because every part is
parented to the chassis GameObject, part offsets are in this chassis-local frame and the
facing yaw is handled once on the chassis GO (house convention: facing yaw = +90° to face
world +X). `manifest.json` gives `attach_local_m` (this engine frame, what code consumes)
**and** `attach_author_m` (Blender frame, for eyeballing) for every part.

---

## Manifest schema (`vp.partkit/1` and `/2`)

**Schema v2 (2026-07-12):** same shape, but all frame-derived fields
(`attach_local_m`, `local_bounds_*`, `frames`) are computed with the EMPIRICALLY PROVEN
mapping `m = (-bY, +bX, bZ)` / nose at model-local +Y / facing yaw −90° (see
docs/part-kit-assembly.md §2 — this doc's original +90° frame prose above is the
pre-resolution prediction and is superseded). v1 = the landed hatch_kit manifest, whose
bounds are 180°-yawed; `PartKitManifest.TryLoad` normalizes v1 at load so consumers are
schema-agnostic. `gen_vehicle.py` emits only v2 onward; parts[] also gained an
informational `tris` field + top-level `total_tris` (budget census). pickup_kit adds
kinds `bed` / `tailgate` / `fascia` / `mirror` / `accessory` — the assembler treats every
non-`wheel` kind as a rigid body part, so kind stays a Stage-C semantic tag.

Top level:

| field | meaning |
|---|---|
| `schema` | `"vp.partkit/1"` |
| `kit`, `spec_m` | kit name + the spec dict it was built from |
| `units` | metres; multiply by 39.37 at the engine boundary |
| `frames` | the axis convention above (authoring, chassis_local, author_to_local) |
| `material_route` | how materials are wired (see below) |
| `collision` | render-only vmdls; the assembler adds code-side BoxColliders |
| `mass_fraction_note` | fractions are suggestions; the assembler owns absolute masses |
| `parts[]` | one entry per part (11 for hatch_kit) |

Each `parts[]` entry:

| field | meaning |
|---|---|
| `part` | logical name (`chassis_shell`, `wheel_fl`…`wheel_rr`, `door_l/r`, `hood`, `trunk`, `bumper_f/r`) |
| `obj`, `vmdl` | backing mesh file (wheels share one `wheel.*`) + root-relative vmdl path |
| `kind` | `chassis` / `wheel` / `door` / `hood` / `trunk` / `bolton` |
| `pivot_semantics` | where the origin sits: `hub-centre`, `front-hinge-line`, `rear-hinge-line`, `forward-hinge-line`, `mount-plane`, `footprint-centre@ground` |
| `rotation_axis_local` | part-local axis the part rotates about (`X`/`Y`/`Z`/`null` for rigid bolt-ons) |
| `dims_m` | part bounding-box size in model-local metres `[x,y,z]` |
| `local_bounds_min_m` / `_max_m` | part bounds in model-local metres (relative to its own pivot) |
| `attach_local_m` | where the part's pivot sits in **chassis model-local** metres — **the offset the assembler sets as the child's LocalPosition × 39.37** |
| `attach_author_m` | same point in Blender authoring metres (reference) |
| `mass_fraction` | suggested fraction of total vehicle mass |
| extras | `steer`/`mirror` (wheels), `open_sign` (doors/hood/trunk swing direction), `mount_normal` (bumpers) |

### Pivot & rotation conventions (the joint contract)

- **wheels** — pivot at **hub centre**; `rotation_axis_local = X` (the axle, left-right).
  Spin the wheel about local X from `VehicleWheel` angular state; **steer** front wheels
  (`steer:true`) about local **Z**; **compress** by translating along local **Z**. `mirror`
  marks the right-side instances (tyre is bilaterally symmetric, so no separate mesh — the
  flag is for spin-sign / any future asymmetric rim).
- **doors** — pivot on the **front (A-pillar) hinge line**; swing about local **Z**
  (vertical). `open_sign` = +1 left / −1 right.
- **hood** — pivot on the **rear hinge line** (cowl); opens up-forward about local **X**.
- **trunk/hatch** — pivot on the **forward hinge line** at the roof rear; opens up-back
  about local **X**.
- **bumpers** — pivot at the **mount-plane centre**; rigid bolt-ons (`rotation_axis_local
  = null`), `mount_normal` gives the outward face direction. On detach they become
  dynamic props hinged/broken at this plane.

---

## Materials — route decision

**Chosen: per-part `usemtl` groups → one shared flat `.vmat` per palette colour**
(shared `white.png` + `g_vColorTint` + `g_flModelTintAmount 1.0`).
Every part's `.vmdl` `DefaultMaterialGroup` remaps its material names — **both `"name"`
and `"name.vmat"`** (the remap must cover both spellings) — to `materials/<name>.vmat`.

Why not the single shared `colormap.png` route (like the Kenney imports)? Both are offered
by the task. The colormap route needs every face's UVs authored into the correct palette
cell; that is proven for *imported* Kenney meshes but **not** demonstrated for *generated*
geometry in this repo. The per-colour-vmat route is fully proven for generated OBJs,
needs no per-face UV-cell precision, and yields ~6 vmats shared across
the whole kit. Documented here so the assembler doesn't "fix" it. If a kit later needs the single-atlas
look, switch to a colormap by authoring UVs into cells and emitting one `colormap.vmat`.

Palette (hatch_kit): `body_blue`, `tire`, `rim`, `glass`, `trim`, `light`.

## Collision — render-only vmdls (deliberate)

Every part `.vmdl` has a `RenderMeshFile` **only** — no `PhysicsShapeList`/`PhysicsMeshFile`.
In s&box that means **zero engine collision**. This is intentional for the part
kit: **the assembler attaches code-side `BoxCollider`s** to each part GameObject (hand-sized boxes are
cheaper and cleaner than per-poly hulls for vehicle parts, and the box dims come straight
from `dims_m`). If a part ever needs real per-poly collision, add a `PhysicsMeshFile`
sibling with the same `filename` + `import_scale`.

---

## Wheel-arch clearance law (the wheel must be the outermost surface)

A flat-shaded box body has no real wheel-well cutout — the wheel reads only if **every
body/trim panel across the wheel silhouette sits inboard of the tyre's outer face**, so
the tyre is the outermost surface at those pixels. The rule, per axle:

- **Arch/flare outer edge = that axle's tyre outer face − 2.5 mm.** The tyre face is
  `track/2 + wheel_width/2`. Front and rear tyres often differ (wide rears): a flare tuned
  to the wide rear tyre will sit **outboard** of the narrower front tyre and swallow the
  front wheel. Compute the flare centre per axle from `wheel_width` / `wheel_width_r` — do
  **not** share one Y across both axles. (This was the coupe's buried-front-wheel bug and
  the pickup's "rim disc plastered on the fender": flares 1–2 cm proud of the tyre.)
- **Rim/hub discs recess inboard of the tyre face** (`Wd − 0.04..0.05`), never flush at
  `Wd`. A rim disc coplanar with the tyre sidewall face is a guaranteed z-fight (different
  material at one plane); a recessed dish reads as depth and never fights.
- **Tyre cylinders use a vertex-aligned vert count** (multiple of 4, e.g. 20) so the radial
  bbox is exactly `2R`. A face-aligned count (e.g. 18) measures `R·cos(π/N)` and trips the
  assembler's self-correcting visual scale (a runtime `wheel r=… != def r=…` warning).

### Two offline gates (`tools/check_coplanar.py`, exit 0 = clean)

- **Coplanar census** (`--part` / `--kit` / `--assembled`): flags two faces sharing a plane
  (z-fight flicker). Adjacent/covered panels dodge it via the **2.5 mm panel-gap inset**
  (subordinate panel steps 2.5 mm off any shared plane).
  Two levers clear a coplanar flag — `check_coplanar` gates on **coplanar AND overlap**, so
  killing *either one* clears it: (a) a **perpendicular ~2.5 mm inset/proud offset** (the
  panel-gap inset above — steps the panel off the shared plane), or (b) an **in-plane shift**
  that removes the projected overlap while the two panels stay coplanar. Reach for the
  in-plane shift when a panel is **sandwiched between two faces of a neighbour**, where a
  perpendicular move would just re-land it on the neighbour's *other* face. (Proven on the
  hatch tailgate plate-vs-glass seam, 2026-07-15.)
- **Arch-clearance census** (`--arches`, also folded into `--assembled`): flags any body
  face lying **entirely outboard** of a wheel's tyre face within its silhouette disc — the
  occlusion that buries a wheel. Coplanar mode is blind to it (an occluding flare is ~1 cm
  *proud* of the tyre, not coplanar), and it is **face-based, not vertex-based** (a box
  flare's verts sit at its X-Z corners, outside the disc, while the face spans across it).

`--assembled DIR` runs both and is the complete visual gate; run it on every kit you touch.

---

## Verification status

| Check | How | Status |
|---|---|---|
| Generator runs headless clean | Blender 5.1.2 `-b -P` | ✅ verified |
| Each OBJ has `usemtl` + `vt` (UV) lines | in-script OBJ parse | ✅ verified (all 8) |
| Wheel diameter = 2·r, width = spec | parse wheel.obj bounds | ✅ 0.60 / 0.22 m |
| Assembled envelope = L×W×H | sum part bounds+attach | ✅ 4.00 × 1.75 × 1.45 |
| Wheelbase / track | wheel attach deltas | ✅ 2.55 / 1.50 m |
| Wheel pivot at hub centre | bounds centre ≈ 0 | ✅ verified |
| Door/hood pivots on hinge lines | one-sided local bounds | ✅ verified |
| Contact-sheet preview renders | Blender Workbench | ✅ `docs/images/hatch_kit_contact.png` |
| **vmdl compiles in s&box** | editor asset compile | ⛔ **unverified-in-engine** |
| **flat vmats resolve / colours read** | in-engine screenshot | ⛔ **unverified-in-engine** |
| **parts assemble at right offsets** | VehicleFactory + screenshot | ⛔ **unverified-in-engine** |
| **wheels track suspension (spin/steer/compress)** | drive + telemetry | ⛔ **unverified-in-engine** |
| **facing yaw (+90°) correct in world** | spawn + screenshot | ⛔ **unverified-in-engine** |

Engine integration must: (1) editor-compile the vmdls and confirm no material-resolve errors;
(2) spawn the chassis, parent each part at `attach_local_m × 39.37`, confirm the silhouette
reads as a car; (3) drive wheel visuals from `VehicleWheel` state and confirm spin/steer/
compress axes match `rotation_axis_local`; (4) confirm the +90° facing yaw.
