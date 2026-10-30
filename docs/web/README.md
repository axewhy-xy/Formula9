# Web application delivery track

Status: proposed implementation plan, 20 October 2026. This directory replaces the previous decision-agent web milestone plan for M2–M6.

Formula9's web application is a shuttle fleet manager training client. Its primary workflow is preparing a shift and resolving route disruptions; fleet monitoring, charging decisions, incident handling and debrief support that workflow. The approved product direction takes precedence over the previous AV engineer/policy-comparison framing.

The primary operational workspace is a mandatory three.js 3D world from M2, not a flat map with an optional 3D tab. Forms, timelines, lists and minimaps support it. Use the same scene manifest, canonical poses and operational geometry as native/backend; visual quality may differ but spatial identity and outcomes must agree.

## Delivery sequence

| Milestone | Integrated outcome | Detailed plan |
| --- | --- | --- |
| M2 | Complete small training exercise across all five product surfaces | [M2](m2.md) |
| M3 | Usable, persisted fleet training MVP with route and charging workflows | [M3](m3.md) |
| M4 | Rich scenario authoring, comparative debrief and controlled incident complexity | [M4](m4.md) |
| M5 | Feature-complete application that survives load, interruption and accessibility checks | [M5](m5.md) |
| M6 | Frozen beta, observed user validation and reproducible release evidence | [M6](m6.md) |

Architecture decisions, protocol ownership and performance definitions belong in [architecture](../architecture.md), [the integrated milestone plan](../milestones.md) and [the performance contract](../performance.md). The [backend](../backend/README.md) and [native](../native/README.md) tracks implement the same scenario and command semantics.

Each numbered task follows the backend format: **Output** identifies the artifact to deliver, and **Verification** defines its task-level exit criterion. Retain test/measurement evidence against the milestone build; these criteria are planned checks, not claims of completed tests. Task completion does not replace the milestone-wide exit evidence. Conditional manoeuvre/media tasks follow their stated capability and scope gates, including an explicit disabled/deferred outcome where permitted.

## Current code to retain and evolve

