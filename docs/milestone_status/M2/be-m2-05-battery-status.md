# BE-M2-05 battery decision

This is the battery half of BE-M2-05. The incident half (scripted
obstruction, wait/escalate/detour options, provisional score) is separate
work.

The energy model lives in `backend/src/energy/`. `model.py` covers energy
use and charging; `ENERGY_R1` is 0.25 kWh/km, 1.5 kW auxiliary load, 0.9
charge efficiency, and a 15% reserve. `forecast.py` covers reserve, margin,
and loops remaining. `chargers.py` covers port reservation. The values are
versioned training estimates, not calibrated vehicle data. The reserve is a
training rule: a shuttle may go below it, and the margin then goes negative.

`backend/src/energy/decisions.py` turns a `FullSnapshot` into the two optional
contract lists that the native Charging tab (NAT-M2-06) displays:

- `energy_forecasts(model, snapshot)` returns one forecast per routed shuttle.
  Each forecast has the reserve, the margin above reserve, and the complete
  loops left. It also has the next depot with its ETA and whether the shuttle
  arrives above reserve. The depot fields are `null` when there is no depot or
  the shuttle is stopped. The ETA assumes the shuttle drives the rest of its
  route at its current speed, with route progress taken from `position.x_m`.
  This is a fixture simplification. The session actor should supply real
  route progress.
- `charge_options(model, snapshot, charge_s=1200)` makes a battery decision
  for each shuttle at or below `LOW_LOOPS` (3) loops that can still move to
  `RETURNING_TO_DEPOT` and is not already on a charger port, so a shuttle that
  has just reserved a port gets no new choices. There is always a
  `CONTINUE` option. There is also a `CHARGE_NOW` option at the first charger
  by ID with a free port, which gives the battery and loops after charging.
  Option IDs include the shuttle, for example
  `charge-option-shuttle-05-charger-1`. Options expire
  `OPTION_LIFETIME_TICKS` (100 ticks = 5 s) after they are offered. They pin
  the shuttle and charger revisions in `dependency_revisions`. An offer already
  in the snapshot stays frozen, with the same revision, expiry and numbers,
  while it is unexpired and those revisions are unchanged. It is reissued with
  the next `revision` when a dependency changes or the offer expires. A
  no-charger offer is also reissued once the draining battery falls below
  what it promised. The numbers a trainee chose are therefore the numbers
  recorded. The snapshot's `charge_option_revisions` map keeps the last
  revision issued for each option ID, even after the offer is withdrawn (for
  example while the shuttle is blocked). A re-created offer therefore never
  reuses a revision that an old choice could match.
- `with_battery_decisions(model, snapshot)` returns a copy with
  `energy_forecasts`, `charge_options` and `charge_option_revisions` filled in.

