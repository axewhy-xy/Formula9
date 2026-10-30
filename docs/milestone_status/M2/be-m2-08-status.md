# BE-M2-08 authoritative road-constrained 3D motion

Implemented at the headless controller and opt-in durable runtime boundary on
27 October 2026. BE-M2-09 still owns the complete route/incident/charging binding,
connected web/native walkthrough, same-tick rendered evidence and 3D P1 baseline.

`backend/src/services/spatial_session_actor.py` extends the existing actor's
50 ms simulation step, control clock, seeded state, pause/resume/reset, energy
and 10 Hz snapshot boundary. `spatial_motion.py` compiles the verified authored
road surfaces and immutable static proxy BVH before ticking. The server needs
no GPU, renderer, terrain sampler or client camera state.

The opt-in HTTP selector is `scenario-m2-road-3d-r1`; existing client creation
buttons keep their planar selector. The 3D actor emits v2.1 snapshots and passes
`validate_snapshot_bindings` at admission. Capabilities now advertise
`authoritative_3d_motion=true`; local manoeuvres and recorded perception remain
unavailable. Successful scene readiness remains client-reported render readiness.
It does not prove that either client's renderer is complete.

## Geometry and authority

The scene pins remain unchanged from BE-M2-07:

- Scene: `scene-05090d14627653e854482814`
- Spatial: `spatial-05090d14627653e854482814`
- Transform: `canonical-enu-r1`
- Manifest SHA-256: `f03c7ddb81a7a420075d450d92294b22cc2907c8d746864414652ff0e4b5ccef`

One directed authored path joins the 40 m flat strip to a ramp rising 4 m over
20 horizontal metres. Its length is `40 + sqrt(20² + 4²)` = 60.39607805437114 m.
Surface topology and coincident endpoints are required; unknown or unsupported
attachments, insufficient width/clearance and unavailable mandatory geometry
fail admission. Paths have at most 64 segments and local coordinates within
±1,000,000 m. A direction reversal without authored reverse topology returns
`UNKNOWN` and holds the fleet.

Each body's forward axis follows the 3D tangent, its up axis follows the support
normal, and its left axis completes the right-handed frame. The canonical
body centre is the support point plus half the declared height along that
normal. A ramp body therefore shifts horizontally as well as vertically.
Snapshots publish the resulting unit XYZW orientation, position, dimensions,
surface/lane/deck, progress, continuity and the unchanged render/proxy bindings.
There is no independent client height or collision calculation.

The five shuttles start at the package's authored poses. The first four brake
and queue before `obstruction-01`; the fifth advances up the ramp, dwells for
two seconds and enters standby at the directed terminal. Driving energy uses
the exact same 3D route-progress delta as motion; auxiliary use and charging
remain `energy-r1`. Gradient-sensitive energy is not included.

## Conservative checks and declared bounds

Static `BLOCKING` proxy boxes enter an immutable median-split 3D BVH.
`ROAD_SUPPORT` and `NON_OPERATIONAL` proxies do not block movement. Spawned
shuttle proxies are replaced by the dynamic fleet index rather than remaining
as static ghosts. A bounded dynamic index is rebuilt from committed/current
body bounds and proposed sweeps each boundary.

Each straight path segment contributes the union of its endpoint body AABBs.
Translation with constant orientation stays entirely inside that volume.
Crossing a corner adds an enclosing rotation volume around its support point,
covering the body and changing support offset. Checks include intermediate
segments, even when both step endpoints are clear. Dynamic sweep overlap
withdraws a movement deterministically, with higher private ID yielding first;
all peers are rechecked against newly stationary bodies before application.

Defaults in `MotionLimits` / `road-controller-r1`:

| Parameter | Bound |
| --- | --- |
| Cruise speed | 4 m/s |
| Acceleration / normal braking | 2 / 4 m/s² |
| Collision inflation | 0.02 m on each vehicle AABB axis |
| Additional stopping gap | 0.10 m |
| Swept pieces per query | 32 |
| Spatial work per tick | 4,096 operations; configuration capped at 16,384 |
| Braking search | 12 iterations; configuration capped at 16 |
| Static/dynamic index inventory | 256 / five bodies |
| Movement-withdrawal passes | fleet size + 1 |

Work counts swept pieces, visited index nodes and candidate box comparisons.
Path scans and each box calculation have separately fixed collection bounds.
Exhaustion returns explicit `UNKNOWN` diagnostics and holds every body before
committing any proposed progress; the logical tick and auxiliary energy still
advance. Normal motion uses acceleration and braking lookahead. A last-resort
swept conflict, unavailable reverse path or exhausted bound forces an immediate
hold; emergency holds are not a physical braking/crash model.
Snapshots expose `spatial_diagnostics` (`CLEAR`, `BRAKING`, `HELD`, `UNKNOWN`),
bounded blocker IDs/reasons and `spatial_work` with used/limit/margin values.

