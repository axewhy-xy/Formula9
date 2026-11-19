# Creating and starting a v2 training session

The native Fleet Training tab and the web workspace at `?training=1` can create,
join, start, pause, resume and archive the same server-owned fleet scenario.

## Run locally

From the repository root, with the backend dependencies installed:

```powershell
$env:PYTHONPATH = 'backend;.'
python -m uvicorn src.app:app --host 127.0.0.1 --port 8000 --workers 1
```

v2 session requests must carry the token a user gets by logging in
(`POST /api/auth/login`); accounts live in MongoDB, so `MONGODB_URI` must be set
in the environment or a local `backend/.env` copied from `backend/.env.example`.
Keep the connection string private. Without a token the request is denied (403); an unknown or
expired token answers 401. The actor is the logged-in username; callers cannot
supply an actor ID in a request. Use HTTPS when accessing the server over a network.

Start the frontend normally and open `http://localhost:5173/?training=1`.
Enter a login token and click **Apply credential**, then **Create session**
and **Start scenario**. The token stays in page memory and must be entered again
after a reload. The created session ID is added to the URL.

In the Unity app, log in and click **Create training session** with
**Singapore, real streets (2D)** selected. The app creates and joins the shared
`scenario-m2-integrated-r1` exercise with incidents, detours and charging.
Open **Route planning**, validate the default detour, enter an approval reason,
and approve it for every assigned shuttle before clicking **Start scenario**.
Existing sessions remain available through **Existing sessions** or by joining
their session ID. The login token is kept in memory only, so
every launch logs in again. If login expires during session creation, signing
back in as the same user confirms the original attempt with its original key.
Another account cannot retry an unresolved creation belonging to that user.

Session IDs are readable: the place, a running number, then the server's local
date and time of creation, such as `toa-payoh-007-10nov-1340` for the 7th session,
a Toa Payoh exercise created on 10 November at 13:40 (`fleet-3d` and `fleet-loop`
name the other two scenarios). The running number is stored in the database and
is never reused, even after a session is deleted. Sessions created before this
change keep their older random IDs.

To observe the same run, copy its session ID into the other app and click
**Open session** (web) or **Join** (native). Joining, refreshing, reconnecting and
closing a client do not create or stop an actor.

In the native app, the **Existing sessions** dropdown lists all stored sessions
with their status and ID, placing live attempts first and newest attempts first
within each group. It loads after login and updates when the joined session or
its status changes. **Refresh list** fetches changes made by another client.
Select an entry to join it, then choose **Archive session** to release an unwanted
attempt's slot. The capacity label shows live usage out of 16 and the total
stored sessions; archived history remains selectable without using a live slot.
The existing session-ID field still supports joining a session directly.

Authenticated `GET /api/v2/sessions` returns lightweight public metadata:
`sessions` entries contain `session_id`, `scenario_revision_id`, `status`, and
`occupies_capacity`; `live_count` and `capacity` describe current admission usage.
It lists sessions across accounts, matching the existing shared-session join
and lifecycle access. Listing does not start, archive or otherwise alter attempts.

## HTTP contract

```http
POST /api/v2/sessions
Authorization: Bearer <login-token>
Idempotency-Key: <unique-attempt-key>
Content-Type: application/json

{"scenario_revision_id":"scenario-m2-r1","seed":1000}
```

Returns **201** with a `TrainingSession`, including `session_id`,
`scenario_revision_id`, `status: READY`, `session_epoch`, `aggregate_revision`
and `authority_id`. Fetch `/api/v2/sessions/{session_id}/snapshot` to load the
baseline. The seed defaults to 1000 and must be an integer in 0..2147483647.

`scenario-m2-r1` selects the curated planar fleet-loop factory. The returned
scenario revision is its content-derived pin, covering route geometry, seed and
model assumptions. It is not the illustrative fixture's `scenario-m2-r1` pin.
Unknown selectors fail with 404. Arbitrary scenario authoring is not exposed.

For the BE-M2-08 road-constrained 3D fixture, use
`{"scenario_revision_id":"scenario-m2-road-3d-r1","seed":1000}` with the same
endpoint and credentials. Its snapshot is v2.1 and pins the shared M2 scene,
spatial revision, proxy/asset bindings and authoritative body-centre poses.
This fixture is available through the API; it is not offered in the Unity
scenario picker. Clients can still join existing 3D sessions by ID. Scene capabilities
advertise `authoritative_3d_motion=true`,
while `local_manoeuvre` and `recorded_perception` remain false. This is the
five-body ramp/obstruction foundation, not the integrated BE-M2-09 exercise.
See [the controller handoff](milestone_status/M2/be-m2-08-status.md) for limits.

Retry the identical creation request with the same key after an uncertain
response. The actor identity/key/request hash mapping is committed with the
initial snapshot and survives restart. Reusing a key with different settings
returns 409. A different attempt uses a new key.

Start through `POST /api/v2/sessions/{session_id}/commands` using a v2
`DispatcherCommand`: `command_type: START`, `target_type: SESSION`,
`target_id: session_id`, `payload: {}`, fresh command/idempotency IDs, and the
epoch and aggregate revision from the current snapshot. `PAUSE` and `RESUME`
use the same contract. Command responses use the existing 202 status endpoint
contract; this adapter returns a committed `APPLIED` outcome on success.
An uncertain response, `QUEUE_FULL`, or `SESSION_RECOVERING` must be retried
with the original command and idempotency key. A retry may carry a new command
ID; the server applies and returns the original durably accepted command.

Choose **Archive session** in either client to end an abandoned or finished
attempt and release its runtime slot. It submits `command_type: ARCHIVE` with
the same session target, epoch, revision and retry rules. READY, RUNNING and
PAUSED attempts become ARCHIVED; the actor stops only after the archive event,
command result and final checkpoint commit together. Snapshots, history,
creation keys and command receipts remain readable, including after restart.
Other queued commands expire in that same transaction so peers can reconcile
their outstanding requests after the actor has stopped.
An archived attempt cannot resume; create a new attempt with a new creation key.

