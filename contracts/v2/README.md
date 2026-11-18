# Formula 9 training contract v2.x

This directory defines the M2 training wire boundary. `message.schema.json` and `command.schema.json` are generated from the Python models in `models.py` by `python -m contracts.v2.generate_fixtures`. The generated schemas and checked-in fixtures are shared by Python, web, and native tests. Version 1 trajectory playback stays available at its existing endpoints. This contract does not implement the M2 actor or persistence APIs.

`sessions.py`, `create_session.schema.json` and `fixtures/create_session.json`
define admission to the curated live planar fleet scenario. The authenticated
`POST /api/v2/sessions` endpoint requires an `Idempotency-Key` and returns a
`TrainingSession`. Authenticated `GET /api/v2/sessions` returns a
`SessionListResponse` from `sessions.py` (also `session_list.schema.json` and the
shared `fixtures/session_list.json`): lightweight session IDs, scenario
revisions, statuses and capacity flags, plus the live count, total stored count
and capacity. It is paged by `limit` (1-500, default 100) and `offset`.
Live attempts appear first, newest first within each group; archived history
remains available for joining without occupying capacity.
See [session creation](../../docs/session_creation.md) for
the lifecycle, credentials, supported scenario and recovery behavior.

## Opt-in spatial extension (2.1)

`spatial.py` defines the shared scene manifest, body-centre pose, spatial
reference, capabilities and readiness request. `python -m scripts.build_m2_scene`
(with `PYTHONPATH=backend;.`) compiles the package and generates the five
`training_scene`, `spatial_vehicle`, `spatial_reference`, `scene_capabilities`
and `scene_readiness` schemas plus spatial fixtures. Regenerate the ordinary
fixtures first. The shared package lives in `backend/fixtures/m2_scene`.

A `FULL_SNAPSHOT.spatial` reference opts into mandatory 3D. It must match the
scenario reference and envelope scene/transform revisions. Every shuttle then
requires `spatial`: finite `position_m`, a unit XYZW quaternion, positive L/W/H,
proxy ID/revision, support surface/lane/deck IDs and offset, route progress,
continuity flag, asset ID/hash and a rigid asset-to-body matrix. Its centre must
match the legacy position fields. Package binding validation additionally checks
proxy revisions, support identities, render hashes, dimensions and matrices.
Missing fields or unsupported spatial versions fail; consumers never synthesize
height or rotation. Old snapshots omit the extension and remain explicitly
outside 3D readiness. There is no breaking change to the 2.x legacy boundary.

Matrices are row-major and multiply column vectors. The Y-up presentation basis
is `B(x,y,z)=(x,z,-y)`. Convert orientation by conjugation `B R B^-1`; compose
raw vertices as `B T_world_from_body T_body_from_asset`. `spatial_cases.json`
tests cardinal headings, asymmetric markers, nonzero positions/origin, nested
rigid asset offsets, a ramp, q/-q, and negative snapshot shapes through Python,
TypeScript and C++. Client ingress invokes this validation from the ordinary
training-message parser. The adapter tests resolve the same package IDs/hashes;
they do not constitute a connected rendered walkthrough.

Read-only `GET /api/v2/scenes/capabilities` advertises the current package pins,
profile/limits, `world_3d=true` for scene data, and separately
`authoritative_3d_motion=true` for the bounded BE-M2-08 controller, `local_manoeuvre=false`,
`recorded_perception=false`. It does not upgrade an existing planar session.
`GET /api/v2/scenes/{scene_revision}/manifest` returns exactly the checksummed
manifest bytes; `/assets/{asset_id}` returns verified GLB bytes. Both support
immutable caching and ETags. Asset paths in the manifest are package-relative;
HTTP requests use asset IDs, never filesystem paths. No binary assets enter
snapshots.
Manifest responses use canonical sorted, two-space JSON with one LF terminator;
equivalent source formatting cannot change the served bytes or manifest hash.
All three manifest parsers validate required metadata, bounded collections and
the road surface's full-width strip against its rigid M2 support slab. Shared
admission regressions live in `fixtures/spatial_manifest_cases.json`.

`POST /api/v2/scenes/{scene_revision}/readiness` checks the reference pins,
profile, client `render_ready`, and hashes of every mandatory asset. It returns
explicit fallback IDs for absent optional detail. A result is labelled
`CLIENT_REPORTED_RENDER_READINESS`; it does not enable a session, mutate state
or certify a renderer. Clients must re-handshake after a scene/session switch
or graphics loss. Bad input returns bounded `SCENE_NOT_READY` errors without
echoing the body. The handler streams at most 16 KiB before parsing.

