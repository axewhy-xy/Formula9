# Performance, capacity and resilience acceptance

Revision 1.1, 27 October 2026: mandatory 3D workloads and spatial checks added without relaxing the existing latency/resource targets. **All numbers below are proposed engineering targets, not measurements of the current application or instructor-approved limits.** M2 records the reference hardware and baseline; M3 must pass the P1 workflow gates; M4 preserves them; M5 measures stress/endurance; M6 repeats release acceptance.

This file is the numerical source of truth for [architecture](architecture.md), [3D contract](three_d_simulation.md), [integrated milestones](milestones.md), [backend](backend/README.md), [web app](web/README.md) and [native](native/README.md). Changes require a versioned rationale and rerunning affected measurements. Do not hide a regression by changing workload or hardware without reporting it.

## 1. Reproducible profiles

| Profile | Dataset and activity | Purpose |
| --- | --- | --- |
| P0 correctness | Small hand-checkable graph with one-way, forbidden-turn, closure, no-route and charger-conflict fixtures | Determinism, boundary conditions and reason correctness |
| P1 acceptance | 5 shuttles, 3 service patterns, 2 chargers, 5,000 directed edges, one active session, one real browser + one real native client, 20 Hz simulation, 10 Hz snapshots, 1x shift speed | Required normal-operation latency and UX |
| P2 stress | 50 shuttles, 10,000 directed edges, one active session, 10 subscribers (the same two visible clients plus 8 protocol harnesses), 20 Hz simulation, up to 10 Hz snapshots | Capacity and bounded degradation |
| P3 fault/endurance | P1 plus defined interruptions, repeated runs, editing, charging, export and resource churn | Recovery, durability and resource stability |

P1/P2 are frozen versioned graph/scenario packages with manifests and checksums. A smaller map is a development test, not the published acceptance profile. Use curated or deterministically generated benchmark graphs when the current OSM scene has a different edge count; distinguish generated benchmark geometry from real map coverage.

P1 activity: two active incidents at peak; route edit/validation once per second during a 60-second editing burst; two submitted operational commands per second during command measurement; one background report; one incident replay/camera asset visible. Traffic is scripted and repeatable. M2 may use one incident while establishing the baseline; M3 and later use the complete profile.

P2 activity: ten active incidents, four concurrent preview jobs offered, ten commands per second in a 60-second burst, one report, repeated attach/detach. Record all rejected/queued work rather than silently dropping load. Eight harness subscribers exercise protocol capacity; they are not evidence that ten GPU clients run on the same machine.

Both visible clients render the mandatory 3D world throughout these workloads. Section 8 fixes the additional render-content/camera/spatial evidence; graph edge count alone is not a 3D benchmark. Existing pre-3D timing reports remain historical baselines and cannot establish acceptance of the expanded workload.

### Reference machine and network

M2 records exact CPU/core count, RAM, GPU/VRAM, SSD, Windows build, power mode, display size/scaling, driver, browser build, Python/Qt/C++/Node versions, source/build IDs and dataset hashes. Initial planning class: 6 physical CPU cores, 16 GiB RAM, SSD and a discrete GPU with 4 GiB VRAM; this is a proposed test class, not the user's measured hardware.

Run a release backend and native build plus production web build, with both foreground clients visible at 1920x1080-equivalent viewport and declared pixel ratio. Record browser process memory separately from GPU-process memory. No debugger, hot reload, artificial VSYNC changes or unloaded native view in the acceptance run.

- Loopback: all three components on the declared machine.
- LAN: controlled network with measured RTT <=40 ms and no induced loss. Command-visible and state-age budgets may add at most 50 ms to loopback limits; client frame budgets stay unchanged.
- Degraded network: 150 ms RTT, 30 ms jitter, 1% packet loss and a 10-second outage. P1 speed gates do not apply here; explicit stale state, correct command reconciliation and recovery do.
- Background browser tabs, minimized native windows and power-saving mode are separate lifecycle tests, not foreground frame benchmarks.

## 2. P1 latency gates

