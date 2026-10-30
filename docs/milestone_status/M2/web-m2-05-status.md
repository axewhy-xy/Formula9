# WEB-M2-05: Battery choice and simple debrief

26 October 2026. The web slice integrates the existing backend battery and
incident services. **Full task acceptance remains open**: the backend cannot
record `CONTINUE` decisions. Minimum active-fleet forecasts and provisional
battery assessments depend on the backend additions currently on `JianWei`;
the web displays them when supplied and marks missing values unavailable.
This is not M2 exit evidence for the complete
five-view exercise or paired web/native run.

## Delivered

The route lab links to `/?training=1`. Open an existing v2 session by ID; the
same session, selected shuttle, reason drafts and command outcomes remain in
the workspace while navigating Battery choice, Debrief and Event replay.
Both themes use at least 14 px text and visible keyboard focus.

- Battery capacity/current energy, reserve, reserve margin, remaining loops,
  depot opportunity and two charger allocations come from validated backend
  snapshots. Consequences and assumptions are labelled estimates. The web
  client does not compute energy, reserve or coverage predictions.
- Charging submits the current offer/revision, charger revision, session epoch,
  reason, command ID and idempotency key to the existing command endpoint.
  Pending, queued, applied, rejected, expired, reconfirmation and unknown
  outcomes remain distinct. Only refreshed server occupancy displays a
  reservation. A lost or malformed acknowledgement retains the command lock;
  automatic retries replay its identical POST with backoff from 1 to 30 seconds,
  allowing the backend to reconcile or finish the original command. Manual
  recheck remains available. Complete backend HTTP errors settle as rejected,
  except 503 and `SESSION_RECOVERING`, which retry. Refresh and rejection retain
  the reason. Retries stop when the workspace unmounts.
  Records refresh after a 2-second pause between reads; fresh records remain
  usable during background refresh, subject to the 5-second freshness limit.
- Debrief shows committed incident decisions/events, simulation and wall
  response times, observed outcome with units, provisional assessment scope,
  rubric revision, awarded/maximum values and backend assumptions. Missing
  dimensions/results remain unavailable. No score is calculated in the web
  client.
- The JSON summary reads fresh server records, includes snapshot/scenario
  provenance, incident evidence and up to 1,000 session events, including
  charging reasons/consequences and any server-supplied battery assessment.
  Availability reflects assessments in charging events and optional coverage
  forecasts on charge offers. Missing retained history or truncation is
  explicit. An epoch/event change during export requires another attempt.
  Failed reads do not produce a download; the UI says a successful browser
  download was requested, without asserting that the file was saved.
- The replay link opens the selected session's paginated, read-only durable
  event log. It does not change the shared simulation clock or substitute
  generated camera evidence. Missing prehistory is reported; a current
  checkpoint offers a valid alternative cursor.

## Verification

Windows, 26 October 2026:

```powershell
# From frontend/; workspace temp avoids the sandbox's process-specific TEMP paths.
New-Item -ItemType Directory -Force ../build/web-m2-05/tmp | Out-Null
$env:TEMP = (Resolve-Path ../build/web-m2-05/tmp).Path
$env:TMP = $env:TEMP
npm run typecheck
npm test
npm run build

# From the repository root.
$env:PYTHONPATH = 'backend;.'
& backend/.venv/Scripts/python.exe -m pytest backend/tests/test_battery_decision_service.py backend/tests/test_contract_battery_v2.py backend/tests/test_incidents.py -q --basetemp=build/pytest-web-m2-05 -p no:cacheprovider
```

Typecheck and build passed. All **160 frontend tests** and **114 selected
backend tests** passed. The frontend suite includes lost acknowledgements,
repeated clicks, conflicts without displayed reservations, stale/expired/unknown
offers, foreign replies, session changes during submission, missing score
dimensions, failed downloads, unavailable history and an export spanning a
revision change. Existing adapter/coordinate/renderer regressions still pass.
Build retains the warning about a JavaScript chunk larger than 500 kB; these
checks make no performance claim. The backend emitted its existing
Starlette/httpx deprecation warning.

