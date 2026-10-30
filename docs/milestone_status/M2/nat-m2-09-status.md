# NAT-M2-09 mandatory native 3D world

Implemented and locally verified on 28 October 2026. The operational viewport
is now permanently beside the training panels, including fleet, routes and
incidents. The old optional scene tab remains a legacy inspection tool.

## Default session integration, 30 October 2026

Native creation now defaults to `scenario-m2-road-3d-r1` and opens the
operational **3D View** for the newly selected spatial session, allowing Qt to
upload geometry before Start becomes available. Both map views consume the
same session snapshots. The scenario picker also exposes **Integrated exercise
(2D)** (`scenario-m2-integrated-r1`) to retain the full incident/detour/charging
workflow. The short 3D road strip still has incomplete domain workflows tracked
by BE-M2-09. Joining keeps the existing backend session. No renderer starts
a simulation or clock.

Verification: Release App, native unit tests and display runner build. CTest
passes 210 checks with the opt-in live test skipped; that test passes separately
against the isolated authenticated backend, covering spatial creation,
Start/Pause/Resume, matching 2D/spatial shuttle positions and explicit integrated
planar creation. Session-creation and spatial-motion Pytest suites pass all 57
tests. The separate shared-fixture display run renders 13 semantic entities
with zero GL errors; corrupt required geometry blocks readiness. Regression
tests also cover scenario selection, preserving creation settings across lost
replies/login renewal, and resetting readiness when joining a new session.

## Implementation

- `TrainingScene` implements the pinned `formula9-gltf2-r1` profile with Qt
  JSON/image decoding, strict duplicate-key admission and explicit little-endian GLB accessors. It verifies
  manifest/required-asset SHA-256, version/profile/basis, bounded geometry and
  accessors, finite positions/unit normals, indices/triangles, nested rigid
  nodes, basic metallic/roughness materials and embedded PNG/JPEG textures.
  External URIs, extensions, animation, skins, sparse accessors, non-baked
  node scale/matrices and unsupported materials produce named failures.
- The importer checks actual asset bounds, decoded inventory and transformed
  body dimensions against the shared manifest. Optional detail is not fetched;
  entities referencing it use only its declared mandatory tier. Missing or
  corrupt mandatory geometry cannot become ready.
- Typed snapshots retain the spatial reference and body poses. The session
  store rejects changing/dropping spatial pins within an attempt. The view
  validates every shuttle's asset, proxy, dimensions and support binding before
  display. Matrices use `B * T_world_from_body * T_body_from_asset`, with the
  existing shared golden fixtures; no Euler component reordering or extra
  local-origin translation is introduced.
- `TrainingSceneLoader` uses the configured backend's public scene endpoints,
  disables redirects, limits each response while streaming and times requests
  out after ten seconds. One request and one immutable CPU decode run at a
  time. Generation checks discard superseded results; destruction disconnects
  and aborts network callbacks. Workers capture bytes, never widgets.
- `TrainingWorldViewport` uploads in `paintGL`, at most one semantic entity
  per frame. Geometry is shared by asset, and textures by image/sampler.
  Depth-tested perspective rendering uses fixed directional/ambient lighting,
  package camera framing, right-drag orbit and wheel zoom. Scene/body matrices
  live in ECS components indexed by stable semantic IDs. Repaints follow
  snapshots, camera input and readiness/context changes; staged uploads request
  subsequent frames only while entities remain. An idle view has no frame timer.
- Upload/draw failures and binding mismatches clear readiness. Start is gated
  on render readiness for spatial sessions. Status/text controls remain usable.
  The primary world does not display an unrelated flat replacement when assets
  fail. The training shell uses a minimum 14-pixel font.

## Ownership and bounds

GL creation, drawing and destruction require the widget's context and thread;
the runtime guards remain active in Release. Context destruction clears meshes,
textures and shader while current, then stages the same immutable package for
re-upload. The destructor disconnects the context callback before member teardown.
Shaders are embedded resources, independent of the working directory.

The pinned limits are 512 KiB manifest, 64 assets, 16 MiB declared package bytes
(including optional assets), 2 MiB encoded/8 MiB declared decoded per asset,
65,536 expanded vertices, 196,608 indices, 1,024-pixel textures and 256 semantic
entities. There is one retained GPU scene and at most one pending decoded scene;
repeated snapshots coalesce to the latest typed value. CPU decoder temporaries,
32-bit index expansion and texture mipmaps are additional bounded allocations,
not included in the manifest's decoded inventory. This is not a measured P1
memory/performance budget.

## Verification

- Release `Engine`, `App`, `training_world_smoke` and `native_unit_tests` build
  using MSVC 2022 and installed Qt 6.10.2. CMake was pointed to installed Python
  3.14 because the cached Python 3.12 executable no longer exists. The existing
  nanobind package was used for configuration; no source toolchain pin changed.
  Qt deployment emitted the existing `VCINSTALLDIR` discovery warning; this
  local build does not certify clean-machine runtime packaging.
