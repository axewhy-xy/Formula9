# Formula9

Unity client for the Formula 9 AV shuttle fleet training simulator. It talks to the same
FastAPI backend as the Qt and web clients in
[Formula_9](https://github.com/axewhy-xy/Formula_9); the backend, contracts and web app stay
there.

## Requirements

- Unity **6000.1.4f1** (open the repository root in Unity Hub)
- Git LFS (`git lfs install`), for models, images and audio
- The Formula_9 backend running locally (default `http://127.0.0.1:8000`;
  set `FORMULA9_BACKEND_URL` to use another address)

## Running the client

1. Start the Formula_9 backend (`run.bat`, or `uvicorn src.app:app --reload` in `backend`).
2. Open `Assets/Scenes/Formula9.unity` and press Play, or build a player.
3. Log in with your team account (or **Create account**), then create or join a training
   session. New sessions default to the integrated exercise.

If the scene or its UI panel settings go missing, rebuild them with
**Formula 9 → Rebuild client scene** (or headless:
`Unity.exe -batchmode -quit -projectPath . -executeMethod Formula9.EditorTools.SceneBuilder.Build`).

## Layout

| Folder | Contents |
|---|---|
| `Assets/Formula9/Runtime/Contracts` | v2 contract classes and validation |
| `Assets/Formula9/Runtime/Backend` | HTTP access to the backend |
| `Assets/Formula9/Runtime/Session` | Joined session, polling, events and commands |
| `Assets/Formula9/Runtime/UI` | Screens (`Formula9App`) and their presenters |
| `Assets/Formula9/Runtime/World` | Verified scene loading, glTFast placement and the 3D view |
| `Assets/Formula9/Runtime/Audio` | Training alerts and the Mute alerts setting |
| `Assets/Formula9/UI` | UI Toolkit layout, styles, theme and panel settings |
| `Assets/Formula9/Editor` | Editor tools, including the scene builder |
| `Assets/Formula9/Tests/EditMode` | EditMode tests and the shared contract fixtures |
| `Tools` | Scripts run outside Unity |

## Contracts

`Runtime/Contracts/Generated` is written by `Tools/generate_contracts.py` from Formula_9's
`contracts/v2/*.schema.json`; do not edit it by hand. The generator also embeds the schemas, so
incoming JSON is validated against exactly the backend's rules, and copies the shared fixtures
into the tests. `ContractRules.cs` holds the cross-field checks the schemas cannot express,
mirroring the pydantic validators in `contracts/v2/models.py`.

After the contracts change in Formula_9 (with both repositories side by side):

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

New sessions default to `scenario-m2-integrated-r1`, the exercise with incidents, detours and
charging.

## Training workspace

All seven remaining Qt client workflows are available in Unity. The workspace tabs beside
the world view are **Fleet**, **Route planning**, **Charging**, **Incidents** and **Reports**.

- **Route planning:** choose a route while the session is READY. Its backend graph shows
  directed edges, closures and endpoint node IDs. Click intermediate nodes in order, or enter
  their IDs; Undo, Redo and Cancel draft edit the proposal. **Validate / preview** displays
  mandatory checks, the proposed path, stop coverage, distance, duration and energy estimates.
  Enter an approval reason and approve for every assigned shuttle. An empty via list asks
  the backend for its configured detour. Start becomes available once the assigned route
  avoids the closure. A revision, closure, epoch or draft change requires a fresh preview.
- **Charging:** review battery state, reserve forecasts, charger occupancy and offered
  consequences. Select a passing, unexpired option, enter a reason, then use
  **Reserve / Continue service**. The backend applies the decision and determines all motion
  and energy consequences.
- **Incidents:** select an incident and offered option, enter a reason, preview, then approve.
  The hypothetical branch has its own labelled map. **History** shows committed historical
  samples with a sample slider and reports missing evidence; **Debrief** shows the backend's
  provisional rubric, decisions and outcomes. These views never replace the live fleet or
  replay alert sounds. **Focus incident shuttle** selects and focuses its 3D vehicle.
- **Reports:** inspect the latest 1,000 events and 1,000 commands observed in the selected
  session by this running client, including pending confirmation, refusal and final status.
  These are bounded client records; they are not a server-wide ledger or an export.
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

For the default integrated exercise: create a session, open Route planning, validate the
default detour, enter a reason, approve, then start. Charging and incident decisions become
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

## Alerts

`AlertGate` and `TrainingAlerts` port the Qt client's NAT-M2-07 rules: only events listed in
the shared `contracts/v2/alert_cues.json` (embedded by the generator as `AlertCues`) play a
sound; each event ID plays at most once, even when a reconnect replays it; history never plays;
and muting still marks events as seen, so unmuting causes no burst. The sounds in
`Resources/Formula9/Audio` are the Qt client's `alert_*.wav`. **Mute alerts** is kept across
restarts in `PlayerPrefs` (`training.muteAlerts`); tests use in-memory preferences.

## Tests

Run EditMode tests in the Test Runner window, or headless:

```
"C:\Program Files\Unity\Hub\Editor\6000.1.4f1\Editor\Unity.exe" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults TestResults.xml
```

Tests in the `LiveBackend` category call a running backend and are skipped when none is
reachable. They use no real account.

The `EndToEnd` tests run the whole client over real HTTP: sign up, log in, create, start, pause,
resume and archive sessions, approve the integrated detour, reserve a charger, preview and
approve an incident, read its history/debrief, and load and place a live 3D world. They sign up throwaway users,
so they only run against an isolated backend and are skipped unless
`FORMULA9_E2E_BACKEND_URL` is set:

```
python Tools/isolated_backend.py          # in-memory accounts, temporary sessions, port 8010
set FORMULA9_E2E_BACKEND_URL=http://127.0.0.1:8010
```

Never point `FORMULA9_E2E_BACKEND_URL` at the team's shared backend. The `Render` test draws the real scene and needs a GPU,
so it is skipped under `-nographics`; set `FORMULA9_RENDER_OUT` to a `.png` path to keep the
image.

The workflow tests also cover stale route graph replies, inclusive offer expiry ticks,
lost approval replies, readiness payloads, and canonical mesh picks against the shared
interaction fixture. Keep Git LFS assets hydrated before running Unity: GLB/WAV pointer
files cannot be loaded as models or audio.