The same generator emits `route_edit_request.schema.json`,
`route_edit_preview.schema.json`, and `route_approval.schema.json` from
`route_edit.py`, with matching shape fixtures. Route previews carry the exact
draft revision, route/scene/closure dependencies, directed path, map polyline,
canonical mandatory checks, and original/edited metrics. `approvable` is true
only when mandatory checks are known and pass or warn. The shape fixtures are
illustrative wire examples; the operational values come from the curated map
overlay described in `backend/src/services/ROUTE_EDIT.md`.

`route_editor_context.schema.json` and its fixture describe the bounded, read-only
graph returned by `GET /api/v2/sessions/{session_id}/routes/{route_id}/editor`.
It carries node coordinates in local metres, directed edges with active closure
flags, the committed route edges, route/session/scene revisions, and backend
estimate assumptions. It supplies mouse picking and numeric waypoint entry;
only a fresh route preview and the approval endpoint can authorize a detour.

## Live session stream (2.x, additive)

`stream.py` and `stream_message.schema.json` define
`WS /api/v2/sessions/{session_id}/stream?traffic=&after_tick=&snapshot_interval_ms=`, which pushes
what a client would otherwise poll. Every message is one JSON text frame with a `type`:

- `snapshot`: `{"type", "session_id", "snapshot"}`, the newest committed `FULL_SNAPSHOT` exactly as
  `GET /snapshot` returns it. The first message of every stream, then each newer one, at most one per
  `snapshot_interval_ms` (100-5000, default 250). Snapshots coalesce and never go backwards; the
  stream never carries events. A client reads events from `GET /events` when a snapshot's
  `event_sequence` is past its cursor, so the replay rules above are unchanged.
- `traffic`: `{"type", "session_id", "frames"}`, the frames after the last sent, as
  `GET /traffic` returns them, while the client wants traffic and the session has some.
- `heartbeat`: `{"type", "session_id"}` after 5 s with nothing else.
- `error`: `{"type", "session_id", "error": {"code", "message", "retryable"}}`. `INVALID_PAYLOAD`
  refuses a client message and the stream goes on; any other code ends it, and the server closes
  with 4404 (`INVALID_REFERENCE`), 4403 (`ROLE_DENIED`) or 1013 (`SESSION_RECOVERING`). 1001 means
  the backend is stopping.

The client sends `{"type": "traffic", "enabled": bool, "after_tick": int}` (`after_tick` optional)
to turn traffic frames on or off. The login is the bearer token on the upgrade request. A client
treats the stream as an accelerator only: on any close it polls over HTTP, which reports the
session's state as before, and reconnects later. Message types it does not know are skipped.
`fixtures/stream_messages.json` has one of each.

## Shared alert cues

`alert_cues.json` is the hand-maintained source for the web and native alert
gates' default cue table. It is a JSON object mapping case-sensitive
`ENTITY_TYPE/EVENT_TYPE` keys to nonempty semantic cue names. Keys without an
entry stay silent; cue names identify sounds, not audio file paths.

The web gate imports this file directly. Native builds embed the same file as
the Qt resource `:/contracts/v2/alert_cues.json` in both `App` and
`native_unit_tests`, so no source checkout or external JSON file is needed at
runtime. Edit this file and rebuild both clients when changing default cues;
do not add separate client tables. The fixture generator does not overwrite it.

The defaults cover incident options ready, an incident detected that a shuttle
will reach, command rejection, command reconfirmation, and session recovery. An
event whose data says `"alert": false` stays silent even when its type has a
cue: the backend sets it on an `INCIDENT_DETECTED` that no shuttle will reach. Both clients test their exact keys and cue
names against fixed expectations to catch accidental semantic changes. Custom
cue tables still replace the defaults. Live-event deduplication, bounded memory,
mute handling, and silent history playback remain the gates' responsibility.

## Transport and version rules