## Permanent deletion and disk cleanup

In the native **Existing sessions** dropdown, select an ARCHIVED, COMPLETED or
FAILED session and click **Delete permanently…**. The confirmation identifies
the session and explains that its history will be removed for everyone. Cancel
is the default. Archive a READY, RUNNING or PAUSED attempt before deleting it.
Successful deletion removes the list entry and clears the joined session's
tables, route editor and operational view; it also stops that client's polling.

Authenticated `DELETE /api/v2/sessions/{session_id}` returns **204** with no body.
The backend checks the persisted lifecycle and deletes snapshots, checkpoints,
events, commands, private runtime state and creation keys in one transaction.
Active attempts return **400 INVALID_TRANSITION** without changing any data.
A missing session also returns 204, so retrying a lost response is safe. Later
snapshot, replay and command-status requests return 404. Like listing, joining
and archiving, deletion uses the existing shared-session permissions: any
authenticated operator can delete a terminal session, including another
operator's attempt. There is no undo.

Deleting a session also forgets its creation idempotency key. Repeating an old
creation request after deletion creates a new attempt; prefer a fresh key.
Archiving alone continues to preserve history and retry receipts.

Deletion makes database pages reusable immediately. To return unused space to
the filesystem, archive all remaining live sessions and click **Reclaim disk
space**. This runs SQLite `VACUUM` through the single writer, followed by a WAL
checkpoint/truncation. It preserves all retained sessions and history. It can
take time and temporarily needs free disk space for SQLite's database rewrite.
New session admission waits until compaction finishes; compaction is refused if
any nonterminal session remains, including stored fixtures without a live actor.

Authenticated `POST /api/v2/sessions/compact` exposes the same operation and
returns `database_bytes_before`, `database_bytes_after` (database plus WAL bytes)
and `wal_truncated`. If an existing reader prevents WAL truncation,
`wal_truncated` is false; finish that read and retry to release the remaining
space. A disconnected caller does not cancel a deletion or compaction already
accepted by the service.

To clear history in bulk, click **Delete old sessions…**. After confirmation it
permanently deletes every ARCHIVED, COMPLETED and FAILED session except the 20
most recently created. Authenticated `POST /api/v2/sessions/purge?keep_latest=N`
exposes the same operation with any N of 0 or more and returns `deleted_count`.
Each session is deleted in its own transaction so running sessions keep ticking;
if interrupted, repeat the request to finish. Live attempts are never touched.

For automatic retention, set `TRAINING_RETENTION_KEEP_LATEST=N` in the backend's
environment. Each backend start then applies the same purge before serving
requests. It is off by default because it deletes history without confirmation.
It does not run while the backend stays up, and it does not `VACUUM`: the freed
pages are reused by new history, so the file stops growing but does not shrink.
A retained attempt's own history keeps growing for as long as it runs.

`GET /api/v2/sessions` is paged by `limit` (1-500, default 100) and `offset`, and
also returns `total`, the number of stored sessions. `live_count` always covers
all sessions, and live attempts sort first, so the first page holds them. The
dropdown lists the newest 50 and **Show more** extends it by 50, up to 500. Older
sessions can still be joined by ID.

## Runtime and recovery

`TrainingSessionService` owns one actor/task per session on the API event loop.
Database access and actor staging run in worker threads. A lock per session
orders ticks and lifecycle commands, and an actor is installed only after its
transaction commits. Concurrent creation requests share one admission lock.
Disconnected callers do not cancel accepted operations; shutdown drains pending
commits and actor installations before closing the store.
READY and PAUSED sessions do not advance simulation time. Running actors use
50 ms steps and commit a presentation snapshot every two steps. Lifecycle
events, command outcomes, public snapshots and private actor state commit
atomically through `TrainingStore`. Reads expose only committed public data.
If a runtime fails and persistence cannot record its terminal status, snapshot
reads return `SESSION_RECOVERING` instead of stale `RUNNING` telemetry. The
service retries the failure record with bounded backoff, then exposes the
durable `FAILED` snapshot and releases its runtime slot. Previously committed
command outcomes remain reconcilable during this interval. Create a new attempt
after terminal failure.

Migration `0002_live_sessions.sql` adds private actor checkpoints and durable
creation keys. Private checkpoints include shuttle progress, dwell/direction,
battery, control clock and RNG state; they never enter HTTP snapshots. On
restart the store increments the epoch and marks queued decisions for
reconfirmation. Unstarted READY attempts remain READY so pre-trip approval and
Start are still available; previously active attempts recover PAUSED and require
an explicit Resume. Earlier integrated pre-trip checkpoints incorrectly marked
PAUSED are restored to READY when their verified private state is still unstarted.
The service validates and restores actor state during application startup. Downtime
does not advance simulation time. A corrupt, missing or incompatible private
actor checkpoint marks only that attempt FAILED with a `CORRUPT_CHECKPOINT`
reason in its failure event. Healthy live attempts and unrelated fixture
sessions remain available.

The 3D actor uses private checkpoint version 2, adding its controller settings,
surface/vehicle bindings, full spatial pins and bounded spatial diagnostics.
Restore verifies the same package and content-derived scenario before exposing
a paused attempt. A missing package or incompatible spatial revision cannot
silently recover against planar geometry. The version 1 planar checkpoint
continues to be supported.

Run one authority worker against the SQLite store. The service admits at most
16 live attempts. RESET is explicitly rejected for this adapter; create a new
attempt instead. Archive an old attempt before admitting another when full.

### Session worker processes

By default the backend ticks live sessions in **session worker processes**, so
they are no longer limited to one core by the GIL. `F9_SESSION_WORKERS` sets how
many: `auto` (the default) is the machine's physical cores less two, within 1–8;
a number sets it exactly; `0` runs every session in the backend's own process,
as before. Tests and `run_backend.py --reload` use `0` unless told otherwise. If
the workers cannot start, the backend logs why and runs sessions in its own
process.

