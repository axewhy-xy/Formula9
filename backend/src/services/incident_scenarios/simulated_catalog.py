"""Load synthetic incident templates and select a reproducible target on a live route.

This module DOES NOT mutate snapshots or publish incident events. The existing
IncidentService must remain the sole writer through its durable store.
"""
import json
import random
from dataclasses import dataclass
from pathlib import Path

DEFAULT_CATALOG = Path(__file__).resolve().parents[3] / 'fixtures' / 'incidents' / 'predefined_scenarios.json'

@dataclass(frozen=True)
class IncidentTemplate:
    kind: str
    duration_s: float
    warning_s: float
    detection_delay_s: float
    recovery_s: float
    max_multiplier: float

class SimulatedIncidentCatalog:
    def __init__(self, path=DEFAULT_CATALOG):
        data = json.loads(Path(path).read_text(encoding='utf-8'))
        if data.get('schema_version') != 1:
            raise ValueError('Unsupported scenario schema version')
        self.policy = data['random_generation']
        if not 0 < self.policy['min_interval_s'] <= self.policy['max_interval_s']:
            raise ValueError('Invalid random incident interval')
        if self.policy['max_active_incidents'] < 1:
            raise ValueError('max_active_incidents must be positive')
        self.templates = []
        for row in data['scenarios']:
            if row.get('kind') not in ('ROAD_BLOCK', 'ROAD_ACCIDENT'):
                raise ValueError('Unsupported incident kind')
            if row.get('source', {}).get('provenance') != 'SYNTHETIC':
                raise ValueError('Only synthetic scenarios are supported')
            if row.get('duration_s', 0) <= 0:
                raise ValueError('Incident duration must be positive')
            self.templates.append(IncidentTemplate(**{key:row[key] for key in
                ('kind','duration_s','warning_s','detection_delay_s','recovery_s','max_multiplier')}))
        if not self.templates:
            raise ValueError('No incident templates')

    def next_interval_ticks(self, rng, step_s=0.05):
        return max(1, round(rng.uniform(self.policy['min_interval_s'], self.policy['max_interval_s']) / step_s))

    def choose(self, snapshot, rng, occupied_edges=()):
        """Return template, vehicle ID, directed edge ID; None if no eligible route.

        Vehicle position along its route is NOT represented here. Until the host
        provides remaining-edge progress, only full-route candidates are safe
        to identify, not guaranteed ahead of the vehicle.
        """
        occupied = set(occupied_edges)
        routes = {r.route_id:r for r in snapshot.routes}
        choices = []
        for vehicle in snapshot.vehicles:
            if vehicle.service_state != 'IN_SERVICE':
                continue
            route = routes.get(vehicle.route_id)
            if route is None:
                continue
            for edge in route.directed_edge_ids:
                if edge not in occupied:
                    choices.append((vehicle.vehicle_id, edge))
        if not choices:
            return None
        vehicle_id, edge_id = rng.choice(choices)
        return rng.choice(self.templates), vehicle_id, edge_id
