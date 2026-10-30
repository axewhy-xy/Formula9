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
| `Assets/Formula9/Runtime/Audio` | Alerts, still to be ported |
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

## Tests

Run EditMode tests in the Test Runner window, or headless:

```
"C:\Program Files\Unity\Hub\Editor\6000.1.4f1\Editor\Unity.exe" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults TestResults.xml
```

Tests in the `LiveBackend` category call a running backend and are skipped when none is
reachable. They use no real account. The `Render` test draws the real scene and needs a GPU,
so it is skipped under `-nographics`; set `FORMULA9_RENDER_OUT` to a `.png` path to keep the
image.