- Canonical coordinates are local metres: x east, y north, z up; headings are degrees counterclockwise from positive x. Speeds are m/s, energy is kWh, charger power is kW, simulation time is seconds, and logical ticks are 50 ms.
- `contract_version` is `major.minor`. Consumers accept `2.x` and additive unknown object fields, retaining them when forwarding. They reject other major versions, missing required fields, unknown enum values, invalid references, and invalid revision relationships. New required fields, changed units, removed fields, or changed enum meanings require version 3. Unknown enum values are **not** treated as a default state.
- IDs are stable opaque strings, unique within their entity collection; never use array indices as identity. Revisions are nonnegative integers scoped to the named aggregate. `session_epoch` changes on recovery; `event_sequence` is monotonic across epochs. Scene and transform revisions identify immutable presentation inputs. An approval compares `expected_revision` with its target aggregate and rechecks dependencies when applied; a rising `tick` is not the command precondition.
- The full snapshot is a consistent baseline with all referenced entities and the included `event_sequence`. Reconnect reads a snapshot at cursor N, then durable events after N. Event replay is strictly contiguous; gaps require a new snapshot. Snapshots may coalesce, durable events may not. `COMMAND_STATUS` distinguishes accepted `QUEUED` from committed `APPLIED` and `REJECTED`. A new epoch invalidates transient client interpolation and old queued approvals need reconfirmation.
- `source.kind` is per entity/event. `RECORDED_SENSOR` requires recording, sequence, frame, and source timestamp; `HISTORICAL_SIMULATION` means replayed simulated history; `TRAINING_SIMULATION` means the live authored run. A scenario inspired by KITTI does not make its synthetic vehicle positions recorded measurements. `source_frame` on simulated data is rejected.
- Error messages are bounded to 512 characters. `field`, `retryable`, and `current_revision` provide actionable validation context. Raw request bodies, stack traces, and binary evidence do not belong in errors. `message.schema.json` validates shape; the parsers also validate cross references, cursor order, and status relationships.

## Entity ownership and references

| Entity | Stable ID | Revision / reference rule |
| --- | --- | --- |
| `ScenarioRevision` | `scenario_id` + `revision_id` | Immutable scene, transform, rule, vehicle, energy, rubric, seed and provenance pins |
| `TrainingSession` | `session_id` | References one scenario revision; carries lifecycle, epoch, aggregate revision and authority |
| `FleetVehicle` | `vehicle_id` | Route ID and revision must match a snapshot route; charging requires occupied port |
| `RouteRevision` | `route_id` + `revision` | Directed edges, stops, scene revision and declared distance/duration |
| `Disruption` | `disruption_id` | Affected directed edges, active tick interval, reason and provenance |
| `Incident` | `incident_id` | References candidate IDs and, when set, a vehicle; exposed vehicle IDs must exist and match `exposures` in order; state is explicit |
| `Candidate` | `candidate_id` | References incident and, when set, the vehicle the option is for; action, validation, expiry, dependency revisions, assumptions |
| `Charger` | `charger_id` | Occupancy count cannot exceed port count; occupancy references vehicles |
| `Depot` | `depot_id` | Position and charger IDs; every charger must exist and belongs to at most one depot |
| `EnergyForecast` | `vehicle_id` | At most one per vehicle; energy model revision must match the scenario; next depot must exist |
| `ChargeOption` | `option_id` | References vehicle and, when set, charger; action, validation, expiry, dependency revisions, assumptions |
| `DispatcherCommand` | `command_id` + actor/session/idempotency key | Target type/ID/revision and epoch; decision commands require a reason. Authenticated actor is transport identity, never trusted from payload. |
| `Event` | `event_id` + `event_sequence` | Ordered durable record; entity type/ID/revision, tick and provenance |

## Incidents as world events (2.x)

An incident stands for a world event at a place on the road network, not an
effect attached to one shuttle. `Incident.event_type` (`BREAKDOWN`,
`COLLISION`, `SIGNAL_FAULT`, `ROAD_CLOSURE`) and `location` (exactly one of
`edge_id` or `node_id`, with an optional `road_name`) say what and where it is.
`exposed_vehicle_ids` lists, nearest first, the shuttles whose remaining route
crosses its footprint (the scene and the queue behind it) before it clears, and
`exposures` gives each one's distance and ETA (`is_estimate: true`). Exposure is
live data, like a vehicle's position: it is refreshed every half second without
a new `aggregate_revision`, and a change to who is exposed is logged as an
`INCIDENT_EXPOSURE_CHANGED` event with `added_vehicle_ids`, `removed_vehicle_ids`
and a `headline`. The first event of an incident (`INCIDENT_DETECTED` or
`OPTIONS_READY`) carries `event_type`, `location`, `footprint_edge_ids`,
`exposures` and a `headline` such as "Shuttle 03 reaches a collision on
Lorong 6 Toa Payoh in about 85 s".

