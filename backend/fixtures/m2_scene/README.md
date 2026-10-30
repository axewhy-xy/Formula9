# M2 shared scene package

This is a small authored blockout for BE-M2-07, separate from the existing OSM
route-edit overlay and the fleet actor's planar route fixture. It supplies five
asymmetric 4.8 x 2.0 x 2.6 m shuttle bodies, flat road and 20 m / 4 m ramp,
building, stop, depot, two chargers and solid obstruction. Its 13 semantic
entities share nine tiny GLB assets (eight mandatory, one optional colour/detail
tier with the same dimensions). Node names are never entity identity.

All geometry and materials are original procedural work by Formula9
contributors, dedicated under [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/).
There are no third-party meshes, textures, KITTI images or surveyed map claims.
Each manifest asset repeats the license and attribution; the GLB `asset`
metadata also records it. `scripts/build_m2_scene.py` is the editable source.

From the repository root in PowerShell, after installing backend requirements:

```powershell
$env:PYTHONPATH = 'backend;.'
python -m contracts.v2.generate_fixtures
python -m scripts.build_m2_scene
python -m pytest backend/tests/test_spatial_scene.py -q
```

The build uses deterministic sorted JSON and little-endian float32 geometry.
`.gitattributes` pins the manifest/fixture line endings and GLB binary handling
so Windows checkout conversion cannot change the shared hashes.
`scene_revision` and `spatial_revision` contain the first 24 hex characters of
SHA-256 over the complete manifest blueprint, with those two revision values
set to `pending`. The SHA-256 of the final manifest bytes is separately pinned
in capabilities, readiness and the spatial snapshot. Mesh, proxy, material,
camera or attribution changes require a rebuilt revision. The loader verifies
this relationship as well as every mandatory asset's full SHA-256.
The server canonicalizes manifest JSON before hashing and serving it, using
sorted keys, two-space indentation and one LF terminator. Reformatting the
source file therefore preserves the immutable URL's bytes, ETag and handshake.

The pinned `formula9-gltf2-r1` importer uses GLB 2.0 with indexed triangles,
float32 positions/normals/UV0, uint16/uint32 indices, opaque basic metallic
roughness materials, and optional embedded PNG/JPEG images. The generated
assets use flat colours and no textures. There are no URI fetches, extensions,
compression, skins, morph targets or animation. Nested rigid node TRS is
supported; node scale/matrices must be baked before publication. Profile
violations have named diagnostics. This is a constrained shared profile, not a
general-purpose glTF importer. The normative format is the
[Khronos glTF 2.0 specification](https://github.com/KhronosGroup/glTF/blob/main/specification/2.0/Specification.adoc).

Raw assets use glTF metres/Y-up. The row-major `asset_to_body` matrix maps each
asset to body X-forward/Y-left/Z-up exactly once; all dimensions are baked.
Published `position_m` is already the body centre in canonical coordinates.
`geographic_origin` is an illustrative latitude/longitude/altitude anchor;
`local_origin_m` is its nonzero canonical coordinate. Neither is an extra
translation to add to published poses. Floating-origin presentation must keep
its own explicit offset and reverse it for picks.

Road slabs are `ROAD_SUPPORT`, solids are `BLOCKING`, and the stop marker is
`NON_OPERATIONAL`. Road surfaces publish path samples, normals, lane/deck,
directed edges, width, clearance and permitted transitions. The
[BE-M2-08 controller](../../../docs/milestone_status/M2/be-m2-08-status.md)
now follows these surfaces and excludes road supports from blocking sweeps.
This development fixture is smaller than P1 and is not performance-acceptance
evidence.

M2 support paths occupy the slab's local +Z face. Admission checks align the
normal and contain each segment's full-width strip in the slab's length/width,
with a shared 0.0001 m tolerance. Python, web and native run the same manifest
admission cases in `contracts/v2/fixtures/spatial_manifest_cases.json`, including
rotated slabs, missing fields and omitted optional fallback IDs.
