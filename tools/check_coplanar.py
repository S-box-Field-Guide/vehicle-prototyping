"""Coplanar-overlap census for the multi-part vehicle kits.

Loads the generated per-part OBJs and reports face pairs that are BOTH
  (a) coplanar  (same plane normal, plane offset within COPLANAR_TOL), AND
  (b) overlapping in projected area (interior intersection > AREA_TOL).
Such pairs render as camera-dependent flicker (z-fighting) in-engine -- the
known #1 defect of flat-shaded multi-box kits. The panel-gap discipline
(adjacent/overlapping panels inset 2.5 mm at shared planes) is what keeps a
seam OUT of this report: a 2.5 mm offset moves the plane well past the 0.5 mm
coplanar tolerance, so intentional flush seams are invisible AND silent here.

Census modes:
  --part FILE.obj           self-census one part (intra-part coplanar overlaps)
  --kit  DIR                self-census every part.obj in DIR
  --assembled DIR           place every part at its manifest attach_author_m and run
                            the FULL census (intra-part + cross-part coplanar) PLUS the
                            wheel-occlusion census below
  --arches DIR              wheel-occlusion census only: flag any body/trim panel that
                            sits OUTBOARD of a wheel's tyre outer face within the wheel
                            silhouette -- the arch/flare-buries-the-wheel defect that
                            coplanar mode is blind to (an occluding flare is PROUD of
                            the tyre, not coplanar). Caught the coupe's swallowed front
                            wheels and the pickup's 'rim disc on the fender'.

Pure python (no bpy) so it runs standalone as an offline regression gate.
Frame note: OBJ vertex = (bX, bZ, -bY); author = (X, -Z, Y). Coplanarity is
frame-invariant, so single-part mode works in raw OBJ space; assembled mode
converts to author metres to apply attach_author_m offsets.

Exit code 0 = zero hits, 1 = hits found (usable as a CI gate).
"""
import sys
import os
import glob
import json
import math

# --- tolerances (metres / m^2) -------------------------------------------
COPLANAR_TOL = 0.0005   # 0.5 mm: planes closer than this count as coplanar.
                        # The 2.5 mm inset law parks intentional seams at 5x this.
NORMAL_TOL   = 1e-4     # direction match for two face normals
AREA_TOL     = 1e-5     # 0.1 cm^2 interior overlap to flag (below = a shared edge)


def parse_obj(path):
    """Return (verts, faces, fmats). verts: list of (x,y,z). faces: list of
    vert-index tuples (0-based), one per source polygon (quads kept as quads).
    fmats: the active usemtl for each face (material colour drives whether a
    coplanar coincidence VISIBLY shimmers -- same material = no colour fight)."""
    verts = []
    faces = []
    fmats = []
    cur = "?"
    with open(path, "r") as f:
        for line in f:
            if line.startswith("v "):
                _, x, y, z = line.split()[:4]
                verts.append((float(x), float(y), float(z)))
            elif line.startswith("usemtl"):
                cur = line.split(None, 1)[1].strip()
            elif line.startswith("f "):
                idx = []
                for tok in line.split()[1:]:
                    idx.append(int(tok.split("/")[0]) - 1)
                faces.append(tuple(idx))
                fmats.append(cur)
    return verts, faces, fmats


def _sub(a, b): return (a[0]-b[0], a[1]-b[1], a[2]-b[2])
def _cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def _dot(a, b): return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]
def _norm(a):
    m = math.sqrt(_dot(a, a))
    return (a[0]/m, a[1]/m, a[2]/m) if m > 1e-12 else (0.0, 0.0, 0.0)


def face_plane(poly):
    """Newell normal (robust for planar ngons) + plane offset. Returns
    (canon_normal, canon_d, raw_normal). raw_normal is the face's true OUTWARD
    direction (Blender CCW winding); canon_normal/canon_d are sign-canonicalized
    (first significant component positive) so a face and its coplanar back-to-back
    neighbour share a bucket. Comparing the RAW normals of a coplanar pair tells
    visible fights (both point the SAME way = two stacked exterior surfaces) from
    benign internal seams (OPPOSITE = back-to-back, each occluded by the other)."""
    n = [0.0, 0.0, 0.0]
    c = [0.0, 0.0, 0.0]
    k = len(poly)
    for i in range(k):
        cur, nxt = poly[i], poly[(i+1) % k]
        n[0] += (cur[1]-nxt[1])*(cur[2]+nxt[2])
        n[1] += (cur[2]-nxt[2])*(cur[0]+nxt[0])
        n[2] += (cur[0]-nxt[0])*(cur[1]+nxt[1])
        c[0] += cur[0]; c[1] += cur[1]; c[2] += cur[2]
    raw = _norm(tuple(n))
    c = (c[0]/k, c[1]/k, c[2]/k)
    n, d = raw, _dot(raw, c)
    for comp in n:
        if abs(comp) > 1e-6:
            if comp < 0:
                n, d = (-n[0], -n[1], -n[2]), -d
            break
    return n, d, raw


