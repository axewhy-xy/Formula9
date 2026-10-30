# BE-M2-07 shared 3D scene and spatial contract

Implemented and verified at the contract/package boundary on 27 October 2026.
The complete M2 exercise remains planned; this handoff does not close
BE-M2-08, BE-M2-09 or the web/native rendering tasks.

`contracts/v2/spatial.py` defines the opt-in 2.1 extension and generated JSON
Schemas. Spatial schema `1.0` requires canonical finite body-centre positions,
unit XYZW orientations, L/W/H dimensions, support surface/lane/deck and offset,
route progress/continuity, render asset ID/hash and rigid asset-to-body matrix,
plus collision proxy ID/revision. Snapshot, scenario and envelope pins agree.
Older 2.x snapshots remain valid without spatial fields and do not qualify for
3D readiness. No legacy actor is assigned fabricated height/orientation.
The existing checkpoint store preserves these pins across paused recovery and
rejects actor publications or staged command applications that change them
within an active attempt.

The immutable package in `backend/fixtures/m2_scene` has 13 dimensioned semantic
entities: five shuttles, flat road, a ramp rising 4 m over 20 horizontal metres,
building, stop, depot, two chargers and obstruction. Nine GLBs total 16,736
encoded bytes; eight are mandatory. Optional shuttle detail can fall back only
to the declared mandatory shuttle asset with identical bounds. Original
procedural geometry/materials are CC0, with attribution in the manifest and
GLBs. This small authored blockout is separate from the OSM route-edit fixture
and is not P1 coverage or surveyed geography.

Package pins:

- Scene: `scene-05090d14627653e854482814`
- Spatial: `spatial-05090d14627653e854482814`
- Transform: `canonical-enu-r1`
- Manifest SHA-256: `f03c7ddb81a7a420075d450d92294b22cc2907c8d746864414652ff0e4b5ccef`
- Render profile/importer: `formula9-gltf2-r1` / `formula9-glb-validator-1.0`

`scripts/build_m2_scene.py` reproduces the package, schemas and golden fixtures
without network, artwork tools or GPU. Scene/spatial revisions derive from the
complete manifest blueprint, including asset hashes and operational proxies.
The loader verifies that derivation and retains verified bytes, preventing
filesystem replacement after validation from changing served content. A rebuild
was verified byte-identical. Source and basis/origin semantics are documented
in the [package README](../../../backend/fixtures/m2_scene/README.md).

The CPU validator checks GLB header/chunks, forbidden features/URIs, bounded
buffer views/accessors, finite vertices/normals/UVs, triangle degeneracy, index
range, node hierarchy/transforms, materials and embedded PNG/JPEG decoding.
Nested rigid node TRS is supported; scale/matrix transforms must be baked.
Render bounds must match the declared body/proxy dimensions. Paths are relative
and containment-checked, including after resolution. Import limits are frozen
in [performance](../../performance.md#8-3d-workload-and-spatial-acceptance):
2 MiB encoded / 8 MiB decoded per asset, 65,536 expanded vertices, 196,608
indices, 1,024-pixel textures, 64 assets / 16 MiB encoded per package, 256
entities and a 512 KiB manifest. Unsupported or uncertain content fails closed.

The read-only public authored-content endpoints are:

- `GET /api/v2/scenes/capabilities`
- `GET /api/v2/scenes/{scene_revision}/manifest`
- `GET /api/v2/scenes/{scene_revision}/assets/{asset_id}`
- `POST /api/v2/scenes/{scene_revision}/readiness`

Manifest and asset responses use full SHA-256 ETags and immutable caching.
Assets never enter snapshots. Readiness admits at most 16 KiB of input and
checks spatial/profile pins, every mandatory asset hash and client renderer
readiness. Errors are bounded and do not echo submitted bodies. Missing optional
detail returns explicit fallback IDs; missing/corrupt mandatory assets block
capability/readiness. `world_3d=true` advertises available scene data while
`authoritative_3d_motion`, `local_manoeuvre` and `recorded_perception` remain
false. Successful readiness is explicitly client-reported, not a server-rendered
GPU certification or a mutation of an existing session.

Python, TypeScript and C++ use the same `spatial_snapshot.json` and
`spatial_cases.json`. They verify semantic IDs, proxy/asset bindings, actual
manifest/GLB SHA-256 bytes, cardinal basis conversion, asymmetric markers,
nonzero origin/position, rigid asset offsets, ramp orientation/support offsets
and q/-q. Missing poses, unsupported spatial versions, mismatched revisions,
non-unit quaternions and scale errors fail ordinary training-message ingress.
Package-level checks reject wrong support IDs, proxy revisions and hashes.
Python additionally exercises malformed/nonfinite/oversized geometry, unsafe
paths, cycles, missing mandatory assets, optional fallback, nested GLB nodes,
PNG/JPEG limits, stale content revisions, bounded HTTP input and ETag replay.

Verification:

- Backend: 604 passed, 4 opt-in network integration tests skipped; 67 of these
  tests exercise the new spatial package/HTTP boundary. A pre-existing
  Starlette/httpx deprecation warning remains.
- Web: 211 tests passed across 18 active test files; TypeScript checking and
  production build passed. The existing bundle-size warning remains.
- Native: 141 tests passed from 22 suites, including all four new spatial
  tests. The existing external route integration suite was excluded, matching
  the CTest unit boundary.
- Khronos glTF Validator `2.0.0-dev.3.10`: nine GLBs, zero errors and zero
  warnings. This supplements the deliberately narrower runtime profile check.
- A separately spawned headless Uvicorn process served capabilities and the
  pinned manifest with HTTP 200; no GPU/context setup is required.
- Generated scene/assets/fixtures rebuilt byte-identically; `git diff --check`
  passed.

Reproduce from the repository root (PowerShell):

```powershell
$env:PYTHONPATH = 'backend;.'
python -m contracts.v2.generate_fixtures
python -m scripts.build_m2_scene
python -m pytest backend/tests -q
npm --prefix frontend run typecheck
npm --prefix frontend test -- --exclude '**/.tools/**'
npm --prefix frontend run build
cmake --build build --config Release --target native_unit_tests -- /m:1 /nr:false
& build/Native/Tests/Release/native_unit_tests.exe '--gtest_filter=-TrainingRouteIntegrationTest.*'
```

The managed sandbox required `TEMP`/`TMP` inside the workspace for Python,
Vite and MSBuild. Existing ignored review copies under `frontend/.tools` were
excluded from the frontend run. MSBuild worker reuse stalled in the sandbox;
the verified build used one worker, node reuse disabled and existing dependency
projects (`/p:BuildProjectReferences=false /p:BuildInParallel=false`).

Remaining integration owners: BE-M2-08 must use `validate_snapshot_bindings`
at scene/session load before producing authoritative surface-following poses,
and add bounded spatial indexes/swept checks. BE-M2-09 must bind the complete
route/incident/battery exercise to this package and collect same-tick connected
rendered evidence. Web/native rendering tasks still own their GLB GPU import,
cameras, spatial interaction, graphics recovery and truthful renderer-readiness
reporting. Those runtime gates and the expanded P1 baseline are not claimed here.