`Incident.vehicle_id` is now optional: it names the shuttle held for a decision,
or the one a random incident was placed ahead of, and is null for a scheduled
world event that belongs to no shuttle. This is the one loosening an older 2.x
consumer can notice; the backend only sends null for events a scenario's
timeline schedules, and the curated scenario's timeline is empty.
`Candidate.vehicle_id` names the shuttle an option is for. All the new fields
are optional, so older 2.x snapshots still parse.

## Decisions per exposed shuttle (2.x)

Every shuttle an incident exposes is offered its own candidates, each with its
`vehicle_id`: `KEEP_ROUTE` (drive on; `WAIT` is its name in older journals),
`ROAD_DETOUR`, `ESCALATE` (once per incident) and `HOLD_AT_STOP`. A decision is
recorded in `Incident.responses` as an `IncidentResponse`: the shuttle, its
`action`, `decided_tick`, whether it came `before_arrival` (before the shuttle
entered the footprint), when what it set going ended (`completed_tick`) and, for
a hold, the stop (`hold_stop_id`). A shuttle that drives into the incident with no
decision gets the action `NO_DECISION`; a later decision replaces it. An incident
has at most one response per shuttle, and each must name a shuttle of the
snapshot. `Incident.clears_at_tick` is when the event is expected to clear; an
`ESCALATE` brings it forward. `IncidentExposure.keep_route_delay_s` estimates how
long keeping its route would hold the shuttle up. Each decision's
`INCIDENT_DECISION_RECORDED` event carries the shuttle's own assessment, with
`delay_avoided` in place of `route_efficiency`. Every field is optional, so older
2.x snapshots still parse; `incident_decisions_snapshot.json` is the golden
fixture.

## Battery and charging (optional 2.x additions)

`FullSnapshot.depots`, `energy_forecasts` and `charge_options` are optional and default to empty lists, so older 2.x snapshots still parse and the contract stays at version 2. The values are computed by the backend energy model (`backend/src/energy`); clients display them and never recompute energy. Every forecast and option has `is_estimate: true`.

