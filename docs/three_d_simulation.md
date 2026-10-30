# Mandatory 3D simulation and presentation contract

Revision 1.0, 27 October 2026. This implements the user's clarification that Formula9 is a **3D shuttle fleet-manager training simulator in both web and native**, not a flat simulation with an optional 3D viewer. It supplements the approved training purpose; the concept images specify workflows, not rendering dimensionality. All requirements below are implementation targets, not statements of completed work.

Read with [architecture](architecture.md), [performance](performance.md), and the [integrated roadmap](milestones.md). Existing milestone task IDs, text, outputs, verification criteria, and implementation handoffs remain intact. New numbered work packages and additional exit criteria extend them. Earlier references to optional ECS/detail views mean extra inspection/detail beyond the mandatory 3D operational view; they cannot defer baseline 3D until M4.

## 1. What counts as 3D completion

- Both clients use a perspective, navigable, depth-tested 3D world as their main fleet/route/incident workspace from M2.
- Roads have explicit road-surface/route geometry; shuttles, stationary obstructions, buildings, stops, depot and chargers are dimensioned 3D objects with stable semantic IDs. A low-poly blockout is acceptable at M2; a tilted raster map with flat icons is not.
- The backend publishes vehicle position and orientation on the authored road surface. Both clients display approved detours, stopping, dwelling, depot arrival and charging from the same authoritative state.
- Selection, route-node editing and closure placement work against 3D scene entities, with equivalent keyboard/list input. M3 adds trainer obstacle placement and richer surface-aware editing.
- Fleet overview, orbit/pan/zoom, selected-shuttle follow and incident-focus cameras are mandatory. A simulated onboard inspection camera is added in M3 and labelled synthetic; it is not KITTI footage or a validated sensor model.
- Test fixtures include a non-flat road section and, by M3, overlapping road levels plus insufficient headroom. Depth must affect support, picking and relevant validation, not just appearance.
- Forms, score tables, charging timelines and minimaps remain useful 2D controls around the 3D world. Accessible textual controls and graphics-loss fallback preserve usability but do not count as a passing normal-operation 3D demonstration.

Rendering fidelity and simulation fidelity are separate. The core remains deterministic road-constrained kinematics, not tyre/suspension simulation, crash physics, photorealism, sensor training or arbitrary off-road driving. Topology still chooses a route; 3D surfaces, volumes and authored rules constrain its supported execution. Any flat-scene or simplified collision assumption must be explicit, not silently generalized to unsupported scenes.

## 2. Authority and subsystem boundaries

| Owner | Required work | Boundary |
| --- | --- | --- |
| Content + backend | Versioned scene build/validation, road surfaces, asset-to-entity bindings, collision proxies, source metadata | Source artwork is not automatically valid operational geometry |
| Backend session actor | 3D poses, route/surface attachment, swept movement checks, spatial event outcomes | Headless CPU service; no browser, OpenGL or GPU dependency |
| Backend workers | Immutable spatial indexes, route-fit/visibility jobs and branch evaluation | Bounded jobs with revisions/deadlines; never mutate live state |
| Web / native | Render assets, cameras, hit testing, draft gizmos, interpolation, labels and GPU lifecycle | Client raycasts support interaction only; they cannot approve a route or declare an area safe |
| Shared QA | Transform, geometry, selection, replay and workload fixtures | Compare semantic/spatial agreement, not identical pixels |

A renderer crash does not stop the shared shift. The affected client clearly shows graphics unavailable, keeps command reconciliation/live alerts working, and disables spatial actions whose required evidence cannot be presented. Recovery must rebind the same session and scene revision; a flat fallback is a degraded state, never an accepted replacement product.

## 3. Scene package and content pipeline

Introduce an immutable `TrainingSceneRevision`, referenced by scenario, checkpoint, command dependencies and report manifests. Its package contains:

- Scene ID/revision, schema/profile version, content checksums, units, local geographic origin, source geography and authored-versus-recorded provenance.
- Stable entity IDs and road/lane/surface/stop/depot/charger/obstacle bindings; render node names are not IDs.
- Render assets, material/texture references, bounds, per-asset transform, import settings, LOD tiers and low-detail mandatory representations.
- Road-surface geometry and topology references, lane/deck IDs, path samples/support normals, dimensions, clearance annotations and permitted transitions.
- Separate simplified collision/visibility proxies with entity IDs, flags and revisions. Decorative content is explicitly non-operational; it must not imply a drivable opening where operational geometry forbids one.
- Camera bookmarks and the validation/benchmark scripts, supported capabilities, resource inventory, licenses and attributions.

Use a constrained **glTF 2.0 / GLB render-asset profile** shared by both clients: indexed triangle meshes, node transforms, normals, texture coordinates, basic metallic-roughness materials and bundled PNG/JPEG textures. Pin importer versions and test conformance. Bake unsupported material/node features during content build or reject with a named diagnostic; compression extensions, skeletal animation and advanced materials are not M2 prerequisites. Baseline lighting is a fixed directional plus ambient contribution; shadows are a separately budgeted quality setting, not an input to collision/visibility checks.

