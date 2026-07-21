"""
Offline evidence for the RampKicker high-speed-hitch fix (MinRadiusM 90 -> 240,
2026-07-21). Root-cause + retune tool for the stunt-kicker "gets stuck / slows
down / launches like crazy at high speed" report.

STABLE point-mass-on-arc port of the VP raycast-wheel car (Hatch) traversing a
RampKicker easement face. The pitch DOF is dropped on purpose: it is numerically
stiff in a plain per-tick integrator and is NOT the suspension mechanism this tool
isolates (the live capture shows the pitch/nose-high story separately). Measures:
  1. suspension bottoming and its SPEED crossover (v > sqrt(a_avail * R)),
  2. the vertical DIVE (chassis sinking into the face) depth vs entry speed,
  3. forward-speed scrub (found negligible: ~99%, matching the live ~1% loss),
  4. whether the chassis belly reaches the surface (it does not on these arcs).

Faithful to VehicleWheel suspension: combined 4-wheel spring/damper, compression
clamped to travel, Load clamped to 4x static, force along contact normal, surface
traced ONCE per 50 Hz fixed tick (VehicleController). Constants are the shipped
Hatch (CarRoster.cs) + CarDefinition.cs defaults + GameBootstrap 1.1 g gravity.

Retune: MinRadiusM is the single dial. Change `FLOOR_AFTER` below and re-run to
trade footprint for residual high-speed sink (e.g. 200 -> 44 mm, 170 -> 59 mm,
240 -> 33 mm at 53 m/s; the old floor 90/104 gave 242 mm).
"""
import math

G = 9.81 * 1.1
MASS = 1150.0
K = 4 * 34000.0     # combined spring
C = 4 * 2500.0      # combined damper
TRAVEL = 0.20
RADIUS = 0.30
RIDE_H = 0.35
COM_DROP = 0.20
GROUND_CLEAR = 0.14
DT = 0.02
STATIC = MASS * G                      # total weight
CLAMP = 4 * (MASS * G / 4) * 4         # 4 wheels * (4x static per wheel) = 4*MASS*G ... = 16*static/4
CLAMP = 4 * ((MASS * G / 4) * 4)       # = 4 * (StaticPerWheel*4)
BLEND = 0.5

# com-above-surface geometry
REST_FULL = RADIUS + TRAVEL + RIDE_H - COM_DROP    # com above surface at full extension = 0.65
STATIC_COMP = STATIC / K                            # combined static compression
COM_ABOVE_REST = REST_FULL - STATIC_COMP            # ~0.559
BELLY_BELOW_COM = (RIDE_H - GROUND_CLEAR) - COM_DROP  # belly bottom below com = 0.01
BELLY_ABOVE_REST = COM_ABOVE_REST - BELLY_BELOW_COM

def easement_profile(L, H, segs=60):
    R = (L*L + H*H) / (2*H)
    thetaExit = math.asin(min(max(L/R, 0.0), 1.0))
    S = thetaExit * R / (1 - BLEND*0.5)
    n = segs * 64
    ds = S / n
    sBlend = BLEND * S
    x = z = theta = 0.0
    stride = n // segs
    xs = [0.0]; zs = [0.0]
    for i in range(1, n+1):
        sMid = (i - 0.5) * ds
        k = (sMid / sBlend) / R if sMid < sBlend else 1.0 / R
        thMid = theta + k*ds*0.5
        theta += k*ds
        x += math.cos(thMid) * ds
        z += math.sin(thMid) * ds
        if i % stride == 0:
            xs.append(x); zs.append(z)
    scale = H / z
    return [v*scale for v in xs], [v*scale for v in zs], xs[-1]*scale, R, math.degrees(thetaExit)

class Surf:
    def __init__(self, L, H):
        self.xs, self.zs, self.run, self.R, self.exitDeg = easement_profile(L, H)
        self.H = H
    def hz(self, x):
        if x <= 0: return 0.0, 0.0
        if x >= self.run: return None, None
        xs, zs = self.xs, self.zs
        lo, hi = 0, len(xs)-1
        while hi-lo > 1:
            m = (lo+hi)//2
            if xs[m] <= x: lo = m
            else: hi = m
        t = (x-xs[lo])/(xs[hi]-xs[lo]) if xs[hi]>xs[lo] else 0
        z = zs[lo] + t*(zs[hi]-zs[lo])
        slope = (zs[hi]-zs[lo])/(xs[hi]-xs[lo]) if xs[hi]>xs[lo] else 0
        return z, slope

