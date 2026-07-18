# Archived assets

Assets moved out of the shipped content tree (`Assets/`). Nothing under `archive/`
is referenced by the game or included in any published package.

| Archived | Files | Reason |
| --- | --- | --- |
| 2026-07-18 | `models/buildings/tower.obj`, `tower.mtl`, `tower.vmdl` | Never referenced at runtime; CityBuilder spawns nine buildings from hard-coded arrays and tower is in none of them. Confirmed by a repo-wide reference audit the same day (compiled `tower.vmdl_c` was untracked and deleted). |
