# Formula9 technical documentation

Current direction: **3D AV shuttle fleet manager training simulator**, approved product direction dated 20 October 2026, with the user's mandatory-3D clarification incorporated on 27 October.

Start with [technical architecture](architecture.md), [mandatory 3D contract](three_d_simulation.md), [integrated M2-M6 roadmap](milestones.md) and [performance acceptance](performance.md). [Product traceability](product_direction.md) maps the approved brief, five visual concepts and later 3D clarification to scope decisions.


## Team delivery plans

| Directory | Scope |
| --- | --- |
| [backend](backend/README.md) | Fleet simulation, planning/validation, incidents, charging, durable commands, assessment and reports |
| [web](web/README.md) | React/TypeScript/three.js web operator and trainer application |
| [native](native/README.md) | C++20/Qt/OpenGL/FMOD equivalent desktop application |

Each directory contains a track overview and separate `m2.md` through `m6.md`, with task IDs, dependencies, deliverables, failure tests and exit gates. `app` refers to the web client whose source remains under `frontend/`.

The 3D revision preserves all existing milestone tasks and implementation handoffs, adding new numbered work packages with outputs/verification and additional 3D exit criteria. Both clients require a 3D operational world from M2; optional detail/media and 2D accessibility fallbacks do not waive that gate. Existing status reports do not establish completion of the new tasks.

M3 is the complete workflow MVP, M5 feature complete, and M6 beta release. Numerical budgets are proposed until measured on the declared reference setup.

## Supporting and historical material

- [KITTI trajectory notes](kitti_trajectory_data.md) retain the data foundation; they do not define synthetic incident/decision semantics.
- [Prior architecture](archive/architecture-decision.md), [prior web plan](archive/deliverables-web-decision.md) and [prior native plan](archive/deliverables-native-decision.md) preserve the superseded direction and historical assessments.
- Existing top-level deliverables documents redirect to the current team directories.

This documentation update specifies implementation work. It does not itself implement fleet simulation, change runtime contracts, or establish measured performance.