All normal-operation end-to-end quantities include request validation, queuing, transport and rendering where applicable; internal span timings are diagnostic.

| Metric | Definition | p95 target | p99 target |
| --- | --- | --- | --- |
| Tick computation | Actor compute time per 50 ms step, excluding intentionally awaited durable I/O; report commit and lag separately | <=10 ms | <=25 ms |
| Tick schedule lateness | Actual start minus intended wall schedule at 1x | <=10 ms | <=25 ms |
| Durable acceptance | Issuing client sends command -> receives persisted `QUEUED` or final rejection | <=150 ms | <=300 ms |
| Applied result visible | Client sends an immediate command -> issuing client presents its committed `APPLIED`/`REJECTED` result | <=250 ms | <=500 ms |
| Fleet state age | Age of newest authoritative sample actually displayed, including interpolation delay, at 1x | <=250 ms | <=500 ms |
| Input feedback | Pointer/key event -> next visible draft/selection/feedback update; not server acceptance | <=50 ms | <=100 ms |
| Route validation | Request -> complete reason-coded validation of the specified draft | <=300 ms | <=750 ms |
| Candidate search | Request -> usable validated candidate set or explicit no-route result | <=1 s | <=2 s |
| Visible frame interval | Presentation-to-presentation interval during active interaction/animation | <=16.7 ms | <=33.3 ms |
| Cached view transition | Navigation -> usable fleet/incident/editor/energy/log controls, cached data | <=150 ms | <=300 ms |

Additional gates:

- No silent tick loss, missed durable event, duplicate effective command, wrong vehicle assignment or hidden source-mode substitution.
- At least 99.5% of supported P1 requests finish with a usable response inside their deadlines. Correct business rejections are usable responses; timeout/transport errors are not.
- Candidate search has a 2-second server deadline including queueing. A timeout returns a structured result, not an unvalidated path. Timeout rate still counts against the gate; censoring timeout samples cannot make p99 pass.
- Cached reconnect: p95 <=2 seconds from transport availability to a verified current baseline and enabled valid controls, over at least 50 reconnects. Asset downloads are not required to restore the basic controls.
- Cached history seek: p95 <=150 ms; uncached bounded history range p95 <=750 ms for a 10-second window.
- Cold bundled P1 scene ready: p95 <=5 seconds after client process/page readiness, measured separately from executable/process startup. Readiness requires verified mandatory low-detail 3D assets, populated fleet, working selection/camera and correct scene identity, not merely a loaded 2D shell. Optional detail streaming is measured separately.
- Report for a 60-minute exercise with <=1,000 incidents: p95 <=3 seconds, hard deadline 10 seconds, asynchronous without blocking control.

The immediate-command metric excludes the duration of a deliberate future schedule or driving manoeuvre. It includes committing that schedule. Separately measure eventual execution start/completion and lateness against the scheduled simulation tick.

A 20 Hz tick budget alone does not establish responsiveness. Report durable write latency, publication latency, real-time factor and oldest committed-state age alongside CPU timings. P1 real-time factor must remain >=0.99 over each measured minute at 1x, with no unexplained backlog.

## 3. Measurement method

1. Warm the frozen fixture for 60 seconds; capture startup separately.
2. Run at least five 10-minute trials. Use monotonic clocks and retain raw timings, counts, errors, queue high-water marks and run metadata.
3. Collect >=1,000 command/validation samples per reported class across the trials, >=50 reconnects and all frame/tick samples. Do not mix cold/warm or success/error classes into a convenient average.
4. Use nearest-rank percentiles; show per-run and aggregate results, sample sizes, max, timeout rate and significant outliers. Report uncertainty for small samples.
5. Measure send-to-visible latency on one client's monotonic clock. A trace ID/event cursor proves which authoritative response the frame contains.
6. For state age across clocks, estimate server/client offset by repeated timestamp exchanges, log RTT/offset uncertainty and include that uncertainty in the result. Also report time since last received/applied sample. Unsynchronized UTC subtraction is not a valid latency measurement.
7. Measure frame intervals plus CPU update time and GPU time where supported. A 60 FPS average can conceal long freezes; preserve long-frame/stall distributions.
8. Measure both clients concurrently; run each alone additionally to locate contention. Independently replay the same action script and compare discrete decisions/metrics, excluding wall-clock and transport fields from simulation hashes.

