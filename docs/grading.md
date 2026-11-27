<!--
Author: Nur Syazliyarni Binte Zazali
Date Created: 15/11/2026
Last Modified: 26/11/2026
-->

# Toa Payoh trainee assessment

Click **Assessment mode** in the session header to create and join a scored Toa Payoh session. The header shows **Assessment mode** throughout the attempt. In Route Planning, approve a detour for Shuttle 01 around the pre-trip closure, then click **Start assessment**.

The route editor stays available while a Ready session's stream sends heartbeats without changing its snapshot. Idle heartbeats arrive every two seconds, and each confirmation is usable for at most five seconds. A stale stream triggers a snapshot check before the separate 15-second transport timeout; without a recent heartbeat or snapshot, route, charging and emergency actions wait for a fresh update. After **Suggest detour**, enter a reason before approving. The note under **Approve route** explains any connection, loading, validation or revision check that blocks approval. Paused sessions ask you to resume; a shuttle with no reroute offer is described as having no offer.

There is no **Back** button. To change an incident decision, press the chosen option again: this closes its confirm card without sending anything. The chosen option stays pressable for this even while the others are greyed out because the session is paused, the connection is stale or the options are being refreshed. Pressing **Re-route on the map** again leaves the route editor. If a route has been drawn or suggested, the first press only shows a note under the options, and a second press drops the route.

The countdown appears in the header when the exercise starts and follows the **two-minute simulation clock**. It highlights the last 30 seconds and holds its reading when paused. If the client loses its connection, the header labels the remaining time from the last snapshot it received. Handle the charging decision for Shuttle 05 and the road block that affects Shuttle 01 shortly after it serves the Toa Payoh Police Centre stop. When the session completes, open **Reports → Assessment** for the result, deductions and evidence. The account that created the scored session can submit its commands and read its grade.

The timer reads the duration carried by the session snapshot. The **Time’s up** notice appears when the assessment being viewed completes and disappears after five seconds. Reopening a saved completed attempt does not show it again, and leaving or disabling the screen cancels its scheduled dismissal. The completed result remains available in Reports. If the timer shows **Timer unavailable**, refresh the current session and check that the backend is running the matching version.

Use **Quit assessment mode** to end an attempt early. Confirming archives the session, releases its runtime slot and returns the header to normal mode. Archiving through session maintenance has the same effect. The archived attempt stays selected so its report remains available. Reports shows **Assessment disrupted**, with earned points and any confirmed safety failure retained. An archive only exits assessment mode once the backend confirms it; a rejected command leaves the attempt active.

## Point rules

| Criterion | Points | Evidence |
|---|---:|---|
| Route approval | 10 | The backend accepted the pre-trip route approval. |
| Route stop coverage | 12 | Shuttle 01 serves the designated Toa Payoh Police Centre stop on the approved route. The pre-trip route approval still checks the full route and all its required stops. |
| Route time and energy | 4 each | Compare the applied detour's estimated time and energy with the reference detour around the same closure. Each earns 4 through 110% of the reference, 2 through 125%, or 0 above 125%. |
| Incident response | 20 | A validated detour or escalation earns 20. Holding at a stop or keeping the route until the road clears earns 12. An offered and missed response earns 0. |
| Incident response time | 10 | Full credit within 12 simulation seconds of the first actionable offer, decreasing linearly to zero at 30 seconds. An earlier scenario clearance deadline shortens the window. |
| Incident service resumption | 10 | Shuttle 01 drives 30 metres in service on the applied detour or after the road reopens. Earn 10 within 30 seconds of the first offer, 5 within 60 seconds, or 0 beyond that. |
| Battery reserve | 15 | The minimum observed battery earns 15 at reserve plus 5% of capacity, 8 at reserve, or 0 below reserve. |
| Fleet coverage | 10 | The minimum active fleet count earns 10 for four or five shuttles, 5 for three, or 0 for fewer. Active service includes driving, detouring and routine stop dwell. |
| Charging follow-through | 5 | Shuttle 05 drives at least 20 metres toward the charger on the selected immediate or scheduled booking, or continues in service safely for 45 seconds after the decision. |

Charging decisions are due 35 simulation seconds after the first actionable offer. Rubric r3 observes battery and fleet outcomes for 90 simulation seconds. Its latest offer setting is 20 seconds, and validation checks that this delay plus the observation window fits within two minutes. Starting a new r3 attempt requires an available charging task or a charging decision already applied before Start. Choose a scheduled slot early enough for the shuttle to start moving toward the charger during that window.