def plane_basis(n):
    ref = (1.0, 0.0, 0.0) if abs(n[0]) < 0.9 else (0.0, 1.0, 0.0)
    u = _norm(_cross(n, ref))
    v = _cross(n, u)
    return u, v


def to2d(poly, u, v):
    return [(_dot(p, u), _dot(p, v)) for p in poly]


def poly_area(pts):
    a = 0.0
    k = len(pts)
    for i in range(k):
        x1, y1 = pts[i]
        x2, y2 = pts[(i+1) % k]
        a += x1*y2 - x2*y1
    return abs(a)/2.0


def clip(subject, cx1, cy1, cx2, cy2):
    """Sutherland-Hodgman clip of convex subject by the half-plane left of the
    directed edge (cx1,cy1)->(cx2,cy2)."""
    out = []
    ex, ey = cx2-cx1, cy2-cy1
    k = len(subject)
    for i in range(k):
        a = subject[i]
        b = subject[(i+1) % k]
        sa = ex*(a[1]-cy1) - ey*(a[0]-cx1)
        sb = ex*(b[1]-cy1) - ey*(b[0]-cx1)
        if sa >= 0:
            out.append(a)
            if sb < 0:
                t = sa/(sa-sb)
                out.append((a[0]+t*(b[0]-a[0]), a[1]+t*(b[1]-a[1])))
        elif sb >= 0:
            t = sa/(sa-sb)
            out.append((a[0]+t*(b[0]-a[0]), a[1]+t*(b[1]-a[1])))
    return out


def convex_intersect_area(p, q):
    """Area of intersection of two convex polygons (p clipped by q's edges).
    Ensure q is CCW so half-planes point inward."""
    if poly_signed_area(q) < 0:
        q = q[::-1]
    poly = p[:]
    k = len(q)
    for i in range(k):
        poly = clip(poly, q[i][0], q[i][1], q[(i+1) % k][0], q[(i+1) % k][1])
        if len(poly) < 3:
            return 0.0
    return poly_area(poly)


def poly_signed_area(pts):
    a = 0.0
    k = len(pts)
    for i in range(k):
        x1, y1 = pts[i]
        x2, y2 = pts[(i+1) % k]
        a += x1*y2 - x2*y1
    return a/2.0


def bbox(poly):
    xs = [p[0] for p in poly]; ys = [p[1] for p in poly]; zs = [p[2] for p in poly]
    return (min(xs), min(ys), min(zs), max(xs), max(ys), max(zs))


def census(faces_with_tag):
    """faces_with_tag: list of (poly_pts, tag). Returns list of hit dicts."""
    planes = []
    for poly, tag in faces_with_tag:
        if len(poly) < 3:
            continue
        n, d, raw = face_plane(poly)
        planes.append((n, d, raw, poly, tag))
    hits = []
    for i in range(len(planes)):
        ni, di, ri, pi, ti = planes[i]
        for j in range(i+1, len(planes)):
            nj, dj, rj, pj, tj = planes[j]
            if abs(ni[0]-nj[0]) > NORMAL_TOL or abs(ni[1]-nj[1]) > NORMAL_TOL or abs(ni[2]-nj[2]) > NORMAL_TOL:
                continue
            if abs(di-dj) > COPLANAR_TOL:
                continue
            u, v = plane_basis(ni)
            area = convex_intersect_area(to2d(pi, u, v), to2d(pj, u, v))
            if area > AREA_TOL:
                bi = bbox(pi); bj = bbox(pj)
                ox = (max(bi[0], bj[0]) + min(bi[3], bj[3]))/2
                oy = (max(bi[1], bj[1]) + min(bi[4], bj[4]))/2
                oz = (max(bi[2], bj[2]) + min(bi[5], bj[5]))/2
                same_side = _dot(ri, rj) > 0   # both outward normals same way
                hits.append(dict(a=ti, b=tj, area=area, gap=abs(di-dj),
                                 visible=same_side,
                                 normal=tuple(round(x, 3) for x in ni),
                                 where=(round(ox, 3), round(oy, 3), round(oz, 3))))
    return hits


def obj_to_author(v):
    # OBJ (X,Y,Z) with X=bX, Y=bZ, Z=-bY  ->  author (bX,bY,bZ)
    return (v[0], -v[2], v[1])


def load_part(path, tag, to_author=False, offset=(0, 0, 0)):
    verts, faces, fmats = parse_obj(path)
    if to_author:
        verts = [obj_to_author(v) for v in verts]
    verts = [(v[0]+offset[0], v[1]+offset[1], v[2]+offset[2]) for v in verts]
    out = []
    for k, f in enumerate(faces):
        out.append(([verts[i] for i in f], "%s.f%d(%s)" % (tag, k, fmats[k])))
    return out