def analytic_crossover(R):
    """Speed at which required centripetal exceeds SUSTAINED bottomed-spring capacity
    (springs fully compressed, damper=0). Above this the ride goes rigid on the face."""
    Nmax_sustained = K * TRAVEL           # = 4*Spring*travel = 27200 N
    a_avail = (Nmax_sustained - STATIC) / MASS   # net centripetal at base (cos~1)
    v = math.sqrt(max(a_avail,0) * R)
    # clamp (peak, damper-driven) ceiling
    a_clamp = (CLAMP - STATIC) / MASS
    v_clamp = math.sqrt(max(a_clamp,0)*R)
    return v, v_clamp, a_avail, a_clamp

def sim(entry_v, L, H, ticks=600):
    surf = Surf(L, H)
    x = -8.0
    com = COM_ABOVE_REST          # com height above surface (surface=0 on flat)
    z = com                        # absolute com (surface 0 pre-ramp)
    vx = entry_v; vz = 0.0
    trace = []
    prev_comabove = com
    for tk in range(ticks):
        zs, slope = surf.hz(x)
        airborne = zs is None
        if airborne:
            # ballistic
            vz -= G*DT; x += vx*DT; z += vz*DT
            trace.append(dict(x=x, spd=math.hypot(vx,vz), com_above=None, comp=0, Load=0,
                              belly=0, vz=vz, air=True))
            if x - 0 > surf.run + 2: break
            continue
        theta = math.atan(slope)
        ct, st = math.cos(theta), math.sin(theta)
        n = (-st, ct)             # up-normal
        com_above = z - zs        # vertical gap com-to-surface
        # compression along normal ~ (rest_full - com_above)*cos(theta) but keep vertical proxy then
        # project; use vertical gap directly against REST_FULL (small-angle, <=11deg -> <2% err)
        compression = min(max(REST_FULL - com_above, 0.0), TRAVEL)
        # compression speed along normal: rate the com approaches the surface
        surf_rise_rate = slope * vx           # dz_surf/dt under the moving com
        comp_speed = (surf_rise_rate - vz)    # >0 when surface rises into com / com sinks
        springF = K*compression + C*comp_speed
        Load = min(max(springF, 0.0), CLAMP)
        Fx = Load*n[0]
        Fz = Load*n[1] - STATIC
        # belly rigid floor
        belly_above = com_above - BELLY_BELOW_COM
        belly_pen = max(0.0, -belly_above)
        # integrate once per tick
        vx += Fx/MASS*DT
        vz += Fz/MASS*DT
        x += vx*DT
        z += vz*DT
        # resolve belly penetration (rigid, along vertical normal approx)
        if belly_pen > 0:
            z += belly_pen
            if vz < surf_rise_rate:   # moving into surface
                # kill relative normal velocity + friction scrub on tangential
                vrel = surf_rise_rate - vz
                vz = surf_rise_rate
                # friction: scrub forward by mu * normal impulse proxy
                vx -= 0.7 * vrel * st   # tangential coupling of the vertical arrest
        spd = math.hypot(vx, vz)
        trace.append(dict(x=x, spd=spd, com_above=z-zs if zs is not None else None,
                          comp=compression, Load=Load, belly=belly_pen, vz=vz, air=False,
                          slopeDeg=math.degrees(theta), a_c_demand=spd*spd/surf.R))
    return surf, trace

