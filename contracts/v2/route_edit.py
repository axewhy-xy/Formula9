"""Versioned pre-trip route edit and approval contracts."""

from typing import Annotated, Literal

from pydantic import Field, FiniteFloat, model_validator

from contracts.v2.models import Id, Position, Revision, WireModel


CheckStatus = Literal["PASS", "WARN", "FAIL", "UNKNOWN"]


class RoutePolylinePoint(WireModel):
    x: FiniteFloat
    y: FiniteFloat
    z: FiniteFloat


class RouteEditorNode(RoutePolylinePoint):
    node_id: int


class RouteEditorEdge(WireModel):
    edge_id: str
    source: int
    target: int
    closed: bool
    # Present for roads of an operating network: the name people know it by, whether the vehicle may
    # use it with the sentence saying why, and its drawn shape. Otherwise it runs straight between its nodes.
    road_name: str | None = None
    fit: CheckStatus | None = None
    fit_note: str | None = None
    path: list[RoutePolylinePoint] = Field(default_factory=list, max_length=1024)


class RouteEditorStop(WireModel):
    stop_id: str
    name: str
    node_id: int
    x: FiniteFloat
    y: FiniteFloat


class RouteEditorContext(WireModel):
    """Bounded, read-only curated graph and estimate assumptions for a route editor."""
    contract_version: Annotated[str, Field(pattern=r"^2\.[0-9]+$")] = "2.0"
    session_id: Id
    session_epoch: Revision
    route_id: Id
    route_revision: Revision
    scene_revision: Id
    fixture_id: Id
    nodes: list[RouteEditorNode] = Field(max_length=8192)
    edges: list[RouteEditorEdge] = Field(max_length=8192)
    stops: list[RouteEditorStop] = Field(default_factory=list, max_length=64)
    original_directed_edge_ids: list[str]
    start_node_id: int
    destination_node_id: int
    assumptions: list[str]


class RouteEditRequest(WireModel):
    contract_version: Annotated[str, Field(pattern=r"^2\.[0-9]+$")] = "2.0"
    route_id: Id
    expected_route_revision: Revision
    draft_revision: Revision
    via_node_ids: list[int] = Field(default_factory=list, max_length=16)
    directed_edge_ids: list[str] | None = Field(default=None, min_length=1, max_length=1024)
    # Roads the operator picked, in the order picked. The backend plans the loop through them.
    waypoint_edge_ids: list[str] = Field(default_factory=list, max_length=16)

    @model_validator(mode="after")
    def one_edit_form(self) -> "RouteEditRequest":
        if sum((self.directed_edge_ids is not None, bool(self.via_node_ids), bool(self.waypoint_edge_ids))) > 1:
            raise ValueError("Use one of via nodes, waypoint roads or explicit directed segments")
        return self


class RouteCheck(WireModel):
    check: Literal["GEOMETRY", "DIRECTION", "CLOSURE", "STOPS", "OPERATING_AREA", "VEHICLE_FIT", "TURNS"]
    status: CheckStatus
    message: str
    feature_id: str | None = None
    mandatory: bool = True
    # The roads to highlight: every segment this check is about.
    edge_ids: list[str] = Field(default_factory=list)


class RouteMetrics(WireModel):
    distance_m: float = Field(ge=0)
    duration_s: float = Field(ge=0)
    stop_coverage: dict[str, bool]
    energy_kwh: float = Field(ge=0)


class RouteServiceImpact(WireModel):
    vehicle_id: Id
    battery_kwh: float = Field(ge=0)
    loops_on_original: int | None = None
    loops_on_edited: int | None = None


class RouteEditPreview(WireModel):
    contract_version: Annotated[str, Field(pattern=r"^2\.[0-9]+$")] = "2.0"
    preview_id: Id
    preview_revision: Revision
    session_id: Id
    session_epoch: Revision
    route_id: Id
    route_revision: Revision
    scene_revision: Id
    fixture_id: Id
    closure_revisions: dict[str, Revision]
    directed_edge_ids: list[str]
    node_ids: list[int]
    polyline: list[RoutePolylinePoint]
    checks: list[RouteCheck]
    validation: CheckStatus
    approvable: bool
    original: RouteMetrics | None
    edited: RouteMetrics | None
    # Entry i is the drawn shape of directed_edge_ids[i]. Empty where only its end nodes are known.
    edge_paths: list[list[RoutePolylinePoint]] = Field(default_factory=list)
    service_impact: list[RouteServiceImpact] = Field(default_factory=list)

    @model_validator(mode="after")
    def consistent_validation(self) -> "RouteEditPreview":
        if not self.checks:
            raise ValueError("route preview requires validation checks")
        statuses = {item.status for item in self.checks if item.mandatory}
        expected = "FAIL" if "FAIL" in statuses else "UNKNOWN" if "UNKNOWN" in statuses else "WARN" if "WARN" in statuses else "PASS"
        if self.validation != expected or self.approvable != (expected in {"PASS", "WARN"}):
            raise ValueError("route preview validation and approvable fields disagree with checks")
        if len(self.polyline) != len(self.node_ids):
            raise ValueError("route preview polyline and node path lengths differ")
        if self.approvable and (not self.directed_edge_ids or self.original is None or self.edited is None):
            raise ValueError("approvable preview requires path and comparison metrics")
        return self

    def edge_paths_m(self) -> list[list[Position]]:
        """Per-edge geometry for RouteRevision.edge_paths_m: edge i runs from node i to node i + 1."""
        if self.edge_paths:
            return [[Position(x_m=p.x, y_m=p.y, z_m=p.z) for p in path] for path in self.edge_paths]
        if len(self.polyline) != len(self.directed_edge_ids) + 1:
            return []
        points = [Position(x_m=p.x, y_m=p.y, z_m=p.z) for p in self.polyline]
        return [[points[i], points[i + 1]] for i in range(len(self.directed_edge_ids))]


class RouteApprovalRequest(WireModel):
    contract_version: Annotated[str, Field(pattern=r"^2\.[0-9]+$")] = "2.0"
    preview_id: Id
    preview_revision: Revision
    command_id: Id
    idempotency_key: Id
    reason: str = Field(min_length=1, max_length=512)
    target_vehicle_ids: list[str] = Field(min_length=1, max_length=16)


class RouteOutcomeStop(WireModel):
    stop_id: str
    name: str
    times_served: int = Field(ge=0)
    served_this_loop: bool


class RouteOutcomeLoop(WireModel):
    duration_s: float = Field(ge=0)
    energy_kwh: float
    all_stops_served: bool
    finished_tick: Revision


class RouteOutcome(WireModel):
    """What an approved route did once it ran: measured by the actor, never inferred by a client."""
    contract_version: Annotated[str, Field(pattern=r"^2\.[0-9]+$")] = "2.0"
    session_id: Id
    route_id: Id
    route_revision: Revision
    state: Literal["NOT_APPROVED", "APPROVED", "RUNNING", "LOOP_COMPLETED"]
    vehicle_id: Id | None = None
    reason: str | None = None
    planned: RouteMetrics | None = None
    original: RouteMetrics | None = None
    stops: list[RouteOutcomeStop] = Field(default_factory=list)
    loops: list[RouteOutcomeLoop] = Field(default_factory=list)
    service_preserved: bool | None = None
    delay_vs_original_s: float | None = None
    delay_vs_plan_s: float | None = None
    extra_energy_vs_original_kwh: float | None = None
    extra_energy_vs_plan_kwh: float | None = None
    battery_kwh: float | None = None
    loops_left_on_charge: int | None = None
    held_for_incident: bool = False
    summary: list[str] = Field(default_factory=list)