The backend process stays the only one that serves HTTP and writes SQLite. The
HTTP API, the snapshots and the Unity client are unchanged. A new session goes
to the least-loaded worker; when every worker's measured tick load would pass
60% of a 50 ms tick with it, creation answers `QUEUE_FULL`, as the fixed limit
of six sessions with traffic did before. If a worker process dies or stops
responding, its sessions answer `SESSION_RECOVERING` briefly and come back
**paused, in a new epoch, from their last committed checkpoint** on another
worker, exactly as after a backend restart; commands already applied keep their
single outcome. Sessions on other workers keep running. Each worker exits when
the backend process is gone. With the bundled backend's log file, each worker
writes its own `backend.worker-N.log` beside it.
Scenario completion and assessment remain future work.

The original `scenario-m2-r1` selector uses the existing five-shuttle **planar loop fixture**.
It does not enable authoritative 3D motion, curated road-detour editing,
scripted incident execution or shared charging reservations. Existing
incident/route/battery fixtures and endpoints remain separate integrations.

## Integrated BE-M2-02 through BE-M2-05 backend exercise

Create a new attempt with `scenario_revision_id: scenario-m2-integrated-r1`
and the same authenticated session-creation endpoint. This opt-in selector is
an authored planar correctness fixture: five shuttles, three closed patterns,
two chargers and a later obstruction. No road is closed at the start, so
shuttle-01 (`route-peer-a`) can start on its own route. The existing client
creation buttons and older persisted attempts retain their original selector
and behavior. This change supplies backend execution; it adds no client UI.

1. Optionally, while READY, fetch `routes/route-peer-a/editor` and submit a
   draft to `route-previews` with `route_id: route-peer-a`,
   `expected_route_revision: 1` and `draft_revision: 1`. Approve the returned
   preview/revision through `route-approvals`, explicitly targeting
   `target_vehicle_ids: ["shuttle-01"]` with a reason. A via-node draft can
   choose bypass B or C. Missing mandatory fit/turn metadata still blocks
   approval.
2. Submit START. A route that uses a road closed at that moment receives a
   durable rejection. Approval and Start use the same session actor and journal.
3. At tick 240 (12 simulated seconds), shuttle-01 finishes its initial entrance
   dwell and receives `incident-m2-obstruction`, stopping before departure.
   Other shuttles continue. The obstruction targets its actually approved
   branch. The incident history endpoint contains ten seconds of committed
   `HISTORICAL_SIMULATION` samples; recorded camera footage stays unavailable.
4. Submit an offered WAIT, ESCALATE or ROAD_DETOUR candidate using
   APPROVE_INCIDENT_OPTION with the current epoch/incident revision and a
   reason. Wait/assistance hold the actor until clearance; the road detour
   drives the alternate validated path and resolves after its execution.
   `incident-previews` reads a committed branch without advancing live time.
5. Shuttle-05 begins near the declared reserve and receives live CONTINUE and
   CHARGE_NOW offers. Submit CONTINUE_SERVICE or RESERVE_CHARGER with the
   offered option ID/revision and current target revision. Reservation drives
   the shuttle along the remaining service path and declared depot access to
   its reserved bay. It then enters QUEUED_FOR_CHARGE, charges for 60 simulated
   seconds, then drives through the depot parking aisle to its own standby
   space. During that final move its state is RETURNING_TO_DEPOT and its port
   remains reserved. Arrival at parking publishes DEPOT_STANDBY_REACHED,
   releases the port and enters STANDBY. No in-place charging or
   position jump implements depot travel. Forecasts use admitted path distance
   and declared speed, with travel, charging and parking assumptions exposed in
   the offer. Later charges use the short yard path from parking to the bay;
   approval rechecks that actual path against the reserve. Parking spaces are
   derived from the authored depot with 30 m spacing. Each bay has a separate
   exit aisle, 20 m apart, followed by a separate entry aisle and parked row,
   each another 20 m south. Opposing simultaneous departures use different aisles.
6. Read the incident debrief and ordered event replay. Rule/decision scores
   remain provisional and uncalibrated; actual arrival/charging events are
   durable. Both observers read the same committed snapshot/event cursor.

All route/incident/battery decisions take the session lock used by ticking.
An `ActorCommit` unit of work reuses the existing domain services while adding
private execution state to their outcome transaction. A staged actor is
installed only after a successful commit. Rejected or failed writes cannot
replace its path, release a charger or publish uncommitted consequences.
Ordinary movement does not advance a command target's aggregate revision on
every tick; state/decision transitions do. Idempotency and stale option checks
remain enforced by the store and domain services.

Checkpoint version 3 retains the public domain state, admitted active paths,
private progress/dwell, incident holds and charging timers alongside the RNG
and clock. Recovery verifies that state against the durable snapshot, restores
one actor with a new epoch, and requires reconfirmation for queued inputs.
Unstarted attempts recover READY; started attempts recover PAUSED. Applied
route/incident/battery choices remain applied. Resume continues the same depot
journey, charging timer or final parking move and retains its bay reservation
until clear. Older checkpoints with a finished shuttle still in a released bay
drive that shuttle to parking on resume without releasing another vehicle's
reservation. RESET retains the existing policy:
create a new attempt rather than resetting an audited live attempt in place.

The fixture and new source/test files document author
**Nur Syazliyarni Binte Zazali**. Run the integrated workflow and instrumentation regressions with:

```powershell
$env:PYTHONPATH = 'backend;.'
& .\.tools\backend-venv\Scripts\python.exe -m pytest backend/tests/test_training_integration.py backend/tests/test_training_telemetry.py -q
```

This is the BE-M2-06 backend integration of BE-M2-02 through BE-M2-05. Later
tasks, rendered client acceptance and P1 certification are outside this change.

