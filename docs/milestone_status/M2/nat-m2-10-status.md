# NAT-M2-10 operational cameras and semantic 3D input

Status (29 October 2026): native camera and picking adapters implemented;
connected task acceptance remains open. The primary viewport supports overview,
orbit, pan, zoom, selected-shuttle follow and incident focus, with Qt controls
and keyboard input. These controls consume the selected session's accepted
snapshots. They create no session, simulation timer, command or vehicle motion.

## Implementation

- `TrainingWorldInteraction` is a CPU-only camera/picking adapter. Camera pitch,
  distance (5–300 m), pan extent and clip planes (0.1–1,200 m) are bounded.
  Follow and incident focus track complete authoritative shuttle positions;
  manual orbit/pan exits tracking. Home resets to the package overview.
- Picking unprojects the perspective camera ray, performs asset-bounds tests,
  then intersects the imported triangles through each entity's inverse model
  matrix. The result includes the stable entity ID and canonical XYZ metres.
  Road hits additionally require the authored support plane and strip, retaining
  exact surface/lane/deck IDs. An occluding object or upper deck wins by depth;
  off-surface points never become nearest-XY route targets.
- Road surfaces are converted to immutable typed values during package decode.
  Route nodes now retain their canonical Z coordinate. A route handle requires
  matching scene revision, directed-edge membership and supported XYZ on the
  explicitly selected surface. Selecting a surface does not substitute a graph
  from another scene. Missing bindings disable spatial validation and approval.
- Right drag orbits, middle drag pans, wheel/+/- zooms and arrows pan. Left click
  selects a semantic entity; a separate **Edit route draft** mode allows visible
  surface-bound node clicks. Camera gestures, multi-button interruption, focus
  loss and releases after cancellation cannot commit a node edit. Visible draft
  handles use the same geometry for occlusion checks as pointer picking.
- Pointer targets are checked against the editor's current session, epoch,
  scene, route revision, surface/lane/deck and exact canonical node before being
  added to the existing ordered via-node draft. Numeric entry uses the same
  allowed targets. Validation and approval remain the existing backend flow;
  changing a surface binding invalidates a reviewed preview.
- Undo/redo retain at most 100 local draft states, each limited to 512 characters;
  the existing sixteen-waypoint bound remains. Cancel/Escape clears the draft
  and reason. Pending approvals lock draft editing, cancellation and history.
  Disconnect, stale snapshots and incompatible contexts disable spatial edits.
- Qt object/incident selectors and keyboard fleet/incident row selection drive
  the same world selection. Snapshot refresh preserves selected IDs without
  emitting selection feedback. Selected meshes receive a presentation tint and
  a persistent text label; camera mode remains visible in the viewport.
- Existing owner-context GL upload/destruction and bounded scene assets remain
  in effect. Picking reuses immutable decoded assets and retains no ray history.
  There is no idle camera/frame timer.

## Verification and evidence

Release `App`, `native_unit_tests` and `training_world_smoke` build successfully
with MSVC 2022 / Qt 6.10.2. Final CTest: **180 passed, one opt-in live-backend
test skipped, zero failed**, out of 181 registered tests. The external
`TrainingRouteIntegrationTest` suite remains excluded by the repository's
existing CTest filter. Qt deployment retains its existing `VCINSTALLDIR`
discovery warning; this is not clean-machine packaging evidence.

The headless fixtures exercise shared flat/ramp/obstruction hits within 0.02 m,
inverse transforms with a nonzero origin, overlapping decks, scene/edge/height
binding failures, camera bounds/reset/tracking and screen-ray round trips.
Widget checks cover equivalent numeric/pointer drafts, stale pins, unavailable
bindings, undo/redo/cancel, pending-approval locks and selection persistence.

The explicit `training_world_smoke --interaction` display run uses the real
training shell, local HTTP fixture delivery and a real OpenGL 3.3 context. It
checks obstruction identity, orbit/edit separation, a surface-bound pointer
edit, numeric equivalence, undo/redo/Escape, follow updates, incident focus and
disconnected editing. Its route context is **synthetic**, with explicit graph
edges matching the shared strip, so it proves the native adapter without
claiming backend route validation or execution.

All twelve interaction assertions and three framebuffer captures pass. The
runner renders 13 entities/eight mandatory assets, observes zero GL errors,
records zero idle viewport paints and one paint each for snapshot/zoom input.
A corrupt required ramp remains blocked by `SCENE_REQUIRED_ASSET_HASH`.
Visual review confirms the route handle at the shared road seam, selected-shuttle
follow and incident focus. A seam regression explicitly disambiguates the same
XYZ point through the selected surface without changing its height/deck.

Evidence: [CTest results](nat-m2-10-evidence/ctest.xml),
[interaction report](nat-m2-10-evidence/nat-m2-10-interaction.json),
[shell](nat-m2-10-evidence/nat-m2-10-shell.png),
[route draft](nat-m2-10-evidence/nat-m2-10-draft.png),
[follow](nat-m2-10-evidence/nat-m2-10-follow.png),
[incident focus](nat-m2-10-evidence/nat-m2-10-incident.png),
[corrupt-asset report](nat-m2-10-evidence/nat-m2-10-corrupt.json) and
[build/executable hashes](nat-m2-10-evidence/build.json).

The interaction fixture's time offsets are a test plan. These assertion runs
do not execute a timed 120-second workload or establish a P1 baseline.

## Reproduce

With the existing Release build configured for MSVC 2022 and Qt 6.10.2:

```powershell
$env:PYTHONPATH = (Resolve-Path .tools/backend-venv/Lib/site-packages).Path
$env:TEMP = (Resolve-Path .tools).Path
$env:TMP = $env:TEMP
cmake --build build/automated/native --config Release --target App native_unit_tests training_world_smoke -- /m:1 /nr:false /p:BuildInParallel=false
ctest --test-dir build/automated/native -C Release --output-on-failure
$env:QT_QPA_PLATFORM = 'windows'
$evidence = (New-Item -ItemType Directory -Force .tools/nat-m2-10-evidence).FullName
& build/automated/native/Native/Tests/Release/training_world_smoke.exe (Get-Location).Path $evidence --interaction
& build/automated/native/Native/Tests/Release/training_world_smoke.exe (Get-Location).Path $evidence --corrupt
```

The PYTHONPATH is a local configuration workaround: Python 3.14 is installed,
while nanobind resides in the existing backend virtual environment. No pinned
source toolchain or dependency version was changed.

## Remaining acceptance gates

1. BE-M2-09/content must provide the real route graph with edges and canonical
   nodes bound to the 3D scene. The current planar detour graph has different
   edge IDs from `surface-flat`/`surface-ramp`. Native deliberately leaves those
   edits unavailable. Real approval, peer invalidation and committed detour
   execution still need the connected exercise.
2. NAT-M2-05/BE-M2-09 must publish the authoritative obstruction-to-incident
   association. The current incident wire values identify the shuttle but no
   obstruction entity. Picking selects the exact obstruction and explains the
   missing link; it cannot automatically focus a guessed incident. Explicit
   incident selection focuses the correct backend incident's shuttle.
3. Closure placement/edit submission has no supported spatial command in the
   current contract. The static closure remains authoritative and inspectable
   in the route panel. Spatial closure authoring/placement remains unavailable;
   no local mesh movement pretends to apply a closure.
4. Same-scene web/native interaction parity, the shared timed camera script,
   actual fleet workflow execution and the full 3D P1 run remain NAT-M2-08/11
   integration evidence. This follow-up does not close NAT-M2-10 or M2.