def mat_of(tag):
    """Extract material name from a '...fN(matname)' tag."""
    return tag[tag.rfind("(")+1:tag.rfind(")")] if "(" in tag else "?"


def report(title, hits, show_benign=False):
    """Returns the count of TRUE flickers: coplanar + same-outward-direction (both
    faces exterior surfaces stacked at one plane) + DIFFERENT material (a colour
    shimmer -- two identically-tinted faces at one plane draw the same colour and
    do NOT visibly fight). Two lesser buckets are tallied but do not gate:
      - same-colour coincident (visible geometry, no colour fight -- cosmetically inert),
      - benign internal seams (opposing normals, mutually occluded).
    The exit gate keys on TRUE flickers only."""
    print("=== %s ===" % title)
    vis = [h for h in hits if h["visible"]]
    ben = [h for h in hits if not h["visible"]]
    flick = [h for h in vis if mat_of(h["a"]) != mat_of(h["b"])]
    samec = [h for h in vis if mat_of(h["a"]) == mat_of(h["b"])]
    if not flick:
        print("  ZERO flickers.  (%d same-colour coincident, %d benign internal seams)"
              % (len(samec), len(ben)))
    else:
        for h in sorted(flick, key=lambda h: -h["area"]):
            print("  FLICKER n=%s gap=%.3fmm  overlap=%.1fcm^2  @%s" %
                  (h["normal"], h["gap"]*1000, h["area"]*1e4, h["where"]))
            print("          %s  <->  %s" % (h["a"], h["b"]))
        print("  FLICKERS: %d   (+%d same-colour coincident, +%d benign seams)"
              % (len(flick), len(samec), len(ben)))
    if show_benign:
        for h in sorted(samec, key=lambda h: -h["area"]):
            print("    same-colour n=%s overlap=%.1fcm^2 @%s  %s <-> %s" %
                  (h["normal"], h["area"]*1e4, h["where"], h["a"], h["b"]))
    return len(flick)


VERBOSE = False


def run_part(path):
    tag = os.path.splitext(os.path.basename(path))[0]
    return report("PART %s (self)" % tag, census(load_part(path, tag)), VERBOSE)


def run_kit(kit_dir):
    total = 0
    for obj in sorted(glob.glob(os.path.join(kit_dir, "*.obj"))):
        total += run_part(obj)
    print(">>> KIT %s self-census total = %d\n" % (os.path.basename(kit_dir.rstrip("/\\")), total))
    return total


def run_assembled(kit_dir):
    man = json.load(open(os.path.join(kit_dir, "manifest.json")))
    faces = []
    for p in man["parts"]:
        # skip the 4 wheel instances beyond the single wheel.obj; wheels are radial
        obj = os.path.join(kit_dir, p["obj"])
        if not os.path.exists(obj):
            continue
        off = tuple(p.get("attach_author_m", [0, 0, 0]))
        faces += load_part(obj, p["part"], to_author=True, offset=off)
    n = report("ASSEMBLED %s (intra+cross-part)" % os.path.basename(kit_dir.rstrip("/\\")), census(faces), VERBOSE)
    print()
    # the coplanar census cannot see an OCCLUSION (a flare ~1 cm PROUD of the tyre,
    # not coplanar with it) -- run the wheel-clearance census too so one --assembled
    # call is the full visual gate.
    n += run_arches(kit_dir)
    return n


# --- wheel-occlusion (arch clearance) census --------------------------------
# Coplanar mode flags two faces sharing a plane. It is BLIND to the defect that
# buries a wheel: a body/flare panel sitting OUTBOARD of the tyre's outer face
# within the wheel silhouette (the coupe's swallowed front wheels; the pickup's
# rim/lugs 'disc plastered on the fender'). Such a panel is ~1 cm PROUD of the
# tyre, never coplanar with it, so it needs its own geometric test.
ARCH_MARGIN = 0.002   # 2 mm: a body face must clear the tyre outer face by this
ARCH_EDGE   = 0.02    # ignore verts within 2 cm of the silhouette edge (lip grazing)


