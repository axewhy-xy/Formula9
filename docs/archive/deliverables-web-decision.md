# Web Application Milestone Delivery Plan

> Historical plan, superseded on 20 October 2026 by [the web app delivery plan](../app/README.md). Earlier test results and requirements below are historical, not current verification.

## Purpose

This plan covers the React, TypeScript, and three.js web client for the proposed Formula9 decision-agent extension. There are six total milestones. Milestone 1 is the completed foundation represented by the current repository; the actionable work below therefore begins at Milestone 2. Milestone 3 is the minimum viable product (MVP), and Milestone 6 is the beta release.

The web client is a scenario-control, visualisation, replay, and explanation client. The Python backend remains authoritative for world state, safety filtering, policy execution, vehicle movement, collision state, and decision evidence. The web client must never independently reproduce a decision in order to explain it.

## Current Progress Assessment

Assessment date: 9 October 2026.

The M1 web foundation is complete enough to carry forward. The current client type-checks successfully, and all 112 frontend tests pass.

| Capability | Current Status | Evidence and Limitation |
| :--- | :--- | :--- |
| React/Vite application shell | Complete for M1 | Main setup, viewport, inspector, and timeline surfaces exist |
| Backend lifecycle integration | Complete for M1 | Creates sessions and supports start, pause, resume, stop, reset, and replay flows |
| WebSocket contract validation | Complete for M1 | Validates session, metadata, tick, completion, and error messages before use |
| Static scene loading | Complete for M1 | Loads scene metadata and bounded road/building/POI chunks |
| Route planning workflow | Complete for M1 | Selects start/destination, requests a backend route, renders the preview, and starts route playback |
| three.js road and entity rendering | Complete for M1 | Renders roads, ego state, basic obstacles, travelled/upcoming trajectory, and interpolated movement |
| Camera controls | Complete for M1 | Orbit/pan/zoom, reset, route framing, and vehicle-follow behavior exist |
| Timeline and replay inspection | Complete for M1 | Buffers received ticks and lets the user inspect an earlier tick |
| Decision inspector | Partial foundation | Displays action, reason code, and summary, but not factors, scores, tree path, safety override, or policy provenance |
| Scenario authoring | Not implemented | No policy selector, scenario catalogue, obstacle placement, closure editor, or runtime obstacle trigger UI |
| Policy comparison | Not implemented | No same-seed weighted/tree/RL comparison or outcome report |
| Perception presentation | Candidate backend foundation only | [PR #25](https://github.com/axewhy-xy/Formula_9/pull/25) proposes sensor-frame, KITTI source, projection, and model-neutral detection foundations; no web contract, sensor asset flow, overlay, model selector, or ground-truth comparison exists |
| Accessibility and release deployment | Partial | Basic labels exist; complete keyboard, screen-reader, responsive, and deployment acceptance work remains |

## Delivery Rules Across All Milestones

- Consume versioned contracts and reject unknown incompatible versions clearly.
- Keep renderer objects and React state outside domain contracts.
- Display decision evidence supplied by the backend; do not recalculate weights, traverse the tree, or infer RL reasoning in the browser.
- Preserve scenario, seed, policy, safety, model, scene, and transform revisions in run and comparison views.
- Use fixture-driven tests for every new contract shape before connecting live endpoints.
- Keep recorded KITTI replay visibly distinct from interactive synthetic simulation.
- Keep perceived detections visibly and semantically distinct from recorded labels and synthetic scenario ground truth.
- Never load detector weights or run perception inference in the browser; consume bounded backend-derived records and referenced assets only.
- Make all milestone acceptance scenarios usable without requiring the native application.

## Milestone 1 Shared Data and Visualisation Foundation Completed

### Completed Deliverables

- [x] Establish the React/Vite/three.js client and production build pipeline.
- [x] Implement renderer-neutral TypeScript types and runtime message guards.
- [x] Connect REST simulation creation and WebSocket lifecycle controls.
- [x] Render backend-owned static scene chunks and authoritative ego ticks.
- [x] Support KITTI replay and backend-planned-route playback.
- [x] Add route selection, camera interaction, basic timeline scrubbing, and decision-summary UI.
- [x] Add automated API, contract, renderer, camera, route, timeline, and audio tests.

### M1 Carry-Forward Constraints

- The current tick stream is still trajectory playback rather than a stateful decision-agent simulation.
- Existing obstacle and decision fields are presentation hooks, not evidence that scenario authoring or policy execution is complete.
- M2 must extend the contracts without breaking the recorded-replay workflow.

## Milestone 2 Deterministic Decision Core Client

### Goal

Prepare the web client for versioned scenarios and the deterministic weighted/decision-tree backend without turning the browser into a simulation authority.

### Subtasks

#### Contract and API Integration

- [ ] Add contract-v2 types and guards for scenario identity, policy metadata, safety revision, decision factors, eligible/rejected actions, policy traces, safety overrides, and outcome state.
- [ ] Add typed clients for scenario catalogue/detail, policy catalogue, run history, and run report endpoints.
- [ ] Extend simulation creation with `scenario_id`, `scenario_revision`, `scenario_seed`, `policy_mode`, `policy_revision`, and `safety_revision`.
- [ ] Keep a compatibility adapter for contract-v1 M1 replay fixtures until the backend migration is complete.
- [ ] Reject incompatible source/policy combinations in the UI before sending the request, while still treating the backend response as authoritative.

#### Scenario and Policy Setup

- [ ] Add a run-mode selector that clearly separates KITTI replay from interactive simulation.
- [ ] Add scenario and deterministic-policy selectors with revision and availability information.
- [ ] Display scenario description, supported obstacle types, supported actions, initial conditions, and success criteria.
- [ ] Allow an optional explicit seed and show the server-confirmed seed after session creation.
- [ ] Disable lane-change selections until the backend reports lane-model support.

#### Decision Presentation

- [ ] Expand the decision inspector to show selected action, reason, factors with units, eligible/rejected actions, policy revision, and safety override.
- [ ] Render weighted scores and decision-tree paths as tagged policy-specific sections without assuming every policy supplies the same trace.
- [ ] Add clear `OBSERVE_ONLY`, deterministic, fallback, and safety-intervention indicators.
- [ ] Update entity/route styling for `MAINTAIN`, `SLOW`, `STOP`, `REROUTE`, collision, and terminal states without embedding decision rules in colors alone.

#### Verification

- [ ] Add fixtures and parser tests for valid/invalid contract-v2 scenario, policy, tick, obstacle, decision, history, and report payloads.
- [ ] Add component tests for run-mode/policy compatibility, revision display, fallback states, and missing optional policy traces.
- [ ] Add renderer tests proving that obstacle and action updates do not mutate received snapshots.
- [ ] Add an integration test that runs the seeded static-obstacle scenario through completion and verifies the displayed decision record.

### Exit Criteria

- A deterministic scenario can be created from the web client using a selected weighted or decision-tree policy.
- The client renders authoritative obstacle and ego updates and displays a complete backend-provided explanation.
- The same scenario/revision/seed can be recreated without contract ambiguity.
- Contract-v1 KITTI replay remains usable during migration.

## Milestone 3 Decision Scenario MVP

### Goal

Deliver the complete MVP loop: configure a scenario, introduce an obstacle, observe a safe AV response, inspect the reason, and replay the result.

### Subtasks

#### Scenario Builder

- [ ] Build a guided scenario setup flow for pedestrian crossing, blocking vehicle, blocked lane, and road-closure presets.
- [ ] Add map-based obstacle placement with backend-provided bounds/snapping feedback and accessible form alternatives for precise values.
- [ ] Add obstacle activation controls for start-of-run and runtime triggers.
- [ ] Show an explicit review step containing scene, route, scenario, seed, policy, safety revision, and obstacles before session creation.
- [ ] Prevent scenario edits after a run starts; offer clone-and-rerun instead of silently changing history.

#### Live Simulation

- [ ] Send and acknowledge `TRIGGER_OBSTACLE` and `REMOVE_OBSTACLE` controls with the backend-assigned activation tick.
- [ ] Render pedestrian, vehicle, blockage, and closure states with distinguishable shapes and accessible labels.
- [ ] Visualise route invalidation/replacement after a closure without presenting the browser as the routing authority.
- [ ] Show connection, loading, fallback, terminal, collision, and validation errors in persistent actionable states.
- [ ] Keep the live renderer responsive while historical ticks are inspected.

#### Explanation and Replay

- [ ] Bind the decision inspector strictly to the selected timeline tick.
- [ ] Show safety constraints, weighted scores or tree path, rejected actions, and affected obstacles for that tick.
- [ ] Add timeline markers for obstacle activation, action changes, safety interventions, reroutes, collisions, and completion.
- [ ] Load missing history ranges from the backend when a user seeks outside the live client buffer.
- [ ] Add a run-summary view for distance, duration, decision count, interventions, collision count, and scenario outcome.

#### MVP Quality Gate

- [ ] Add end-to-end tests for all agreed MVP scenario presets using deterministic seeds.
- [ ] Test keyboard-only scenario setup, controls, timeline navigation, and decision inspection.
- [ ] Test reconnect and history recovery without duplicating ticks or losing the selected historical decision.
- [ ] Test at the agreed desktop viewport and minimum supported responsive width.
- [ ] Conduct a cross-client fixture review so web and native labels represent the same semantic fields.

### Exit Criteria

- A user can complete the full MVP loop without developer tools or manual JSON editing.
- The AV safely reacts to the agreed obstacle scenarios using an interpretable deterministic policy.
- Every displayed action is traceable to the exact stored decision record for that tick.
- The MVP acceptance suite passes with no critical failure.

## Milestone 4 RL, Policy Comparison, and Optional Perception

### Goal

Expose an admitted RL policy safely and let users compare it with the deterministic baseline on the same scenario revision and seed. As a could-have subtrack, inspect evaluated KITTI perception output without presenting it as scenario ground truth or making it a dependency of the M4 core.

### Subtasks

#### RL and Policy Comparison

- [ ] Add RL availability and model-manifest fields to the policy catalogue UI.
- [ ] Prevent selection of an unavailable, incompatible, or non-admitted model and show the backend-provided reason.
- [ ] Add a comparison setup that creates separate baseline and candidate runs sharing scenario revision and seed.
- [ ] Build an aligned comparison timeline keyed by simulation time and scenario events rather than message arrival order.
- [ ] Show action agreement/disagreement, safety overrides, collision/terminal outcomes, progress, interventions, and completion metrics.
- [ ] Present RL scores as model outputs, not probabilities or explanations unless the manifest explicitly defines them as such.
- [ ] Clearly label experimental RL runs and deterministic fallbacks.
- [ ] Support exporting a comparison report containing run IDs and all relevant revisions.
- [ ] Add fixtures for admitted, rejected, missing, and incompatible model manifests.
- [ ] Add end-to-end tests for successful comparison, model-load rejection, safety override, and deterministic fallback.

#### Optional Perception Could-Have

- [ ] Add capability-gated types and runtime guards for perception model manifests, source-frame provenance, 2D boxes, confidence, estimated depth/position, evaluation summaries, and unavailable/unknown states.
- [ ] Add an experimental perception view for selecting an immutable KITTI sequence/frame and an available backend perception model revision.
- [ ] Retrieve paged perception observations and bounded image/overlay asset references through the optional REST path; do not place camera images or point clouds in normal tick messages.
- [ ] Overlay model detections on the corresponding camera frame with class, confidence, and depth, and label each result as a derived estimate.
- [ ] Render an optional three.js perception marker only when the backend supplies a canonical position plus transform revision; never convert raw KITTI sensor coordinates independently in the browser.
- [ ] Provide a comparison layer for compatible labels/fixtures that differentiates matches, false positives, false negatives, and unavailable labels without implying that confidence is a safety probability.
- [ ] Keep the camera/overlay, selected timeline tick, source frame, model revision, and calibration revision synchronized.
- [ ] Handle missing ML dependencies, weights, calibration, images, scans, evaluation labels, and unsupported model versions as optional-feature states while leaving replay/simulation usable.
- [ ] Add fixture, accessibility, visual-regression, and integration tests for derived-versus-ground-truth styling, frame synchronization, confidence/depth display, and optional-feature failure isolation.

### Exit Criteria

- A user can run and compare the deterministic baseline and admitted RL policy from the web client.
- Results identify the same scenario/seed and preserve separate immutable histories.
- The UI exposes policy disagreements and safety interventions without claiming unsupported causal explanations.
- If the perception subtrack is activated at M4 scope lock, a user can inspect reproducible detections for at least one versioned KITTI sequence and trace every result to its source frame, calibration, preprocessing, threshold, and model revision.
- Deferring or disabling perception does not block the core M4 exit criteria or any baseline web workflow.

## Milestone 5 Feature Complete and Hardening

### Goal

Complete all planned beta features and harden the web client for longer, more complex sessions.

### Subtasks

- [ ] Add scenario editing for multiple simultaneous obstacles with validation and conflict feedback.
- [ ] Complete road-closure and reroute visualisation, including stale-route and no-route terminal states.
- [ ] Add lane geometry and lane-change visualisation only if the backend advertises the accepted lane-model revision.
- [ ] Add run/history export and import views for supported portable formats.
- [ ] Bound tick, geometry, and comparison buffers and load data incrementally for long runs.
- [ ] Profile and reduce render-frame, draw-call, memory, and message-processing costs against the beta-scale scene.
- [ ] Add robust reconnect, duplicate/out-of-order message handling, cancellation, and stale-request protection.
- [ ] Finish responsive layout, keyboard operation, focus management, contrast, non-color status cues, and screen-reader labels.
- [ ] Add browser compatibility coverage for the supported matrix.
- [ ] Add visual regression coverage for setup, live run, explanation, comparison, error, and completion states.
- [ ] If the M4 perception gate passed, harden sensor-asset paging/cache bounds, overlay performance, long-sequence navigation, and derived-versus-ground-truth presentation; otherwise keep the unfinished capability hidden.
- [ ] Run the complete frontend suite in CI with type-check and production build gates.

### Exit Criteria

- All planned beta web features are code complete and testable against frozen contracts.
- The agreed performance and memory targets are met on the demonstration machine.
- Accessibility, compatibility, reconnect, long-run, and visual-regression checks pass with no critical defect.

## Milestone 6 Beta Release

### Goal

Publish a reproducible beta web/backend package with frozen contracts, documented limitations, and a passed acceptance suite.

### Subtasks

- [ ] Freeze the beta contract, scenario catalogue, policy catalogue, supported browser matrix, and any accepted optional perception contract/model manifest.
- [ ] Produce an optimised production build with environment-based backend configuration and no development-only panels enabled by default.
- [ ] Validate clean-machine startup through the documented repository workflow and hosted/deployed web workflow selected by the team.
- [ ] Add a visible beta version/build identifier and policy/scenario revision information to diagnostics.
- [ ] Write user guidance for replay, scenario setup, explanations, policy comparison, exports, limitations, and error recovery.
- [ ] Write operator troubleshooting for backend unavailability, incompatible contracts, missing RL artifacts, optional perception dependencies/assets, WebGL failure, and corrupted local state.
- [ ] Execute the complete web beta acceptance suite against deterministic-only and RL-enabled backend configurations, with perception both unavailable and enabled if it was accepted for beta.
- [ ] Perform a usability pass with representative non-developer users and resolve all beta-blocking findings.
- [ ] Triage known issues, publish release notes, and document deferred post-beta work.
- [ ] Confirm that no blocker or critical-severity defect remains and that deterministic fallback works when the RL artifact is absent.

### Exit Criteria

- A clean supported machine can start the beta and complete the web acceptance scenarios from setup through report/replay.
- The production build uses frozen compatible contracts and clearly reports its beta version and limitations.
- Deterministic fallback, unavailable-model handling, optional-perception isolation, and recovery paths are verified.
- The beta acceptance report contains no unresolved blocker or critical-severity defect.

## Cross-Team Handoffs

| Dependency | Web Input Needed | Web Output Provided |
| :--- | :--- | :--- |
| Backend simulation | Versioned scenarios, policies, history/report endpoints, deterministic errors | Validated requests, controls, and user-visible error context |
| Decision/ML team | Policy manifests, trace schemas, baseline-comparison semantics | Policy selection and evidence presentation without client-side recomputation |
| Perception team | Versioned perception records, sensor asset references, model/calibration manifests, evaluation semantics | Capability-gated overlays and clear derived-versus-ground-truth presentation without browser inference |
| Native team | Shared fixtures, terminology, colors/status semantics, acceptance scenarios | Cross-client UI/contract discrepancies and reproducible fixtures |
| UX/product | Product flow, terminology, accessibility targets, usability findings | Working interaction prototypes and implementation constraints |