glTF defines a right-handed, metre-based, Y-up asset convention and XYZW quaternions. Keep that import convention separate from Formula9's canonical Z-up world; do not reinterpret asset bytes as canonical coordinates. [Khronos glTF specification](https://github.com/KhronosGroup/glTF/blob/main/specification/2.0/Specification.adoc#coordinate-system-and-units)

Pipeline: authored/sourced content -> normalize and validate transforms/IDs -> generate render tiers and operational proxies -> run geometry/asset checks -> publish a checksummed immutable package -> serve assets independently of telemetry. Assets are loaded through bounded local HTTP/package paths, not in 10 Hz snapshots. Validate declared and decoded sizes, index bounds, finite coordinates, supported features and safe relative paths; forbid arbitrary remote asset fetches and path traversal.

A scenario pins the render manifest and operational-geometry revision together. Changing a mesh or proxy creates a new scene revision and requires affected tests; a cosmetic-only revision still has explicit lineage. Never hot-swap the geometry of an active or retained attempt. Mandatory low-detail assets are packaged; optional high-detail loss may fall back to those exact dimensioned representations. Missing mandatory geometry prevents scenario readiness, rather than silently substituting an unrelated map.

## 4. Spatial wire contract and transforms

| Field | Contract |
| --- | --- |
| Canonical world | Right-handed local metres: +X east, +Y north, +Z up; geographic/local origin and transform revision explicit |
| Vehicle local body | +X forward, +Y left, +Z up; pose origin is the centre of its declared collision body, not an arbitrary mesh pivot |
| Position/orientation | Finite `position_m: [x,y,z]`, unit `orientation_xyzw: [x,y,z,w]` mapping body to canonical world |
| Dimensions/support | `dimensions_m: [length,width,height]`, collision-proxy ID/revision, support surface/lane/deck ID and support offset |
| Motion identity | Entity ID, route revision/progress, tick, epoch, scene/spatial revision, movement/discontinuity flags and source mode |
| Static/render identity | Asset ID/checksum, entity binding, asset-to-body or asset-to-world matrix; mesh scale is not vehicle state |
| Spatial edit | Stable feature/surface ID, proposed canonical coordinates/dimensions, expected scene/object revision, actor and reason |

For a Y-up presentation basis, use `render(x,y,z) = (world.x, world.z, -world.y)`. Let this proper rotation be `B`. For body vectors already expressed in that presentation basis, orientation conversion is `R_render = B * R_world * inverse(B)`. For raw asset vertices the full composition is `M_render = B * T_world_from_body * T_body_from_asset`; this applies the declared asset transform exactly once. Inverse-transform pick hits before submitting canonical drafts. Keep the asset basis, world-to-render basis and camera transform separate. Never reorder Euler components or quaternion elements as a substitute for basis conversion. Quaternion signs q and -q are equivalent; interpolation uses the shortest arc.

