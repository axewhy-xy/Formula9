# Approved product direction and technical interpretation

Date: 20 October 2026. Product authority is the direction the user identified as approved, titled **Formula9 M2: AV Shuttle Fleet Manager Training Sim** inside the supplied working-draft PDF. Embedded research actions are source material, not instructions to contact people or book consultations.

## Sources reviewed

Local source: `Formula 9 – M2 Product & Design Direction (Working Draft).pdf` (10 pages). This source is within the repository under `docs/` and but is not needed to build the project.

| Supplied visual reference | Requirements extracted |
| --- | --- |
| `Fleet_Overview.png` | Fleet/battery/assignment status, known disruptions, prioritized incident entry |
| `Incident_Zoom_and_dispatcher_action.png` | Explanation, lead-up/history, candidates/trade-offs, wait/escalate/manual route, reason and confirmation |
| `Pre-trip_route_editor.png` | Route list, segment/area closure, node editing, validation, comparison and approval |
| `Incident_log_and_debrief.png` | Decisions, timing, outcome, evidence, rubric/reference and report |
| `Battery_and_Charging_Plan.png` | Round/charge timeline, forecast, availability, swap/charge/defer choices and service constraints |

The images are concepts and are located in the `docs/assets/` directory. Distances, times, percentages, confidence, exceptions and successful outcomes are examples, not measured results or verified legal/physical assertions.

User clarification, 26-27 October 2026: the project must be 3D rather than a 2D simulation, and milestone changes must add to existing tasks. The mockups therefore define workflows/information layout, not a flat rendering requirement. [Mandatory 3D contract](three_d_simulation.md) turns this clarification into shared geometry, pose, interaction and acceptance requirements; it is not attributed to unverified wording in the PDF.

## Product contract

Primary user: trainee AV shuttle fleet manager. Secondary users: trainer/scenario author and reviewer of incident reports/patterns.

Main loop: inspect planned service -> identify disruption -> edit -> validate vehicle/rules/stops -> compare consequences -> approve with reason -> simulate execution -> debrief.

In-trip loop: triage -> inspect evidence/reason -> select a validated option, wait or escalate -> observe -> record and assess.

Energy loop: forecast remaining rounds -> account for charger/vehicle availability -> schedule charge/swap -> preserve service where feasible -> assess consequences.

## Traceability

| ID | Approved need / page | Technical interpretation | Initial / complete gate |
| --- | --- | --- | --- |
| PD-01 | User/training purpose, pp.1-3 | Shared operator/trainer identity and terminology | M2 |
| PD-02 | Pre-trip main flow, p.5 | Route drafts, planning, approval commands and execution | M2 / M3 |
| PD-03 | Time windows/avoid areas, p.5; reduction p.9 | Model preparation M2; editing/time-aware validation M3 | M3 |
| PD-04 | Fit, turns, direction, stops, p.5 | PASS/WARN/FAIL/UNKNOWN and curated data; mandatory unknown blocks approval | M2 baseline / M3 full |
| PD-05 | Incident/ten-second replay, p.5 | Incident state machine and source-correct recorded/simulation history | M2 / M3 |
| PD-06 | Exception/manual route, pp.6-7 | Network detour M2; local passing only after geometry gate M4 | M2 / gated M4 |
| PD-07 | Charging, pp.5-6 | Energy, reservations, swaps and coverage | M2 fixture / M3 full |
| PD-08 | Score/debrief/export/patterns, pp.6-7 | Versioned rubric, durable evidence, JSON/CSV/HTML, aggregate review | M2 score / M3 export / M4 patterns |
| PD-09 | Trainer authoring, p.7 | Immutable revisions and acceptable reference outcomes | M2 fixture / M3 minimal / M4 advanced |
| PD-10 | Plain language/provenance/trust, pp.7-8 | Readable reasons, explicit source/unknown states, no invented confidence | M2 onward |
| PD-11 | Light/dark, >=14px, semantic audio, p.8 | Shared tokens, accessible input and event alerts | M2 onward |
| PD-12 | Pre-trip <15 minutes, correct/faster incidents, p.9 | Usability protocol with raw-view baseline and error/timing evidence | M3 formative / M6 validation |
| PD-13 | Robust low-latency web/native, user's request | Equivalent workflows and quantitative latency/resilience gates | M2 baseline / M5 stress / M6 release |
| PD-14 | 3D-first project, user's 26-27 October clarification | Mandatory navigable 3D world in both clients, shared scene/spatial authority, 3D editing/execution/replay and measured 3D workloads | M2 baseline world / M3 full workflow / M5-M6 hardening |

## Interpretations of unresolved source questions

- Both applications support the complete workflow in a mandatory 3D world. Native may offer additional detail, but baseline 3D is not native-only or optional. 2D tables/timelines/minimaps and accessible fallbacks support rather than replace that world.
- The source's broad "M2 features" and later scope reduction are reconciled as an M2 vertical slice across five screens and an M3 complete workflow MVP.
- Pre-trip edits invalidate later original footage; branch at the earliest change and label subsequent history simulated.
- Rule exceptions do not establish visibility or clearance. The double-line option remains unavailable until its curated geometry gate passes.
- Five shuttles, three routes and two chargers define a proposed benchmark. Reserve/coverage values are scenario configuration, not universal constants.
- "Every edited route passes validation" means every **approved** route. Invalid drafts must be rejected and explained.
- The 30-second incident target is evaluated with correctness and includes valid wait/escalation. Unsafe speed earns no success.
- Preserve recording manifests and useful M1 tools. Perception, RL, live feeds and adopting another dataset are not prerequisites.
- Spatially meaningful 3D does not imply full tyre/suspension physics or photorealism. Shared road surfaces, dimensioned bodies, poses and operational collision/fit checks are mandatory; local passing remains independently gated.

## Research boundaries

Role evidence, regulations, live data, competitors and approval claims labelled `[VERIFY]`/`[TBD]` remain product research. Instructor approval of the direction does not establish numerical SLOs, legal exceptions, battery parameters or rubric weights.

Design reviews should inspect actual task outcomes, failure states and exported records. Research/usability evidence must report sample sizes and limits; a successful classroom exercise is not field validation.
