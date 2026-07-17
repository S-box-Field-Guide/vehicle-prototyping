# Village building set (`buildings/`)

Ten flat-shaded, low-poly building models used as the residential / civic / industrial
fill for the proving-grounds city: `barn`, `civic_hall`, `house_small`, `house_medium`,
`house_large`, `inn`, `shop`, `tower`, `warehouse`, `workshop`.

## Provenance

Original low-poly art authored in Blender and baked to render-only `.vmdl`, shipped here as
static kit assets under this repository's MIT license. Only the models and their materials are
included — no generator code.

## Format

Each building is an OBJ mesh + MTL, imported by a render-only `.vmdl` with
`import_scale 39.37` (metres to source units). Materials route through the shared
flat-vmat house style: `materials/white.png` + per-colour `g_vColorTint` vmats
(`shaders/complex.shader`), so the whole set stays in the kit's low-poly flat-shaded look.

Colliders are **not** baked into the models — the city and playground builders
(`Code/Game/CityBuilder.cs`, `Code/World/PlaygroundBuilder.cs`) attach one bounds-fit static
`BoxCollider` per placed building at runtime.

The commercial "mini high-rise" towers that share the city core are a separate, generated
set under `models/city/` (see `tools/gen_buildings.py`).
