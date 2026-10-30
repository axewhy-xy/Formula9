# BE-M2-05 incident handoff

The incident portion of #30 is implemented in `backend/src/services/incidents.py`.
It uses the existing `Incident`, `Candidate`, `DispatcherCommand`, `Event`, and
`FullSnapshot` models. No v2 model, schema, or shared golden fixture changes are
required. Battery forecasts, charger allocation, and the native PR #52 remain
separate work.

## Authored exercise

`backend/fixtures/m2_incident.json` identifies scenario `m2-incident` and revision
`m2-scripted-obstruction-r1`. It reuses the directed road overlay and validation
from BE-M2-04. Shuttle `shuttle-01` is staged at the entrance of
`route-m2-detour`. From tick 240 (12 seconds), the fixture waits for the first
publication with that shuttle `IN_SERVICE` on the authored route, then obstructs
the direct edge, stops that shuttle, and offers three stable candidate IDs.
While the shuttle is ineligible, fleet publications continue without creating
the incident. Clearance and option expiry are measured from actual detection.
Other shuttle publications are preserved.

- **WAIT:** remain stopped until the obstruction clears ten simulated seconds
  after detection.
- **ESCALATE:** request assistance inside the simulation; the obstruction clears
  within five seconds of the decision, or at its earlier natural clearance.
- **ROAD_DETOUR:** validate the curated directed detour, assign a separate route
  to the affected shuttle, and enter `DETOURING` / incident `EXECUTING`. The actor
  reports `IN_SERVICE` after completing the detour; the next publication records
  incident resolution. Peer route assignments do not change.

Both hold options validate the original road at release time. The detour checks
geometry, direction, closures, ordered stops, operating area, fit and turns.
Failed or unknown mandatory checks prevent application. No local lane-passing
candidate exists. Candidate assumptions describe expiry and invalidation; the
durable `OPTIONS_READY` event stores the route, scene, rule, epoch and obstruction
dependencies. Backend application rechecks these plus the incident revision,
safe hold, expiry and road feasibility. A failed attempt receives a durable
`REJECTED` command outcome.

## Actor integration boundary

This checkout has persistence and legacy single-trajectory playback, but no v2
fleet driving actor or authenticated session-creation API. The incident service
does not add another simulation clock. The live-session owner should:

1. Create a `FullSnapshot` for the authored scenario through
   `TrainingStore.create_session`. `incident_demo.initial_snapshot` is a runnable
   example baseline.
2. Stage fixed 50 ms actor steps from the latest committed snapshot. Keep the
   affected shuttle at the authored entrance before this fixture triggers.
3. At each 10 Hz presentation boundary, call
   `IncidentService.publish(store, proposed_snapshot)` in place of
   `store.commit_snapshot`. Continue using the returned committed snapshot as
   the baseline. Its identity changes on every commit. A concurrent decision
   yields `STALE_REVISION`; restage from the latest baseline.
4. Respect `AWAITING_GUIDANCE`, `DETOURING`, and approved route assignments when
   advancing the fleet. Feed the energy module's actual battery readings into
   the same snapshot before publication. The incident service preserves those
   values and records their observed change at resolution.

Decision staging and actor publication use checkpoint identity guards inside
the SQLite writer. An intervening publication cannot be overwritten by an old
decision, even if its incident revision is unchanged. Applied decisions, reasons,
option evidence, scores and checkpoints commit atomically. Recovery pauses the
session with a new epoch; queued decisions require reconfirmation. On the first
publication after resuming an unfinished incident, the service refreshes its
options against the new epoch and any changed dependencies, advancing the
incident revision. Old options remain unusable. Original detection timing is
retained through refreshes.

## HTTP surface

The existing authenticated training API now supports:

| Endpoint under `/api/v2/sessions/{id}` | Behaviour |
| --- | --- |
| `POST /commands` | Existing `APPROVE_INCIDENT_OPTION` command, payload `{"candidate_id":"..."}`, required reason. For this scenario, returns the durable final `APPLIED` or `REJECTED` status. Check `status`, not just HTTP 202. Other scenarios retain their existing queue behaviour. |
| `POST /incident-previews` | Same command body, evaluated from a committed checkpoint on a separate branch. Returns the checkpoint ID/tick, branch snapshot and estimated consequence. Does not accept a command, add events, advance time, or mutate the live session. |
| `GET /incidents/{incident_id}/history` | The ten seconds preceding detection, read from durable checkpoints, with one sample per tick. `complete=false` explicitly reports missing prehistory or sampling gaps. |
| `GET /incidents/{incident_id}/debrief` | Durable attempts, reasons, timing, chosen action, checks, estimated route/energy consequences, observed resolution outcome, provisional assessment, and reference acceptable actions. |

History and previews have `camera: null`. History is labelled
`HISTORICAL_SIMULATION`; alternatives are `TRAINING_SIMULATION` with branch
provenance. No KITTI camera asset is inferred from these snapshots. Once the
obstruction creates a branch, subsequent incident publications strip recorded
frame metadata from current fleet state and preserve the earliest branch tick.

## Provisional assessment

Rubric `m2-incident-provisional-r1` scores this incident only:

| Criterion | Maximum | Rule |
| --- | ---: | --- |
| Safety/compliance | 40 | Approved response passed the required model checks. |
| Response time | 20 | Full through 30 simulated seconds; linear decline to zero at 60. |
| Attempts | 10 | Subtract two per earlier journalled attempt, floor zero. |
| Service coverage | 20 | All required stops remain served. |
| Route efficiency | 10 | Original duration / (response delay + chosen estimated duration), capped at one. |

The composite is explicitly provisional and uncalibrated. All three responses
are reference acceptable choices when valid. An unanswered incident becomes
`UNRESOLVED` with zero points when the obstruction clears; clearing the road
does not invent a trainee decision.

`response_time_s` is measured from detection to application on the authoritative
simulation clock and excludes shared pauses. `wall_response_time_s` separately
measures detection to durable command acceptance using server timestamps. No
client-supplied elapsed time is trusted. The reason, denied attempts and selected
option remain durable. The recorded score is frozen with the successful decision;
later denied commands appear in the log without retroactively changing it.

Route/service/energy comparisons use the declared BE-M2-04 overlay assumptions
(0.8 kWh/km, 1.5 kW auxiliary load), explicitly labelled estimates and separately
identified from the energy actor's `energy-r1` integration parameters. They never
subtract forecast energy from the live battery. Additional service delay and
energy estimates include the pre-decision hold, so delaying a wait decision
cannot improve route-efficiency credit. Observed resolution fields use
the battery reading supplied by the actor. The battery criterion and combined
incident/battery assessment await the energy integration.

## Runnable verification

From the repository root in PowerShell:

```powershell
$env:PYTHONPATH = 'backend;.'
python -m src.services.incident_demo --database build/incident-demo.sqlite --action WAIT
python -m src.services.incident_demo --database build/incident-demo.sqlite --action ESCALATE
python -m src.services.incident_demo --database build/incident-demo.sqlite --action ROAD_DETOUR
python -m pytest backend/tests/test_incidents.py backend/tests/test_training_store.py backend/tests/test_route_edit.py -q --basetemp=build/pytest-incident -p no:cacheprovider
```

Each demo creates a new session and preserves earlier database records. It moves
four peers synthetically to exercise the incident publication boundary, retains
ten seconds of history, applies the selected action with a one-second response,
and prints its durable decision and assessment. Wait and escalation resolve.
The detour remains `EXECUTING` because the headless example does not implement
the fleet road-driving actor. The demo leaves battery integration to its owner.

The tests cover all three actions, deferred triggers, peer continuity, truthful
history and gaps, preview isolation, changed obstruction/route/rules, delayed
escalation feasibility, expiry, failed/unknown options and missing baseline
rejection, unsupported options, nonblank reasons, deterministic scoring,
unanswered incidents, idempotency, crash rollback, restart reconciliation,
checkpoint races, and authenticated HTTP behaviour.
