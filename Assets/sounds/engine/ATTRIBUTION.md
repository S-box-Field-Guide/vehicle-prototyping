# Engine sound attribution

Real recorded engine loops used by the RPM-crossfade engine audio model
(`Code/Vehicle/EngineAudio.cs`). All files below are **CC0 1.0 (public domain)** — no
attribution is legally required, but the source is credited here as good practice.

All four clips come from the same author (**FreeCarSoundsGaming** on Freesound), so the
low/high layers share a consistent recorded character. Each was uploaded already
"edited for loop optimization" by the author.

| File in repo | Source title | Author | Source URL | License | Modification |
|---|---|---|---|---|---|
| `engine_real_low.mp3` | Muscle_Idle.ogg | FreeCarSoundsGaming | https://freesound.org/people/FreeCarSoundsGaming/sounds/535038/ | CC0 1.0 | Downloaded the site's 128 kbps MP3 preview of the OGG source; renamed. No audio editing. |
| `engine_real_high.mp3` | Revving.ogg | FreeCarSoundsGaming | https://freesound.org/people/FreeCarSoundsGaming/sounds/535041/ | CC0 1.0 | Downloaded the site's 128 kbps MP3 preview of the OGG source; renamed. No audio editing. |
| `engine_real_truck_low.mp3` | Truck_Idle.ogg | FreeCarSoundsGaming | https://freesound.org/people/FreeCarSoundsGaming/sounds/535044/ | CC0 1.0 | Downloaded the site's 128 kbps MP3 preview of the OGG source; renamed. No audio editing. |
| `engine_real_truck_high.mp3` | Revving_2.ogg | FreeCarSoundsGaming | https://freesound.org/people/FreeCarSoundsGaming/sounds/535040/ | CC0 1.0 | Downloaded the site's 128 kbps MP3 preview of the OGG source; renamed. No audio editing. |

CC0 1.0 Universal: https://creativecommons.org/publicdomain/zero/1.0/

## Layer wiring

- **Cars (hatch, coupe):** `engine_real_low` (deep muscle idle) crossfaded up to
  `engine_real_high` (revving) by normalized RPM.
- **Pickup:** `engine_real_truck_low` (deeper truck idle) + `engine_real_truck_high`,
  with the pickup's lower base pitch, for a heavier low-rev character.
- **Kart:** unchanged — keeps its single synth loop (`engine_b_sport_purr`).