- Original native unit run: **159 passed, one opt-in live-backend test skipped** out of
  160 tests / 24 suites. External `TrainingRouteIntegrationTest` was excluded.
  New tests cover all shared GLBs, mandatory corruption/loss, optional tier,
  bounds/inventory, malformed/oversized input, unsupported features, accessor
  bounds, cycles/scale, nested nodes, embedded texture limits, shared transform
  markers, typed poses, immutable scene pins, 3D creation and Start readiness.
- The explicit display runner serves the exact backend fixture bytes through
  local HTTP and renders the real training shell. It records OpenGL 3.3,
  13 semantic entities, eight mandatory assets, 12,864 declared decoded bytes,
  successful framebuffer capture and zero observed GL errors.
- A second display run corrupts the required ramp bytes. It reports
  `SCENE_REQUIRED_ASSET_HASH`, leaves readiness false and uploads no entities.
- Visual inspection confirms dimensioned road/ramp, building, stop, depot,
  two chargers, five shuttle bodies and the solid obstruction. The sloped
  shuttle follows the ramp orientation. Scale/offset correctness additionally
  uses the shared numerical fixtures rather than pixel comparisons.

Evidence: [primary shell](nat-m2-09-evidence/nat-m2-09-shell.png),
[viewport](nat-m2-09-evidence/nat-m2-09-viewport.png),
[render report](nat-m2-09-evidence/nat-m2-09-render.json),
[unit results](nat-m2-09-evidence/nat-m2-09-unit-results.xml),
[corrupt-asset display](nat-m2-09-evidence/nat-m2-09-corrupt.png),
[corrupt-asset report](nat-m2-09-evidence/nat-m2-09-corrupt.json).

Scene/package pins remain the backend/web shared fixture:
`scene-05090d14627653e854482814`, spatial
`spatial-05090d14627653e854482814`, transform `canonical-enu-r1`, manifest
`f03c7ddb81a7a420075d450d92294b22cc2907c8d746864414652ff0e4b5ccef`.

## PR #88 feedback verification

The follow-up Release build passes for Engine, App, the display runner and both
unit-test executables. CTest reports **169 passed, one opt-in test skipped**;
the native live-backend test and web/native session parity test pass separately
against an isolated database. The web check used local Vitest directly and
excluded archived checkouts under `frontend/.tools`.

Regressions verify the exact planar Create request and independent planar/spatial
Start readiness. Launched from its build directory without plugin-path overrides,
the display runner renders all 13 entities with zero GL errors, records zero idle
viewport paints and one paint each for a snapshot and zoom input. Its corrupt
asset run still blocks with `SCENE_REQUIRED_ASSET_HASH`. These follow-up results
are recorded locally in `.tools/pr88-*`; the linked original evidence is retained.

## Reproduce

From the repository root, with a configured Release build:

```powershell
$env:TEMP = (Resolve-Path .tools).Path
$env:TMP = $env:TEMP
cmake --build build/automated/native --config Release --target Engine training_world_smoke App native_unit_tests -- /m:1 /nr:false /p:BuildProjectReferences=false /p:BuildInParallel=false
& build/automated/native/Native/Tests/Release/native_unit_tests.exe '--gtest_filter=-TrainingRouteIntegrationTest.*'
$env:QT_QPA_PLATFORM = 'windows'
$evidence = (New-Item -ItemType Directory -Force .tools/nat-m2-09-evidence).FullName
& build/automated/native/Native/Tests/Release/training_world_smoke.exe (Get-Location).Path $evidence
& build/automated/native/Native/Tests/Release/training_world_smoke.exe (Get-Location).Path $evidence --corrupt
```

The display runner requires a working desktop OpenGL context; it is deliberately
separate from headless CTest. Its Windows post-build step copies the configured
Qt kit's platform plugin into `platforms` beside the executable, so no
`QT_PLUGIN_PATH` or `QT_QPA_PLATFORM_PLUGIN_PATH` override is required.
Workspace-local TEMP/TMP avoids sandbox failures in existing file-write tests.

## Remaining integrated M2 evidence

This verifies the loader, primary viewport and shared scene package, not a live
paired-client exercise. NAT-M2-10 still owns the complete operational cameras,
semantic picking and equivalent 3D route input. NAT-M2-11 owns authoritative
movement/discontinuity presentation and same-tick live web/native evidence.
The current viewport holds complete authoritative poses without invented
interpolation. BE-M2-09's topology/workflow gaps and NAT-M2-08/11's full-3D P1
baseline remain open; this report does not close the overall M2 milestone.
