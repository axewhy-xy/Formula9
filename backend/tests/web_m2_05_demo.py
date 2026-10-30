"""Local WEB-M2-05 browser fixture using real services and an isolated SQLite store.

Run from the repository root:
  $env:PYTHONPATH = 'backend;.'
  backend/.venv/Scripts/python.exe -m backend.tests.web_m2_05_demo --export-fixture
  backend/.venv/Scripts/python.exe -m backend.tests.web_m2_05_demo --serve

Only this test process overrides authentication; production auth is unchanged.
Open the Vite client at /?training=1&session=web-m2-05.
"""
import argparse
import json
import tempfile
from pathlib import Path

from fastapi.testclient import TestClient

from contracts.v2.models import Charger, Depot, DispatcherCommand, Position
from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.energy.decisions import with_battery_decisions
from src.energy.model import ENERGY_R1
from src.persistence.training_store import TrainingStore
from src.services.incident_demo import initial_snapshot
from src.services.incidents import get_incident_service


def seed(store):
    service = get_incident_service()
    initial = initial_snapshot(service, "web-m2-05", tick=238)
    initial.vehicles[0].speed_mps = 5.0
    # The curated incident route is shorter than the 1.2 km contract route.
    # Use 0.1 kWh above reserve so it offers the same low-loop choice.
    initial.vehicles[4].battery_kwh = 6.1
    initial.chargers = [Charger(charger_id=f"charger-{i}", aggregate_revision=1,
                               port_count=1, power_kw=50, occupied_vehicle_ids=[]) for i in (1, 2)]
    initial.depots = [Depot(depot_id="depot-1", position=Position(x_m=0, y_m=0, z_m=0),
                            charger_ids=[c.charger_id for c in initial.chargers])]
    store.create_session(with_battery_decisions(ENERGY_R1, initial))
    staged = store.snapshot(initial.session_id)
    staged.tick = 240
    staged.simulation_time_s = 12
    current = service.publish(store, staged)
    incident = current.incidents[0]
    candidate = next(c for c in current.candidates if c.action == "WAIT")
    service.decide(store, "web-test", DispatcherCommand(
        contract_version="2.0", command_id="web-incident-decision", idempotency_key="web-incident-key",
        session_id=current.session_id, command_type="APPROVE_INCIDENT_OPTION",
        expected_epoch=current.session_epoch, expected_revision=incident.aggregate_revision,
        target_type="INCIDENT", target_id=incident.incident_id, payload={"candidate_id": candidate.candidate_id},
        reason="Wait for the scripted obstruction to clear safely",
    ))
    staged = store.snapshot(initial.session_id)
    staged.tick = 440
    staged.simulation_time_s = 22
    service.publish(store, with_battery_decisions(ENERGY_R1, staged))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--export-fixture", action="store_true")
    parser.add_argument("--serve", action="store_true")
    args = parser.parse_args()
    scratch = Path(__file__).resolve().parents[2] / "build/web-m2-05"
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="run-", dir=scratch) as directory:
        with TrainingStore(Path(directory) / "training.sqlite") as store:
            seed(store)
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "web-test"
            try:
                if args.export_fixture:
                    with TestClient(app) as client:
                        result = client.get("/api/v2/sessions/web-m2-05/incidents/incident-m2-obstruction/debrief")
                        result.raise_for_status()
                        target = Path(__file__).resolve().parents[2] / "frontend/src/features/debrief/_fixtures_/incident_debrief.json"
                        target.parent.mkdir(parents=True, exist_ok=True)
                        target.write_text(json.dumps(result.json(), indent=2) + "\n", encoding="utf-8")
                        print(f"Exported real backend debrief to {target}")
                if args.serve:
                    import uvicorn
                    uvicorn.run(app, host="127.0.0.1", port=8000)
            finally:
                app.dependency_overrides.clear()


if __name__ == "__main__":
    main()
