"""
Round-4 offline port: the DRIVETRAIN/ASSIST response to driven-wheel unload at a
kicker lip (owner: "hitches going off the ramps" at any speed, unmoved by the
suspension/geometry/visual fixes of rounds 1-3).

Digit-faithful ports of the kit paths that run at the lip, from
Libraries/fieldguide.vehiclephysics (hatch, Casual, full throttle):
  - Drivetrain.Simulate: torque curve, auto-clutch, rev limiter, ground-rpm auto
    shifting, the LIMITER-CAMP ESCAPE upshift (Rpm >= 0.94 redline held 0.25 s).
  - VehicleWheel.IntegrateWheelSpin: drive rolloff from 90% of DriveOmegaCap +
    per-substep drive omega clamp; airborne wind-down 0.5%/s.
  - VehicleWheel.Substep grounded branch: slip ratio, longitudinal TireCurve force
    with the one-substep fxStable clamp and load sensitivity.
  - VehicleController.ApplyTractionControl (Casual): worstSlip over GROUNDED
    driven wheels, target 0.14, floor 0.2 relaxed past slip 1.0.
Scripted chassis: constant forward speed, front (driven) axle grounded -> airborne
for the ballistic flight off the rated ladder lane (H 2.0, R 180, exit 8.5 deg,
1.1 g) -> landing with a 0.2 s 2x-load transient. 50 Hz ticks, 4 substeps.

VERDICT (see __main__ tables): the lip itself cuts drive force to zero simply by
unload (ballistic - fine); the felt malfunction is the AIRBORNE CASCADE + LANDING:
fronts flare to redline-equivalent within ~2 substeps, Rpm pins at redline (the
live 6008-6010 capture), the limiter-camp escape marches the box up a gear ~0.4 s
into any full-throttle flight, and the car LANDS with the wheels spinning at the
(new, taller) gear's redline surface speed: slip 0.3-1.4 at touchdown, Casual TC
slams throttle to its floor exactly at the landing, and drive force stays cut for
0.2-0.5 s while the flared wheels grind back down through the tire. That
cut-then-snap-back at touchdown is the felt hitch "going off the ramps".
"""
import math

# ---- hatch constants (CarRoster.cs + CarDefinition.cs defaults) ----
G = 9.81 * 1.1
MASS = 1150.0
R_W = 0.30
I_W = 1.2
STATIC_W = MASS * G / 4.0
GRIP_SURF = 0.80
LOAD_SENS = 0.06
PEAK_TQ = 162.0
REDLINE = 6300.0
IDLE = 900.0
ENG_BRAKE = 40.0
GEARS = [3.6, 2.1, 1.4, 1.05, 0.85]
FINAL = 3.9
SHIFT_UP = 5800.0
SHIFT_DOWN = 2200.0
DT = 0.02
SUBSTEPS = 4
TAU = 2 * math.pi

# LongitudinalCurve (roster): peak 0.10/1.35, tail 0.45/1.08
def tire_eval(slip):
    s = abs(slip)
    if s <= 0.10:
        n = s / 0.10
        return 1.35 * n * (2 - n)
    t = min(max((s - 0.10) / (0.45 - 0.10), 0.0), 1.0)
    t = t * t * (3 - 2 * t)
    return 1.35 + (1.08 - 1.35) * t

