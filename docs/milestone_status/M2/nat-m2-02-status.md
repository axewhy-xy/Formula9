# NAT-M2-02 implementation status

Status: implemented in native transport/store/command paths; paired native/web recovery drill remains milestone integration evidence.

`TrainingSessionStore.*` keeps ordered durable events separate from replaceable full snapshots. Snapshot acceptance validates selected session identity, epoch monotonicity, snapshot ID reuse, scene/transform/scenario compatibility, and stale tick/sequence/authority regressions before mutating visible state. Event acceptance validates session/epoch baseline and strict contiguous sequence growth; duplicates or gaps reject the whole batch atomically.

`BackendClient.*` now keeps one authoritative replay cursor per selected run and requests full snapshot resync on event cursor mismatch, epoch mismatch, malformed payload, rejected event batch, or transport failure. During resync or disconnect, training availability is marked stale and decision submissions are blocked until a validated snapshot is accepted again.

Charging decisions (`RESERVE_CHARGER` and `CONTINUE_SERVICE`) and route approvals both carry command IDs, idempotency keys, expected epoch, and expected aggregate revisions. The workspace command ledger displays `QUEUED`, `APPLIED`, `REJECTED`, and `NEEDS_RECONFIRMATION` distinctly, including failure code and effective tick when supplied. `QUEUED` is treated as durable acceptance; only terminal statuses release the command.

While the session is still live, an ambiguous charging-command reply (lost, truncated, or non-final) is reconciled by replaying the identical POST body with the same command ID and idempotency key. After freshness loss/reconnect, outstanding charging decisions switch to command-status reconciliation (`GET /api/v2/sessions/{id}/commands/{idempotency_key}`) instead of sending a fresh approval automatically. Unknown or missing recovered status surfaces `NEEDS_RECONFIRMATION`, requiring explicit operator review and resubmission.

## Declared bounds and limits

- Event history retained in native store: latest `1000` entries (`SessionStore::MaxRecentEvents`).
- Event replay page size: `100` events per request (`/events?limit=100`).
- Recovery event backfill window: up to `500` sequences before new-epoch baseline.
- Command ledger rows rendered in workspace: latest `200` command statuses.
- Route transport payload bound: `512 KiB` per context/preview/approval response.
- Route approval resend backoff: starts `1 s`, doubles to max `30 s`.
- Charging command reconciliation backoff:
  - post-reply ambiguity while still live: `100 ms` to max `5 s` (same command body).
  - post-reconnect status query: `250 ms` to max `5 s` (status endpoint only).

## Command-state transitions used by native UI

- Local submit starts as pending send, then server `QUEUED` marks durable acceptance.
- `QUEUED -> APPLIED` or `QUEUED -> REJECTED` is authoritative terminal outcome.
- Recovery/freshness loss can move unresolved work to `NEEDS_RECONFIRMATION` (manual operator resubmission required).
- Expired offers return `REJECTED` with failure code `OPTION_EXPIRED` and remain non-actionable until a fresh proposal/option is loaded.