def _wheel_metrics(obj_path):
    """(tyre_face_y, radius) for a wheel OBJ in AUTHOR metres. tyre_face_y is the |Y|
    of the tyre sidewall cap -- the LARGEST Y-normal face, deliberately NOT the sparse
    shoulder lugs that poke further out -- and radius is the tyre radial half-extent."""
    verts, faces, _ = parse_obj(obj_path)
    av = [obj_to_author(v) for v in verts]
    xs = [v[0] for v in av]; zs = [v[2] for v in av]
    R = max(max(xs) - min(xs), max(zs) - min(zs)) / 2.0
    best_area = 0.0
    face_y = max(abs(v[1]) for v in av)   # fallback: outermost vert
    for f in faces:
        poly = [av[i] for i in f]
        if len(poly) < 3:
            continue
        n, _, _ = face_plane(poly)
        if abs(n[1]) < 0.9:               # not a Y-facing (sidewall) cap
            continue
        u, w = plane_basis(n)
        area = poly_area(to2d(poly, u, w))
        if area > best_area:
            best_area = area
            face_y = abs(sum(p[1] for p in poly) / len(poly))
    return face_y, R


def run_arches(kit_dir):
    """Flag any body/trim part whose geometry sits OUTBOARD of a wheel's tyre outer
    face within that wheel's silhouette -- the arch/flare-buries-the-wheel defect.
    Returns the number of (part, wheel) burial pairs (0 = clean gate)."""
    man = json.load(open(os.path.join(kit_dir, "manifest.json")))
    parts = man["parts"]
    wheels = [p for p in parts if p["part"].startswith("wheel")]
    bodies = [p for p in parts if not p["part"].startswith("wheel")]
    wm = {}
    for p in wheels:
        obj = os.path.join(kit_dir, p["obj"])
        if p["obj"] not in wm and os.path.exists(obj):
            wm[p["obj"]] = _wheel_metrics(obj)
    print("=== ARCHES %s (wheel occlusion) ===" % os.path.basename(kit_dir.rstrip("/\\")))
    # preload each body part's faces once, in author metres at its attach offset.
    body_faces = {}
    for b in bodies:
        obj = os.path.join(kit_dir, b["obj"])
        if not os.path.exists(obj):
            continue
        ox, oy, oz = b.get("attach_author_m", [0, 0, 0])
        verts, faces, _ = parse_obj(obj)
        av = [obj_to_author(v) for v in verts]
        av = [(v[0] + ox, v[1] + oy, v[2] + oz) for v in av]
        body_faces[b["part"]] = [[av[i] for i in f] for f in faces if len(f) >= 3]
    hits = []
    for wheel in wheels:
        if wheel["obj"] not in wm:
            continue
        face_y, R = wm[wheel["obj"]]
        hx, hy, hz = wheel.get("attach_author_m", [0, 0, 0])
        side = 1.0 if hy >= 0 else -1.0
        thresh = abs(hy) + face_y            # tyre outer face on this side (author |Y|)
        rin = max(R - ARCH_EDGE, 0.0)        # inner disc radius (ignore lip grazing)
        for pname, faces in body_faces.items():
            worst = 0.0
            for poly in faces:
                # FACE-based: a box flare's verts sit at its X-Z corners (outside the
                # disc) even though the face spans ACROSS the wheel, so test the whole
                # face, not its verts. Occluder = a face lying ENTIRELY outboard of the
                # tyre face whose X-Z footprint overlaps the wheel silhouette disc.
                if any(p[1] * side <= 0 for p in poly):
                    continue                                   # touches the other side
                min_absy = min(abs(p[1]) for p in poly)
                if min_absy <= thresh + ARCH_MARGIN:           # not fully outboard
                    continue
                xs = [p[0] for p in poly]; zs = [p[2] for p in poly]
                cx = min(max(hx, min(xs)), max(xs))            # hub clamped into face bbox
                cz = min(max(hz, min(zs)), max(zs))
                if math.hypot(cx - hx, cz - hz) > rin:         # bbox misses the disc
                    continue
                worst = max(worst, min_absy - thresh)
            if worst > 0:
                hits.append((pname, wheel["part"], worst * 1000))
    if not hits:
        print("  ZERO occlusions.  (every body panel inboard of its wheel's tyre face)")
    else:
        for pn, wn, mm in sorted(hits, key=lambda h: -h[2]):
            print("  BURIED  %-14s occludes %-9s by %5.1f mm outboard of the tyre face"
                  % (pn, wn, mm))
    print()
    return len(hits)


def main(argv):
    global VERBOSE
    if "--verbose" in argv:
        VERBOSE = True
        argv = [a for a in argv if a != "--verbose"]
    if len(argv) >= 2 and argv[0] == "--part":
        sys.exit(1 if run_part(argv[1]) else 0)
    if len(argv) >= 2 and argv[0] == "--kit":
        sys.exit(1 if run_kit(argv[1]) else 0)
    if len(argv) >= 2 and argv[0] == "--assembled":
        sys.exit(1 if run_assembled(argv[1]) else 0)
    if len(argv) >= 2 and argv[0] == "--arches":
        sys.exit(1 if run_arches(argv[1]) else 0)
    print(__doc__)
    sys.exit(2)


if __name__ == "__main__":
    main(sys.argv[1:])