class Drivetrain:
    """Digit port of Drivetrain.Simulate (auto mode)."""
    def __init__(self, gear):
        self.rpm = IDLE; self.gear = gear; self.shiftT = 0.0; self.lockout = 0.0
        self.freeRpm = IDLE; self.limiterHold = 0.0; self.gearProven = True
        self.events = []
    def ratio(self):
        return GEARS[self.gear - 1] * FINAL if self.gear > 0 else 0.0
    def redline_wheel_speed(self):
        r = self.ratio()
        return float('inf') if r == 0 else REDLINE * TAU / 60.0 / abs(r)
    def engine_torque(self, rpm):
        n = min(max((rpm - IDLE) / (REDLINE - IDLE), 0.0), 1.0)
        return PEAK_TQ * (0.5 + 1.5 * n - n * n) / 1.0625
    def simulate(self, dt, throttle, avgDrivenSpeed, groundWheelSpeed, drivenCount, t_now):
        if self.shiftT > 0:
            self.shiftT -= dt
            throttle = 0.0
        ratio = self.ratio()
        wheelRpm = abs(avgDrivenSpeed * ratio) * 60.0 / TAU
        stall = IDLE * 1.05; lock = IDLE * 1.7
        eng = 0.0 if self.gear == 0 else min(max((wheelRpm - stall) / (lock - stall), 0.0), 1.0)
        freeTarget = IDLE + throttle * (REDLINE * 0.5 - IDLE)
        self.freeRpm = min(max(self.freeRpm + (freeTarget - self.freeRpm) * 5 * dt, IDLE), REDLINE)
        lockedRpm = min(max(wheelRpm, IDLE), REDLINE)
        self.rpm = self.freeRpm + (lockedRpm - self.freeRpm) * eng
        engineTorque = self.engine_torque(self.rpm) * throttle
        engineBrake = ENG_BRAKE * (1 - throttle) * (self.rpm / REDLINE) * eng
        slipTrans = min(max(throttle * 1.2, 0.0), 1.0) * 0.85
        clutchF = slipTrans + (1 - slipTrans) * eng
        torqueOut = (engineTorque * clutchF - engineBrake) * ratio
        if self.rpm >= REDLINE and throttle > 0:
            torqueOut = min(torqueOut, 0.0)
        self.lockout -= dt
        groundRpm = abs(groundWheelSpeed * ratio) * 60.0 / TAU
        nearLim = self.rpm >= REDLINE * 0.94 and throttle > 0.5
        self.limiterHold = self.limiterHold + dt if nearLim else max(0.0, self.limiterHold - dt * 0.5)
        if not self.gearProven and groundRpm >= SHIFT_DOWN:
            self.gearProven = True
        if self.gear > 0 and self.shiftT <= 0 and self.lockout <= 0:
            wantUp = groundRpm > SHIFT_UP
            escape = False
            if not wantUp and self.limiterHold > 0.25 and self.gear < len(GEARS):
                nextRatio = GEARS[self.gear] * FINAL
                postRpm = abs(groundWheelSpeed * nextRatio) * 60.0 / TAU
                wantUp = escape = postRpm >= SHIFT_DOWN * 0.5
            if wantUp and self.gear < len(GEARS):
                self.gear += 1
                self.shiftT = 0.15
                self.lockout = 1.5 if escape else 0.8
                self.limiterHold = 0.0
                self.gearProven = False
                self.events.append((t_now, f"UPSHIFT{' (escape)' if escape else ''} -> gear {self.gear}"))
            elif groundRpm < SHIFT_DOWN and self.gear > 1 and (self.gearProven or throttle < 0.5):
                self.gear -= 1
                self.shiftT = 0.12
                self.lockout = 0.8
                self.gearProven = True
                self.events.append((t_now, f"DOWNSHIFT -> gear {self.gear}"))
        return torqueOut / drivenCount if drivenCount > 0 else 0.0

ROLLOFF_ONSET = 0.90

def integrate_spin(omega, dt, driveTorque, tireForce, cap):
    """Digit port of IntegrateWheelSpin (no brakes)."""
    pre = omega
    driving = driveTorque != 0.0
    dirn = math.copysign(1.0, driveTorque) if driving else 0.0
    if driving and cap != float('inf'):
        approach = dirn * pre / cap
        if approach > ROLLOFF_ONSET:
            f = min(max((1 - approach) / (1 - ROLLOFF_ONSET), 0.0), 1.0)
            driveTorque *= f * f * (3 - 2 * f)
    omega += (driveTorque - tireForce * R_W) / I_W * dt
    if driving and cap != float('inf'):
        c = max(cap, dirn * pre)
        omega = min(omega, c) if dirn > 0 else max(omega, -c)
    return omega

def tc_casual(throttle, slips_grounded):
    """Digit port of ApplyTractionControl (Casual, no handbrake)."""
    if throttle <= 0:
        return throttle, 1.0
    worst = max(slips_grounded) if slips_grounded else 0.0
    if worst <= 0.14:
        return throttle, 1.0
    floor = 0.2
    if worst > 1.0:
        floor *= min(max((2.5 - worst) / 1.5, 0.0), 1.0)
    f = min(max(0.14 / worst, floor), 1.0)
    return throttle * f, f

