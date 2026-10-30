# BE-M2-09 cross-client 3D exercise follow-up

Status on 28 October 2026: **acceptance remains blocked**. This follow-up adds
reproducible prerequisite evidence, a shared motion fixture and a collection
plan. It does not implement the missing route/incident/charging integration or
the two primary 3D renderers, and does not close BE-M2-09 or the M2 exit gate.

## Delivered evidence

- [Acceptance bindings/plan](../../../backend/fixtures/m2_3d_acceptance.json)
  pins the existing scene/spatial/transform revisions and manifest hash, five
  shuttle IDs, road surfaces, stop, visible obstruction, depot and charger IDs.
  Proposed bay support coordinates are explicitly **unadmitted**: a depot mesh
  is not evidence of a drivable road connection. The 120-second camera/input
  sequence is a collection plan, not an executed walkthrough.
- [Shared motion samples](../../../contracts/v2/fixtures/m2_3d_motion.json)
  contain actual backend snapshots at ticks 0, 40, 100, 240 and 600, including
  body poses/dimensions, support attachment, route, state, asset/proxy pins and
  asymmetric asset landmarks. Two independent seeded runs agree at every one
  of 600 steps. Python regenerates these bytes; TypeScript and C++ parse the
  same snapshots and compare adapter output at the exact sampled boundaries.
  These are headless adapter tests, not observations from connected renderers.
- [Acceptance runner](../../../backend/src/services/spatial_acceptance.py)
  audits the current workflow references and writes raw CPU timings with
  nearest-rank percentiles, counts, inventory, source/fixture hashes and machine
  metadata. It can also compare canonical exports from both clients against
  one exact backend snapshot; a different session, epoch, snapshot, tick,
  spatial revision, support/asset identity, route or service/charger state fails.
  Quaternion sign equivalence is supported; interpolation must be disabled.
- [Measured headless baseline](be-m2-09-headless-baseline.json) records this
  small correctness package under `P0_HEADLESS_DIAGNOSTIC`. It measures actor
  compute, snapshot/JSON construction, static indexed queries and CPU GLB
  validation. Package loading is a single filesystem-cache-uncontrolled sample.
  Raw timings retain outliers. Asset decode/upload, HTTP, persistence, tick
  scheduling, both GPU renderers and expanded P1 are explicitly excluded.

## Concrete integration gaps

| Check | Observed boundary | Required next work / owner |
| --- | --- | --- |
| Five spatial bodies | Pass: `shuttle-01` through `shuttle-05` have canonical poses | Retain roster and same-tick identity, BE-M2-09 |
| Three route patterns | One directed route, `route-m2-road-3d` | Author three supported patterns and bind actor assignments, BE-M2-09 + content |
| Route detour | All seven original/detour OSM edges are absent from the scene's two surface edges | Author/version detour surfaces, stop coverage and continuity; integrate approval with actor path replacement, BE-M2-09 + content |
| Incident | Actor scenario is `fleet-fixture`; incident service owns `m2-incident` / `route-m2-detour` / `obstruction-m2`; rendered blocker is `obstruction-01` | Bind one obstruction/incident identity, trigger/options/history and durable decision consequences to the actor, BE-M2-09 |
| Depot/bay access | Depot centre is `[-12, 0, -0.2]`; proposed bays `[-12, -3, 0]` and `[-12, 3, 0]` attach to no reachable authored road path | Add supported access/return topology, bay attachments and body-fit checks, BE-M2-09 + content |
| Charging | Two scene charger entities at `[-15, ±3, 0.9]`; no corresponding runtime charger records in the initial 3D snapshot | Bind reservation, return travel, bay arrival, charging/release and durable outcomes; remove in-place fixture-port assumptions for this exercise, BE-M2-09 |

The depot access audit requires a bay support point on an authored directed path
reachable from the route. This is a necessary prerequisite, not a general
vehicle-fit or swept-route approval. Do not make this report green by renaming
OSM edge IDs, putting shuttles at charger coordinates without driving, or
changing only snapshot labels. Every resulting path/state must belong to the
committed actor/checkpoint and recover with the existing durable semantics.

## Acceptance and remaining evidence

| Gate | Current result | Evidence required to close / owner |
| --- | --- | --- |
| AT-13 | Blocked | Complete route/incident/charging/debrief flow, web and native visibly attached to the same session in navigable primary 3D worlds; BE-M2-09, WEB-M2-07..09, NAT-M2-09..11 |
| AT-14 | Not run in connected clients | Same-tick rendered poses, body extents, asymmetric anchors, exact semantic picks and support IDs; WEB-M2-08..09, NAT-M2-10..11 |
| AT-15 bounded M2 | Partial | Existing headless ramp/support/solid-obstruction/swept checks plus connected support/fit and stopping evidence; BE-M2-09 + shared QA |
| AT-17 bounded M2 | Partial | Existing server corruption/readiness checks plus both client render-readiness failures, declared optional fallback and revision rejection; WEB-M2-07, NAT-M2-09 + shared QA |
| Expanded 3D P1 | Not run | Frozen 5,000-edge/three-pattern/200-static-object package, curved road and ramp, two visible release renderers, fixed quality/hardware inventory, five 10-minute trials with raw timing/resource/command evidence; BE-M2-06/09 + content + web/native |

