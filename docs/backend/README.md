# Backend delivery plan: M2–M6

Status: proposed implementation plan for the approved shuttle fleet manager training direction, 20 October 2026. These documents describe work to build and verify; they do not claim the features or performance targets already exist.

The backend owns the training exercise: fleet state, route validation, incidents, charging resources, command outcomes, evidence, and scoring. Web and native clients are equal participants in one authoritative session. The user being trained makes operational decisions; developing an autonomous driving policy is no longer the product objective.

## Read alongside

- [Technical architecture](../architecture.md): ownership, contracts, persistence, replay provenance, and deployment.
- [Mandatory 3D contract](../three_d_simulation.md): shared scene package, canonical poses, support/clearance/collision geometry and asset readiness.
- [Performance and reliability specification](../performance.md): authoritative workload definitions, measurement procedures, and budgets.
- [Integrated milestone plan](../milestones.md): cross-team sequencing and demonstration gates.
- [Web application plan](../web/README.md) and [native application plan](../native/README.md): clients consuming the same capabilities.

If this plan and a shared contract disagree, resolve the contract before implementation. Do not create client-specific route validators, scoring rules, or fleet clocks.

## Delivery sequence

| Milestone | Backend outcome | Integration evidence |
| :--- | :--- | :--- |
| [M2](m2.md) | Five-shuttle exercise foundation with one route change, incident, reserve/charging choice, and provisional score/log | Both clients operate the same session and recover the same applied decision |
| [M3](m3.md) | Complete training workflow MVP: pre-trip, incidents, charging, authoring, score and export | A complete shift can be assigned, completed, reviewed, and replayed |
| [M4](m4.md) | Richer authored exercises, replay comparison, validated constraints, and debrief patterns | Several acceptable decisions yield explainable, reproducible outcomes |
| [M5](m5.md) | Feature-complete service with measured latency, resilience, and bounded resource use | Load and failure tests demonstrate budgets and recovery semantics |
| [M6](m6.md) | Frozen release candidate with migration, offline operation, and reproducible acceptance | A clean machine completes the packaged assessment without developer intervention |

## Existing foundation and migration

3D extension, 27 October 2026: all original tasks and handoffs remain intact; each milestone adds BE-Mn-07 through BE-Mn-09 and additional 3D exit criteria. The backend stays headless but owns 3D world semantics, not just planar routes. Both clients consume one scene/spatial revision; their renderers cannot independently determine fit, collision or visibility. New tasks remain planned until their own evidence is captured.

The current implementation provides reusable foundations, not the finished training runtime:

- `backend/src/services/session_manager.py` maintains an in-memory playback session, one playback task per session, and bounded subscriber queues. Extract reusable lifecycle and fan-out behavior; add an authoritative multi-vehicle training actor and durable state.
- `backend/src/services/route_planner.py` computes independent route previews against the road graph. Extend this into revisioned operational route plans; keep expensive computation outside the simulation step.
- `backend/src/routing/road_graph.py` builds directed roads from the scene database and sibling OSM tags. Add curated operational metadata and explicit validation for missing or unsupported constraints.
- `backend/src/services/scene_repository.py` reads the static scene database. Keep this asset read-only and store sessions, commands, events, and reports in a separate runtime database.
- KITTI ingestion, normalization, map alignment, and recorded playback remain useful as explicitly sourced replay evidence. They cannot supply camera frames for a newly generated route.
- Existing tests cover route planning, scene access, trajectory playback, and data conversion. Preserve relevant coverage and add training-domain tests; no passing result is asserted by this plan.

The initial deployment is a FastAPI modular monolith with one authoritative actor per session, a bounded durable writer, and bounded process workers for route search, previews, scoring batches, and exports. Do not add distributed infrastructure until a measured capacity limit justifies it.

## Shared engineering obligations

1. Every operational mutation has a command ID, authenticated actor identity, expected target-aggregate revision, durable outcome, and rejection reason. Idempotency is scoped by authenticated actor, session, and key; option approval also binds the exact option revision and expiry.
2. Durable `QUEUED` acceptance precedes its acknowledgement. Queued work may still become `REJECTED` at application if its preconditions changed. State and `APPLIED`/`REJECTED` outcome events commit before they become visible.
3. Fixed 20 Hz logical simulation and 10 Hz presentation snapshots are distinct from rendering and wall-clock decision time. Pausing and speed changes must not distort trainee timing metrics.
4. Recovery uses a persisted checkpoint plus ordered events/commands and a new connection epoch, then exposes the session as `PAUSED`. Old queued approvals become `NEEDS_RECONFIRMATION` or expire unless explicitly safe and revalidated; applied commands are deduplicated after restart. Exactly-once network delivery is not assumed.
5. Fleet snapshots may be coalesced. Incident, command, route approval, charging, and scoring events must remain recoverable by sequence number.
6. Every replay segment identifies its actual source, scene/transform revision, run/branch, and time range. Previously simulated history remains simulated history even when replayed.
7. Failures in optional assets, exports, previews, and slow clients must not stall the authoritative simulation.
8. Training rules and score weights are versioned scenario data. A simulator rule exception is not authorization to operate a real shuttle.

## Performance scope

P1 is the initial shared acceptance workload: five shuttles, three route patterns, two chargers, a 5,000-directed-edge scene, and two paired clients at 1× simulation speed. These numbers define a benchmark; the mockup values do not become fixed product limits.

M2 locks hardware, datasets, and instrumentation. M3 must meet P1 budgets; M4 must preserve them while adding functionality. M5 measures P2 stress separately: 50 shuttles, 10,000 directed edges, and ten subscribers in one session. P2 correctness, recovery, and bounded-resource checks are mandatory; its speed targets are indicative, with measured supported capacity and degradation reported explicitly. M5 includes a two-hour soak; M6 repeats a four-hour soak on frozen packages.

Each milestone produces fixtures, a contract version, an integration recording or trace, test results, and a measurement report. Timing results include sample counts, cold/warm conditions, percentile calculation, and failures; averages alone cannot pass a gate.