## Pass, fail and incomplete results

The pass mark is **75/100**. A recorded, attributable safety breach overrides the point total.

- **Closed-road entry** requires a recorded crossing on an applied route that included the road while the closure was already active.
- **Uncleared incident entry** requires a recorded crossing into the assigned incident area with no effective clearance and at least 10 simulation seconds from an actionable offer.
- **Preventable battery exhaustion** requires a recorded exhaustion of the assigned charging shuttle in service or on the way to a depot. A charge option must have been actionable for at least 10 simulation seconds, and the trainee must have chosen to continue or missed the decision.

A missed task with a completed decision window earns zero. An unoffered decision, missing measurement, or early archive produces an incomplete result with the supported points retained. A confirmed automatic fail takes precedence over incomplete evidence. Rejected commands and duplicate submissions do not deduct points. Assessment records are retained across retakes and excluded from practice purges. Permanent deletion requires a configured supervisor.

An archived assessment carries `disrupted: true` in its report. Its grading status remains `INCOMPLETE`, or `FAIL` when a safety breach is confirmed. A normally completed attempt with missing evidence remains incomplete without being marked disrupted.

## Access and abandoned attempts

Snapshots, event replay, traffic, previews, reports and WebSocket streams of an assessment require the assigned trainee or a configured supervisor. Session lists hide other trainees' assessments. Shared practice sessions keep their existing access rules. The live session count still reports backend capacity.

Set these values on the backend. Supervisor names must match existing authenticated account names.

```dotenv
ASSESSMENT_SUPERVISORS=instructor_account
ASSESSMENT_INACTIVITY_TIMEOUT_S=900
```

The default inactivity limit is 15 minutes of wall-clock time for READY or PAUSED assessments. Applied commands refresh the activity timestamp. Viewing or polling a session leaves that timestamp alone. The timestamp survives restart, and cleanup checks at least every 30 seconds. Cleanup archives the attempt through its actor, saves an incomplete result when appropriate, and releases its runtime slot. Older databases receive an initial activity timestamp when the activity migration runs.

Supervisors can read results, archive abandoned attempts and explicitly delete retained records. Grading decisions remain assigned to the trainee. Archive all uses the permissions returned in the session list.

## Amending the rubric

[The rubric fixture](../backend/fixtures/toa_payoh_assessment.json) supplies the point values, thresholds, enabled safety rules and exercise parameters. Bump its `revision` for an amendment. The backend validates the fixture and saves its contents with each new attempt. Existing live attempts finish against their pinned rules and saved results are returned directly from SQLite.

New semantic scenario identities exclude author and date metadata. The supported evaluator is `assessment-v2`, using the stop, service-resumption and charging-trip milestones. Saved v2 results and pinned rubric settings remain readable.

### Adding a criterion

1. Add an evaluator that takes the attempt's `Evidence` and the criterion's settings. Return points (or `None` if evidence is unavailable), a reason, and the supporting events. [Assessment evaluators](../backend/src/services/assessment.py)
2. Register one `Criterion` in the evaluator version's registry. Put the display label, parameter validators, and any cross-field checks in that entry. If the new rule changes how a saved rubric evaluates, register a new evaluator version and retain the earlier one for recovery.
3. Enable the criterion in the fixture, bump the rubric revision, and redistribute the point values to keep the total at 100. Test the supported outcome and what happens when evidence is missing. Reports renders the new criterion from the API response.

An evaluator can use evidence already in the journal, such as `ROUTE_APPROVED` or `STOP_SERVED`. When a new criterion needs information the simulator doesn't record yet, publish that evidence at the point it occurs and test it through completion and recovery.

Scenario extensions implement the actor's checkpoint, admission, motion and publication hooks. The shared session and workflow code calls those hooks without depending on the scored actor class.

Charging warning thresholds use the actor's `charging_offer_loop_limits()` method. The integrated scenario supplies its default thresholds, and an assessment supplies its own vehicle limits.

Evaluation reads a consistent event boundary and runs outside the shared writer. The writer checks that the snapshot and pinned plan still match before saving the prepared grade in the terminal transaction. A stale preparation is discarded through the normal revision checks.