def lip_run(v, assists_tc=True, H=2.0, R_face=180.0, pre_s=1.0, post_s=1.5, gear0=None):
    """Scripted lip crossing at forward speed v (m/s), full throttle."""
    exitA = math.asin(math.sqrt(2 * H / R_face))     # exit angle of the rated face
    vz = v * math.sin(exitA)
    t_fly = (vz + math.sqrt(vz * vz + 2 * G * H)) / G
    # start in the ground-correct gear
    gws = v / R_W
    if gear0 is None:
        gear0 = 1
        while gear0 < len(GEARS) and abs(gws * GEARS[gear0 - 1] * FINAL) * 60 / TAU > SHIFT_UP:
            gear0 += 1
    dtr = Drivetrain(gear0)
    omega = gws                       # rolling
    slip = 0.0
    rows = []
    t = -pre_s
    lipT = 0.0
    landT = t_fly
    while t < t_fly + post_s:
        grounded = (t < lipT) or (t >= landT)
        load = STATIC_W
        if t >= landT and t < landT + 0.2:
            load = 2 * STATIC_W       # landing transient (round-2 sims)
        thr_in = 1.0
        # tick-level TC reads the LAST substep's slip of grounded driven wheels
        if assists_tc:
            thr, tcf = tc_casual(thr_in, [slip] if grounded else [])
        else:
            thr, tcf = thr_in, 1.0
        sdt = DT / SUBSTEPS
        drive_force_avg = 0.0
        for _ in range(SUBSTEPS):
            tq = dtr.simulate(sdt, thr, omega, gws, 2, t)
            cap = dtr.redline_wheel_speed()
            if grounded:
                slipVel = omega * R_W - v
                slip = slipVel / max(abs(v), 2.0)
                loadF = 1 - LOAD_SENS * max(0.0, load / STATIC_W - 1)
                maxF = load * loadF * GRIP_SURF
                fx = tire_eval(slip) * math.copysign(1.0, slip) * maxF if slip != 0 else 0.0
                fxStable = abs(slipVel) * I_W / (R_W * R_W * sdt)
                fx = min(max(fx, -fxStable), fxStable)
                omega = integrate_spin(omega, sdt, tq, fx, cap)
                drive_force_avg += fx / SUBSTEPS
            else:
                slip = 0.0
                omega = integrate_spin(omega, sdt, tq, 0.0, cap)
                omega *= 1 - 0.5 * sdt
        rows.append(dict(t=t, grounded=grounded, rpm=dtr.rpm, gear=dtr.gear,
                         omega=omega, surf=omega * R_W, slip=slip, tcf=tcf,
                         drive2=2 * drive_force_avg, shifting=dtr.shiftT > 0))
        t += DT
    return rows, dtr.events, t_fly

def report(v, assists_tc=True):
    rows, events, t_fly = lip_run(v, assists_tc)
    pre = [r for r in rows if r['t'] < -0.1]
    f_pre = sum(r['drive2'] for r in pre) / len(pre)
    land = [r for r in rows if r['t'] >= t_fly]
    # power hole: first landing tick until drive force recovers to 50% of pre-lip
    hole_end = next((r['t'] for r in land if r['drive2'] > 0.5 * f_pre and r['t'] > t_fly + 0.02), None)
    slip_land = land[0]['slip'] if land else 0
    peak_slip = max((r['slip'] for r in land[:25]), default=0)
    min_tcf = min((r['tcf'] for r in land[:25]), default=1)
    air = [r for r in rows if not r['grounded']]
    rpm_pin = sum(1 for r in air if r['rpm'] >= 0.94 * REDLINE) * DT
    mode = "CASUAL (TC on)" if assists_tc else "SPORT/SIM (no TC)"
    print(f"\n=== v={v} m/s {mode} | flight {t_fly:.2f}s ===")
    print(f"  pre-lip drive force {f_pre:.0f} N | airborne rpm>=94% redline for {rpm_pin:.2f}s")
    for te, ev in events:
        print(f"  {te:+.2f}s {ev}")
    print(f"  LANDING (t={t_fly:.2f}): touchdown slip {peak_slip:.2f}, min TC factor {min_tcf:.2f}, "
          f"power hole (to 50% of pre-lip force) {'never in window' if hole_end is None else f'{hole_end - t_fly:.2f}s'}")
    # tick table around landing
    print("     t     st  gear  rpm   surf m/s  slip   tcf  drive N")
    for r in rows:
        if t_fly - 0.10 <= r['t'] <= t_fly + 0.40 or -0.06 <= r['t'] <= 0.10:
            st = 'G' if r['grounded'] else 'A'
            print(f"  {r['t']:+.2f}   {st}   {r['gear']}   {r['rpm']:5.0f}  {r['surf']:6.1f}   "
                  f"{r['slip']:+5.2f}  {r['tcf']:4.2f}  {r['drive2']:+7.0f}{'  <shift cut>' if r['shifting'] else ''}")

if __name__ == "__main__":
    for v in (20, 30, 45):
        report(v, assists_tc=True)
    print("\n" + "=" * 70)
    for v in (20, 30, 45):
        report(v, assists_tc=False)
