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


class RouteEditorContext(WireModel):
    """Bounded, read-only curated graph and estimate assumptions for a route editor."""
    contract_version: Annotated[str, Field(pattern=r"^2\.[0-9]+$")] = "2.0"
    session_id: Id
    session_epoch: Revision
    route_id: Id
    route_revision: Revision
    scene_revision: Id
    fixture_id: Id
    nodes: list[RouteEditorNode] = Field(max_length=256)
    edges: list[RouteEditorEdge] = Field(max_length=256)
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
    directed_edge_ids: list[str] | None = Field(default=None, min_length=1, max_length=128)

    @model_validator(mode="after")
    def one_edit_form(self) -> "RouteEditRequest":
        if self.directed_edge_ids is not None and self.via_node_ids:
            raise ValueError("Use either via nodes or explicit directed segments")
        return self


class RouteCheck(WireModel):
    check: Literal["GEOMETRY", "DIRECTION", "CLOSURE", "STOPS", "OPERATING_AREA", "VEHICLE_FIT", "TURNS"]
    status: CheckStatus
    message: str
    feature_id: str | None = None
    mandatory: bool = True


class RouteMetrics(WireModel):
    distance_m: float = Field(ge=0)
    duration_s: float = Field(ge=0)
    stop_coverage: dict[str, bool]
    energy_kwh: float = Field(ge=0)


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
