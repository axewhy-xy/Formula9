# Native Application Milestone Delivery Plan

> Historical plan, superseded on 20 October 2026 by [the native delivery plan](../native/README.md). Earlier test results and requirements below are historical, not current verification.

## Purpose

This plan covers the C++20, Qt 6, OpenGL, ECS, and FMOD native client for the proposed Formula9 decision-agent extension. There are six total milestones. Milestone 1 is the completed foundation represented by the current repository; actionable work begins at Milestone 2. Milestone 3 is the minimum viable product (MVP), and Milestone 6 is the beta release.

The native application is a high-fidelity visualisation, control, replay, and explanation client. It does not own safety rules, policy execution, route authority, simulation state transitions, or reinforcement-learning inference. The Python backend publishes the authoritative canonical state and decision evidence.

## Current Progress Assessment

Assessment date: 17 October 2026.

The M1 native foundation is complete enough to carry forward. The native Debug application builds successfully and all 31 registered native unit tests pass.

| Capability | Current Status | Evidence and Limitation |
| :--- | :--- | :--- |
| Qt application shell | Complete for M1 | Main window, renderer tabs, toolbar, status bar, and debug tooling exist |
| REST/WebSocket backend controller | Complete for M1 | Creates/attaches/replays sessions, plans backend routes, creates accepted-route sessions, loads scene metadata/chunks, validates messages, and sends lifecycle controls |
| Canonical static map view | Complete for M1 | Renders backend roads, buildings, and POIs with panning, zooming, LOD, and bounded scene requests |
| Authoritative ego playback | Complete for M1 | Consumes tick positions and interpolates the authoritative ego in the map and ECS views |
| Simulation lifecycle controls | Complete for M1 | Start, pause, resume, stop, and replay toolbar actions follow session status |
| OpenGL/ECS 3D renderer | Basic canonical visualisation | Backend road chunks, accepted route previews, travelled/upcoming trajectory, ego state, and obstacle entities replace the demo cube; high-fidelity road/building presentation remains later work |
| Obstacle rendering | Basic M1 presentation | Current authoritative obstacle positions are displayed in both map and ECS views using stable backend IDs in the ECS registry |
| Decision inspector | Not implemented | No native action/reason/factors/tree/safety-override presentation exists |
| Scenario and policy setup | Partial | Users can select and preview a backend-planned route or start KITTI replay with a chosen playback speed; scenario catalogues and policy selection remain M2 work |
| Timeline/history | Not implemented | Native playback has live state and replay creation, but no historical-tick scrubber or backend history-range client |
| Perception presentation | Candidate backend foundation only | [PR #25](https://github.com/axewhy-xy/Formula_9/pull/25) proposes sensor-frame, KITTI source, projection, and model-neutral detection foundations; no native perception contract, camera overlay, bounded point-cloud path, model selector, or ground-truth comparison exists |
| Audio | M1 engine foundation | FMOD-backed audio and preview tooling exist; semantic simulation cues are not yet driven by canonical events |
| Packaging | Partial | CMake and `windeployqt` support exist; clean-machine beta packaging and recovery validation remain |

## Delivery Rules Across All Milestones

- Keep all OpenGL resource mutation on the context-owning thread.
- Network callbacks may validate and queue immutable plain values; they must not invoke arbitrary renderer operations.
- Parse versioned JSON into typed native domain snapshots before rendering.
- Use stable backend entity IDs to create, update, and remove render entities.
- Display backend-provided explanations; do not duplicate weighted rules, decision-tree traversal, or RL inference in C++.
- Keep renderer queues bounded and preserve authoritative history in the backend/session layer.
- Keep KITTI replay visibly distinct from synthetic decision-agent simulation.
- Keep perceived detections visibly and semantically distinct from recorded labels and synthetic scenario ground truth.
- Keep ML inference and weights in the backend; the native presentation executable consumes typed derived records and bounded sensor assets only.
- Ensure the backend and web client can operate normally when the native executable is absent or closed.

## Milestone 1 Shared Data and Visualisation Foundation Completed

### Completed Deliverables

- [x] Establish the C++20/Qt 6/OpenGL application and CMake build.
- [x] Implement a Qt REST/WebSocket controller with version and coordinate checks.
- [x] Load canonical scene metadata and camera-bounded static scene chunks.
- [x] Render roads, buildings, POIs, and the authoritative ego position in the map view.
- [x] Add start, pause, resume, stop, and replay controls.
- [x] Establish the ECS 3D renderer, camera/input, shader/mesh, property panel, and FMOD foundations.
- [x] Add native tests for map contract behavior and reconstructing simulation replay requests.

### M1 Carry-Forward Constraints

- Canonical backend data now drives the 2D map and the basic ECS 3D road/ego/trajectory/obstacle presentation.
- The 3D tab still uses simple primitive geometry and is not yet the high-fidelity AV visualiser planned for later milestones.
- M2 must replace the remaining raw JSON boundary with typed snapshots and add decisions and scenario configuration without moving simulation authority into C++.

## Milestone 2 Deterministic Decision Core Client

### Goal

Make the native application a correct contract-v2 consumer for deterministic scenarios, obstacles, and decision evidence.

### Subtasks

#### Typed Contract Adapter

- [ ] Define native value types for scenario identity, policy metadata, ego state, obstacle state, trajectory window, decision factors, policy trace, safety override, and outcome state.
- [ ] Parse and validate contract-v2 REST and WebSocket payloads before they enter either renderer.
- [ ] Keep a contract-v1 adapter for M1 replay until the backend migration is complete.
- [ ] Add typed scenario catalogue, policy catalogue, history-range, and run-report requests.
- [ ] Preserve unknown optional policy traces for diagnostics while rejecting incompatible required schema versions.

#### Snapshot and Entity Pipeline

- [ ] Replace raw `QJsonObject` handoff at the renderer boundary with immutable typed snapshots.
- [ ] Implement a bounded newest-complete-snapshot queue from the network/controller thread to the OpenGL/map update path.
- [ ] Add a stable entity registry keyed by backend ego/obstacle IDs.
- [ ] Add create/update/remove behavior for pedestrian, vehicle, blockage, and closure entities in the canonical map view.
- [ ] Expose final action, collision, terminal, and safety-intervention states to renderer presentation without embedding policy logic.

#### Setup and Decision UI

- [ ] Add a simulation setup dock/dialog for run mode, scene, scenario, seed, deterministic policy, and revision summary.
- [ ] Enforce source/policy compatibility in the UI and display backend validation errors verbatim with useful context.
- [ ] Add a decision inspector for action, reason, factors/units, eligible/rejected actions, policy revision, and safety override.
- [ ] Add tagged weighted-score and decision-tree-path views that tolerate missing or future policy traces.
- [ ] Add explicit `OBSERVE_ONLY`, deterministic, fallback, and safety-intervention indicators.

#### Verification

- [ ] Extend C++ contract tests with valid/invalid scenario, policy, obstacle, decision, history, and report fixtures shared with Python and TypeScript.
- [ ] Add queue tests for superseded live snapshots, shutdown, malformed payloads, and renderer lag.
- [ ] Add entity-registry tests for add/update/remove and stable IDs.
- [ ] Add a headless adapter integration test that completes the seeded static-obstacle scenario without creating an OpenGL context.

### Exit Criteria

- The native client creates a deterministic scenario using a weighted or decision-tree policy.
- It renders authoritative ego/obstacle updates and displays a complete backend-provided decision record.
- Network and rendering ownership remain separated and bounded.
- M1 KITTI replay remains available through the compatibility path.

## Milestone 3 Decision Scenario MVP

### Goal

Deliver the complete native MVP loop: configure a scenario, introduce an obstacle, observe the AV response, inspect the explanation, and replay the result.

### Subtasks

#### Scenario Interaction

- [ ] Add preset selection for pedestrian crossing, blocking vehicle, blocked lane, and road closure.
- [ ] Add map picking plus numeric property editing for obstacle placement, velocity, activation rule, and identifier.
- [ ] Add runtime `TRIGGER_OBSTACLE` and `REMOVE_OBSTACLE` controls and show the backend-assigned activation tick.
- [ ] Add a pre-run review containing scene, route, scenario revision, seed, policy revision, safety revision, and obstacles.
- [ ] Lock the active scenario after start and offer clone-and-rerun for edits.

#### Canonical MVP Rendering

- [ ] Render all MVP obstacle types and route-closure state in the canonical map view.
- [ ] Render the ego orientation and action state, not only its x/y point.
- [ ] Visualise travelled/upcoming/replanned route segments and clearly mark an invalidated route.
- [ ] Connect canonical ego/obstacle snapshots to the ECS 3D scene, replacing the demo cube for the supported MVP scene.
- [ ] Keep the 2D map and 3D scene synchronized to the same selected tick.
- [ ] Add camera follow, frame-selected entities, and non-color action/obstacle indicators.

#### Timeline and Explanation

- [ ] Add a native timeline with live/follow mode and historical tick selection.
- [ ] Request missing history ranges from the backend rather than retaining an unbounded local run.
- [ ] Bind map, 3D scene, decision inspector, and audio cues to the selected historical tick.
- [ ] Mark obstacle activation, action changes, safety interventions, reroutes, collisions, and completion on the timeline.
- [ ] Add a run-summary panel for distance, duration, decision count, interventions, collision count, and scenario outcome.

#### MVP Quality Gate

- [ ] Add end-to-end native tests for all agreed MVP deterministic scenario presets.
- [ ] Verify close/shutdown while connected, while paused, while loading a chunk, and while rendering a live run.
- [ ] Verify OpenGL resources are destroyed with a valid context and no network callback touches released widgets.
- [ ] Verify web/native parity against the same golden snapshots and explanation records.
- [ ] Test the MVP on the agreed demonstration GPU, DPI scale, and window-size matrix.

### Exit Criteria

- A user can complete the full MVP loop in the native application without manual JSON or developer-only tools.
- The 2D and 3D native views show the same authoritative selected tick, obstacle state, action, and route state.
- Every displayed action is traceable to its stored decision record.
- Closing or lagging the native client does not change backend state or disrupt a simultaneous web client.

## Milestone 4 RL, Policy Comparison, and Optional Perception

### Goal

Expose an admitted backend RL policy and compare it with the deterministic baseline without adding ML inference to the native executable. As a could-have subtrack, inspect evaluated KITTI perception output while keeping it separate from scenario ground truth and from the core M4 exit gate.

### Subtasks

#### RL and Policy Comparison

- [ ] Extend the policy catalogue UI with admitted-model manifest, compatibility, evaluation revision, and experimental status.
- [ ] Prevent selection of missing, rejected, or incompatible artifacts and present the backend reason.
- [ ] Add a comparison setup that launches separate baseline and candidate runs with the same scenario revision and seed.
- [ ] Add synchronized comparison controls and event/time alignment for two immutable run histories.
- [ ] Display action agreement/disagreement, safety overrides, interventions, collisions, terminal state, progress, and completion metrics.
- [ ] Show RL scores as model outputs and deterministic action as a comparison, not as a reconstructed explanation.
- [ ] Add side-by-side or switchable ego/route/obstacle views without duplicating authoritative state.
- [ ] Add comparison export with run IDs and all scenario/policy/safety/model revisions.
- [ ] Add native fixtures and tests for admitted, rejected, missing, corrupt, and incompatible model metadata.
- [ ] Verify deterministic fallback when the backend cannot load the requested RL model.

#### Optional Perception Could-Have

- [ ] Add typed native values and parsers for perception model manifests, source-frame/calibration provenance, 2D boxes, confidence, estimated depth/position, evaluation summaries, and unavailable/unknown states.
- [ ] Add an experimental perception dock for selecting an immutable KITTI sequence/frame and an available backend model revision.
- [ ] Retrieve paged perception observations and bounded camera/overlay or point-cloud asset references outside the normal tick stream.
- [ ] Display the camera frame and derived detection boxes with class, confidence, depth, model revision, and explicit non-ground-truth styling.
- [ ] Optionally render bounded LiDAR points or derived 3D markers in OpenGL only from backend-described buffers/transforms; do not reimplement KITTI calibration or projection in C++.
- [ ] Keep camera view, map/3D selection, timeline tick, source frame, model revision, and calibration revision synchronized.
- [ ] Add a compatible-label/fixture comparison for matches, false positives, false negatives, and unavailable labels without treating detector confidence as safety probability.
- [ ] Bound decoding, CPU memory, GPU buffers, upload queues, and texture lifetimes; perform every OpenGL upload/destruction operation on the context-owning thread.
- [ ] Handle missing ML dependencies, weights, calibration, images, scans, evaluation labels, and unsupported model versions as optional-feature states while leaving replay/simulation usable.
- [ ] Add parser, queue, resource-lifecycle, visual, and integration tests for frame synchronization, derived-versus-ground-truth styling, stale asset cancellation, and optional-feature failure isolation.

### Exit Criteria

- A user can launch and inspect comparable deterministic and RL runs from the native client.
- The client accurately labels policy disagreements, safety overrides, and experimental status.
- No ML framework or model execution is linked into the native presentation executable.
- If the perception subtrack is activated at M4 scope lock, a user can inspect reproducible detections for at least one versioned KITTI sequence and trace every result to its source frame, calibration, preprocessing, threshold, and model revision.
- Deferring or disabling perception does not block the core M4 exit criteria or any baseline native workflow.

## Milestone 5 Feature Complete and Hardening

### Goal

Complete all planned native beta features and make long, complex sessions stable and performant.

### Subtasks

- [ ] Support editing and rendering multiple simultaneous obstacles with clear selection and validation feedback.
- [ ] Complete road-closure/reroute presentation, including no-route and terminal outcomes.
- [ ] Add lane geometry and lane-change rendering only after the backend advertises an accepted lane-model revision.
- [ ] Consolidate duplicate map/3D presentation code behind shared typed snapshot and entity adapters.
- [ ] Replace remaining legacy/fallback file-loading paths in the normal beta workflow with explicit debug-only tools.
- [ ] Add semantic audio cues for obstacle activation, safety intervention, collision risk, reroute, and completion, with independent accessibility controls.
- [ ] Profile and optimize static geometry batching, entity updates, draw calls, CPU/GPU frame time, and queue/memory bounds.
- [ ] Add reconnect, stale chunk cancellation, out-of-order/duplicate message handling, backend restart recovery, and clean session detachment.
- [ ] Complete keyboard navigation, scalable UI/DPI behavior, non-color status cues, readable inspector layouts, and reduced-motion/audio options.
- [ ] Validate Debug and Release builds in CI and run non-window tests without a GPU/display.
- [ ] Add an optional OpenGL smoke/visual regression test on a supported display runner.
- [ ] If the M4 perception gate passed, harden asset streaming/cache bounds, decoding/upload performance, long-sequence navigation, and cleanup across tab/session/window transitions; otherwise keep the unfinished capability hidden.
- [ ] Rehearse `windeployqt`, FMOD runtime placement, configuration files, assets, and clean-machine installation.

### Exit Criteria

- All planned beta native features are code complete against frozen candidate contracts.
- Performance, queue, memory, reconnect, shutdown, DPI, accessibility, and packaging-rehearsal targets pass.
- The normal workflow no longer depends on legacy fallback data or the demo cube.
- No unresolved critical defect remains.

## Milestone 6 Beta Release

### Goal

Publish a reproducible native beta package with frozen contracts, validated runtime dependencies, and a passed acceptance suite.

### Subtasks

- [ ] Freeze the beta contract, scenario catalogue, policy catalogue, supported GPU/driver/Windows matrix, Qt/FMOD versions, and any accepted optional perception contract/model manifest.
- [ ] Produce a signed or checksum-published Release package containing the executable, Qt runtime, FMOD runtime where licensed, shaders, assets, and default configuration.
- [ ] Remove development consoles, debug-only dataset tools, and unrestricted property editing from the default beta experience while retaining documented developer builds.
- [ ] Add a visible beta version/build identifier and complete scene/scenario/policy revision diagnostics.
- [ ] Validate install, first run, backend connection, WebSocket reconnect, shutdown, uninstall/manual removal, and upgrade-from-previous-package behavior on clean supported machines.
- [ ] Write user guidance for setup, controls, cameras, scenarios, decisions, replay, comparison, audio, exports, and limitations.
- [ ] Write troubleshooting for missing Qt/FMOD DLLs, unavailable backend, incompatible contracts, missing RL models, optional perception dependencies/assets, OpenGL/driver failure, and corrupt configuration.
- [ ] Execute the complete native beta acceptance suite in deterministic-only and RL-enabled backend configurations, with perception both unavailable and enabled if it was accepted for beta.
- [ ] Run extended soak tests with repeated sessions, reconnects, tab changes, timeline scrubbing, and window close/reopen cycles.
- [ ] Triage known issues, publish release notes, and document deferred post-beta work.
- [ ] Confirm that deterministic fallback works without an RL artifact and no blocker or critical-severity defect remains.

### Exit Criteria

- A clean supported Windows machine can install/start the native beta and complete the acceptance scenarios.
- The package reports compatible versions and fails with actionable messages when a required runtime or backend contract is unavailable.
- Long-run, shutdown, reconnect, resource-lifecycle, deterministic-fallback, and optional-perception isolation checks pass.
- The beta acceptance report contains no unresolved blocker or critical-severity defect.

## Cross-Team Handoffs

| Dependency | Native Input Needed | Native Output Provided |
| :--- | :--- | :--- |
| Backend simulation | Versioned typed fixtures, scenario/policy APIs, history/report endpoints, bounded errors | Contract validation failures, queue/reconnect behavior, and platform constraints |
| Decision/ML team | Policy manifests, trace schemas, baseline-comparison semantics | Correct evidence presentation without duplicated policy logic |
| Perception team | Versioned perception records, bounded sensor assets, model/calibration manifests, evaluation semantics | Typed adapters, synchronized overlays, resource bounds, and clear derived-versus-ground-truth presentation without native inference |
| Web team | Shared fixtures, terminology, status semantics, acceptance scenarios | Cross-client parity findings and native-specific performance constraints |
| UX/product | Native product flow, inspector hierarchy, accessibility/usability targets | Working Qt interaction prototypes and packaging limitations |
| Build/release | Supported Qt, compiler, OpenGL, FMOD, and Windows matrix | Reproducible CMake/Release package and clean-machine validation results |