Each option also carries `min_in_service_vehicles`, the minimum
active-fleet consequence that WEB-M2-05 (#44) asks the server to supply.
It is the number of shuttles still serving (`IN_SERVICE`, `DWELLING` or
`DETOURING`) while the option plays out: `CONTINUE` keeps the current count,
and `CHARGE_NOW` removes its shuttle if that shuttle was serving. In the
low-reserve fixture the values are 5 and 4. A frozen offer is reissued
when this number changes, even if its own shuttle and charger have not.

Each option also carries `offered_at_tick`, the tick the shuttle's current
decision was first offered. A reissued offer keeps it, and an offer that
appears later in the same decision (a charger port frees up) takes the
shuttle's earliest one. It starts again only after the shuttle has had no
offers. In the low-reserve fixture it is 200.

These are pure functions, and they reproduce `contracts/v2/fixtures/snapshot.json`
and `low_reserve_snapshot.json` exactly. **They are not called during a live
session yet.** The BE-M2-02 session actor should call `with_battery_decisions`
for each snapshot it commits, so the revision history is carried forward. Until it does, live
snapshots carry empty lists and the Charging tab shows "—".

An authenticated client records a battery decision with
`POST /api/v2/sessions/{id}/commands`. To charge, it sends a
`RESERVE_CHARGER` command that targets the charger at its current
`aggregate_revision`. To keep the shuttle in service, it sends a
`CONTINUE_SERVICE` command that targets the shuttle (`target_type`
`VEHICLE`) at its current `aggregate_revision`. Both put the chosen
`option_id` and the offer's `option_revision` in the payload, and both need
a reason. The endpoint hands them to `submit_battery_decision` in
`backend/src/services/battery_decision.py`. That function journals the
command, deferring preconditions so that even an invalid choice gets one
durable outcome, then applies the persisted command. The response is
therefore the committed `APPLIED` or `REJECTED` status rather than `QUEUED`. A
retry with the same idempotency key, even with a new command ID, resumes or
returns the original command. The option checks live in the shared contract
validator (`validate_command_against_snapshot`), so the store enforces the
same rules:

| Check | Rejection code |
| --- | --- |
| Option is still in `charge_options` and not past `expires_at_tick` | `OPTION_EXPIRED` |
| `RESERVE_CHARGER`: option is `CHARGE_NOW` for the targeted charger | `INVALID_PAYLOAD` |
| `CONTINUE_SERVICE`: option is the targeted shuttle's `CONTINUE` | `INVALID_PAYLOAD` |
| `option_revision` matches the offer now in the snapshot | `STALE_REVISION` |
| Option validation is not `FAIL` or `UNKNOWN` | `UNKNOWN_CONSTRAINT` |
| Every `dependency_revisions` entry is unchanged | `STALE_REVISION` |
| `RESERVE_CHARGER`: shuttle may move to `RETURNING_TO_DEPOT` | `INVALID_TRANSITION` |
| `RESERVE_CHARGER`: charger has a free port and the shuttle is not already on one | `CHARGER_CONFLICT` |
| `CONTINUE_SERVICE`: shuttle is serving (`IN_SERVICE`, `DWELLING` or `DETOURING`) | `INVALID_TRANSITION` |

A command whose `expected_revision` no longer matches its charger or
shuttle is also rejected with `STALE_REVISION`.

On success, the following are committed together: the shuttle takes the port
and moves to `RETURNING_TO_DEPOT`, both revisions rise by one, and that
shuttle's options are withdrawn. Every other shuttle's offers are rebuilt
with `with_battery_decisions`, so their `min_in_service_vehicles` counts the
reserved shuttle as out of service, and a `CHARGE_NOW` offer at the
now-full charger moves to the next free charger. The commit also includes a `CHARGER_RESERVED`
event. The event's `data` records the `option_id`, `option_revision`, `vehicle_id`,
the `reason`,
and `response_time_s`, which is the time from `offered_at_tick` (the
first offer of the decision, not the latest reissue) to the decision. It also records the consequence: `out_of_service_s`,
`battery_after_kwh`, and `loops_after`. It also retains the chosen option's
`min_in_service_vehicles` forecast for debrief/export after offers are withdrawn
(`0` is valid; `null` means unknown; older events may omit the field).
It records `rejected_attempts` (see the attempts criterion
below). A busy charger never accepts a second simultaneous reservation. A
repeated idempotency key returns the original outcome.

`CONTINUE` is only offered to a serving shuttle; a low shuttle in another
state (for example `STANDBY`) can only charge. A `CONTINUE_SERVICE` success
keeps the shuttle in service: its revision rises by one and its options are
withdrawn. Other shuttles' offers are
untouched, since no shuttle leaves service. The snapshot's optional
`charge_decision_deferrals` map records the shuttle's loops remaining at
the decision, and `charge_options` does not ask it again until it falls
below that number (it has used another loop). A CONTINUE at zero loops
records no deferral, since there is no further loop to wait for, so the
shuttle is asked again at once. `with_battery_decisions`
drops a deferral once it no longer applies: the shuttle has been asked
again, recovered above `LOW_LOOPS`, lost its forecast, or taken a charger
port. The commit includes a `SERVICE_CONTINUED` event on the shuttle, with
the same data as `CHARGER_RESERVED`.

When a shuttle's charging time is up, the session actor calls
`finish_charging(snapshot, vehicle_id, charged_s)` and commits the result
with `store.commit_snapshot`. The shuttle must be `CHARGING` on a port. It
gains what its charger delivers in `charged_s` (capped at capacity), moves
to `STANDBY` and leaves the port, and both revisions rise by one. Offers
are rebuilt, so a waiting low shuttle can now charge at the freed port. A
`CHARGING_FINISHED` event records the shuttle, `charged_s`,
`energy_added_kwh` and the battery after charging. An unknown shuttle or
energy model is `INVALID_REFERENCE`, and a shuttle that is not charging on
a port is `INVALID_TRANSITION`.

Both decision events also carry an `assessment`: a provisional score of
the decision as it was made, from `backend/src/energy/assessment.py`. It
uses rubric `m2-battery-provisional-r2`, totalling 100 points. It uses the
energy model named by the scenario's `energy_model_revision`, looked up in
`ENERGY_MODELS` (`model.py`). If that revision is unknown, the reservation
still applies, `assessment` is `null`, and every remaining offer is
withdrawn, because its coverage can no longer be recomputed:

| Criterion | Points | Rule |
| --- | --- | --- |
| Reserve protection | 35 | Full points if the shuttle's margin at the decision is at least 0 (decided before falling below reserve), otherwise 0 |
| Response time | 25 | Full points within 30 s of the offer, falling linearly to 0 at 120 s |
| Energy outcome | 25 | Points × min(1, `loops_after` / 20); unknown loops score 0 |
| Attempts | 15 | Minus 5 for each rejected battery command for this shuttle since the decision was first offered, down to 0 |

Rejected attempts are counted from the store's journal
(`TrainingStore.rejected_commands`). A rejected `RESERVE_CHARGER` or
`CONTINUE_SERVICE` counts if it named one of this shuttle's options
(`vehicle_option_ids`), was rejected at or after the decision's
`offered_at_tick`, and was sent in the current session epoch. A rejected
command whose `option_id` is not a string is never counted.

Like the incident assessment, the score is marked `provisional` and
`uncalibrated`, lists its assumptions, and keeps the maximum points per
criterion next to the points earned. It is an authored training rule, not
a real-world assessment.

Not in this slice: driving the shuttle to the depot, starting the charge,
timing it and calling `finish_charging` (all the session actor's job), and
returning a `STANDBY` shuttle to service.

Run these from the repository root:

```bash
python -m pytest backend/tests/test_energy.py backend/tests/test_battery_decisions.py backend/tests/test_battery_decision_service.py backend/tests/test_battery_assessment.py backend/tests/test_contract_battery_v2.py -q
```

The tests reproduce both fixtures and cover the depot and threshold edge
cases, charger choice, and every rejection code. They also cover the
busy-charger race and the HTTP endpoint. Each test file was mutation-checked.