def report(entry_v, L, H):
    surf, tr = sim(entry_v, L, H)
    face = [r for r in tr if not r['air'] and r['x']>=0 and r['x']<=surf.run]
    if not face:
        print("no face"); return
    v0 = face[0]['spd']
    vmin = min(r['spd'] for r in face)
    vmin_r = min(face, key=lambda r:r['spd'])
    vlip = face[-1]['spd']
    max_comp = max(r['comp'] for r in face)
    max_load = max(r['Load'] for r in face)
    bottomed = [r for r in face if r['comp'] >= TRAVEL-1e-4]
    max_belly = max(r['belly'] for r in face)
    # dive: how far com_above drops below its clean-arc equilibrium (COM_ABOVE_REST minus extra comp)
    min_comabove = min(r['com_above'] for r in face if r['com_above'] is not None)
    # vertical velocity min (dive speed)
    min_vz = min(r['vz'] for r in face)
    vx, R = surf.exitDeg, surf.R
    xc, vc, aa, ac = analytic_crossover(surf.R)
    print(f"\n=== entry {entry_v:.0f} | H={H} R={surf.R:.0f} run={surf.run:.1f} exit={surf.exitDeg:.1f}deg ===")
    print(f"  crossover: sustained-cap v={xc:.1f} (a_avail {aa:.1f} m/s2), clamp-cap v={vc:.1f} (a_clamp {ac:.1f})")
    print(f"  v0 {v0:.1f}  vmin {vmin:.2f} @x{vmin_r['x']:.1f}  vlip {vlip:.2f}  "
          f"faceRetention {vmin/v0*100:.1f}%")
    print(f"  bottomed ticks {len(bottomed)}/{len(face)}  maxLoad {max_load:.0f}N ({max_load/STATIC:.2f}x wt)  "
          f"maxComp {max_comp*1000:.0f}/{TRAVEL*1000:.0f}mm")
    print(f"  min com_above {min_comabove*1000:.0f}mm (rest {COM_ABOVE_REST*1000:.0f}, bottom {(REST_FULL-TRAVEL)*1000:.0f})  "
          f"min vz {min_vz:.2f} m/s (dive)  belly {max_belly*1000:.1f}mm")

FLOOR_BEFORE = 90.0    # old MinRadiusM
FLOOR_AFTER = 240.0    # new MinRadiusM (retune dial)
LADDER = (0.6, 1.2, 2.0, 3.0, 4.5)

def sink_at(v, L, H):
    surf, tr = sim(v, L, H)
    face = [r for r in tr if not r['air'] and 0 <= r['x'] <= surf.run]
    sink = (COM_ABOVE_REST - min(r['com_above'] for r in face)) * 1000
    bot = sum(1 for r in face if r['comp'] >= TRAVEL - 1e-4)
    return sink, bot, surf.exitDeg, L, surf.R

if __name__ == "__main__":
    print("Per-speed detail (H=2.0 showcase kicker, old vs new floor):")
    for floor in (FLOOR_BEFORE, FLOOR_AFTER):
        H = 2.0; R = max(floor, 52*H); L = math.sqrt(H*(2*R - H))
        print(f"\n########## floor {floor:.0f}  H={H}  L={L:.2f}  R={R:.0f} ##########")
        for v in (25, 35, 45, 53):
            report(v, L, H)

    print("\n\n=== BEFORE/AFTER sink (mm) across the ladder, hatch ===")
    print(f"  {'H':>4} | {'oldR/L/exit':>16}  25   53      | {'newR/L/exit':>16}  25   53")
    for H in LADDER:
        Ro = max(FLOOR_BEFORE, 52*H); Lo = math.sqrt(H*(2*Ro - H))
        Rn = max(FLOOR_AFTER, 52*H); Ln = math.sqrt(H*(2*Rn - H))
        o25 = sink_at(25, Lo, H); o53 = sink_at(53, Lo, H)
        n25 = sink_at(25, Ln, H); n53 = sink_at(53, Ln, H)
        print(f"  {H:>4} | R{Ro:3.0f} L{Lo:4.1f} {o25[2]:4.1f}  {o25[0]:3.0f}  {o53[0]:3.0f}(b{o53[1]}) "
              f"| R{Rn:3.0f} L{Ln:4.1f} {n25[2]:4.1f}  {n25[0]:3.0f}  {n53[0]:3.0f}(b{n53[1]})")
    print("  (sink = vertical chassis collapse into the face; b = bottomed 50 Hz ticks)")