Current package inventory: 13 semantic entities, including five shuttle bodies,
two road surfaces and six other static objects; one building, one obstruction
and one stop marker. It is smaller than P1. No frame-rate, GPU-memory,
dispatch-to-visible or full-workflow P1 result is inferred from its CPU spans.
Missing measurements have no invented cause or latency value. Remediation order
is content/topology -> authoritative workflow integration -> both renderer and
camera/picking tasks -> paired walkthrough and frozen expanded-P1 trials.
These dependencies must close before claiming the acceptance gate.

## Verification on 28 October 2026

- Backend acceptance/motion/scene tests: **194 passed**, with existing pytz and
  Starlette/httpx deprecation warnings. The acceptance tests include regeneration
  of the checked-in samples, obstruction/ramp outcomes, exact-boundary and
  semantic comparison failures, numeric tolerances, duplicate/missing clients,
  bounded capture input, raw timing retention and incomplete-gate exit behavior.
- Web spatial/training contract tests: **75 passed**, including all five new
  authoritative motion samples.
- Release native unit-test target rebuilt; spatial/training contract suites:
  **13 passed**, including the shared motion samples.
- Deterministic sample SHA-256:
  `5827b53403d1543f15382d4a0ffd68faa3666dd4f2520ce90530623488fa01bb`.
- The measured report still records `acceptance_status: BLOCKED` and zero
  connected client observations. These test counts do not certify a renderer.

The baseline was re-recorded from clean commit
`c1733c7752b12eccce6ad302d5ccae991565f354` (`source_dirty: false`) before
running builds or tests. The checked-in report retains the first of two
sequential runs, including all 600 raw tick samples and outliers. Its tick
compute p95/p99/max is **2.2034 / 2.5617 / 3.2151 ms**; the second clean run
measured **2.2724 / 2.5913 / 3.6408 ms**. This host used Windows 11 and Python
3.14.4, rather than the CI Python 3.12 environment. Two-second system CPU
samples immediately before/after the first run measured **5.38% / 10.04%**
busy (second run: **6.53% / 9.85%**). Background activity and filesystem caches
are uncontrolled; these remain small-sample CPU diagnostics.

## Reproduction

From the repository root, with dependencies installed:

```powershell
$env:PYTHONPATH = 'backend;.'
$env:PYTHONDONTWRITEBYTECODE = '1'
New-Item -ItemType Directory -Force .tools/be-m2-09-temp | Out-Null
$env:TEMP = (Resolve-Path .tools/be-m2-09-temp).Path
$env:TMP = $env:TEMP
python -m src.services.spatial_acceptance --write-motion-fixture --report .tools/be-m2-09-report.json --headless-only
python -m pytest backend/tests/test_spatial_acceptance.py backend/tests/test_spatial_motion.py backend/tests/test_spatial_scene.py -q -p no:cacheprovider --basetemp .tools/be-m2-09-tests
npm --prefix frontend test -- src/api/spatial_contract.test.js src/api/training_contract.test.ts --exclude '**/.tools/**'
cmake --build build/automated/native --config Release --target native_unit_tests -- /m:1 /nr:false /p:BuildProjectReferences=false /p:BuildInParallel=false
& build/automated/native/Native/Tests/Release/native_unit_tests.exe '--gtest_filter=SpatialContractTest.*:TrainingContractTest.*'
```

The existing configured native build here is `build/automated/native`; use your
configured build directory on another machine. The managed sandbox needs TEMP
and TMP inside the workspace. The runner's default exit code is **2** while
acceptance is incomplete. `--headless-only` explicitly limits successful exit
to prerequisite collection; the report still says `BLOCKED`. An out-of-tolerance
numeric client comparison exits **1**, including with `--headless-only`.

To refresh the measured baseline, start from a clean commit and run collection
before builds or tests, writing `--report` to an ignored `.tools/` path. Omit
`--write-motion-fixture` for this measurement and verify `source_dirty: false`
in the generated report before copying it to
`docs/milestone_status/M2/be-m2-09-headless-baseline.json`. Copying the result
then dirties the checkout as expected. The motion fixture and baseline report
are pinned to LF by `.gitattributes` so regeneration preserves checkout bytes
on Windows as well.

For a paired numeric capture, pass `--observations <bundle.json>`. The bounded
(1 MiB) JSON bundle contains `snapshot` (one backend FullSnapshot) and
`observations` (exactly one `web` and one `native` record). Each record carries
`client`, `session_id`, `session_epoch`, `tick`, `snapshot_id`, `scene_revision`,
the full `spatial` reference, `interpolation: "DISABLED"`, `chargers` (the current
charger records) and `vehicles`. Each vehicle carries `vehicle_id`, `route_id`,
`route_revision`, `service_state`, all spatial pose/dimension/support/asset/proxy
fields and `render_marker_m`, the actual renderer-space location of asset vertex
`[2.1, 0.8, -0.6]`. The comparator checks canonical position <=0.01 m,
orientation <=0.1 degree, half-extents/support <=0.02 m and marker alignment
<=0.01 m. This collector does not perform ray picking: controlled hit IDs and
<=0.02 m inverse-hit errors require separate rendered evidence.

A numeric pass remains `NUMERIC_OBSERVATION_ONLY`; it does not authenticate
that a capture came from a visible renderer, prove a live connection, or close
any rendered gate. Retain video/screenshots, connection identity, test results,
camera/input traces and raw P1 measurements with the eventual acceptance run.