- `EnergyForecast`: `reserve_kwh`, `reserve_margin_kwh` (negative when below reserve), `loops_remaining` (`null` when unavailable), and the next depot opportunity. `next_depot_id` needs `next_depot_eta_s`; without a depot, the ETA and `reaches_depot_above_reserve` must be `null`.
- `ChargeOption`: a battery decision for one shuttle, modelled on `Candidate`. `action` is `CHARGE_NOW` (requires `charger_id`), `CONTINUE` (no charger and `out_of_service_s` of 0) or `RETURN_TO_DEPOT`. `out_of_service_s`, `battery_after_kwh` and `loops_after` give the consequence; `battery_after_kwh` cannot exceed the vehicle's capacity, and an option without a charger cannot end above the vehicle's current battery.
- `min_in_service_vehicles` (optional, `null` = unknown) is the fleet consequence of an option: the lowest number of shuttles still serving (`IN_SERVICE`, `DWELLING` or `DETOURING`) while it plays out. `CHARGE_NOW` takes its shuttle out; `CONTINUE` keeps everyone. It cannot exceed the number of shuttles in the snapshot.
- `offered_at_tick` (optional, `null` in older 2.x options) is the tick the shuttle's current battery decision was first offered. Reissued offers keep it, so a response time runs from the first offer; it cannot be after `expires_at_tick`.
- Numbers must be finite; durations, reserves, battery levels and loop counts cannot be negative.
- `charge_option_revisions` maps each option ID to the last revision issued for it. Entries stay after an offer is withdrawn, so a re-created offer gets a new revision. An active offer must match its entry when present; an omitted map or missing entry uses the offer's own revision as the last issued, preserving older 2.x snapshots.
- A `RESERVE_CHARGER` command targets the charger and names the chosen offer in its payload: `{"option_id": ..., "option_revision": ...}`. The shared validator rejects it if the offer is gone or expired, is not a `CHARGE_NOW` for that charger, has a different revision, has `FAIL` or `UNKNOWN` validation, or depends on a shuttle or charger revision that has changed.
- The resulting `CHARGER_RESERVED` event retains the chosen option's `min_in_service_vehicles` forecast (`0` is valid; `null` means unknown) for debrief and export after the option is withdrawn. Older events may omit this field; clients leave that historical forecast unavailable rather than reconstructing it from the current fleet.
- A `CONTINUE_SERVICE` command keeps a low shuttle in service. It targets the shuttle (`target_type` `VEHICLE`, at the shuttle's `aggregate_revision`), names its `CONTINUE` offer in the same payload shape, and needs a reason. The shared validator applies the same option checks, and rejects an offer that is not the targeted shuttle's `CONTINUE`. `CONTINUE` is only offered to a serving shuttle, and the backend rejects it with `INVALID_TRANSITION` otherwise. `CommandStatus.target_type` can therefore also be `VEHICLE`. A success records a `SERVICE_CONTINUED` event on the shuttle with the same data as `CHARGER_RESERVED`.
- `charge_decision_deferrals` (optional map, shuttle ID to loops) records the loops remaining when the operator chose `CONTINUE`; that shuttle is offered a new decision only once it falls below that number. Keys must be shuttles in the snapshot, and values cannot be negative. A shuttle at zero loops cannot fall further, so it is never deferred: a `0` entry means "not deferred" and is dropped when the snapshot is parsed, so snapshots saved by earlier builds still load.

### Time-slot charging bookings (optional 2.x addition)

`FullSnapshot.charge_bookings` (optional, default empty) lists shuttles' holds on charger ports. Clients display them and never compute windows or energy.

- `ChargeBooking.status`: `SCHEDULED` holds a future slot and occupies nothing; `ACTIVE` means the shuttle took a port at dispatch and holds it until it has cleared the bay; `COMPLETED`, `CANCELLED` and `MISSED` are final (`status_reason` says why). `origin` is `OPERATOR`, `CHARGE_NOW` or `SAFETY_RETURN`: every port hold appears on the schedule, and a charge-now or safety return replaces that shuttle's own slot.
- A shuttle has at most one live (`SCHEDULED` or `ACTIVE`) booking. Occupancy still comes only from `Charger.occupied_vehicle_ids`. An overrunning `ACTIVE` booking has its `end_tick` extended by the backend; a due slot waits for its port and shuttle, and is `MISSED` once its window has passed.
- `POST /{session_id}/charge-schedule-previews` takes a `ChargeScheduleRequest` and returns a `ChargeSchedulePreview` (`validation`, structured `issues`, and estimated consequences). It reserves nothing. The commit runs the same check.
- `SCHEDULE_CHARGE` targets the charger at its current revision, with a reason and payload `{"vehicle_id", "start_tick", "replaces_booking_id"}`; a non-null `replaces_booking_id` moves that `SCHEDULED` booking in one step. `CANCEL_CHARGE_BOOKING` targets the booking's charger with payload `{"booking_id"}`; an `ACTIVE` booking cannot be cancelled (`INVALID_TRANSITION`). Overlaps are rejected with `CHARGER_CONFLICT`, and `CHARGE_NOW` is not offered on a charger another shuttle has booked for that time. The events are `CHARGE_SCHEDULED`, `CHARGE_BOOKING_CANCELLED`, `CHARGE_BOOKING_STARTED` and `CHARGE_BOOKING_MISSED`.

`fixtures/snapshot.json` carries two chargers, one depot and five forecasts. `fixtures/low_reserve_snapshot.json` is the same snapshot with shuttle-05 at 7 kWh (1 kWh above reserve, 2 loops left) and a `CONTINUE` and a `CHARGE_NOW` option. `backend/tests/test_contract_battery_v2.py` recalculates the fixture numbers with the energy model.

## Permitted lifecycle transitions

The live fleet adapter accepts the session-targeted `ARCHIVE` command from
READY, RUNNING or PAUSED. It ends the attempt, releases runtime capacity and
retains its final snapshot, events and idempotent command receipts. See
[session creation](../../docs/session_creation.md) for the authenticated controls.

The transition tables in `models.py` are the executable source for Python. Other consumers display these states but do not authorize transitions. Same-state updates are ordinary revision changes, not transitions.

| Machine | From | Allowed next states |
| --- | --- | --- |
| Session | DRAFT | READY, FAILED |
| Session | READY | RUNNING, ARCHIVED, FAILED |
| Session | RUNNING | PAUSED, COMPLETED, RECOVERING, ARCHIVED, FAILED |
| Session | PAUSED | RUNNING, COMPLETED, RECOVERING, ARCHIVED, FAILED |
| Session | RECOVERING | PAUSED, FAILED |
| Session | COMPLETED | ARCHIVED |
| Session | ARCHIVED, FAILED | none |
| Vehicle | STANDBY | IN_SERVICE, RETURNING_TO_DEPOT, OUT_OF_SERVICE |
| Vehicle | IN_SERVICE | DWELLING, BLOCKED, DETOURING, RETURNING_TO_DEPOT, OUT_OF_SERVICE |
| Vehicle | DWELLING | IN_SERVICE, BLOCKED, RETURNING_TO_DEPOT, OUT_OF_SERVICE |
| Vehicle | BLOCKED | AWAITING_GUIDANCE, IN_SERVICE, OUT_OF_SERVICE |
| Vehicle | AWAITING_GUIDANCE | DETOURING, IN_SERVICE, RETURNING_TO_DEPOT, OUT_OF_SERVICE |
| Vehicle | DETOURING | IN_SERVICE, DWELLING, BLOCKED, RETURNING_TO_DEPOT |
| Vehicle | RETURNING_TO_DEPOT | STANDBY, QUEUED_FOR_CHARGE, OUT_OF_SERVICE |
| Vehicle | QUEUED_FOR_CHARGE | CHARGING, STANDBY, OUT_OF_SERVICE |
| Vehicle | CHARGING | RETURNING_TO_DEPOT (move to depot parking), STANDBY, OUT_OF_SERVICE |
| Vehicle | OUT_OF_SERVICE | STANDBY |
| Incident | DETECTED | AWAITING_REVIEW, OPTIONS_READY, RESOLVED |
| Incident | AWAITING_REVIEW | OPTIONS_READY, ESCALATED, UNRESOLVED |
| Incident | OPTIONS_READY | ACTION_COMMITTED, WAITING, ESCALATED, EXECUTING, DETECTED, RESOLVED, UNRESOLVED |
| Incident | ACTION_COMMITTED | EXECUTING, AWAITING_REVIEW |
| Incident | EXECUTING | RESOLVED, UNRESOLVED, AWAITING_REVIEW, OPTIONS_READY, ESCALATED, WAITING |
| Incident | WAITING | AWAITING_REVIEW, OPTIONS_READY, EXECUTING, ESCALATED, RESOLVED, UNRESOLVED |
| Incident | ESCALATED | OPTIONS_READY, EXECUTING, RESOLVED, UNRESOLVED |
| Incident | RESOLVED, UNRESOLVED | none |

An incident's state sums up its shuttles' responses: `OPTIONS_READY` while one
is still to be decided for (and again when another becomes exposed), then
`EXECUTING` while a detour round it is driven, `ESCALATED` once assistance is
requested, `WAITING` while every decided shuttle keeps its route or holds, and
`DETECTED` when nobody has had to decide. It settles when the event clears.

Transitions also require domain guards, such as route feasibility, available energy, and charger reservation. The table alone never approves a command.

## Migration from M1 single trajectory

| M1 contract | V2 training mapping |
| --- | --- |
| `simulation_id` and `SimulationResponse.status` | New `session_id`, `TrainingSession.status`, `session_epoch`; create a new training session, do not reinterpret an M1 ID |
| WebSocket `TICK.ego` | One `FleetVehicle` in a `FULL_SNAPSHOT` only when an actual scenario supplies fleet identity; no automatic conversion of a recorded ego sample to five shuttles |
| `trajectory.points` and `source_frame` | Historical evidence with explicit `RECORDED_SENSOR` provenance for actual source frames; synthetic route playback is `HISTORICAL_SIMULATION` |
| M1 `coordinates` origin/axes | Same canonical axes and metres; v2 fixes units in field names and pins `transform_revision` |
| M1 `route_id` preview | New `RouteRevision` with directed edge IDs, stops, scene revision and aggregate revision; M1 previews cannot be approved as v2 revisions |
| M1 `START`/`PAUSE`/`RESUME`/`STOP` controls | V2 `DispatcherCommand` lifecycle commands, durable status and event replay; `STOP` maps to a scenario-specific completion command when defined, never implicitly |
| M1 `OBSTACLE_TRIGGERED` and `decision` | V2 `Disruption`/`Incident`/`Candidate` and durable `Event`; fixture reasons from M1 do not become assessed decisions |
| M1 error body | V2 bounded `ERROR` envelope with code, retryability, field and current revision |

The v1 and v2 endpoints remain separate. The M1 adapter may render a labelled legacy playback view; it must not fabricate fleet, incident, charging or assessment records.