## Persistence and verification

`TrainingSessionService` stages this actor through the existing authenticated,
idempotent command and snapshot transactions. A moved actor is installed only
after its checkpoint/public snapshot commits. Private checkpoint version 2
pins controller settings, surfaces, roster, scene/spatial reference and spatial
diagnostics. Recovery verifies those pins and the public/private poses, starts
a new epoch and exposes the attempt as `PAUSED`. Old planar version 1 attempts
continue to restore through their original actor.

The [deterministic slope/stop trace](be-m2-08-trace.json) records the manifest,
scenario, seed, controller limits, 15 sampled fleet boundaries, support poses,
dimensions, energy, blocker outcomes and hashes. Two independent seeded runs
match at every one of 600 steps. The final shuttle-04 support/centre X is
26.47999103919818 m; its 2.4 m front extent remains before the obstruction's
29 m near face. Shuttle-05 reaches ramp progress 60.39607805437114 m and
body-centre position approximately `[59.745049, 0, 5.274755]`.

Focused regressions independently check the analytic ramp equation and normal,
flat-to-ramp transition, 3D distance/energy, acceleration/braking, dwell/standby,
solid and fast-follower stopping, intermediate sweeps and corner rotation,
height-aware AABBs, support classification, missing topology, spatial work and
piece exhaustion, pause/resume/reset and detached client reads. Runtime tests
exercise HTTP lifecycle with both selectors, durable acceptance/deduplication,
failed-commit isolation, checkpoint corruption, paused restart and resume.

Recorded verification results:

- Full backend suite on Python 3.14: **726 passed, four opt-in network tests
  skipped**. Existing pytz and Starlette/httpx deprecation warnings remain.
- Final focused motion/session/scene run: **181 passed**.
- All **29** new controller regressions also passed on Python **3.12.14**.
- Shared web spatial/training contract checks: **70 passed** across two active
  files, excluding ignored review copies under `.tools`.
- Two 600-step runs matched at every boundary. The independently regenerated
  trace was byte-identical: SHA-256
  `4207a7b0f228d9b0304c7d00cc7e022548c36bcf35d901de8f042aaaf3eb1979`.
- `git diff --check` passed. The scene manifest and all GLB bytes remain at
  their BE-M2-07 hashes; only the server-motion capability/schema changed.

Reproduce from the repository root with backend dependencies installed:

```powershell
$env:PYTHONPATH = 'backend;.'
$env:PYTHONDONTWRITEBYTECODE = '1'
python -m src.services.spatial_session_actor --trace docs/milestone_status/M2/be-m2-08-trace.json
python -m pytest backend/tests/test_spatial_motion.py backend/tests/test_session_creation.py backend/tests/test_spatial_scene.py -q
python -m pytest backend/tests -q
npm --prefix frontend test -- src/api/spatial_contract.test.js src/api/training_contract.test.ts --exclude '**/.tools/**'
```

In the managed Windows sandbox, set `TEMP` and `TMP` to an existing directory
under the workspace before testing. Some existing graph-loader tests use
`tempfile.TemporaryDirectory` independently of pytest's `--basetemp`; the
default sandbox temp directory denied their file writes. The successful full
run used `.tools/be-m2-08-temp` for both variables.

## Model and integration limits

This is centre-support kinematics on planar strips and piecewise-linear 3D
paths, not tyre contacts or suspension. Orientation changes at authored sharp
joins; the collision envelope covers that change, but a rigid body's corners
are not solved against both adjoining road slabs. Terminal body overhang is
allowed by this centre-support model. Smooth contact patches, bank transitions,
multi-level/headroom acceptance and general curved-road fit remain later work.

AABBs and whole-step dynamic sweep overlap are conservative: rotated or long
diagonal bodies, sharp turns and different-time crossings may stop early.
No arbitrary mesh/convex narrow phase, moving trainer-obstacle authoring,
off-road motion, crash response or local passing is implemented. Only the fleet
is dynamic; other operational proxies are immutable for an attempt.

The inherited low-battery state machine uses declared in-place fixture charging
ports. It does not implement travel to the two rendered depot chargers. The
road-block stop has explicit obstruction identity but is not yet bound to the
scripted incident/decision service or detour approval path. Three service route
patterns, depot/bay membership and that complete workflow belong to BE-M2-09.
This trace is headless correctness evidence, not rendered client acceptance or
a claim that the expanded P1 workload meets its latency budgets.
