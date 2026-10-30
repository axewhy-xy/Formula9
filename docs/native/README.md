# Native client delivery plan

Status: proposed implementation plan for the approved fleet-manager training direction, 20 October 2026.

The C++20 / Qt 6 application provides the complete dispatcher and trainer workflow: prepare routes, supervise a simulated fleet, resolve incidents, plan charging, and review training outcomes. Its primary OpenGL/ECS operational world is mandatory 3D from M2, with Qt forms/timelines and a supporting map. The backend owns all operational rules and session state. Web has the same required 3D workflows; extra native detail does not create a native-only core action.

## Plan and ownership

| Milestone | Native outcome | Plan |
| --- | --- | --- |
| M2 | Five-shuttle training slice across the five product screens | [M2](m2.md) |
| M3 | Complete training MVP with concurrent incidents, charging swaps, and authored scenarios | [M3](m3.md) |
| M4 | Advanced shift supervision, trainer analysis, and capability-gated manoeuvre inspection | [M4](m4.md) |
| M5 | Feature-complete client meeting recovery and performance gates | [M5](m5.md) |
| M6 | Frozen, packaged beta with cross-client acceptance evidence | [M6](m6.md) |

The [architecture](../architecture.md), [integrated milestones](../milestones.md), and [performance contract](../performance.md) govern shared semantics and acceptance. Task IDs `NAT-Mn-xx` identify native work; backend and app handoffs must use the same schemas, fixtures, command semantics, and scenario revisions.

Each numbered task follows the backend format: **Output** identifies the artifact to deliver, and **Verification** defines its task-level exit criterion. Retain test/measurement evidence against the milestone build; these criteria are planned checks, not claims of completed tests. Task completion does not replace the milestone-wide exit evidence. Conditional manoeuvre/media tasks follow their stated capability and scope gates, including an explicit disabled/deferred outcome where permitted.

## Repository baseline and migration

3D extension, 27 October 2026: follow the [mandatory 3D contract](../three_d_simulation.md) and [3D workload/spatial gates](../performance.md#8-3d-workload-and-spatial-acceptance). Existing tasks and handoffs remain unchanged; additions are NAT-M2-09 through NAT-M2-11, and NAT-M3/M4/M5/M6-10 through -12. Earlier optional ECS/detail wording refers only to enhancements beyond the M2 primary world. Accessible 2D/text fallback remains valuable but cannot pass the normal 3D gate. New tasks require new evidence.

The following are source-inspection findings, not claims that builds or tests have been rerun.

| Existing source | Carry forward | Required change |
| --- | --- | --- |
| `Native/App/BackendClient.*` | Qt REST/WebSocket transport, `QPointer`, request-generation cancellation | Typed contracts, session attach, command lifecycle, durable event resume, bounded snapshot delivery |
| `Native/App/MainWindow.*` | Qt shell, tabs, toolbar | Five-screen training navigation and trainer controls with role-aware actions |
| `Native/App/Phase2_3/map_widget.*` | Pan/zoom, canonical chunks, picking, OpenGL ownership | Fleet selection, route-node editing, disruptions, incident and charging overlays |
| `Native/App/Phase2_3/map_renderer.*` | Local coordinates, road batching, scene levels of detail | Stable fleet entities and semantic route layers instead of a single ego |
| `Native/App/GLViewport.*` | ECS view, pending canonical data, camera and renderer | Typed immutable input, fleet entity registry, capped trails and GPU resources |
| `Native/Engine/Audio/*` | FMOD lifecycle and optional no-audio implementation | Opt-in incident cues, deduplication, rate limits, no routine click sounds |
| `Native/Tests/*` | Headless map/request/time tests and CTest registration | Contract, model, concurrency, command, resource-lifecycle, and integration coverage |
| `Native/App/CMakeLists.txt` | Qt deployment and conditional FMOD DLL copy | Reproducible package with explicit assets and clean-machine checks |
| Root `CMakeLists.txt` | C++20 and Qt targets | Separate native build requirements from optional Python/nanobind dataset tooling |

Introduce native domain/store, transport, view-model, and presentation modules behind the existing shell. The exact folder names are implementation choices; avoid moving unrelated engine code merely to rename it. Old standalone map and dataset utilities become explicit developer tools after the product flow is complete.

## Non-negotiable client boundaries

- Parse JSON into validated value types before it reaches views or renderers; reject incompatible required schemas and preserve useful diagnostics.
- Backend results determine route validity, fleet transitions, charging feasibility, proposal eligibility, scores, and export contents.
- Interpolate displayed positions only. Never predict that an approval succeeded, a shuttle crossed a junction, or a charge reservation was accepted.
- Keep an ordered resumable event stream separate from replaceable fleet snapshots. Snapshot coalescing must not discard incident creation, approval outcome, or audit events.
- Track session epoch, snapshot/base identifiers, event sequence, and entity revisions; a gap requests recovery instead of guessing state.
- Every v2 command has an idempotency key and expected aggregate revisions. Show `QUEUED`, `APPLIED`, `REJECTED`, and `NEEDS_RECONFIRMATION` distinctly; expiry and unknown-after-disconnect also have explicit presentation states.
- Disable operational submissions while disconnected or stale. Reconnect queries command status; after authority recovery, old queued decisions require reconfirmation or expire rather than applying unexpectedly.
- Keep review cursor state separate from live session state. Scrubbing an immutable checkpoint/history cannot move or pause the live fleet or replay historical audio; current live incident alerts and return-to-live remain available.
- Identify recorded, simulated, and derived material per entity and asset; stop current-camera continuity at the earliest world divergence, including a pre-trip detour or inserted obstacle. Later incidents use simulation history.
- KITTI imagery retains its actual sequence and geography. A Singapore training map cannot relabel German footage as a view from its shuttle.
- Keep OpenGL creation, upload, mutation, and destruction on the context-owning thread. Worker tasks return immutable CPU values.
- Cancel requests and invalidate callbacks with session/request generations and guarded QObject references during close, reattach, and view replacement.
- Cap queues and caches by both item count and bytes; bound decoded images, trajectory history, geometry, GPU buffers, and outstanding work.
- Use readable type at least equivalent to 14 px, light/dark themes, keyboard alternatives to dragging, focus indicators, and non-colour status cues.

## Evidence required at every milestone

Deliver a Release build identifier, supported configuration, scenario/fixture revisions, linked task results, and a native/web parity record. Performance evidence records hardware, build mode, scene size, run duration, samples, percentile method, network conditions, and whether assets were warm or cold.

P1/P2 budgets are proposed targets until measured; never present them as existing performance. Measure native frame time, input-to-feedback, acceptance-to-application, authoritative state age, queue high-water marks, resident memory, GPU allocation, and reconnect recovery separately.

Each milestone reserves integration time for paired clients on one backend session. An attractive native screen does not meet an exit gate if its route, command status, charge schedule, incident record, or debrief differs from the app.
