# BE-M2-02 actor follow-up

Assessed 27 October 2026 against `docs/backend/m2.md`. Status: headless planar
foundation verified; live training integration and the cumulative 3D gates
remain open.

## Assessment and changes

The existing actor already provided fixed 50 ms simulation steps, a seeded
private RNG, stable shuttle iteration, endpoint dwell, conservative swept
polyline separation, headway checks, control outcomes, state hashes and detached
v2 snapshots. Its tests passed, but the default fixture had no liveness check:
all five shuttles eventually blocked after its three open paths reversed into
one another. The actor also accepted unbounded route geometry/control history,
reused one scene/scenario revision for different content and omitted auxiliary
power while charging. Detached models alone did not demonstrate immutable
10 Hz fan-out or ownership with concurrent consumers.

The follow-up changes are:

- Replace the default asset with three separated closed patterns: a rectangle,
  triangle and concave loop. All five shuttles complete at least two laps in a
  120 simulated second regression, maintaining separation through bends, dwell
  and wrap. Cross-route intersections, head-on holds and stopped-peer cascades
  remain separate adversarial safety fixtures; the default does not establish
  general junction scheduling or deadlock recovery.
- Reject actor configurations that assign multiple shuttles to an open route,
  before simulation or host task creation. Assignments are round-robin, so only
  routes actually shared by the requested fleet must be closed; singly assigned
  open routes retain endpoint dwell and reversal behavior.
- Validate a maximum 64 KiB asset, three routes, 64 waypoints per route and
  finite coordinates within +/-1,000,000 m before creating the actor. Tick code
  performs no asset loading, route search, filesystem access or exports.
- Bound pending controls at 256 and recent outcomes at 1,024. Queue overflow is
  rejected without dropping accepted controls. The command revision remains
  monotonic through retention and reset; full durable history belongs to the
  persistence integration.
- Derive scene identity from normalized geometry and scenario identity from the
  geometry, seed, fleet size, rules and declared model parameters. Changing
  these inputs no longer silently reuses `fleet-fixture-r1`.
- Apply `energy-r1` auxiliary consumption during charging and test the release
  target against net energy. The fixture still uses dedicated in-place ports;
  depot travel, shared reservations and operator charging choices are not
  implemented by this actor.
- Add `FleetSessionHost`: one actor/task per session ID, independent of client
  attachment; frozen publications containing immutable v2 JSON every second
  control boundary; one-item coalescing queues; bounded session/subscriber counts;
  failure notifications; and awaited shutdown. Two concurrent consumers receive
  the same publication object and clock. Reconnection reuses the task, and the
  session continues with no subscribers. A delayed scheduler preserves fixed
  logical steps, counts missed wall deadlines and avoids unbounded catch-up.

The mutable `generate_snapshot()` result remains a detached v2 value for store
and contract consumers. The host serializes it once after an in-memory actor
boundary; it exposes that immutable string to presentation consumers.

## Verification

From the repository root in PowerShell:

```powershell
$env:PYTHONPATH = 'backend;.'
python -m pytest backend/tests/test_session_actor.py backend/tests/test_fleet_session_host.py -q -p no:cacheprovider --basetemp .tools/be-m2-02-tests
python -m src.services.session_actor
```

The actor/host tests cover all five private service states, 120 seconds of
five-shuttle replay, shuffled entity storage, continuous crossing/bend checks,
endpoint and loop boundaries, pause/resume/reset ordering, bounded inputs and
history, shared-open-route rejection and mixed open/closed assignments, net
charging, revision identity, detached v2/store interoperability,
two observers, slow/reconnected observers, actor failure and shutdown.

Repeated identical runs require exact hashes and discrete states in the same
Python/runtime build. Positions/progress/headway use metres, time seconds and
battery kWh at the wire boundary. The planar safety comparison allows 1e-9 m
numeric slack; endpoint dwell completion allows 1e-9 s floating-point slack.
Cross-runtime comparisons should compare numbers with absolute tolerance 1e-9
in their declared units and exact IDs/states/ticks/epochs/command ordering.
Cross-runtime hash equality and the later 3D spatial tolerances are not claimed.
The RNG is explicit and resettable; current authored motion uses no random draw.

The 600-control-step headless runner with seed 1000, pause at 100, resume at 120
and reset at 400 emits the following reproducible report (Python 3.14 locally):

```json
{
  "verified_command_steps": 600,
  "presentation_snapshots": 300,
  "shuttles": 5,
  "seed": 1000,
  "scene_revision": "fleet-scene-914a996b991bee5cac3c22cc",
  "scenario_revision": "fleet-scenario-51d290bf37cf48907e7d37ae",
  "final_epoch": 2,
  "final_tick": 200,
  "state_hash": "1c28351b94dd51c7a1966eb0e553974e6f1b8e6004b98d1a168155240ab1a129"
}
```

Initial compatibility verification also covered the existing v2 contract, SQLite store,
energy, battery decisions/services/assessment, incident service and route-edit
suites. Result: 60 actor/host tests plus 356 compatibility tests, all 416 passed
in 16.82 s locally. Only existing pytz/Starlette dependency deprecation warnings
were reported. This is functional evidence, not a P1 timing measurement.

PR #79 review verification: 68 actor/host tests passed; the full backend suite
passed with 545 tests and 4 opt-in network integration tests skipped (37.31 s,
Python 3.14.2). The full run used workspace-local `TEMP`/`TMP` directories to
avoid sandbox permissions on the system temporary directory. Backend
byte-compilation passed, and the headless runner reproduced the report above
with the same revisions and state hash.

## Known charging limitations

Below 30% battery, a shuttle enters standby and charges at its current route
position until it reaches the 60% release target. It remains an obstacle to
following shuttles throughout charging; followers can stay blocked until the
charging shuttle resumes and clears the release headway. Long sessions can
therefore accumulate charging and blocked vehicles. The 120-second default
fixture regression does not establish sustained fleet throughput.

Charger capacity and contention are not modelled. Each charging shuttle gets a
dedicated one-port `fixture-charger-N` in the snapshot, so multiple shuttles can
charge concurrently without a shared capacity limit. Depot travel, shared
charger reservations and queueing remain integration work.

## Integration gates still open

1. Wire a live host to authenticated v2 commands and transactional persistence.
   Every visible boundary must follow the durable store's successful commit;
   reset/recovery needs explicit epoch handling and recoverable private actor
   state. `FleetSessionHost` currently publishes in-memory boundaries only and
   is not registered with FastAPI. The existing M1 playback manager is separate.
2. Attach the real web/native transports to the same live fleet session and
   verify cross-client command outcomes. The new paired-consumer tests prove
   scheduler ownership and immutable fan-out, not actual client integration.
3. Integrate incident holds/guidance and authored depot/charger execution with
   the BE-M2-04/05 services. The private combined blocked state currently maps
   to v2 `BLOCKED`; it is not an incident/decision lifecycle implementation.
4. Extend this explicitly planar point-centre controller under BE-M2-07/08/09:
   dimensioned scene/support geometry, 3D body-centre poses, conservative swept
   volumes, ramp/obstruction traces and mandatory scene readiness. No fabricated
   `world_3d` capability or 3D acceptance is implied by these legacy z=0 snapshots.
5. Record tick/publication/acceptance/recovery and expanded 3D P1 timings under
   BE-M2-06/09. The existing 10 m separation, 15 m route headway, 20 m release
   hysteresis and instantaneous stopping are declared training assumptions;
   they do not establish a physical braking model or a real-world safety bound.
