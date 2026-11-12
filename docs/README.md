# Formula9 technical documentation

Formula9 is an AV shuttle fleet manager training simulator. It has one Unity client and a
Python/FastAPI backend, both in this repository. To set up, run, build and test it, start with the
[repository README](../README.md).

## Current documentation

These describe the system as it is built today.

| Document | Covers |
| --- | --- |
| [Technical architecture](architecture.md) | Runtime topology, backend modules and session engine, contracts, the Unity client's layers and views, update rates, data sources, testing and known limitations |
| [Session creation and recovery](session_creation.md) | Session lifecycle, the v2 HTTP contract, deletion and disk cleanup, runtime and crash recovery, instrumentation |
| [Training contract v2](../contracts/v2/README.md) | Wire rules, entities, battery and booking additions, state transition tables |
| [Backend](../backend/README.md) | Running the server and the headless actors |
| [Backend migration record](backend-migration.md) | What moved from `Formula_9`, how it was adapted and validated ([source hashes](backend-migration-source.json)) |
| [Third-party vehicle assets](third_party_assets.md) | Imported model packs and their licence status |
| [Product direction](product_direction.md) | The approved training brief and how its open questions were interpreted. Its concept images are in [assets](assets). |

## Historical and planning material

These files come from before the Unity migration. They plan for separate React/three.js web and
C++/Qt native clients, which this repository no longer has. Their targets and task lists are not a
description of the current system. Keep them for reference only, or remove them.

| Material | What it was |
| --- | --- |
| [milestones.md](milestones.md), [backend/](backend/README.md), [web/](web/README.md), [native/](native/README.md), [deliverables_web.md](deliverables_web.md), [deliverables_native.md](deliverables_native.md) | M2–M6 roadmap and per-team milestone deliverables |
| [milestone_status/](milestone_status/M2) | M2 task handoffs and evidence. `backend/tests/test_spatial_motion.py` reads `M2/be-m2-08-trace.json`, so move or keep that file before removing the folder. |
| [three_d_simulation.md](three_d_simulation.md) | Planned 3D contract for the web and Qt clients |
| [performance.md](performance.md) | Proposed performance and resilience targets, not measurements |
| [archive/](archive) | The earlier decision-policy architecture and client plans |
| [kitti_trajectory_data.md](kitti_trajectory_data.md), [gps_exports/](gps_exports), [trajectory_plots/](trajectory_plots) | KITTI trajectory notes and outputs, used only by the legacy v1 playback |
| `Formula 9 – M2 Product & Design Direction (Working Draft).pdf` | The source brief behind [product direction](product_direction.md) |
