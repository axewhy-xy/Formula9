# NAT-M2-01 implementation status

Update 27 October 2026: the curated planar fleet scenario now has authenticated
session creation, shared live lifecycle commands and restart recovery. See
[session creation](../../session_creation.md). The integration limitations below
describe the earlier foundation checkpoint.

Status: native foundation implemented; live cross-client acceptance pending backend integration.

The existing `TrainingContract.h` and `TrainingSessionStore.*` parse shared v2 fixtures into typed session, fleet, route, stop, closure, incident, proposal, charger, battery, receipt, event, and provenance values. The native client now converts REST JSON at `BackendClient` and sends typed snapshots and events to a single `SessionStore` in `TrainingWorkspace`. The five read-only views use that selected store. The map currently plots authoritative shuttle positions; route geometry and operational controls belong to NAT-M2-03 through NAT-M2-07.

The Fleet Training tab has explicit Create, Join, and Refresh controls. Join reads `/api/v2/sessions/{id}/snapshot` and does not create a run. It polls snapshots and replays events after the first accepted baseline. Selection changes only after a validated backend snapshot arrives. The existing M1 simulation controls remain a separate legacy flow.

Rejoining the selected session preserves its event replay cursor so events arriving after the new snapshot continue the existing log. The store and Log tab retain the newest 1,000 events; older events remain available through the backend replay endpoint. The Log tab adds new rows and removes expired rows as events arrive instead of rebuilding the table on each poll.

The replay cursor advances only after the store accepts an event batch. Rejected or malformed event responses trigger a fresh snapshot and an explicit event-baseline reset, including when the session epoch is unchanged. The replay cursor and limit are sent as URL query parameters.

The current backend has no `POST /api/v2/sessions` route, so Create reports the backend error. Its v2 reads also require an authenticated `request.scope["user"]`, while production authentication middleware is still pending. A native/web same-run demonstration therefore cannot be claimed yet. The frontend has v2 types and fixture validation, but no paired live session flow was verified here.

Verification on 23 October 2026: `cmake --build build --config Release --target App native_unit_tests -- /m:1 /v:minimal` succeeded, and `ctest --test-dir build -C Release --output-on-failure` passed 42/42 tests. The tests cover shared valid and invalid fixtures, stable IDs and revisions, malformed snapshot rejection, old epoch rejection, incompatible scenario rejection, and atomic event batches bound to the selected session and epoch. A GUI smoke test and paired native/web join remain to be run once the backend exposes authenticated v2 session creation and read access.

Review follow-up on 23 October 2026: the Debug `App` and `native_unit_tests` targets built, and all 8 focused session-store tests passed, including same-session rejoin and bounded event history.

Event-resync follow-up on 23 October 2026: the Debug `App` built and all 46 CTest cases passed. A local HTTP integration test exercises a rejected event batch, a same-epoch snapshot resync, and continued replay from the accepted baseline.