Store acceptance artifacts under a future `artifacts/acceptance/<build>/<profile>/` output location, excluded from normal source history as appropriate: `manifest.json`, raw timing files, summary, trace IDs, test results, screenshots/video, and an issue register. These are future implementation outputs, not artifacts generated by this documentation task.

## 4. Initial resource limits and overload

Limits are configuration defaults to benchmark and tune at M2. Expose usage/high-water marks. Reject excess work with a reason and preserve the last committed state.

| Resource | Initial bound | Exhaustion response |
| --- | --- | --- |
| Pending operational commands | 128 per session; bounded request body 32 KiB | Explicit queue-full/retry response before acceptance |
| Route/preview jobs | 2 executing per session, 4 queued; shared process pool <=2 workers initially | Cancel obsolete previews; reject excess; never cancel an applied command |
| Export jobs | 1 executing per deployment, 4 queued | Job queue limit with retry guidance |
| Reliable outbound events | 256 or 1 MiB per subscriber, whichever first | Resumable disconnect; replay durable log after reconnect |
| Fleet telemetry buffer | 2 complete snapshots per subscriber | Coalesce oldest; delta baseline mismatch forces full snapshot |
| Telemetry payload | P1 <=64 KiB, P2 <=256 KiB per full snapshot | Move static geometry/media out; split only with explicit consistency semantics |
| Client live/history buffer | 30 seconds of live snapshots; selected incident windows; total <=64 MiB | Evict least-used history, fetch older ranges from backend |
| Scene/media cache | <=256 MiB decoded CPU cache per client initially | LRU eviction; cancel unneeded decode; textual views remain usable |
| Server history/checkpoint cache | <=128 MiB per active session | Page from durable storage |
| Backend + calculation workers | <=2 GiB steady working set combined at P1 | Diagnose before expanding workload |
| Browser training workload | <=1 GiB attributable renderer/worker memory | Profile allocations/retainers; report shared GPU memory separately |
| Native process | <=1.5 GiB resident memory | Profile caches/allocations; bounded resource reclamation |
| Estimated GL asset allocation | Web <=512 MiB; native <=768 MiB | Lower detail/upload budget, evict unused assets |

GPU allocation estimates are not exact driver residency; report both when tooling permits. Disk retention is separate from RAM: configure a quota, show remaining space, and pause/reject new work before required audit writes fail. Do not delete completed attempts to rescue a benchmark without a documented retention operation.

Priorities under pressure: durable command/outcome events -> current fleet/incidents -> visible scene -> previews/history/assets -> exports. A slow client cannot backpressure the actor indefinitely. Overload never means silently discarding an incident.

## 5. P2 characterization and supported capacity

M5 must execute P2 and publish measured capacity, queue bounds, admitted/rejected workload and degradation. Correctness/recovery/bounded memory are mandatory at P2; P1 latency is not implied.

Indicative P2 targets: frame p95 <=33.3 ms / p99 <=66.7 ms, visible state age p95 <=500 ms / p99 <=1 second, immediate applied-visible p95 <=500 ms / p99 <=1 second. Candidate/report deadlines stay explicit. Lowering visual detail or snapshot rate to 5 Hz is permitted only with a visible capability/performance state; never lower the authoritative simulation timestep silently.

If these indicative speed targets cannot be reached, report the largest measured supported fleet/subscriber envelope meeting the published profile and the P2 results. A reduction requires a documented scope decision before final presentation; it cannot be described as 50-shuttle low-latency support.

Classroom scale beyond one active session, WAN video streaming and hundreds of vehicles are outside the measured beta capacity unless separately profiled.

## 6. Resilience acceptance matrix

