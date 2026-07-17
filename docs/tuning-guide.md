# Tuning guide - what every dial does, in plain language

Press `T` in-game to open the **Tuning panel**. Every slider writes straight onto the car you
are driving, so you feel the change the instant you move it - no recompile, no restart.

You do not need to know anything about real cars to use this. This guide explains, for each dial,
what it changes and which way to move it, in everyday words. A few driving terms show up (they are
explained the first time), but the short version is: **drag a dial, drive, and see how it feels.**

Two words worth knowing up front, because they describe most of what tuning changes:

- **Understeer** - the car does not turn in enough; the nose "washes out" wide toward the outside
  of the corner. It feels safe but sluggish.
- **Oversteer** - the back steps out and the car wants to spin; the tail slides. It feels lively
  but twitchy.

Tuning is mostly about trading between those two, and between "planted and stable" versus "sharp
and nimble".

The dials are grouped into four tabs at the top of the panel: **Engine**, **Tires**, **Susp**
(suspension), and **Assists**. This guide follows the same order.

---

## Engine

How the car makes and delivers power.

| Dial | What it does | Turn it UP | Turn it DOWN |
|---|---|---|---|
| **Engine torque** | The twisting force the engine makes - the car's raw pulling power. | Stronger acceleration, more shove out of corners, spins the wheels more easily. | Gentler, easier to drive smoothly, less wheelspin. |
| **Final drive** | The overall gearing. Think of it as the trade between "punchy off the line" and "fast at the top". | Snappier acceleration, but a lower top speed (the engine hits the rev limit sooner in each gear). | Taller gearing: lazier off the line but a higher top speed. |
| **Launch boost** | An extra kick of torque only at a standstill and very low speed, to help you launch hard. `1x` turns it off. | A more aggressive launch off the line (and more wheelspin if grip can't take it). | A calmer, more measured getaway. |
| **Engine brake** | How much the engine slows you when you lift off the throttle, before you even touch the brake. | The car scrubs off speed when you lift; feels planted and settles into corners on a lift. | The car coasts freely when you lift off. |
| **Redline** | The highest RPM the engine will rev to before it holds. | The engine pulls longer in each gear and carries a gear to a higher speed before shifting. | The engine shifts up earlier and revs out sooner. |

**Coupling to know - Redline moves the shift points with it.** In automatic gearbox mode the car
upshifts and downshifts at set RPM points. The Redline dial carries those points along
proportionally, so if you lower redline the car simply shifts earlier instead of getting stuck
bouncing off the limiter unable to change up. You do not have to manage the shift points yourself -
Redline handles them.

---

## Tires

Grip and braking - where the car meets the road.

| Dial | What it does | Turn it UP | Turn it DOWN |
|---|---|---|---|
| **Grip** | An overall multiplier on how much the tires stick, for both cornering and accelerating/braking. This is the single biggest "how planted is the car" dial. | More stick everywhere: sharper turn-in, harder cornering, less sliding. | Slippery and loose - the car slides around easily (fun for drifting, harder to place precisely). |
| **Drift grip (handbrake)** | How much rear grip is *left* while you hold the handbrake. Low values let the back end break loose for slides. | The handbrake barely upsets the car (hard to drift). | The rear lets go easily when you pull the handbrake - big, easy slides. |
| **Brake torque** | How hard the brakes clamp - your straight-line stopping power. | Shorter stops, but it is easier to lock the wheels up under hard braking. | Longer, softer stops. |
| **Brake assist** | An extra layer of assisted deceleration on top of the raw brakes. `0` turns it off. | Stronger, more confident stopping. | Removes the assist; braking is down to the raw brake torque. |
| **Handbrake torque** | How hard the handbrake grabs the rear wheels. | The rear locks instantly - snappier, sharper handbrake slides. | A softer handbrake that is gentler to trigger. |

**Coupling to know - Grip is a clean multiplier.** Grip always scales from the car's original,
untouched tire behaviour. Moving it to `1.4x` and back to `1.0x` returns you exactly to stock - it
never stacks on itself or drifts over time. So feel free to sweep it around while hunting for a
feel; it stays honest.

*Feel tip:* if the car understeers (won't turn in), more Grip or a stiffer rear (see suspension)
helps the front bite. If it oversteers (tail slides), more Grip calms it down across the board.

---

## Susp (suspension)

How the car rides, rolls, and carries its weight.

| Dial | What it does | Turn it UP | Turn it DOWN |
|---|---|---|---|
| **Spring rate** | Suspension stiffness. Stiffer springs resist leaning, diving under braking, and squatting under power. | Firmer and flatter - less body roll, more responsive, but harsher over bumps. | Softer - more roll, dive and squat; soaks up bumps but feels floatier. |
| **Damper rate** | How quickly the suspension settles after it is disturbed - it controls the springs' bounce. | Tightly controlled, less bouncing; can feel stiff if pushed high. | Floaty - the car keeps bobbing after bumps and weight shifts. |
| **Mass** | The car's weight. | Heavier: more momentum, more stable, but slower to change direction and longer to stop. | Lighter: nimble and eager to turn, but easier to unsettle. |
| **Gravity** | How strongly everything is pulled down. | The car plants harder and jumps land heavier and sooner. | Moon-like and floaty - long, hanging jumps. |

**Coupling to know - Gravity affects the whole scene, not just your car.** This dial changes the
world's gravity, so it is more of a playground/experiment control than a per-car setup. It is not
saved as part of a car's authored physics the way the other dials are; treat it as a global "what
if" toggle.

---

## Assists

Steering behaviour and the driver aids that keep the car catchable.

| Dial | What it does | Turn it UP | Turn it DOWN |
|---|---|---|---|
| **Steer speed** | How fast the front wheels swing to follow your input. | Snappy, immediate steering - reacts the instant you turn. | Slower, smoother, more relaxed steering. |
| **Steer lock (low speed)** | The most the wheels can turn when you are going slowly. Bigger = a tighter turning circle. | Tighter low-speed turns - better for hairpins, parking, tight maneuvers. | A wider turning circle at low speed. |
| **Steer lock (high speed)** | The most the wheels can turn at speed. Cars automatically use less steering the faster they go, so high speed does not become twitchy. | More responsive (and more nervous) steering at high speed. | Calmer, more stable steering when you are going fast. |
| **Reverse speed cap** | The top speed the car will reach in reverse. | Faster backing up. | A lower reverse top speed. |
| **Spin recovery** | An assist that kills leftover backward/sideways motion after a spin so the car stops sliding and points where you steer again. `0` turns it off for a raw, unassisted feel. | A snappier, more arcade-like recovery after a spin or handbrake slide. | Less help catching a slide - the car keeps whatever motion the spin gave it. |

*Note:* the two steer-lock dials work as a pair - low-speed lock for tight maneuvering, high-speed
lock for stability, and the car blends between them automatically as your speed changes.

---

## Saving and reusing a tune

Everything you set with the dials can be saved as a named **tune** for the car you are driving.

- **Save current** snapshots all the current dial values as a new tune. It is named automatically
  (like "Hatch tune 1") - use **Edit** to rename it to anything you like.
- **Load** applies a saved tune to the car, restoring every dial at once.
- **Del** deletes a saved tune.

Saved tunes are **per car** and stored **locally on your machine**, so they survive restarts and a
coupe tune never gets applied to the hatch. Each car keeps its own list. (The one exception is the
Gravity dial, which is a world setting rather than a car setting, but it is captured with your tune
so loading it restores the same feel.)

## Reset all

**Reset all** returns the car you are currently driving to its original, authored setup - the
values it ships with. It only touches the current car, so resetting while driving the coupe will
never write the hatch's physics onto it. It also returns the world's gravity to its default. Think
of it as your safety net: no matter how far you have wandered with the dials, one click puts the
car back to how it started.

---

## How to approach tuning (for beginners)

Change one thing at a time, and drive between changes. It is tempting to move five dials at once,
but then you cannot tell which one did what. Pick a single dial, move it a step or two, drive the
same stretch of road, and notice what changed. If the car feels worse, put it back.

A good first loop: start with **Grip** to set how planted the car feels, then **Engine torque** and
**Final drive** for how it accelerates, then the suspension dials to dial in how it rides. Save a
tune whenever you land on something you like, so you can always get back to it. And when in doubt,
**Reset all** wipes the slate clean and you start fresh. Nothing you do here is permanent or
breakable - it is a sandbox, so experiment freely.
