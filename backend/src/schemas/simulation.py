"""
Pydantic schemas for the simulation REST lifecycle.

Author: Lau Xiang Yang, CC
Date Created: 09/09/2026
Last Modified: 20/09/2026

Description:
    This module defines Pydantic schemas for the simulation REST API lifecycle. It includes request and 
    response models for creating and retrieving simulation sessions, as well as metadata related to the 
    simulation trajectory and route.
    
"""

from datetime import datetime
from typing import Literal

from pydantic import BaseModel, Field, model_validator


class CreateSimulationRequest(BaseModel):
    """Configuration for a demo, dataset replay, or accepted planned route."""

    scenario_name: str = Field(default="default", min_length=1, max_length=100)
    dataset_name: str | None = Field(default=None, min_length=1, max_length=50)
    sequence_id: str | None = Field(default=None, min_length=1, max_length=100)
    route_id: str | None = Field(default=None, min_length=1, max_length=100)
    playback_speed: float = Field(default=1.0, gt=0.0, le=16.0)
    scene_id: str | None = Field(default=None, min_length=1, max_length=100)
    ego_source: Literal["KITTI_OXTS", "SIMULATION"] | None = None
    alignment_mode: Literal[
        "GEOGRAPHIC", "SIMULATED_SCENE", "SOURCE_LOCAL"
    ] | None = None

    @model_validator(mode="after")
    def validate_dataset_selection(self) -> "CreateSimulationRequest":
        if self.dataset_name is not None and self.dataset_name.lower() != "kitti":
            raise ValueError("dataset_name must be 'kitti' when supplied")
        if self.dataset_name is not None and self.sequence_id is None:
            raise ValueError("sequence_id is required when dataset_name is supplied")
        if self.dataset_name is not None and self.route_id is not None:
            raise ValueError("dataset_name and route_id cannot be supplied together")
        if self.route_id is not None and self.scene_id is None:
            raise ValueError("scene_id is required when route_id is supplied")

        uses_kitti = self.dataset_name is not None
        expected_source = "KITTI_OXTS" if uses_kitti else "SIMULATION"
        expected_alignment = (
            "GEOGRAPHIC"
            if uses_kitti and self.scene_id is not None
            else "SOURCE_LOCAL"
            if uses_kitti
            else "SIMULATED_SCENE"
            if self.scene_id is not None
            else "SOURCE_LOCAL"
        )
        if self.ego_source is not None and self.ego_source != expected_source:
            raise ValueError(
                f"ego_source must be '{expected_source}' for the selected data source"
            )
        if self.alignment_mode is not None and self.alignment_mode != expected_alignment:
            raise ValueError(
                f"alignment_mode must be '{expected_alignment}' for the selected data and scene"
            )
        # Older clients may omit these fields. Store the inferred values so every
        # created run still has explicit provenance in its response and metadata.
        self.ego_source = expected_source
        self.alignment_mode = expected_alignment
        return self


class GeographicOrigin(BaseModel):
    latitude: float
    longitude: float


class CoordinatesMetadata(BaseModel):
    """Renderer-independent local Cartesian coordinate convention."""

    version: str = "1.0"
    system: Literal["local_cartesian"] = "local_cartesian"
    x_unit: Literal["m"] = "m"
    y_unit: Literal["m"] = "m"
    z_unit: Literal["m"] = "m"
    heading_unit: Literal["deg"] = "deg"
    geographic_crs: Literal["EPSG:4326"] = "EPSG:4326"
    x_axis: Literal["east"] = "east"
    y_axis: Literal["north"] = "north"
    z_axis: Literal["up"] = "up"
    heading_zero_axis: Literal["positive_x"] = "positive_x"
    heading_positive_direction: Literal["counter_clockwise"] = "counter_clockwise"
    origin: GeographicOrigin = Field(
        default_factory=lambda: GeographicOrigin(latitude=1.2998, longitude=103.7874)
    )


class RouteBounds(BaseModel):
    """Planar bounds of the complete normalized trajectory in metres."""

    min_x: float
    min_y: float
    max_x: float
    max_y: float


class RouteSummary(BaseModel):
    distance: float
    distance_unit: Literal["m"] = "m"
    estimated_duration: float
    duration_unit: Literal["s"] = "s"
    route_point_count: int
    bounds: RouteBounds


class MapMatchingSummary(BaseModel):
    algorithm: Literal["nearest_road_segment_v1"]
    sample_count: int
    matched_sample_count: int
    road_sequence: list[int]
    coverage_ratio: float = Field(ge=0.0, le=1.0)
    mean_cross_track_error: float | None = Field(default=None, ge=0.0)
    p95_cross_track_error: float | None = Field(default=None, ge=0.0)
    max_cross_track_error: float | None = Field(default=None, ge=0.0)
    maximum_match_distance: float = Field(gt=0.0)
    acceptance_min_coverage_ratio: float = Field(ge=0.0, le=1.0)
    acceptance_max_mean_cross_track_error: float = Field(gt=0.0)
    acceptance_max_p95_cross_track_error: float = Field(gt=0.0)
    acceptance_passed: bool
    distance_unit: Literal["m"] = "m"


class TrajectoryMetadata(BaseModel):
    sample_count: int
    duration: float
    duration_unit: Literal["s"] = "s"
    dataset_name: str | None = None
    sequence_id: str | None = None
    route_id: str | None = None
    source_coordinate_system: str | None = None
    source_start_timestamp: str | None = None
    source_end_timestamp: str | None = None
    ego_source: Literal["KITTI_OXTS", "SIMULATION"]
    alignment_mode: Literal["GEOGRAPHIC", "SIMULATED_SCENE", "SOURCE_LOCAL"]
    scene_id: str | None = None
    scene_revision: str | None = None
    transform_revision: str
    map_matching: MapMatchingSummary | None = None


class SimulationResponse(BaseModel):
    simulation_id: str
    scenario_name: str
    status: Literal["READY", "RUNNING", "PAUSED", "COMPLETED", "STOPPED"]
    created_at: datetime
    websocket_url: str
    dataset_name: str | None = None
    sequence_id: str | None = None
    route_id: str | None = None
    playback_speed: float = 1.0
    scene_id: str | None = None
    scene_revision: str | None = None
    transform_revision: str
    ego_source: Literal["KITTI_OXTS", "SIMULATION"]
    alignment_mode: Literal["GEOGRAPHIC", "SIMULATED_SCENE", "SOURCE_LOCAL"]
    coordinates: CoordinatesMetadata = Field(default_factory=CoordinatesMetadata)
    route_summary: RouteSummary
    trajectory: TrajectoryMetadata
    contract_version: str = "1.0"