| Injection | Required invariant and evidence |
| --- | --- |
| Lost command acknowledgement / duplicate submission | Same ID resolves one persisted outcome; different payload with same ID conflicts |
| Two clients approve same incident/route/charger | One revision wins; loser sees actionable conflict; no partial assignment/reservation |
| Candidate expires or obstacle/closure changes | Approval revalidates and rejects/refreshes stale work |
| Crash before/after acceptance/application commit | Recover durable state; applied commands never execute twice; queued stale approvals require reconfirmation |
| Subscriber event gap / snapshot baseline loss | Cursor replay/full resync before authoritative controls re-enable |
| Slow consumer or preview storm | Bounded memory, independent other clients, explicit admission/overflow result |
| Disk full/read-only / WAL contention | No false acknowledgement or applied state; pause at last committed state; actionable recovery |
| Missing recording/optional model | Correct unavailable label; valid training controls continue |
| Malformed/oversized payload | Bounded parse failure, no crash or partial domain mutation |
| Unauthorized role/session / reference-answer request | Server denies operation and answer leakage |
| Clock jump / pause / accelerated run | Stable simulation ordering; assessment times identify pauses and clock basis |
| Web context loss / native GL teardown / audio device loss | Controlled recreation or usable fallback; no stale callbacks or repeated alerts |
| Export/trainer edit during live exercise | Export is consistent; published scenario and active attempt do not change |

Recovery after a backend crash is measured separately from socket reconnect: target p95 <=5 seconds to a verified paused P1 session on the reference machine, excluding manual user restart of the process. RPO is zero for acknowledged commands/committed outcomes; uncommitted progress is explicitly discarded.

## 7. Endurance and release gates

- M2: baseline and ownership tests; misses get measured causes and assigned remediation.
- M3: P1 mandatory latency/correctness gates over complete workflows.
- M4: P1 regression and initial P2 characterization with rich content.
- M5: two-hour mixed-workflow P3 soak, P2 matrix, fault suite and feature-complete acceptance.
- M6: four-hour soak on packaged releases, repeated P1 suite, representative P2 limits and clean-machine recovery.

After a 15-minute warmup, compare retained resources after returning to the same idle scene. Allow <=10% residual process-memory growth over the remaining soak and require a stable plateau; caches must remain within configured byte limits. Persistent upward trend, leaked subscriptions, growing trajectory history or unreclaimed GL assets fails even if total RAM has not yet been exhausted.

Each gate delivers a pass/fail report. No critical data loss, silent invalid approval, provenance error, unreproducible assessment, unbounded resource growth or sustained P1 budget breach is accepted as a cosmetic known issue.

## 8. 3D workload and spatial acceptance

BE-M2-07 freezes the development asset profile `formula9-gltf2-r1` at 2 MiB
encoded / 8 MiB decoded per asset, 65,536 expanded vertices, 196,608 indices,
and 1,024 pixels per texture dimension (decoded RGBA budget charged before
loading). A package has at most 64 assets / 16 MiB encoded and 256 semantic
entities; manifests are at most 512 KiB. The capability handshake carries the
asset byte/texture limits and two executing / four queued client decode jobs.
The initial package is a small correctness fixture, not the P1 workload. These
limits bound import/admission; client GPU usage and frame scheduling still
require the active-renderer P1 measurements below.

These additions use the same P1/P2/P3 timing, memory, sampling and soak definitions. They are proposed benchmark fixtures, not claims about a real city or the currently shipped content.

