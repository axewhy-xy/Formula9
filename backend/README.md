# Running the Backend Server

This server now lives in the Unity repository `Formula9`. All source, shared contracts,
map files and default scenario assets are local to this repository. See the
[root startup guide](../README.md) and [migration record](../docs/backend-migration.md).

From the repository root:

```powershell
python Tools/run_backend.py --setup
if (!(Test-Path backend/.env)) { Copy-Item backend/.env.example backend/.env }
# Configure MONGODB_URI privately in backend/.env, then:
python Tools/run_backend.py
python Tools/run_backend.py --test
```

Windows shortcuts: `run.bat --setup`, `run.bat`, `run.bat --test`.

Setup also builds the native background traffic engine when CMake and a C++ compiler (Visual
Studio's "Desktop development with C++") are installed. Rebuild it after changing
`backend/native/traffic/traffic_core.cpp` with `python Tools/build_native.py`. Without a current build the
backend logs so and drives the same traffic in Python, about five times slower per session.
`F9_NATIVE_TRAFFIC=0` forces Python; `=1` refuses to start sessions without the native engine.
Both engines walk the area's pedestrians on its sidewalks and mapped footpaths
(`fixtures/traffic/<area>-walkways.json`), and vehicles and shuttles give way to them at crossings, a
vehicle only until the walkers are past its lane; `F9_PEDESTRIANS=0` turns them off. Walkers make trips
between homes, places and bus stops, and how many are out follows the time of day a session's clock starts
at (`F9_DAY_START=HH:MM`, 08:00 by default). Rebuild the walkways file with
`python -m src.traffic.import_walkways` (`--download-footways` fetches the footpaths from Overpass first).
Python 3.12+ is required; the existing lock targets Python 3.12. `--reload` watches
backend source and contracts. The default map is `backend/fixtures/map/map.db`;
the default session database is ignored `state/training.sqlite`. Environment variables
`MAP_DATABASE_PATH`, `TRAINING_DATABASE_PATH`, `MONGODB_URI` and `MONGODB_DATABASE`
override those settings. The bundled map also requires the adjacent `map.osm`.

For a disposable server with in-memory accounts and temporary session storage:

```powershell
backend\.venv\Scripts\python Tools/isolated_backend.py --seed-incidents
```

It listens on port 8010 and is intended for Unity end-to-end tests. `--seed-incidents`
schedules known incidents in every session, beside the random ones; the incident
end-to-end test and `RouteWalkthrough` need them. The sections
below preserve the original backend service documentation, including historical
Qt/web references; those client applications are not required to run this backend.

For authenticated v2 session creation and Start/Pause/Resume/Archive in both apps,
plus permanent deletion and native disk cleanup, see
[session creation and live runtime](../docs/session_creation.md).

The opt-in `scenario-m2-integrated-r1` backend exercise connects route approval,
incident response, battery decisions, physical depot travel and charging with
durable recovery on one actor. See the
[integrated exercise instructions](../docs/session_creation.md#integrated-be-m2-02-through-be-m2-05-backend-exercise).
The same scenario is exercised by the BE-M2-06 benchmark; see
[backend instrumentation and evidence](../docs/session_creation.md#backend-instrumentation-and-evidence)
for collection commands, generated artifacts and the separate client acceptance gate.

Its routes are edited on the operating network in `fixtures/m2_operating_network.json`, built from
the OpenStreetMap extract by `src/routing/build_operating_network.py`. Every shuttle's route is a list
of its roads with its own start, stops (each a node at its kerb) and approach roads: shuttle-01's in
`route_edit` of `fixtures/m2_integrated.json`, the peers' in `route_edit.peer_routes`.
`RouteEditService.for_route` picks a route's data by its ID, and a detour keeps that of the route it
was taken from. One physical road is one
edge per direction, with its width, headroom and access and where each came from. A preview with
`waypoint_edge_ids` plans over the whole network (`src/routing/operating_network.py`: no U-turns,
stops served at the kerb); empty and `via_node_ids` requests stay on the curated paths, as do the
legacy route fixture and spatial sessions. Each check names its roads in `edge_ids`, unknown
mandatory metadata blocks approval, and the extract has no turn restriction relations, so only
U-turns are refused. `GET /api/v2/sessions/{id}/routes/{route}/outcome` reads back the durable
`STOP_SERVED` and `LOOP_COMPLETED` events against what was approved.

An integrated session drives its shuttles through the area's background traffic
(`src/traffic/city_traffic.py`): the 0.1% sample of Singapore's vehicles from
`fixtures/traffic/toa-payoh.json` on its OpenStreetMap roads, under the simulated lights of
`fixtures/traffic/toa-payoh-signals.json`, ported from the 3D client's traffic. `city_traffic.py` is
the source of truth for how the traffic drives: the client's own driving
(`Assets/Formula9/Runtime/World/CityTrafficDriving.cs` and `CityTrafficShuttles.cs`) is only a fallback
for a session whose backend sends no traffic frames, so a change to how traffic or shuttles drive is
made here, and ported to the client only if the fallback should match. The session owns and
checkpoints it: the rest of its private state (some 35 KB) with every publication, the traffic
(65-100 KB) apart, in `session_traffic`, and at most once a second (`TRAFFIC_CHECKPOINT_TICKS`). A recovered session drives
the saved traffic on to its own tick around where its shuttles were, then places them where they now
are, sending off any vehicle under one; so it drives on exactly as the original would only when it
was saved on a traffic checkpoint. Each session with traffic costs several times one without (about
5 ms of a 50 ms tick on a desktop, 14.5 ms on a slow laptop, all on one core), so at most
`TrainingSessionService.MAX_TRAFFIC_SESSIONS` of them are admitted at once. In the traffic a
shuttle keeps to the kerb lane, accelerates and brakes, follows the vehicle in front, gives way, and
queues behind vehicles waiting at a red light; incidents close their road and back the traffic up
(`src/traffic/incident_hazards.py`). `GET /api/v2/sessions/{id}/traffic?after_tick=N&wait_ms=M`
returns the traffic's latest frames for the 3D city to draw: presentation only, not part of the
durable snapshot. A frame comes with every traffic step, ten a session second, stamped with the
moment it shows the vehicles and shuttles at; its vehicles are packed (`city_traffic.FRAME_RECORD`,
14 bytes each, base64) so the client reads a thousand of them without parsing JSON numbers. With
`wait_ms`, a request with no newer frame yet is held until the next one (or the wait runs out), so
the client, asking again as each reply arrives, hears of every frame as it is made. A tick that runs
late does not push back the ticks after it: `TrainingSessionService` catches up on up to
`MAX_TICK_BACKLOG_S` of missed ticks, so a session's clock, and its frames, keep the wall clock's
pace. The client draws the frames a little behind the newest on a clock that matches their pace and
lateness (`Assets/Formula9/Runtime/World/TrafficPlaybackClock.cs`). The tests run integrated
sessions without traffic unless marked `traffic` (`tests/conftest.py`); `tests/test_city_traffic.py`
covers it, and the main workflow and recovery tests of `tests/test_training_integration.py` also run
with it.

A session pins the fixture it started on. After the network or scenario changes, sessions from
the old revision can't be recovered: they are marked `FAILED` with `CORRUPT_CHECKPOINT`, stay
readable, and a new session has to be created.

The v2 training persistence boundary and restart behavior are documented in
[BE-M2-03 persistence handoff](../docs/milestone_status/M2/be-m2-03-status.md).

The shared 3D scene package, bounded asset endpoints, spatial handshake and
cross-language fixtures are documented in the
[BE-M2-07 handoff](../docs/milestone_status/M2/be-m2-07-status.md) and
[package source/readme](fixtures/m2_scene/README.md). Scene loading and
validation are CPU-only. The [BE-M2-08 handoff](../docs/milestone_status/M2/be-m2-08-status.md)
documents authoritative ramp motion, bounded swept checks and the opt-in durable
`scenario-m2-road-3d-r1` session. Native creation defaults to this 3D scenario,
with the full planar workflow available as **Integrated exercise (2D)** in its
scenario picker. Web creation retains its planar default and can join the
same 3D session by ID. The integrated 3D route/incident/charging exercise
remains BE-M2-09 work.

The [BE-M2-09 follow-up](../docs/milestone_status/M2/be-m2-09-status.md)
provides shared same-tick motion samples, a prerequisite audit, a bounded numeric
client-capture comparator and measured headless diagnostics. Collect them with
`python -m src.services.spatial_acceptance --report .tools/be-m2-09-report.json --headless-only`
after setting `PYTHONPATH` as below. The report keeps connected rendering and
expanded 3D P1 explicitly incomplete; omitting `--headless-only` exits 2.

## Headless road-constrained 3D actor

```powershell
$env:PYTHONPATH = 'backend;.'
python -m src.services.spatial_session_actor
python -m pytest backend/tests/test_spatial_motion.py -q
```

The runner compares two seeded 600-step runs against the verified M2 package.
It reports the five canonical body poses, obstruction stopping, ramp terminal
and final state hash. `--trace <path.json>` writes deterministic sampled output
after the run. The controller loads assets/indexes before ticking, uses 3D arc
length for progress and `energy-r1`, and holds on an exhausted spatial bound.
Snapshots include explicit spatial diagnostics and model limits. This small
headless fixture is separate from the expanded rendered P1 workload.

To run the backend server, please install the required dependencies and then execute the following command:

```bash
uvicorn src.app:app --reload
```

## Headless five-shuttle actor

From the repository root in PowerShell:

```powershell
$env:PYTHONPATH = 'backend;.'
python -m src.services.session_actor
python -m pytest backend/tests/test_session_actor.py backend/tests/test_fleet_session_host.py -q -p no:cacheprovider --basetemp .tools/be-m2-02-tests
```

The runner loads `backend/fixtures/routes_testing.json` relative to its module,
independent of the working directory. Missing or invalid requested assets fail
explicitly. It compares two seeded runs for 600 control steps, including pause,
resume and reset, and reports 300 canonical v2 presentation boundaries, the
content-derived scene/scenario revisions and final SHA-256 state hash as JSON.
The default geometry is three separated closed patterns (rectangle, triangle
and concave loop), with five deterministic initial assignments. The former
three open routes reversed into head-on deadlock; open routes remain supported
with at most one assigned shuttle each. Actor creation rejects round-robin
assignments that share an open route, since oncoming traffic scheduling is not
implemented. Closed routes can be shared.

The actor has two clocks. Simulation ticks advance in fixed 50 ms steps only
while running. Commands use a separate monotonic control-step counter, so a
scheduled resume can execute during a pause. `Command.triggered_tick` and
`CommandOutcome.processing_tick` refer to that control clock. Reset starts a new
session epoch, resets simulation time, vehicles and RNG, and retains the control
clock and future command schedule. Commands at the same boundary retain their
enqueue order, including commands following a reset.
The control queue accepts at most 256 pending commands and rejects overflow
before enqueueing. The actor retains the latest 1,024 outcomes in memory;
the session command revision remains monotonic when that window rolls over.
The durable store must retain the complete command history for a live session.

Motion is checked before application, with 15 m route headway (20 m release
hysteresis) and continuous 10 m centre separation along each polyline segment.
Open routes reverse after a two-second endpoint dwell; closed routes wrap only
after dwelling at their shared endpoint. An initially overlapping or mutually
obstructed fleet holds position conservatively. The geometry is the planar M2
fixture; authoritative 3D vehicle bodies remain BE-M2-08 work.
Assets are capped at 64 KiB, three routes and 64 waypoints per route, with
finite coordinates inside +/-1,000,000 m; validation happens before ticking.

`generate_snapshot()` returns a detached `FullSnapshot` subtype with an additive
SHA-256 `state_hash`. It uses the shared v2 IDs, states, metre/second/kWh units,
authored provenance and route references. Private battery percentages are
converted with the declared 40 kWh capacity. Energy and charging use `energy-r1`;
this headless fixture assumes a dedicated 50 kW in-place charging port per
charging shuttle, represented explicitly in the snapshot. It does not model
travel to a depot or contention for a shared charger. Auxiliary power is
deducted during charging, and the 60% release target uses the net energy gain.

`FleetSessionHost` owns one actor and one 20 Hz task per session ID on a single
asyncio event loop. Creation starts the clock even without subscribers;
subscribing, reconnecting and reading never create another task. Each 10 Hz
publication is one frozen `FleetPublication` containing serialized v2 JSON,
shared between client queues. Each queue retains only its latest publication.
The host permits 16 sessions and 16 subscribers per session. `close()` wakes
observers and cancels/awaits all owned tasks. Actor errors produce a bounded
`ACTOR_FAILED` terminal publication and a server-side diagnostic. Missed wall
deadlines are counted per hosted session; the host slows simulation time rather
than generating an unbounded catch-up burst.

The session host owns durable publication, event cursors, command journaling and
API scheduling; reading snapshots never starts another loop. A reset's new
epoch is a new baseline for that host, not an ordinary same-epoch
`TrainingStore.commit_snapshot` call. The actor's control queue is private and
is not a replacement for authenticated v2 `DispatcherCommand` acceptance.
`FleetSessionHost` remains a headless in-memory boundary. The separate
`TrainingSessionService` connects the actor to FastAPI, durable lifecycle
commands and private-state recovery; see [session creation](../docs/session_creation.md).
See the [BE-M2-02 follow-up](../docs/milestone_status/M2/be-m2-02-status.md)
for verification evidence, numeric tolerances and remaining integration gates.

## Note
- The backend server uses FastAPI and requires Python 3.12 or newer.
- The backend server must be kept running in order for both the frontend web application as well as the native application to function properly. If the backend server is not running, the frontend will not be able to communicate with it, and the application will not work as intended.
