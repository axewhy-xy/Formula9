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

## Layout

| Folder | Contents |
|---|---|
| `Assets/Formula9/Runtime/Contracts` | v2 contract classes and validation |
| `Assets/Formula9/Runtime/Backend` | HTTP access to the backend |
| `Assets/Formula9/Runtime/{Session,World,UI,Audio}` | Client features as they are ported |
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

## Tests

Run EditMode tests in the Test Runner window, or headless:

```
"C:\Program Files\Unity\Hub\Editor\6000.1.4f1\Editor\Unity.exe" -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults TestResults.xml
```