| Content/activity | P1 3D acceptance | P2 3D stress |
| --- | --- | --- |
| Static semantic instances | 200 dimensioned 3D objects, including at least 20 solid buildings/barriers, six stop markers, a depot and two chargers; roads/surfaces counted separately | 1,000 dimensioned 3D objects with the same operational object classes |
| Dynamic state | Five shuttle bodies plus incident obstruction(s), all represented by the authoritative poses | 50 shuttle bodies plus the ten-incident workload |
| Required geometry | Curved road, sloped/ramp section, solid obstruction; from M3 add overlapping road decks and a headroom restriction | Retain all P1 correctness geometry while increasing content/actor density |
| Cameras | Both clients visible in perspective; repeat a frozen overview -> orbit -> follow -> incident -> route-edit script | Same modes through dense-content regions; log quality changes explicitly |
| Review views | From M3, main live view plus one bounded history/preview or synthetic onboard inset; maximum two active 3D viewports per client in the baseline | Same two-viewport cap while other review tabs are suspended or on demand |
| Content lock | Manifest records exact mesh/index/triangle/material counts, texture dimensions/decoded bytes, LOD distances, bounds and light/shadow settings | Separate frozen manifest with the same counters; never claim render load from graph count alone |

M2 may use simple procedural-authored low-poly assets compiled into the shared package. Freeze exact asset inventories and a deterministic 120-second camera/input script at the M2 contract gate; repeat the script during existing five 10-minute trials. Report submitted triangles, draw calls, visible/culled instances, CPU/GPU timings where available and decoded/GPU allocations along the script. No triangle-count inflation is required; resource ceilings and realistic operational content matter more than decorative complexity. Rebaseline explicitly when richer M3/M4 content changes the package, retaining previous results for comparison.

Spatial correctness is evaluated at the **same authoritative tick and scene revision**, with interpolation disabled or its sample time identified. Initial fixture tolerances:

- Canonical-to-render-to-canonical position round trip and body/asset anchor alignment: error <=0.01 m; quaternion angular disagreement <=0.1 degree, accounting for q/-q equivalence. Test nonzero origins, cardinal turns and slopes.
- Pick results: exact entity/surface/deck IDs; inverse-transformed canonical hit error <=0.02 m on controlled fixtures. Do not substitute nearest XY edge when levels overlap.
- Rendered operational body versus declared collision body: extents/support offsets agree within 0.02 m on the curated primitive fixtures; any conservative collision margin is separately recorded and visible in diagnostics, not an unexplained mesh/proxy mismatch.
- Collision/clearance fixture classifications: all required true-contact, separated-level, overhang, invalid-support and swept-obstruction cases produce their expected result. Geometry near a declared numeric uncertainty band is conservative/unknown, not an invented precision guarantee.
- Replay/branch parity: identical discrete entities, attachments and outcomes; poses within declared tolerances at the same tick. No camera operation, LOD setting or render-only animation can change session hashes/commands.

M2 requires transforms, ramp attachment, obstruction stopping, picking and the connected 3D walkthrough; M3 adds multi-level/headroom, authored placement and synthetic camera/replay cases. M4 visibility tests use authored observation origins and versioned proxy coverage; operator-camera visibility cannot substitute for them. Matching lighting pixels is not required.

The existing CPU cache and GPU memory ceilings include scene geometry, textures, comparison render targets and staging buffers; compressed download size is not decoded memory. Start with at most two executing asset decode jobs and four queued per client, with a <=2 ms CPU submission/upload scheduling budget per frame; split or defer large work and report GPU stalls separately. Freeze decoded per-asset byte/texture-dimension limits at M2. Saturation cancels obsolete work or rejects excess without losing mandatory scene state.

P1 quality settings are fixed before measurement, including output resolution/pixel ratio and light/shadow configuration. P2 may reduce shadows, distant detail or optional textures, but must retain dimensioned 3D operational objects, semantic picking and incident evidence. Required occluders/proxies cannot vanish from operational checks because they are culled visually. A flat map, unloaded scene or minimized renderer cannot produce a passing 3D speed result.

Fault tests include corrupt/missing mandatory geometry, unsupported asset features, wrong scene hashes, oversized decoded assets, stale completion after session switch and GPU context recreation. Mandatory failures block scene readiness or spatial actions as appropriate while preserving audit/reconciliation; optional detail uses declared low-detail fallback. The two-second cached reconnect gate assumes required scene assets are already resident/verified; missing mandatory assets require separately reported cold readiness and must not prematurely enable spatial actions.