3D extension, 27 October 2026: follow the [mandatory 3D contract](../three_d_simulation.md) and [3D workload/spatial gates](../performance.md#8-3d-workload-and-spatial-acceptance). Existing tasks and handoffs remain unchanged; each milestone adds WEB-Mn-07 through WEB-Mn-09 with cumulative 3D exit criteria. New tasks remain planned until verified. Optional detail/media and accessible fallback do not waive baseline 3D acceptance.

The following is a source inspection, not a fresh test pass or a claim that the proposed features exist.

| Existing foundation | Reuse or migration |
| --- | --- |
| `frontend/src/app.jsx` | Split the current setup/viewer component into fleet, route editor, incident, charging, debrief and trainer features; migrate touched modules to TypeScript. |
| `frontend/src/api/simulation_client.ts` | Retain stale-connection callback protection and injectable transport tests; add a separate v2 session/command/event client instead of widening the M1 playback contract indiscriminately. |
| `frontend/src/api/scene.ts`, `route.ts`, `coordinates.ts` | Reuse cancellation, scene loading and canonical-coordinate validation; add graph revision, route draft and authoritative validation contracts. |
| `frontend/src/renderer/*` | Retain the framework-independent three.js adapter, camera control, geometry ownership and disposal; extend from ego playback to indexed fleet entities and route/incident layers. |
| `frontend/src/simulation_timeline.js` | Replace unbounded history and shared playback assumptions with a bounded incident history cursor independent of the live shift. |
| `frontend/src/audio/*` | Reuse audio lifecycle support for optional semantic alerts; remove automatic hover sound from the training workflow. |
| Existing Vitest fixtures and tests | Keep meaningful M1 regression coverage and add v2 contract, conflict, replay-boundary and workflow evidence. |

Recommended module boundaries are `api/v2`, `state`, `workers`, `features/fleet`, `features/routes`, `features/incidents`, `features/energy`, `features/debrief`, `features/trainer`, `renderer` and shared accessible UI components. The energy feature presents battery, charging and swaps. These are implementation destinations under `frontend/src`; this documentation change does not create or rename application source directories.

## Client rules

- The backend owns movement, route validity, battery, charging reservations, command application, score and report content.
- Local drafts and predictions are labelled previews. A button click, HTTP acceptance or route animation never proves that a command applied.
- Every durable command carries an idempotency key and the relevant expected revisions. Show `pending`, `queued`, `applied`, `rejected`, `needs reconfirmation` and `outcome unknown` distinctly.
- Transport retry reuses the original command key. User edits after a rejection create a new command. Reconnect reconciles unknown outcomes before permitting conflicting resubmission.
- Route-preview requests may be cancelled and replaced; sent approvals, decisions and reservations must be reconciled even if the user leaves the view.
- Keep ordered durable events separate from coalescible world snapshots. A missing baseline or event gap triggers explicit resynchronisation; never silently omit an incident or approval.
- Establish worker-based transport parsing, validation and coalescing with bounded queues from M2; profile and optimise the pipeline as load grows. Render interpolated snapshots imperatively without rerendering the entire React tree per tick.
- Use immutable typed stores with selector subscriptions for individual panels. Separate high-frequency positions from low-frequency route revisions, alerts and incident records.
- An incident's last ten seconds are stored simulation history unless individually identified as genuine source recording. A speculative outcome is a separate simulated branch with its own identifier and a visible branch boundary.
- Track per-item `source_mode` and provenance. Branch at the earliest change to the recorded world, including a pre-trip detour or authored obstacle; later incidents use simulation history.
- Recorded KITTI media keeps its original location, timestamps and provenance. It cannot be relabelled as a Singapore camera view or continue as evidence after a simulated deviation.
- Local historical inspection does not pause the shared shift or suppress new live incident alerts. Only an explicit authorised session-control action can change the simulation clock.
- A role-limited trainer, trainee and observer may use separate clients on one session. Backend authorisation remains mandatory even when controls are hidden.
- Keep the UI plain: reason, consequence, uncertainty and next action. Qualitative uncertainty labels identify their source; do not invent calibrated confidence percentages.
- Light and dark themes use at least 14 px body text, non-colour status cues, keyboard alternatives to map editing, persistent stale-state indicators and optional muted-by-default alerts.

## Proposed performance gates

These are engineering targets to measure, not measurements of the existing code. Use the exact fixtures, machine specification, warm-up, sample counts and clock definitions in [performance.md](../performance.md).

| Profile or measure | Proposed gate |
| --- | --- |
| P1 acceptance fixture | 5 shuttles, 3 routes, 2 chargers, exactly 5,000 graph edges; web and native connected together; 20 Hz simulation and 10 Hz state publication at 1× |
| P2 stress fixture | 50 shuttles, exactly 10,000 graph edges and 10 subscribers: two real clients plus eight harness subscribers; use bounded degradation and the separate stress gates |
| Command acceptance | p95 ≤150 ms / p99 ≤300 ms on loopback |
| Applied command visible in issuing client | p95 ≤250 ms / p99 ≤500 ms on loopback; controlled LAN RTT ≤40 ms adds at most 50 ms |
| Visible authoritative state age | p95 ≤250 ms / p99 ≤500 ms at 1× |
| Route validation | p95 ≤300 ms / p99 ≤750 ms; candidate generation p95 ≤1 s / p99 ≤2 s |
| Render frame duration | p95 ≤16.7 ms / p99 ≤33.3 ms on the declared reference machine |
| Cached session reconnect | p95 ≤2 s to a verified current baseline |

M2 instruments these paths and establishes a baseline; M3 meets the P1 functional and latency gate; M4 checks that added functionality preserves it; M5 supplies stress and endurance evidence; M6 reruns the frozen acceptance matrix. Do not compare background-tab frame rates with foreground acceptance results.

## Required handoff

Every milestone delivers source, fixtures, failed-path coverage, a demonstration script, benchmark evidence where applicable and a list of known limitations. Cross-client evidence must show that the same authoritative incident, route, charge reservation and score mean the same thing in both clients.

Complexity is concentrated in state consistency, route editing, scheduling, provenance, responsiveness and recovery. RL, photorealism and perception are not prerequisites for the approved training workflow.