PR #57 feedback verification, 26 October 2026: TypeScript checks, all **182
frontend tests**, and the production build passed. Added regressions cover
omitted/partial offer revision history, explicit revision mismatches, complete
HTTP rejections, transient and malformed failures, identical-command automatic
retries with capped backoff, manual recheck concurrency, retry lifecycle cleanup,
optional coverage values (including zero), and charging assessment availability.
The production build retains the existing chunk-size warning. The optional
backend fields were checked against `JianWei`; no backend branch was merged
into this change.

The shared low-reserve fixture verifies 7 kWh current battery, 6 kWh reserve,
1 kWh margin, two remaining loops, and a 1,200 s charge estimate of 22 kWh / 42
loops. The debrief JSON fixture is an actual response from the incident service,
exported using the harness below; its rubric maxima match
`backend/fixtures/m2_incident.json`.

The browser run against real FastAPI services and an isolated SQLite store
confirmed one reservation for shuttle-05 on charger-1, charger revision 2,
and an unchanged current battery. The curated map's shorter route uses
6.1 kWh current energy, 6 kWh reserve and two loops, with a 21.1 kWh charge
estimate. The debrief showed a resolved incident, 0 s simulation response time,
10 s observed delay and the backend's **98.59** provisional points under
`m2-incident-provisional-r1`. The downloaded JSON contained all six session
events, including the single `CHARGER_RESERVED` event and its reason. Event
replay showed those same records. Stopping the test backend and requesting
another download visibly reported failure and cleared the prior success
message.

Screenshots from that run:

- [Dark battery view](evidence/web-m2-05-battery-dark.jpg)
- [Light debrief view](evidence/web-m2-05-debrief-light.jpg)

## Reproduce the browser exercise

```powershell
# Repository root, terminal 1. This process alone supplies a test actor.
$env:PYTHONPATH = 'backend;.'
& backend/.venv/Scripts/python.exe -m backend.tests.web_m2_05_demo --serve

# frontend/, terminal 2.
npm run start -- --host 127.0.0.1
```

Open `http://127.0.0.1:5173/?training=1&session=web-m2-05`. Inspect the two
charger statuses and the continue/charge estimates for shuttle-05. Enter a
reason and submit charging; wait for Applied and the server-confirmed
allocation. Visit Debrief, download the JSON and follow Event replay. Switch
views/themes and confirm shuttle/reason identity is retained. Stop terminal 1
and retry the download to inspect the failure state.

The fixture server is a bounded test setup, not the live fleet actor. Its
isolated database is under `build/web-m2-05/`; production authentication is
unchanged. To regenerate the committed debrief response, use the same command
with `--export-fixture` instead of `--serve`.

## Remaining dependencies

- `CONTINUE` is now recorded with the `CONTINUE_SERVICE` command (see the
  [backend battery handoff](be-m2-05-battery-status.md)). The web submits it
  pinned to the shuttle's revision and, once applied, says the shuttle stays
  in service until it uses another loop. `RETURN_TO_DEPOT` offers remain a
  visible preview.
- The web accepts optional `min_in_service_vehicles` on each charge offer and
  includes provisional assessments from `CHARGER_RESERVED` events in summary
  availability. These backend additions are currently on `JianWei`; integration
  and the scripted coverage conflict still need verification after merge.
  Older responses retain explicit unavailable labels. Swaps remain deferred
  by backend M2.
- Production session creation/authentication, continuously updated energy
  forecasts, actual charging/depot movement and release of ports require the
  shared backend/session actor work. Existing authenticated sessions work;
  the local fixture provides a reproducible integration check only.
- The command ledger and reason drafts live for this mounted workspace.
  Recovery across browser exit/reload and cross-tab coordination belong to
  WEB-M2-02. The five-feature shared shell belongs to WEB-M2-01. The summary is
  a basic JSON export, and replay is an event log; persisted reports, complete
  motion replay and a dedicated battery assessment view are not claimed here.
