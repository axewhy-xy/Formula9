# Formula9

Unity client and Python/FastAPI backend for the Formula 9 AV shuttle fleet training
simulator. The backend, shared contracts, fixtures, static map and support scripts have
been migrated from [Formula_9](https://github.com/axewhy-xy/Formula_9). This repository runs
independently of that checkout. See [migration details](docs/backend-migration.md).

## Getting started

Clone the repository and double-click **`run.bat`**. It asks for nothing. It installs whatever is
missing (Python 3.12, Git LFS, Unity Hub and Unity **6000.1.12f1**), downloads the large files, sets
up the backend, opens the project in Unity and serves the backend in its window. Keep that window open
while you work, then press Play in Unity. Unity versions install side by side, so other versions you
have are left alone.

The first run needs internet and takes a while, because Unity alone is several GB. Windows may ask
you to allow an installer, and Unity may ask you to sign in with a free account; those two cannot be
automated.

## Running the backend

From the repository root, start the server by double-clicking `run.bat`. It asks for nothing: the
first run sets up its Python environment, which takes a few minutes and needs internet.
`backend/.env` is committed for the team, so the shared MongoDB connection works on a fresh clone of
this private repository.

```powershell
python Tools/run_backend.py
# Windows shortcut: run.bat
# Development reload: run.bat --reload
```

The launcher requires this repository's `backend/.venv` (created by `--setup`)
and prints the repository, interpreter and backend paths at startup. It serves the API at
`http://127.0.0.1:8000` (health: `/api/health`, API explorer: `/docs`); Ctrl+C stops it.
Without a MongoDB connection the server starts, but account endpoints return 503.
Existing accounts remain available when configured with the same account database.
Session state defaults to ignored `state/training.sqlite`; `TRAINING_DATABASE_PATH`
selects another database. The bundled static map is `backend/fixtures/map/map.db`.
Downloaded KITTI datasets and optional ML weights are not required for the default
training scenarios. See [backend documentation](backend/README.md) for more workflows.

## Running the client

1. Start this repository's backend with `run.bat` or `python Tools/run_backend.py`.
2. Open `Assets/Scenes/Formula9.unity` and press Play, or build a player.
3. Log in with your team account (or **Create account**), then create or join a training
   session. New sessions default to the integrated exercise.

The Unity client defaults to `http://127.0.0.1:8000`; set `FORMULA9_BACKEND_URL` to use
another address. Opening the Unity project does not start a backend process automatically.

If the scene or its UI panel settings go missing, rebuild them with
**Formula 9 → Rebuild client scene** (or headless:
`Unity.exe -batchmode -quit -projectPath . -executeMethod Formula9.EditorTools.SceneBuilder.Build`).

## Building the executable

A build made from Unity's Build menu only contains the client. To build one that also runs the
backend, close the project in Unity and double-click **`build.bat`** (it sets up `backend/.venv`
first if `run.bat` has not):

```powershell
python Tools/build_executable.py [--output Builds/Formula9] [--zip] [--skip-unity] [--include-env]
# Windows shortcut: build.bat [--zip] [--include-env]
```

It builds the Windows player in batch mode into `Builds/Formula9`, then adds a `Backend` folder with
a portable copy of Python 3.12 (the one `backend/.venv` was made from, with its packages), the backend
source and fixtures, and the contracts, all precompiled so the first launch starts quickly. It then
starts that copy from outside the repository to check it answers `/api/health`. `--zip` also packs
`Builds/Formula9.zip`; `--skip-unity` keeps the player already there and only bundles the backend
again. The Unity log goes to `Logs/build-player.log`. `--output` only replaces a folder that is empty
or an earlier build.

`backend/.env`, which holds the team's MongoDB connection, is left out by default, so a build passed
around cannot reach the team's database; without it the account endpoints answer 503 and nobody can
log in. `--include-env` ships it; share such a build only with people who may use that database.

Copy the whole folder. `Formula9.exe` starts the bundled backend hidden on `http://127.0.0.1:8000`
when it opens (`BundledBackend.cs`), and requests wait for it to start listening. Every open
`Formula9.exe` shares that one backend, which shuts down cleanly once the last of them has closed,
even after a crash (`Tools/bundled_backend.py`). Its log, appended to on every launch, is
`Backend/logs/backend.log`, and its sessions are kept in `Backend/state`. When a backend is already
listening on the port, such as one from `run.bat`, the players use it instead. `FORMULA9_BACKEND_URL`
moves the bundled backend to another local port, or turns it off when it points to another machine.

## Layout

| Folder | Contents |
|---|---|
| `backend` | FastAPI server, SQL migrations, simulation services, fixtures and Python tests |
| `contracts` | Shared Python contracts, schemas, fixtures and generators |
| `scripts` | Backend scene generation, optional dataset and ML support scripts |
| `docs` | Backend migration record, session documentation and historical M2 handoffs |
| `Assets/Formula9/Runtime/Contracts` | v2 contract classes and validation |
| `Assets/Formula9/Runtime/Backend` | HTTP access to the backend |
| `Assets/Formula9/Runtime/Session` | Joined session, polling, events and commands |
| `Assets/Formula9/Runtime/UI` | Screens (`Formula9App`) and their presenters |
| `Assets/Formula9/Runtime/World` | Verified scene loading, glTFast placement and the 3D view |
| `Assets/Formula9/Runtime/Audio` | Training alerts, the Mute alerts setting and interface sounds |
| `Assets/Formula9/UI` | UI Toolkit layout, styles, theme and panel settings |
| `Assets/Formula9/Editor` | Editor tools, including the scene builder |
| `Assets/Formula9/Tests/EditMode` | EditMode tests and the shared contract fixtures |
| `Tools` | Scripts run outside Unity |

## Contracts

`Runtime/Contracts/Generated` is written by `Tools/generate_contracts.py` from local
`contracts/v2/*.schema.json`; do not edit it by hand. The generator also embeds the schemas, so
incoming JSON is validated against exactly the backend's rules, and copies the shared fixtures
into the tests. `ContractRules.cs` holds the cross-field checks the schemas cannot express,
mirroring the pydantic validators in `contracts/v2/models.py`.

After the contracts change in this repository:

```
python Tools/generate_contracts.py
```

## Backend access

`Runtime/Backend` holds the HTTP layer. `AuthClient` logs in against the team's shared
accounts and `AuthSession` keeps the token in memory only. `TrainingApi` calls
`/api/v2/sessions` and returns validated contract objects. Every failure is one of three
kinds: `BackendErrorException` (a complete error reply, for example `SESSION_RECOVERING`),
`NoReplyException` (no complete reply; the request may still have taken effect, so commands
must be confirmed, never re-created) or `NotLoggedInException`. Training requests are never
sent without a login.

## Sessions

`Runtime/Session` ports the training half of the Qt client's `BackendClient` and its
`SessionStore`. `SessionController` creates (keeping the creation key and its owner until the
attempt is confirmed), joins and polls a session every second, replays events from a
committed cursor, fetches events a recovery appended after an epoch change, and resynchronises
from a fresh snapshot when an event batch does not fit. It holds at most one open command:
a lost reply is confirmed by resending the same command, or by asking for its status after a
recovery, backing off to at most 5 s, and the command waits for its own account across logins.
`SessionStore` refuses older epochs, stale or reused snapshots and event gaps, and keeps the
latest 1,000 events.

New sessions use **Singapore, real streets (2D)**, the exercise with incidents, detours and charging.

## Editing the pages

The **Pages** tab beside the Inspector lists every page (reopen it from **Formula 9 > Pages**). Click
a page to show it in the Game view, or **Edit** to open it in UI Builder. Your choice stays on screen
outside Play mode; pressing Play always starts at Login. The files are in `Assets/Formula9/UI/Pages`, and
colours and spacing are in `Assets/Formula9/UI/Formula9App.uss`. Live content such as the map, fleet
cards and table rows comes from the backend at runtime, so it only appears in Play mode. Controls
are found by `name`, so keep a control's name when you move or restyle it.

## Real Singapore map

The 2D scenario, **Singapore, real streets**, runs on real Toa Payoh roads and sits on an
OpenStreetMap basemap. Tiles are cached in memory and on disk. `FORMULA9_TILE_URL` selects another
`{z}/{x}/{y}` tile server; `none` turns the basemap off for tests and offline demos. Existing 3D
sessions use a local frame rather than a real place, so they have no basemap.

Real bus stops appear when zoomed in. They come from OpenStreetMap, with their LTA stop codes, and
are committed in `backend/fixtures/transit/transit.sqlite`, so nobody needs a key. To refresh them, run
`python -m src.transit.import_osm` from `backend/`. Bus services and routes are not in OSM; those
need a free [DataMall key](https://datamall.lta.gov.sg) in `backend/.env` as `LTA_ACCOUNT_KEY=...` and
`run.bat --import-bus-data`, which replaces the file.
`python -m src.routing.build_singapore_fleet_fixture` rebuilds the scenario from the committed
extract in `backend/fixtures/osm`.

The 3D city uses illustrative tree placements in
[`ToaPayohTrees.csv`](Assets/Formula9/Resources/Formula9/Vegetation/ToaPayohTrees.csv). Each tree uses one
uniform scale, keeping the original trunk and canopy proportions while still varying in size. The smaller
of `height_scale` and `width_scale` sets its size, so correcting a stretched tree never expands its canopy
beyond the placement's existing clearance. Both detail levels use the same scale. Planting follows
roadside setbacks and green spaces using the bundled geometry, [OpenStreetMap contributors](https://www.openstreetmap.org/copyright),
and the [National Parks Board's Parks and Nature Reserves dataset](https://data.gov.sg/datasets/d_77d7ec97be83d44f61b85454f844382f/view).
Trees use spatially culled GPU batches and reuse matching materials between detail levels. Rendering
settings are prepared once per city, and disposing a city releases only its own material copies.
Nearby off-screen batches contribute shadows within 45 metres. Missing or malformed models disable
optional trees without stopping the city load. Trees are also disabled when GPU instancing is unavailable,
avoiding individual draws for every tree on older hardware.

Grass is a shared, compressed 512-pixel texture on the existing ground tiles and roadside verges.
It adds no grass objects, triangles, colliders, materials or draw calls. Ground detail repeats in world
coordinates, so it stays aligned between tiles, and mipmaps soften it in distant views. When map imagery
has loaded, grass is restricted to green map areas; roads, paving and water keep their map colours.
The colour mask favours pale vegetation fills and rejects dark labels, saturated greens and blue-green
sports surfaces, using the [OSM Carto landcover palette](https://github.com/gravitystorm/openstreetmap-carto/blob/master/style/landcover.mss)
and cached Toa Payoh tiles as references. This remains an approximate colour mask: features sharing a
vegetation fill colour cannot be distinguished without landcover polygons.
Without imagery, the existing road, sidewalk and building geometry sits above the textured ground.
The `grass` settings in `Resources/Formula9/world_look.json` control `enabled`, `repeatM` and `strength`.
Set `enabled` to `false` to keep the previous ground and verge appearance without loading the grass asset.

## Background traffic data

`backend/fixtures/traffic/toa-payoh.json` holds the background traffic prepared for the 3D view. The 3D
view draws its roads, served by `/api/roads/<area>`, as asphalt with lane lines over the map tiles, and drives the
vehicles over them. They follow each other, obey the lights, give way at junctions, and keep off live incidents,
queues and shuttles (`CityTrafficDriving.cs`). This is Unity-side illustration: the backend does not know about them
and they never affect a shuttle. To rebuild the file, run this from `backend/`:

```powershell
python -m src.traffic.import_vehicle_population [--percent 0.1] [--year YYYY] [--seed N] [--offline]
```

It downloads LTA's [Annual Motor Vehicle Population by Vehicle Type](https://data.gov.sg/datasets/d_2873f3b1b2a836103f51f696350b98fa/view)
from data.gov.sg (no key needed) and saves the rows as `vehicle-population.json`; `--offline` rebuilds from that copy.
It takes `--percent` of each vehicle type, keeping at least one of every type, and places them in lanes of
the real roads inside the service area's box. Each vehicle has a model, colour, road, lane, position and
heading in local metres. The file also has the model sizes and the road network, split at junctions.

The counts come from the dataset. The sizes, colours and how much busier each road class is are illustrative
assumptions, and the mix of types is national, because the dataset has no location data.

## Traffic lights, crossings and road markings

Toa Payoh's traffic lights, crossings and road markings are ready for the 3D view to draw. The view does not
draw them yet, though its traffic obeys the lights, on its own clock. Positions are local metres in the same frame as the buildings: `x_m` east and `y_m` north.

| File in `backend/fixtures/traffic/` | API, under `/api` | Holds |
|---|---|---|
| `toa-payoh-signals.json` | `GET /road-features/toa-payoh/signals` | `junctions` with their `approaches`, signal heads in `signals`, cycle in `timing` |
| (worked out live) | `GET /road-features/toa-payoh/signals/state?t=SECONDS` | Each junction's colour and `seconds_left` per phase; `t` defaults to now |
| `toa-payoh-crossings.json` | `GET /road-features/toa-payoh/crossings` | `crossings`: a `line_m` across the road, a `render_as`, and `stripes_m` (zebra bars, four corners each) or `edges_m` (a signal crossing's two lines) |
| `toa-payoh-markings.json` | `GET /road-features/toa-payoh/markings` | `lane_lines`, `arrows`, `words`, `road_humps` |

To colour a head, take its `junction` and `phase` and look up that phase's colour in `/signals/state`. To run
without polling, work it out from the same numbers, with `P = green_s + yellow_s + all_red_s` and
`u = (t + offset_s - phase * P) mod cycle_s`: green while `u < green_s`, then yellow while `u < green_s + yellow_s`, then red.

To rebuild them, run this from `backend/`. It downloads LTA's public files, and needs no key:

```powershell
python -m src.traffic.import_road_features [--release Sep2026] [--source-dir FOLDER]
```

Headings are degrees anticlockwise from east, like the vehicles'. A head's `traffic_heading_deg` is the way the traffic
it controls is going, and `facing_heading_deg` is the way its lights point, back at that traffic. Each junction's
`approaches` are the roads leading in, with where traffic stops (`stop_x_m`, `stop_y_m`) and its `phase`. `arrows` are
points with their own `heading_deg`, and are not among the `lane_lines`.

DataMall names its files by release and updates them quarterly, so a later release needs `--release`.

What is real and what is not:

- **Real, from LTA:** where each head, crossing, line, arrow, word and hump is, and each bearing, road code and word.
  An arrow's bearing is its direction of travel, within 2 degrees on a typical one-way road.
- **Simulated:** the colours and timings. LTA does not publish them; every junction runs the same 70 s two-phase
  cycle (30 s green, 3 s yellow, 2 s all red). Show this as a simulation, not as what the junction really does.
- **Worked out here:** junctions are heads within 60 m of each other. Approaches come from the OpenStreetMap roads, and
  each head is given the nearest, preferring one with the head on its kerb side, so a head on the far side of a junction
  may get the wrong one. `direction_source` says where a head's direction came from: `road_map`, or `lta_bearing` if no
  mapped road is near. Phases come from the road axes, so turning phases are not modelled. `render_as` is `zebra` for a
  crossing with no signal head within 15 m. LTA does not label zebras, so that is a good guess, not a fact.
- **Chosen sizes:** crossings' 3 m width and zebra bars' 0.5 m thickness and gap, listed in `crossing_shape`.
- **Unknown:** LTA does not explain `type_code`, so it is passed on as is. It is not a colour. Nor does LTA say whether
  a head's `bearing_deg` (clockwise from north) is the way it faces. It often does not line up with the road, so it
  is kept for reference and the direction fields are what to use.

Any screen showing this data must credit LTA with the `attribution` in each file, under the
[Singapore Open Data Licence](https://datamall.lta.gov.sg/content/datamall/en/SingaporeOpenDataLicence.html).

## Training workspace

Click **Assessment mode** to create the timed Toa Payoh exercise and receive a grade for route
planning, incident response, and charging under **Reports → Assessment**. See the [grading rubric](docs/grading.md) for the
exercise, point rules and automatic failures.

All seven remaining Qt client workflows are available in Unity. The workspace tabs beside
the world view are **Fleet**, **Route planning**, **Charging** and **Reports**. Incidents are
decided on Route planning, and reviewed in Reports.

- **Route planning:** the page has two tabs, **Exercise** and **Fleet table**. Without a joined session it
  asks you to join one, and a session without shuttles asks you to add one. The exercise's cards are numbered
  steps down a line: the current step's circle is filled in and finished steps are ticked. Before the start,
  the exercise runs Problem, Detour, Check, Approve, Outcome. The map shows
  the closure and every road of the operating network. Click roads to send the shuttle along
  them; the backend plans the loop through them and the stops, and each change is checked
  straight away. A failed check says why in plain words and highlights the road (**Show** brings
  it into view). A road with no recorded width or headroom can't be cleared, so it blocks
  approval. Time, energy, distance, stop coverage and loops on charge are shown against the closed
  route. Click a pin or use ×, Undo, Redo or Clear to change the proposal; **Suggest detour**
  asks for the scenario's own way round. Give a reason and approve for every assigned shuttle.
  **Start and watch in 3D** then follows the shuttle as it drives that exact geometry, and
  **What happened** reports the stops served, each loop's measured time and energy, and whether
  service was preserved. With no problem and nothing to report yet, that card keeps only its
  **Start and watch in 3D**, **Watch in 3D** and **Cameras** buttons. A revision, closure, epoch or
  draft change requires a fresh check. The map data has no turn restrictions, so U-turns are the
  only turns refused.
  The **Fleet table** lists each shuttle's state, route and battery. The route reads as its ID followed by
  **original**, or by how many times it has been edited. A shuttle sent round an incident shows
  **Detour round** and the incident, or **Incident detour** once that incident has gone.
- **Charging:** review battery state, reserve forecasts, charger occupancy and offered
  consequences. Select a passing, unexpired option, enter a reason, then use
  **Reserve / Continue service**. The backend applies the decision and determines all motion
  and energy consequences.
- **Incidents:** an incident a shuttle will reach shows a notification over the map. **Decide**
  opens Route planning on that shuttle. A strip at the top lists every shuttle waiting, soonest first,
  as "Shuttle 05 needs a decision (1) · ~2 s". The Problem card says what and where the incident
  is and when the shuttle reaches it. The question under it shows when the backend next refreshes
  the options. Choose **Keep route** (with what keeping the route costs), **Hold at a stop**,
  **Request assistance** or **Re-route on the map**. Any option but Re-route opens a confirm card:
  give a reason and confirm. Re-route opens the route editor for that shuttle. An option the backend
  doesn't offer is greyed out with the reason, and all of them wait while the session is paused.
  There is no Back button: press the chosen option again to close its confirm card, even while the
  others are greyed out. Pressing **Re-route on the map** again leaves the editor. If a route has
  been drawn or suggested, the first press shows a note and a second press drops the route.
- **Reports:** inspect the latest 1,000 events and 200 commands observed in the selected
  session by this running client, including pending confirmation, refusal and final status.
  Search each tab by IDs, event type, target, status or failure code; every search word must
  match a displayed field, ignoring case. Combine search with the event-type or command-status
  dropdown, review the matching count, or use **Clear filters**. Filters survive incoming
  records and reset when switching sessions.
  These are bounded client records; they are not a server-wide ledger or an export.
  The **Incidents** tab lists the session's incidents and what was decided. **History** replays
  the simulation around one on a map, a sample at a time, and **Debrief** shows the backend's
  provisional rubric, decisions and outcomes. These views never replace the live fleet or replay
  alert sounds.
- **3D interaction:** click rendered geometry to select an entity; select a fleet row and use
  **Follow selected** or **Overview**. Right-drag orbits, middle-drag pans and the wheel zooms.
  For route drafting, select **Draft in 3D** and the road surface/lane/deck, then click a node
  marker on that surface. A draft can cross multiple surfaces; clicks respect height and
  occlusion. The backend validates the resulting route.
- **2D fleet map:** planar sessions show authoritative route paths, active closures,
  shuttles, depots, charger IDs and incident markers. Click a shuttle to select it,
  middle-drag to pan and use the wheel to zoom.
- **Scene readiness:** after verifying, placing and rendering mandatory 3D geometry, Unity
  posts `/api/v2/scenes/{scene_revision}/readiness` with the pinned manifest and asset hashes.
  Start remains disabled until that report succeeds; use **Retry 3D** after a load failure.

For the default integrated exercise: create a session, open Route planning, click roads round
the closure (or ask for a suggestion), enter a reason, approve, then start. Charging and incident decisions become
available as the exercise runs. Unsupported route metadata and failed or unknown mandatory
checks are shown as such; Unity does not fabricate a feasible path.

## 3D world

A spatial session's snapshot pins its scene revision and manifest hash. `SceneLoader` fetches
`/api/v2/scenes/{revision}`: the manifest must match the pinned hash and revisions, and each
mandatory GLB its declared size and SHA-256, before anything is decoded. `WorldBuilder` loads
the GLBs with glTFast and places every entity; each one's rendered extent must match its
declared `dimensions_m`, so a coordinate mistake is reported ("3D unavailable · …") rather than
drawn. `WorldView` renders into the training screen's world panel (right-drag orbit,
middle-drag pan, wheel zoom) and moves shuttles to each snapshot. Start stays disabled until a
spatial session's world is ready.

Coordinates (`WorldMath`): the contract world is right-handed, X east, Y north, Z up; Unity is
left-handed, Y up, so canonical (x, y, z) becomes Unity (x, z, y). Assets are raw glTF, taken
into the body frame by `asset_to_body` after undoing glTFast's X flip. The tests check this
against the backend's own marker points in `m2_3d_motion.json`.

The project uses Linear colour space (glTF colours are linear), and the scene builder adds
glTFast's shaders to *Always Included Shaders* so player builds render them.

### Building textures

The real-streets city textures its buildings' walls: plain wall on the ground floor (3.5 m, like an
HDB void deck or a shop front) and windows above it, tinted by each building's colour. Roofs stay
plain. Each building uses one of five sets of a wall and a window texture, chosen in
`CityMesh.TexturePair` from its OpenStreetMap type and height (25 m, about eight storeys, makes a tower):

| Set | Buildings |
| --- | --- |
| HDB | `residential` towers (OSM tags HDB blocks `residential`) |
| Home | `apartments` towers (private condominiums) |
| Low-rise | houses, terraces, detached and semi-detached houses, and `residential` or `apartments` under 25 m |
| Office | `office`, `commercial`, `hotel`, `hospital`, and untagged (`yes`) towers |
| Other | everything else: shops, schools, industry, garages |

`CityMesh` gives each wall vertex its distance along the outline and its height in metres, so
`CityBuilding.shader` tiles the textures at their real size; the sizes are material properties, one a
set (`_TileHome`, `_TileOther`, `_TileHdb`, `_TileLowRise`, `_TileOffice`: wall in xy, windows in zw),
with `_GroundFloorM` for the plain ground floor.

The textures are in `Resources/Formula9/Textures`, all CC0 (free to use without attribution), from
ambientCG, ShareTextures and Poly Haven:

| File | Source |
| --- | --- |
| `building_wall_home.png` | [Plaster001](https://ambientcg.com/view?id=Plaster001) |
| `building_wall_other.png` | [Concrete034](https://ambientcg.com/view?id=Concrete034) |
| `building_window_home.png` | [Facade006](https://ambientcg.com/view?id=Facade006) |
| `building_window_other.png` | [Facade019A](https://ambientcg.com/view?id=Facade019A) |
| `building_wall_hdb.png` | [Plaster002](https://ambientcg.com/a/Plaster002) |
| `building_window_hdb.png` | [Facade 1](https://www.sharetextures.com/textures/other/facade_1) |
| `building_wall_lowrise.png` | [Plaster Brick Pattern](https://polyhaven.com/a/plaster_brick_pattern) |
| `building_window_lowrise.png` | [Facade018A](https://ambientcg.com/a/Facade018A) |
| `building_wall_office.png` | [Exterior Wall Cladding 03](https://polyhaven.com/a/exterior_wall_cladding_03) |
| `building_window_office.png` | [Facade005](https://ambientcg.com/a/Facade005) |

## Alerts

`AlertGate` and `TrainingAlerts` port the Qt client's NAT-M2-07 rules: only events listed in
the shared `contracts/v2/alert_cues.json` (embedded by the generator as `AlertCues`) play a
sound; each event ID plays at most once, even when a reconnect replays it; history never plays;
and muting still marks events as seen, so unmuting causes no burst. The sounds in
`Resources/Formula9/Audio` are the Qt client's `alert_*.wav`, except the incident alert
(`ui_accident.wav`, played at half volume), battery critical (`ui_battery_low.wav`, the only battery sound, played once when a shuttle's
gauge turns red below 20%; a shuttle with no battery capacity reads a red 0%), disconnect
(`ui_dull_01.wav`) and reconnect (`ui_confirmation.wav`, after any outage that ends). When the
backend comes back with a session it recovered after a crash, its `SESSION_RECOVERED` event plays
the session recovered alert instead of the reconnect sound, so the two never play together. **Mute alerts** is kept across
restarts in `PlayerPrefs` (`training.muteAlerts`); tests use in-memory preferences.

## Interface sounds

`UiSounds` applies amee's sound consultation with Vuk and the audio professor's review. Buttons
make no hover sound, and every press not listed below stays silent:

| Sound | Clip | Plays when |
|---|---|---|
| Start session | `ui_start_session.wav` (UI Button Click-Dull) | **Start scenario**, **Start and watch in 3D** or **Resume simulation** is clicked |
| Confirmation | `ui_confirmation.wav` (UI Confirmation_2) | A decision from **Approve route** (a re-route), Route Planning's **Confirm** (keep route, hold or request assistance) or the Charging page's **Apply decision** (a battery decision) is sent. A click its checks stop (options withdrawn or refreshed, an invalid reason, a busy session) sends nothing and stays silent |
| Map point | `ui_map_point.wav` (UI Hover New 1) | a road is picked for a route on the map |
| Refused | `alert_command_rejected.wav` | a greyed-out button is pressed while nothing is being sent |

The refused sound listens once on the simulator's root, so controls built later are covered
without extra wiring. Interface sounds play at 0.35 volume, except the confirmation, a soft
chime that plays at full volume; all clips are loaded when the app starts. They are
separate from training alerts, so **Mute alerts** does not silence them.

## Tests

Run the backend suite with temporary session storage and MongoDB disabled:

```powershell
python Tools/run_backend.py --test
# Windows shortcut: run.bat --test
```

The OpenStreetMap download tests remain opt-in (`RUN_INTEGRATION_TESTS=1`). The normal
suite uses the bundled map and needs no dataset download. The original backend test
suite, including real process crash/recovery tests, is retained.

Run EditMode tests in the Test Runner window, or headless:

```
"C:\Program Files\Unity\Hub\Editor\6000.1.12f1\Editor\Unity.exe" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults TestResults.xml
```

Tests in the `LiveBackend` category call a running backend and are skipped when none is
reachable. They use no real account.

The `EndToEnd` tests run the whole client over real HTTP: sign up, log in, create, start, pause,
resume and archive sessions, approve the integrated detour, reserve a charger, preview and
approve an incident, read its history/debrief, and load and place a live 3D world. They sign up throwaway users,
so they only run against an isolated backend and are skipped unless
`FORMULA9_E2E_BACKEND_URL` is set:

```
backend\.venv\Scripts\python Tools/isolated_backend.py --seed-incidents  # in-memory accounts, temporary sessions, port 8010
set FORMULA9_E2E_BACKEND_URL=http://127.0.0.1:8010
```

`--seed-incidents` schedules a known collision and signal fault in every session, beside the
random ones. The incident end-to-end test decides the seeded collision, so without the flag it
fails after waiting 60 seconds for it. Never point `FORMULA9_E2E_BACKEND_URL` at the team's shared backend. The `Render` test draws the real scene and needs a GPU,
so it is skipped under `-nographics`; set `FORMULA9_RENDER_OUT` to a `.png` path to keep the
image. `RouteWalkthrough`, in the same category, plays the routing exercise through the real UI in Play
mode and keeps a picture and a log line per step in `FORMULA9_WALKTHROUGH_OUT`. It signs up
throwaway users too, so it is skipped unless `FORMULA9_BACKEND_URL` is also set, at the isolated
backend. It decides the seeded incidents, so that backend must also run with `--seed-incidents`.

The workflow tests also cover stale route graph replies, inclusive offer expiry ticks,
lost approval replies, readiness payloads, and canonical mesh picks against the shared
interaction fixture. Keep Git LFS assets hydrated before running Unity: GLB/WAV pointer
files cannot be loaded as models or audio.