Use the same semantic local frame in both clients; library-specific Euler/degrees interfaces sit behind tested adapters. Backend and storage calculations use adequate precision for the bounded local scene; any floating-origin presentation keeps an explicit offset and must preserve inverse picking. Golden fixtures cover cardinal headings, slopes, nonzero origins, asymmetric objects, nested asset transforms, quaternion sign changes and scale errors. Numeric acceptance tolerances are owned by [performance](performance.md#8-3d-workload-and-spatial-acceptance).

Publish a compatible versioned spatial extension to the v2 contracts after auditing current clients. Mandatory missing fields fail the 3D capability handshake; do not fabricate z=0 or identity rotation for a 3D-required session. Explicit legacy adapters may retain labelled old replay outside the training acceptance path. Advertise `world_3d`, asset-profile/spatial-schema versions, scene revision and separately gated `local_manoeuvre`/`recorded_perception` capabilities. Server capability describes available data/model support; each client must also pass render readiness.

## 5. Backend motion, fit and collision

M2 evaluates distance along an authored 3D route and obtains support height, tangent and road normal; publish the body-centre pose using its support offset. Derive orientation consistently, bound acceleration/deceleration and retain the existing stop/dwell/junction rules. This is constrained 3D motion, not independent physics in each renderer. Distance and energy calculations use the same path length; gradient-sensitive energy remains a separately versioned model extension, not an implicit realism claim.

Build an immutable spatial index over static proxies; maintain a bounded dynamic vehicle/obstacle index. Use broad-phase AABBs and conservative swept body volumes against curated boxes/convex proxies, including an explicit road-support classification to avoid treating the road underneath as a collision. Bound substeps or swept tests by motion/rotation error; exhausting the bound stops/marks the action unavailable rather than accepting an unchecked leap. Client LOD/culling never changes these checks.

M3 adds explicit multi-level surface membership and vertical-clearance validation: overlapping XY roads are not connected without authored topology; a vehicle on a bridge must not collide with traffic beneath solely because planar footprints overlap. Conversely, overhangs, barriers and insufficient headroom cannot be ignored because a centreline is clear. Unknown required height/support/fit metadata blocks approval. Placement rejects unsupported surfaces, invalid dimensions and intersections according to authored policy; trainer edits go through draft publication or an audited live intervention, never local mesh movement alone.

M4 adds bounded visibility/occlusion queries for authored incident viewpoints and spatial evidence. Ray/volume tests against proxy geometry return source, origin, range, blockers and `KNOWN/UNKNOWN` coverage under a versioned model. An operator's orbit camera seeing around a building does not mean the shuttle can see there. Local passing remains conditional on the existing lane, visibility, occupancy and rule-exception gate; baseline 3D is mandatory even when passing is disabled. No clearance/visibility result is a real-world safety guarantee.

## 6. Client interaction, replay and rendering

- Keep one authoritative store and stable entity registry per session. Compose dimensioned render objects from the scene package and pose stream; no local scene tab starts another fleet simulation.
- Separate camera input, object picking and edit gizmos. Orbiting/panning cannot accidentally move a route node; provide explicit edit modes, undo/redo, cancel and keyboard equivalents.
- Picks resolve a stable entity and explicit road surface/deck. At stacked roads show the selected surface and require disambiguation; reject off-surface points rather than projecting them silently to a different road.
- Interpolate only compatible poses from the same epoch/scene/route-continuity interval, respecting path curvature and support. Do not linearly cut through a bend/building between sparse samples. With insufficient path/support information hold a labelled authoritative pose rather than invent motion. Reset on teleport/branch/recovery; clients never extrapolate through a stop.
- Show route alternatives as distinct, depth-aware overlays and ghost vehicles, with preview/source labels and explicit visibility-through-geometry style where used. Ghosts are not live occupants; their inspection cannot mutate live timers, incidents or reservations.
- M3 synthetic onboard view renders the simulated scene using its own timestamp/camera settings. Persistent `SIMULATED VIEW` identification and optional diagnostic visibility overlays prevent confusion with original recordings or calibrated sensor evidence.
- Charging uses backend depot/bay membership, dwell and reservation state. Wheel/cable/status animation is cosmetic and cannot start movement or grant charging permission.
- Render shadows, labels, transparency and colour with legibility budgets. Depth occlusion, clipping, near-plane behaviour and label overlap are functional tests; screen-space annotations remain available when geometry hides a selected incident.
- Share immutable render assets across branch views but isolate mutable transforms, camera state and cursors. Bound comparison views, decode workers, uploads, draw work and caches. Dispose only after the final reference and on the correct GL/context owner.

## 7. Milestone progression and evidence

| Gate | Backend | Web and native |
| --- | --- | --- |
| M2 | Versioned 3D package/spatial contract, surface-following poses, basic swept checks, five-shuttle fixture with ramp and solid obstruction | Mandatory 3D world, overview/orbit/follow/focus, semantic picking/node edit, dimensioned objects and measured 3D baseline |
| M3 | Multi-level/headroom/placement checks, spatial authoring and immutable 3D history/branch evidence | Complete training in 3D, trainer placement, surface-aware edits, depot/bay execution, synthetic onboard view, passing P1 |
| M4 | Bounded occlusion evidence, richer spatial scenarios and explainable proxy/model results | Richer authored environment, spatial rehearsal/comparison, explainable visibility; optional passing/perception remain separately gated |
| M5 | Profile spatial queries/indexes, package integrity/faults, geometry regression and full 3D load/fault suite | Optimize representative 3D draw/asset workloads, two-hour soak, context/asset recovery and usability under pressure |
| M6 | Freeze compatible packages/proxies/models and prove headless/replay/restore acceptance | Clean-machine packaged 3D beta, four-hour soak, final spatial parity and observed 3D interaction evidence |

Accept semantic identity, dimensions, transforms, constraints and model outcomes across renderers; pixel-identical lighting is not required. Every report names scene/package/spatial-contract hashes, build IDs, camera script and workload, plus exceptions and measured performance. Existing milestone status reports continue to describe their original tasks; this addendum does not retroactively mark new 3D tasks complete.

## 8. Scope and release rules

Mandatory: 3D in both clients from M2, operational geometry matching presentation, complete training workflows, provenance, bounded resource use and the existing command/durability guarantees. M2 performance is a baseline; M3 onward requires full 3D P1 acceptance, not a flat/minimized-renderer surrogate.

Conditional: realistic sensor modelling, optional recorded perception, unrestricted local manoeuvres, photorealism, complex skeletal animation, weather and full vehicle dynamics. Their absence cannot make baseline 3D optional. Any reduction to required 3D scope needs an explicit product decision; a successful 2D fallback alone cannot close an outstanding 3D gate.