## Backend instrumentation and evidence

From the repository root, collect the integrated backend diagnostic with the
configured 50 ms-step wall-clock scheduler:

```powershell
$env:PYTHONPATH = 'backend;.'
& .\.tools\backend-venv\Scripts\python.exe -m src.services.training_benchmark --output .tools/be-m2-06-benchmark --diagnostic-only
```

Use a new or empty output directory for each run; existing evidence is never
overwritten. The default profile completes the entire depot/charging journey
and records at least 4,000 logical ticks, so allow several minutes. It uses an
isolated SQLite database and in-process HTTP with fixture authentication; the
running development server and its credentials are not used.

`summary.json` contains correctness checks, timing sample counts and nearest-rank
p95/p99/max by stage, outcome and command class, scheduler misses, queue high-water
marks, content/source hashes, host metadata and remaining risks.
`raw-timings.jsonl` preserves individual correlated spans; `integration-trace.json`
records checkpoints, command outcomes and ordered events for the same session.
The manifest, trace counters, checks, risk list and database remain alongside
them. Failures retain partial trace/checks and `failure.json`. Compare actual
travel/charging events with estimates; do not treat a reservation as completed
charging.

`real_time_factor` divides installed logical-time progress by active monotonic
wall time. It separates sessions/epochs and continuous running segments,
excluding intentional pauses and restart downtime. Each segment includes exact
60-second windows and a separately labelled partial tail. A step counts when
its installed result is observed: a stall can produce a zero-progress minute,
and later progress does not fill that minute retroactively. Full-minute values
are compared with the 0.99 reference; this P0 comparison is not P1 acceptance.

`committed_state_age` reports current age and a conservative peak between runtime
observations. Age begins when the physics step supplying the committed vehicle
state starts, so it includes computation, durable-write and installation time.
The peak retains the older frame's age just before replacement. A command at
the same tick does not refresh the pose timestamp. Paused-only observations are
excluded; an observation on resume still reflects the elapsed wall age.
Recovered sample origins are unknown until a fresh step is committed, and the
report counts those unknowns. These are server-observed ages; client transport,
interpolation and paint still require the client probes. Raw `runtime.progress`
records retain the inputs for both reports outside authoritative checkpoints.

