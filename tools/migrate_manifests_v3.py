#!/usr/bin/env python3
"""migrate_manifests_v3.py -- upgrade a SHIPPED part-kit manifest.json to schema vp.partkit/3
IN PLACE, without re-running Blender.

WHY (emit-manifests-only):
  gen_vehicle.py now emits schema v3 (proven frame + inert damage band). Re-running it would
  regenerate the OBJ/vmdl geometry too — and the LANDED hatch_kit OBJs predate later generator fixes
  (e.g. build_door's mirror fix), so a full regen would overwrite tuned/landed assets beyond the
  manifest. The choice is: emit the v3 MANIFEST only, leaving the compiled geometry untouched. This
  script performs exactly that manifest upgrade:

    * v1 -> v3: apply the SAME v1->true bounds flip PartKitManifest.TryLoad already performs
      (trueMin.xy = -recordedMax.xy, trueMax.xy = -recordedMin.xy, z unchanged) and recompute
      attach_local_m via the proven author->local mapping, so the on-disk manifest advances to the
      proven frame WITHOUT changing a single consumed value (behaviour-preserving by construction),
      then add the damage band + refresh the stale frames metadata.
    * v2 -> v3: frame already proven; just add the damage band.

  Damage-band defaults come from gen_vehicle.damage_defaults (LOCKSTEP with the C# loader), so a
  migrated manifest is byte-equivalent to a freshly generated one for the damage fields.

USAGE
  python tools/migrate_manifests_v3.py            # migrate both shipped kits in place
  python tools/migrate_manifests_v3.py --check    # dry-run: report what would change, write nothing
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import gen_vehicle as gv  # noqa: E402  (damage_defaults + author_to_local, importable without bpy)

SHIPPED = [
    os.path.join(REPO, "Assets", "models", "vehicles", "hatch_kit", "manifest.json"),
    os.path.join(REPO, "Assets", "models", "vehicles", "pickup_kit", "manifest.json"),
]

PROVEN_FRAMES = {
    "authoring": "+X forward, +Y left, +Z up (Blender)",
    "chassis_local": "+X right, +Y FRONT, +Z up (nose points local +Y; -90 deg yaw faces root +X) — EMPIRICALLY PROVEN 2026-07-11",
    "author_to_local": "(mx,my,mz) = (-bY, +bX, bZ)",
    "obj_import": "model = (objZ, objX, objY) — plain Y-up->Z-up cyclic permutation, no sign flips",
}


def migrate(man):
    """Return (migrated_manifest, changed_bool). Idempotent."""
    schema = man.get("schema")
    was_v1 = schema == "vp.partkit/1"
    for p in man.get("parts", []):
        if was_v1:
            lo = p["local_bounds_min_m"]
            hi = p["local_bounds_max_m"]
            # identical to PartKitManifest.TryLoad's v1 bounds normalization
            p["local_bounds_min_m"] = [round(-hi[0], 4), round(-hi[1], 4), round(lo[2], 4)]
            p["local_bounds_max_m"] = [round(-lo[0], 4), round(-lo[1], 4), round(hi[2], 4)]
            # proven author->local for the (bound-but-unconsumed) attach_local_m field
            p["attach_local_m"] = [round(v, 4) for v in gv.author_to_local(p["attach_author_m"])]
        # add the damage band where absent (setdefault => idempotent, keeps explicit values)
        for k, v in gv.damage_defaults(p["kind"], p["part"], p["dims_m"]).items():
            p.setdefault(k, v)
    if was_v1:
        man["frames"] = PROVEN_FRAMES
    man["schema"] = "vp.partkit/3"
    return man


def main():
    check = "--check" in sys.argv
    for path in SHIPPED:
        with open(path, "r", encoding="utf-8") as f:
            before = f.read()
        man = json.loads(before)
        old_schema = man.get("schema")
        migrate(man)
        after = json.dumps(man, indent=2) + "\n"
        rel = os.path.relpath(path, REPO).replace("\\", "/")
        if after.strip() == before.strip():
            print(f"  [unchanged] {rel} (already {man['schema']})")
            continue
        if check:
            print(f"  [would migrate] {rel}: {old_schema} -> {man['schema']}")
            continue
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(after)
        print(f"  [migrated] {rel}: {old_schema} -> {man['schema']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
