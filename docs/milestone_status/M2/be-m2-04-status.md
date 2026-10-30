# BE-M2-04 pre-trip route edit

The curated `backend/fixtures/m2_route_detour.json` overlay binds to directed
segments in the committed `Native/Assets/Phase5_Map/map.db` and sibling
`map.osm`. Its direct segment is closed at tick 0. The six-segment detour serves
`stop-terminal`. The overlay declares the operating nodes, known turns,
vehicle and road clearance, cruise speed, stop dwell, travel energy, and
auxiliary load. These are exercise assumptions, not inferred roadworthiness
measurements. Changing the map topology requires updating and checking this
overlay.

The request, preview, and approval models and generated JSON Schemas live in
`contracts/v2/route_edit.py` and `contracts/v2/*.schema.json`.

An authenticated client uses:

1. `POST /api/v2/sessions/{id}/route-previews` with `route_id`,
   `expected_route_revision`, `draft_revision`, and either ordered
   `via_node_ids` or explicit `directed_edge_ids`. The endpoint does not change
   the session. It returns a bounded preview ID, polyline, original and edited
   distance/duration/stop coverage/energy, and canonical check results. A
   `FAIL` or `UNKNOWN` mandatory check sets `approvable=false`.
2. `POST /api/v2/sessions/{id}/route-approvals` with the exact `preview_id`,
   `preview_revision`, command and idempotency IDs, reason, and explicit
   `target_vehicle_ids`. Approval is pre-trip only (`READY`). The M2 route
   pattern requires the target set to include every shuttle assigned to that
   route revision. The service revalidates graph feasibility, route and closure
   revisions, then durably accepts and applies through `TrainingStore`. The
   response is the committed command status; route, shuttle assignments,
   event, and checkpoint are committed together. A repeated idempotency key
   returns the original outcome even if the preview cache is gone; changed
   content conflicts. A same-process retry of a queued approval revalidates
   from its durable command and completes or rejects it.

The generic v2 command POST rejects `APPROVE_ROUTE`; it cannot bypass the
preview validator. Route search runs outside the simulation step. A second
approval based on the old route revision is rejected. Previews are process
local and bounded to 256; after restart the client requests a new preview,
while accepted commands follow the store's recovery rules. A stale staged
boundary is durably rejected if another command commits before approval.
This slice does not
provide partial route-pattern assignment or timed closures, which are M3 work.

`distance_m` sums graph segment lengths. `duration_s` sums segment traversal at
the lower of declared cruise speed and graph speed estimate, plus declared
dwell for covered required stops. `energy_kwh` is travel distance times
`travel_kwh_per_km` plus duration times `auxiliary_kw`. The same assumptions are
used for original and edited routes.

Run `python -m pytest backend/tests/test_route_edit.py -q` from the repository
root. The tests bind the curated overlay to the committed map and cover the
detour, wrong direction, no route, missed stop, discontinuity, unknown rules,
stale revisions, idempotency, and approval conflict.