For fast correctness checks, add `--manual-clock`. This advances the same locked
actor/commit path without wall-clock waits and explicitly omits scheduler timing
evidence. Real-time factor and state-age summaries are labelled
`NOT_MEASURED_MANUAL_CLOCK`. `--ticks` sets a minimum runtime; reducing it does not skip incident,
depot travel, charging or recovery. Neither clock mode supplies client paint,
network reconnect or forced-process-crash evidence. The separate
[process-crash drill](#process-crash-recovery) measures forced termination and
automatic backend restart.

`--diagnostic-only` exits 0 when backend correctness and complete trace retention
pass. Timing budget misses remain reported with their samples/outliers; they do
not turn a small P0 run into a P1 result. Without that flag, successful backend
collection exits 2 because full client acceptance remains incomplete. Runtime or
correctness failures return a nonzero exit status. WEB-M2-06 and NAT-M2-08 supply
the client probes for the coordinated acceptance run.

Earlier backend baseline, 28 October 2026: the wall-clock run in
`.tools/be-m2-06-integrated-baseline-20261028/` passed all 75 checks over 4,006
ticks, including two recoveries. It retained 28,452 timing records with zero
drops. Its manifest records the measured source hashes and Windows/Python/CPU
details; the machine is not a locked P1 reference.
The subsequent complete backend regression passed **793 tests, with 4 opt-in
live-OSM tests skipped**. `test-results.xml` in the same directory records the
run; the two existing TestClient deprecation warnings were nonfatal.

| Internal measurement | Samples | p95 (ms) | p99 (ms) | Observation |
| --- | ---: | ---: | ---: | --- |
| Tick computation | 4,006 | 20.99 | 28.84 | Misses proposed 10/25 ms budgets |
| Running schedule lateness | 4,006 | 15.27 | 16.53 | Misses proposed 10 ms p95 budget |
| Route validation | 20 | 1.87 | 9.68 | Small backend-only sample |
| Private actor recovery | 2 | 63.66 | 63.66 | Small backend-only sample |

The scheduler deadline counter recorded 1,529 misses; this is not a count of
lost durable events or skipped logical ticks. Measured profiling priorities
include actor staging (p95 13.61 ms), snapshot building (18.35 ms), snapshot
commit (27.67 ms), and the complete running boundary (107.68 ms). These spans
overlap and their percentiles must not be added. BE-M2-06 owns profiling these
misses before the full performance gate; deeper causes remain unverified.
The baseline establishes backend correctness and records performance gaps.
Real web/native paint, network reconnects and full P1 acceptance remain open.
The subsequent reporting update adds real-time factor and committed-state age
as defined above. The historical baseline did not contain those fields. A
diagnostic PASS establishes the checks listed in its report, not completion of
every BE-M2-06 measurement requirement.

Progress-reporting baseline, 28 October 2026:
`.tools/be-m2-06-progress-baseline-20261028/` passed 77 checks over 4,004 ticks
with 32,469 retained timing records and zero drops. Active logical progress was
200.20 seconds over 200.267 seconds of running wall time: aggregate real-time
factor **0.999666**, with two full-minute windows at **1.0**. Partial windows
are retained separately. Current committed-state age was p95 **65.15 ms** /
p99 **68.25 ms**; the conservative oldest age was **127.95 / 134.25 ms**.
There were 5 unknown current-age and 8 unknown peak-age observations around
initial/recovered samples, explicitly counted in the report.

This run's tick computation was p95 **4.77 ms** / p99 **6.85 ms**. Scheduler
lateness remained a measured miss at **15.11 / 15.92 ms**, with 3 deadline
misses. These are local P0 observations on an unlocked machine; the different
results from the historical run do not establish an optimization or P1 pass.
Client-displayed age and the full production/fault measurements remain open.
The complete regression after this update passed **802 tests, with 4 skips**;
its JUnit result is `test-results.xml` beside the progress-reporting baseline.

## Scheduler profiling

Run the controlled Windows timing experiment from the repository root:

```powershell
$env:PYTHONPATH = 'backend;.'
& .\.tools\backend-venv\Scripts\python.exe -m src.services.scheduler_profile --output .tools/be-m2-06-scheduler-profile --samples 200 --warmup 20 --repeats 2 --timer-period-controls
```

Use a new or empty output directory. The profiler runs each condition
sequentially, reverses run order on the second repeat and records 20 warmup
boundaries separately from the next 200 samples. Raw records and selected
sequence IDs remain in each case directory. The wait-only control uses the
production deadline helper; the actor cases use one real integrated actor and
SQLite writes during the initial exercise slice. This is a small P0 attribution
experiment, separate from the complete workflow/P1 benchmark.

The default and counter-clock cases use disposable event loops. The optional
timer-period case requests 1 ms timing for the profiling process and releases
that request in `finally`, including on failure. It requires Windows 10 version
2004 or newer, where the request is process-local. Production event-loop policy,
bootstrap/runtime selection and persistent OS settings are unchanged.
`tick.sleep` includes intentional idle time; its wake lateness is distinct from
total sleep duration. Lock wait, worker dispatch and durable-write timings
provide separate work/queue attribution.

The run in `.tools/be-m2-06-scheduler-timer-profile-20261028/` collected 12 cases
with zero trace loss. Each cell below pools two runs, 400 samples per condition:

| Workload, p95 wake lateness | Default | Finer loop clock only | Temporary 1 ms timer request |
| --- | ---: | ---: | ---: |
| Wait only | 15.17 ms | 15.51 ms | 1.75 ms |
| Integrated actor | 14.95 ms | 15.10 ms | 1.76 ms |

The actor cases had no measured boundary above 50 ms. Lock-wait p95 was below
0.03 ms, worker-dispatch p95 below 0.43 ms and snapshot-commit p95 below 7.71 ms.
Wait-entry lateness p95 was zero: the late starts occurred after waiting, not
after entering an already-overdue boundary. The same delay without actor work,
and its reduction under the timer-period intervention, identify Windows wait
granularity as the dominant measured cause on this Windows 11/Python 3.12.10
machine. Clock precision alone did not resolve it. All four timer requests were
released; the clock-only preliminary run is retained separately.

This matches the platform constraints described by
[Python's Windows asyncio documentation](https://docs.python.org/3.12/library/asyncio-platforms.html)
and the process/timing semantics of
[Microsoft's timeBeginPeriod documentation](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod).
The latter also documents power and Windows 11 visibility effects.

**Remediation owner: BE-M2-06.** Validate an opt-in process-local timer-resolution
guard while backend actors are RUNNING, balanced after the last running actor
stops and on shutdown/error. Keep the high-resolution deadline recheck and the
50 ms logical step. Before enabling that policy in production, repeat the full
workflow and paired-client measurements and assess CPU/power/visibility behavior
on the locked reference machine. The experimental loop override is not a
production recommendation. This profiling change records the cause and candidate
remediation; application of the production policy remains open.
The full backend regression after the profiling additions passed **808 tests,
with 4 skips**. Its JUnit result is `test-results.xml` beside the timer-profile
summary. The new profiler's file header names **Nur Syazliyarni Binte Zazali**.

## Multi-session scaling

Every live session ticks, publishes and answers HTTP in the one backend process,
so under the GIL they share about one core. This baseline (Phase 0 of the
multi-core sessions plan) measures how many integrated sessions keep 1x on a
machine and where each tick's time goes as the count grows. Run it from the
repository root, with nothing else heavy running:

```powershell
$env:PYTHONPATH = 'backend;.'
& .\backend\.venv\Scripts\python.exe -m src.services.session_scaling run --output .tools/scaling-baseline --traffic both --client both
```

For each session count (default 1, 2, 4, 6, 8, 12, 16) and each traffic/client
mode, it starts a fresh Python process, as a server would be. Run in one long
process, later cases inherited the heap and memory of earlier ones and measured
slower than the same case run alone; `--in-process` keeps that mode for
debugging only. The process creates that many integrated sessions in a fresh SQLite store, starts
them, warms up for 5 s, then measures a window in which every session advances at
least 600 ticks (`--ticks`). Shorter windows miss the first random incident at
about tick 214. The admission caps (`MAX_SESSIONS`, `MAX_TRAFFIC_SESSIONS`) are
raised for the case and restored afterwards. With `--client on`, one client polls
one session's snapshot once a second and long-polls its traffic, as the Unity
client does. That client runs in the same process, so its own work also takes
GIL time, which makes client cases slightly pessimistic.

`report.md` gives, per mode, the last session count at which every session kept
1x (real-time factor ≥ 0.99) and the first at which one fell behind. Then, per
case: the slowest and median session's real-time factor, tick lateness
p50/p95/p99, missed deadlines, tick work per session tick (`tick.compute`,
`snapshot.build`, `snapshot.commit` and `traffic.frame`; `snapshot.commit`
includes waiting for the writer), process CPU in cores, the client's
traffic frames per second (10 is 1x), the cost split by span with each span's
share of one core, and request handling per route. `summary.json` has the same
data plus per-session results, the case process's memory (which includes the
in-memory trace, so it overstates the server's) and host details (physical
cores, power source). `--keep-raw` also writes each case's trace records.

Laptop timing can drift 2x between runs, so compare a change only with
interleaved rounds. `ab` runs the sweep alternately on a clean copy of a git ref
and on this checkout, each side in its own process with the same copy of the
harness, reversing the order every round, and writes `ab-report.md` with
medians and the work ratio:

```powershell
& .\backend\.venv\Scripts\python.exe -m src.services.session_scaling ab --output .tools/scaling-ab --baseline-ref HEAD --rounds 3 --sessions 1,4,8
```

Record the laptop plugged in and on battery separately; the manifest records the
power source.

### Local baseline, 17 November 2026

One round, one process per case, on an Intel Core Ultra 5 125H laptop (14 cores,
18 threads) on AC power, Windows 11, Python 3.12.10, source 62c5cdf plus this
harness. Evidence: `.tools/scaling-baseline-20261117-isolated/`. Every case had
zero trace loss.

| Sessions | Traffic, client on | Traffic, client off | No traffic, client on | No traffic, client off |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 1.000 | 1.000 | 1.000 | 1.000 |
| 2 | 0.999 | 0.999 | 1.000 | 1.000 |
| 4 | **0.726** | **0.821** | 1.000 | 1.000 |
| 6 | 0.457 | 0.504 | 0.999 | 1.000 |
| 8 | 0.317 | 0.349 | 1.000 | **0.911** |
| 12 | 0.180 | 0.199 | **0.708** | 0.536 |
| 16 | 0.123 | 0.127 | 0.385 | 0.390 |

Slowest session's real-time factor; bold is the first count below 0.99.

- **With traffic only two sessions keep 1x on this machine.** The current
  `MAX_TRAFFIC_SESSIONS = 6` admits sessions that run here at about 0.46x. A
  session with traffic costs about 15 ms per tick: `tick.actor` about 9 ms, plus
  every second tick about 3 ms to build and 5 ms to commit its snapshot. That is
  about 2.8 times a session without traffic (about 5.4 ms, most of it publication).
- **Past one core, everything waits on the writer.** Once process CPU reaches 1.0
  core, `writer.transaction` is busy for the whole window and `snapshot.commit`
  grows from 5 ms to 100–750 ms, because ticks queue for the one writer thread.
  Its parsing and validation need the GIL that the ticks are holding. Phase 1.2
  and 1.3 reduce this, and the gateway's single writer in Phase 2 must be sized
  against it.
- **Request handling is small.** One client's `/snapshot` polls took 4–6 ms
  each at one session and 40–50 ms under load, at most about 5% of a core. The
  client received exactly the slowed traffic frame rate (7.3 frames a second at
  0.73x), which is the slow motion users see.
- Lateness p95 stays at about 15 ms while sessions keep up (Windows timer
  granularity, see Scheduler profiling) and is pinned at the 250 ms backlog
  limit once they don't.

The no-traffic counts at 8 differ between the client-on and client-off columns
(1.00 against 0.91). A same-day run in one long process measured 0.84 for the
same client-off case. That is within run-to-run variation on this laptop, so
compare changes with `--rounds` or `ab`, not with one run. Desktop and
battery-power baselines are not yet recorded.

### Session workers, 18 November 2026

`--workers N` runs the sessions in N session worker processes behind the
gateway (as `F9_SESSION_WORKERS=N`; `auto` as the backend chooses); the default
`0` keeps the Phase 0 single-process mode. Trace, CPU and memory then cover every
process, and admission by load is lifted for the case.

```powershell
& .\backend\.venv\Scripts\python.exe -m src.services.session_scaling run --output .tools/scaling-workers --sessions 1,2,4,8,12,16 --workers auto
```

One round per worker count, traffic and client on, same laptop and power as the
Phase 0 baseline, source 698dc1e plus Phase 2. Evidence:
`.tools/scaling-phase2-20261118/`. Every case completed with zero trace loss.

| Sessions | 0 workers | 2 workers | 4 workers | auto (8 workers) |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 1.000 | 0.999 | 1.001 | 1.000 |
| 2 | 0.998 | 1.000 | 1.000 | 0.998 |
| 4 | **0.776** | 0.998 | 0.999 | 1.000 |
| 8 | 0.354 | **0.646** | **0.827** | 0.999 |
| 12 | 0.226 | 0.414 | 0.616 | **0.625** |
| 16 | 0.146 | 0.258 | 0.447 | 0.550 |

Slowest session's real-time factor; bold is the first count below 0.99.

- **Sessions with traffic kept at 1x went from 2 to 8.** Process CPU now
  reaches the worker count (4.1 cores with 4 workers, 8.3 with 8) instead of
  stopping at 1.0.
- **It does not scale linearly on this laptop.** A session's work per tick rose
  from about 14 ms alone to 25 ms at 8 sessions on 8 workers and 40 ms at 8 on 4
  workers. The Core Ultra 5 125H has 4 performance cores and 10 efficiency
  cores, and shares one power budget across all of them, so the fifth and later
  busy workers run slower. Admission measures each session's cost, so it adapts
  to that; the 15 ms default only applies until sessions are measured. A desktop
  with uniform cores should come closer to the plan's estimate.
- **The writer is no longer the ceiling.** `writer.transaction` was busy 100%
  of the window in one process from 4 sessions on; with workers it was at most
  22% (16 sessions on 8 workers), because publications arrive already encoded.
- **Requests got faster under load.** A client's `/snapshot` p50 was 35–40 ms
  in one process with 2 or more sessions, and 2–6 ms with workers.
- **One session:** work per tick 13.7 ms in one process, 13.6–14.7 ms with
  workers; `snapshot.commit` grows by about 0.5 ms for the round trip to the
  gateway. These are single rounds, within this laptop's run-to-run drift;
  confirm a regression bound with `ab` rounds.
- **Memory:** about 110 MiB in one process, plus about 70 MiB per worker: 866
  MiB for 8 workers with 16 sessions, within the 2 GiB backend budget.
- Lateness p95 stays at about 15 ms while sessions keep up (Windows timer
  granularity), above the 10 ms target, as in Phase 0. The auto, 2-session case
  had p95 150 ms in this round (RTF 0.998); it was not seen at other counts.

### Native traffic engine, 18 November 2026

Phase 4 of the plan makes each tick cheaper. A profile of one integrated session showed the background
traffic as about four fifths of `tick.actor`: `CityTraffic.step` 49%, `plan_shuttle` 26%, and placing
and checking the shuttles most of the rest. The traffic now has a native engine,
`backend/native/traffic/traffic_core.cpp` (nanobind, already a dependency), which drives exactly as
`src/traffic/city_traffic.py` does:

- **Bit-identical.** It reproduces CPython's own float arithmetic where that is not the C library's
  (`math.hypot`'s correctly rounded norm, float `%`, `** 2`, `sum`'s compensated summation, `math.atan2`'s
  special cases), uses the same UCRT as CPython for `pow`, `atan2` and `acos`, and is compiled without
  fused multiply-adds. Both engines give the same `state()`, frames and snapshots, and continue each
  other's checkpoints, so switching engines changes no session hash. `test_native_traffic.py` checks
  the arithmetic on random and edge inputs, the area's traffic over minutes of steps, hazards, pins and
  signal faults, a whole session staged tick by tick through its first incident, checkpoints crossing
  engines and threads stepping at once. `test_city_traffic.py` and `test_traffic_events.py` run on
  both engines. Longer local checks (five more seeds, 8,000 staged ticks each, 4–5 incidents per
  session) were identical too.
- **Without the GIL.** Stepping the traffic, planning and placing shuttles, body checks, clearing and
  frame packing release the GIL, so sessions sharing a process (or a session worker) overlap there.
- **Cheap staging.** A staged copy shares the vehicles until one side steps, as the Python engine's
  does; a copy is a few pointers, and the first step clones the vehicle array.
- **Chosen per process.** `F9_NATIVE_TRAFFIC`: `auto` (default) uses the module when it is built from
  the current `traffic_core.cpp` and its tuning constants match `city_traffic.py`'s, else Python, with
  a log line saying why; `0` forces Python; `1` makes sessions fail to start without it. A module
  built from older source is never used. A network whose junction ids are not integers is driven in
  Python.
- **Building.** `python Tools/build_native.py` (CMake and Visual Studio's C++ tools). `run_backend.py
  --setup` tries it and carries on without it. The module links the C++ runtime statically, so the
  bundled build's portable Python loads it on a machine without the Visual C++ redistributable.
  `build_executable.py` builds it if needed and leaves it out of the bundle unless it is current, so
  a bundle never carries a stale module. The built `_traffic_native*.pyd` is not committed.

Change the driving in `city_traffic.py` and `traffic_core.cpp` together, then rebuild: until then the
backend falls back to Python, and the parity tests fail.

Interleaved A/B, two rounds each, of HEAD (Python traffic) against this change, traffic and client on,
300-tick windows, same laptop on AC power. Evidence: `.tools/scaling-phase4-inprocess-20261118/` and
`.tools/scaling-phase4-workers-20261118/`. Medians over rounds:

| Sessions | Workers | Slowest RTF, Python / native | Lateness p95 ms | Work ms per session tick |
| ---: | --- | --- | --- | --- |
| 1 | 0 | 1.000 / 1.000 | 16.1 / 16.6 | 13.78 / 5.23 |
| 4 | 0 | **0.813** / 0.999 | 250.0 / 16.2 | 49.18 / 6.50 |
| 8 | 0 | 0.356 / 0.999 | 250.0 / 23.4 | 125.20 / 15.65 |
| 16 | 0 | 0.158 / **0.568** | 250.0 / 250.0 | 297.90 / 81.28 |
| 8 | auto (8) | 0.998 / 1.000 | 15.2 / 15.1 | 16.00 / 5.01 |
| 12 | auto (8) | 0.998 / 0.999 | 22.7 / 15.9 | 18.22 / 5.59 |
| 16 | auto (8) | 0.993 / 0.999 | 147.3 / 17.1 | 26.82 / 6.39 |

- **One session costs 2.6 times less** (13.8 to 5.2 ms a tick; a traffic step alone went from 6 ms to
  0.2 ms, 32 times faster).
- **One process now keeps 8 sessions with traffic at 1x** instead of 2–4. Past that it is still
  bound by the GIL on the Python part of the tick: 16 in one process used 1.2 cores and ran at 0.57x.
  `MAX_TRAFFIC_SESSIONS` (6, for `F9_SESSION_WORKERS=0`) is left as it is, now conservative.
- **With workers, 16 sessions with traffic keep 1x with room to spare:** p95 lateness 17 ms (147 ms
  before), 0–2 missed deadlines in the window (3,400–7,135 before), and the workers used 1.4–2.2 cores
  in all, where they used 7–8. The 16-session product cap is now the limit on this laptop, as
  the plan expected of a desktop. The gateway's default estimate for a traffic session before it is
  measured is 7 ms with the native engine (15 ms with Python).
- Memory is unchanged: about 850–900 MiB for the gateway and eight workers with 16 sessions.
- What remains of a tick is mostly Python: under a profiler, copying the actor to stage each tick
  (`deepcopy` of the domain's Pydantic models and the actor's dicts) is now over half of it, ahead of
  the native traffic. That, not the traffic, is the next thing to make cheaper.

**Free-threaded CPython spike.** The plan's gate is wheels for the whole lock file. Checked on PyPI on
18 November: every compiled package at its pinned version has a `cp314t` Windows wheel (numpy, pandas,
pydantic-core, shapely, pyproj, pyogrio, Pillow, httptools, PyYAML, pymongo, charset-normalizer,
websockets) except `watchfiles` 1.2.0, which only uvicorn's `--reload` uses. The rest are pure Python. So
the gate is open apart from reload. The native module is built `FREE_THREADED` and locks the one cache
sessions share (junction conflicts), so it is ready for 3.14t. Running the backend on 3.14t (installing
it, a second dependency set, auditing shared state that relies on the GIL, and comparing threads with
worker processes) has not been done. Phase 2's workers already give the cores, and this phase made
them cheaper, so it is only worth doing to replace process plumbing.

## Process-crash recovery

Run the isolated BE-M2-06 crash drill from the repository root:

```powershell
$env:PYTHONPATH = 'backend;.'
& .\.tools\backend-venv\Scripts\python.exe -m src.services.crash_recovery `
  --output .tools/be-m2-06-crash-recovery --repeats 3
```

Use a new or empty evidence directory. The harness prepares one integrated
incident-hold checkpoint and one charging checkpoint with explicit accelerated
steps. Preparation and SQLite backup are outside the recovery timing intervals;
neither is scheduler or real-time-factor evidence. Each trial then launches the
real application, authentication middleware and lifespan on an ephemeral loopback
port with a separate database and temporary in-memory account. It resumes the
normal 50 ms actor scheduler before reaching the selected fault point.

The four cases interrupt:

- A running hold tick after public/private writes but before SQLite commit.
- A running charging tick at the same uncommitted boundary.
- A command after durable acceptance and before application.
- A command after atomic application/commit but before the HTTP reply.

Only the subprocess owned by that trial is terminated, using `TerminateProcess`
on Windows or `SIGKILL` elsewhere. The diagnostic fault gates and authenticated
control routes are registered only by the helper; normal application startup
does not expose them. Recovery starts another real backend process against the
same database. Production actor authentication is not bypassed in either helper.
IPC marker reads tolerate short Windows sharing locks within their existing
readiness deadline; a permanently unreadable marker fails the trial and retains
the underlying error. PID/fault checks still precede forced termination.

The reported `crash_to_verified_paused_ms` starts immediately before forced
termination and ends after automatic process launch, imports/startup, public
snapshot/receipt/event reconciliation, SQLite integrity and private execution
checks, and two identical PAUSED reads separated by 150 ms. It includes process
termination and automatic restart; no manual user restart occurs. The separate
`restart_to_verified_paused_ms` starts just before the replacement process is
launched. Fixture preparation and post-verification retries/resume are excluded
from both. Compare the crash interval with the 5-second reference in
[the performance specification](performance.md#6-resilience-acceptance-matrix).

Checks require the same durable tick and workflow content in a new PAUSED epoch,
retained private route/depot paths and charging clocks, unchanged committed
receipts, an ordered event prefix, and exactly one recovery event. Accepted but
unapplied input becomes `NEEDS_RECONFIRMATION`. The interrupted POST has no
successful reply; its durable QUEUED/APPLIED receipt is read before termination.
Commands already acknowledged by the helper must also survive. Retrying every
stored command with a new command ID and the original idempotency key must leave
the snapshot, private checkpoint, receipts and event journal unchanged. A final
normal-scheduler resume checks that peers move while the held shuttle stays
stopped, or that charging continues to add energy.

The output retains source/fixture hashes and host details in `manifest.json`,
aggregate/per-case timing summaries, and each trial's before/after state,
receipts, replay, fault marker, resumed snapshot, server logs and process cleanup
records. Raw traces and overflow metadata are saved for both processes. The
pre-crash export intentionally cannot close spans interrupted by termination;
it is diagnostic evidence, not a complete successful-request latency sample.
Failures retain partial evidence and `failure.json` without a success summary.
The helper and regression-test headers name **Nur Syazliyarni Binte Zazali**.

### Local crash baseline, 28 October 2026

`.tools/be-m2-06-crash-verified-20261028/` contains **12 passing trials**,
three per case with reversed case order on the second repeat. The measured
crash-to-verified-paused p95/p99/max was **1,777.88 ms**; restart-launch to
verified-paused was **1,769.89 ms**. With only three observations per case, the
nearest-rank p95 is the maximum and is not a production percentile estimate.

| Forced termination point | Trials | Crash-to-verified-paused p95/max (ms) |
| --- | ---: | ---: |
| Incident hold, uncommitted tick | 3 | 1,588.20 |
| Charging, uncommitted tick | 3 | 1,777.88 |
| Durable acceptance before application | 3 | 1,678.68 |
| Committed application before HTTP reply | 3 | 1,721.78 |

All committed domain state and receipts survived. Three interrupted unapplied
commands required reconfirmation, retries produced no duplicate effects, and
resumed actors continued the held/charging workflow. All 24 helper processes
exited, with cleanup evidence retained. Their 3,472 raw records have zero buffer
overflow, and recorded source hashes match the measured implementation.
The local P0 result is within the 5-second reference; full P1 fault acceptance
remains pending. The associated backend JUnit result is retained beside the
summary as `test-results.xml`.

The final full backend regression passed **832 tests, with 4 skips** and the
two existing dependency deprecation warnings. The crash module contributes nine
tests, including all four real process faults and bounded Windows marker-read
retries. The merge's duplicate app/middleware registrations were removed, and
the new authentication tests' fake stores now supply the required trace field.

This is a bounded P0 process-crash drill. P1 hardware/content, paired visible
clients, power-loss/storage faults and wider endurance acceptance remain separate.

## Verification

```powershell
$env:PYTHONPATH = 'backend;.'
python -m pytest backend/tests/test_session_creation.py backend/tests/test_training_store.py -q
python -m pytest backend/tests/test_training_integration.py backend/tests/test_training_telemetry.py backend/tests/test_crash_recovery.py -q
cmake --build build/automated/native --config Release --target App native_unit_tests -- /m:1 /v:minimal
python -m backend.tests.native_session_smoke --executable build/automated/native/Native/Tests/Release/native_unit_tests.exe --web
```

The smoke harness starts an isolated real backend with a generated credential
and temporary database. Two native clients create/join one run and verify
Start/Pause/Resume/Archive, advancing shared ticks, the same paused boundary and five
shuttles. The production actor authentication dependency is not overridden.
With `--web`, the actual TypeScript API client then joins and pauses the
native-created run, creates another attempt with a different seed, starts it and
archives it. Regression tests cover capacity reuse, terminal retries and restart
recovery with corrupt private checkpoints.
