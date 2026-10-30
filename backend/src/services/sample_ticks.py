"""
Deterministic sample simulation ticks for testing the WebSocket interface.

Author: Lau Xiang Yang
Date Created: 12/09/2026
Last Modified: 12/09/2026

Description:
    This module provides functions to generate deterministic sample simulation ticks for testing the WebSocket interface.
    It includes functions to create session ready messages, tick messages, simulation complete messages, and error messages.
    The sample ticks are based on a predefined route and include information about the ego vehicle, obstacles, and decisions
    made during the simulation. The module also includes utility functions to calculate the pose of the ego vehicle along the 
    route, generate pedestrian obstacle data, and create decision messages based on the current tick. The sample ticks are 
    designed to be used for testing and development purposes, and they provide a consistent and repeatable simulation experience.
    The module defines constants for the tick rate and sample tick count, as well as a predefined route for the ego vehicle to follow. 
    The route is defined as a list of (x, y) coordinates, and the ego vehicle's position and heading are calculated based on the 
    progress along this route. The module also includes functions to generate summary information about the route, such as the total 
    distance, estimated duration, and the number of route points. The sample ticks include information about the ego vehicle's position, 
    heading, speed, and route segment, as well as information about any obstacles that may be present in the simulation. The module also
    includes functions to generate decision messages based on the current tick, including the action taken, the reason for the action, 
    and any factors that influenced the decision. The module is designed to be used in conjunction with the WebSocket interface for 
    testing and development purposes, and it provides a consistent and repeatable simulation experience for developers and testers.

"""

from datetime import UTC, datetime
from math import atan2, degrees, hypot
from typing import Any

from src.schemas.simulation import CoordinatesMetadata

TICK_RATE_HZ = 10
SAMPLE_TICK_COUNT = 80
_ROUTE_POINTS = [
    (0.0, 0.0),
    (18.0, 0.0),
    (38.0, 8.0),
    (58.0, 24.0),
    (78.0, 32.0),
    (104.0, 32.0),
    (128.0, 46.0),
]

"""
# ---------------------------------------------------------------------------------------------
# Function: route_summary
# ---------------------------------------------------------------------------------------------
Description:
This function calculates and returns a summary of the predefined route for the ego vehicle. 
It computes the total distance of the route by summing the distances between consecutive route 
points. It also calculates the estimated duration of the simulation based on the sample tick count 
and tick rate. The function returns a dictionary containing the total distance, distance unit, 
estimated duration, duration unit, and the number of route points.

Args:
    None

Returns:
    dict[str, float | int | str]: A dictionary containing the route summary information, including
    the total distance, distance unit, estimated duration, duration unit, and the number of route points.

"""
def route_summary() -> dict[str, float | int | str]:
    distance = sum(
        hypot(end[0] - start[0], end[1] - start[1])
        for start, end in zip(_ROUTE_POINTS, _ROUTE_POINTS[1:])
    )
    return {
        "distance": round(distance, 1),
        "distance_unit": "m",
        "estimated_duration": round(SAMPLE_TICK_COUNT / TICK_RATE_HZ, 1),
        "duration_unit": "s",
        "route_point_count": len(_ROUTE_POINTS),
    }

"""
# ---------------------------------------------------------------------------------------------
Function: session_ready_message
# ---------------------------------------------------------------------------------------------
Description:
This function generates a session ready message for the WebSocket interface. It includes the simulation ID,
coordinates metadata, timestamp, status, tick rate, current tick, and simulation time. The message is
structured as a dictionary and is intended to be sent to the client when a simulation session is ready to start.

Args:
    simulation_id (str): The unique identifier for the simulation session.
    coordinates (CoordinatesMetadata): The metadata for the geographic coordinates, including version, 
    system, units for x, y, and heading, the geographic coordinate reference system (CRS), and the origin coordinates (latitude and longitude).

Returns:
    dict[str, Any]: A dictionary representing the session ready message, including the simulation ID,
    coordinates metadata, timestamp, status, tick rate, current tick, and simulation time.
"""
def session_ready_message(simulation_id: str, coordinates: CoordinatesMetadata) -> dict[str, Any]:
    return {
        "message_type": "SESSION_READY",
        "simulation_id": simulation_id,
        "coordinates": _model_dump(coordinates),
        "timestamp": _timestamp(),
        "status": "READY",
        "tick_rate": TICK_RATE_HZ,
        "tick_rate_unit": "hz",
        "current_tick": 0,
        "simulation_time": 0.0,
        "simulation_time_unit": "s",
    }

"""
# ---------------------------------------------------------------------------------------------
Function: tick_message
# ---------------------------------------------------------------------------------------------
Description:
This function generates a tick message for the WebSocket interface. It includes the simulation ID,
coordinates metadata, timestamp, current tick, simulation time, ego vehicle information, obstacles,
and decision information. The message is structured as a dictionary and is intended to be sent to the
client for each tick of the simulation.

Args:
    simulation_id (str): The unique identifier for the simulation session.
    coordinates (CoordinatesMetadata): The metadata for the geographic coordinates, including version,
    system, units for x, y, and heading, the geographic coordinate reference system (CRS), and the origin coordinates (latitude and longitude).
    tick (int): The current tick number in the simulation.

Returns:
    dict[str, Any]: A dictionary representing the tick message, including the simulation ID,
    coordinates metadata, timestamp, current tick, simulation time, ego vehicle information, obstacles,
    and decision information.

"""
def tick_message(
    simulation_id: str,
    coordinates: CoordinatesMetadata,
    tick: int,
) -> dict[str, Any]:
    progress = min(tick / (SAMPLE_TICK_COUNT - 1), 1.0)
    position, heading, route_segment = _sample_pose(progress)
    obstacle_active = tick >= 28
    speed = 7.6 if tick < 28 else 4.1 if tick < 48 else 6.4

    return {
        "message_type": "TICK",
        "simulation_id": simulation_id,
        "coordinates": _model_dump(coordinates),
        "timestamp": _timestamp(),
        "tick": tick,
        "simulation_time": round(tick / TICK_RATE_HZ, 1),
        "simulation_time_unit": "s",
        "ego": {
            "position": {"x": round(position[0], 2), "y": round(position[1], 2)},
            "heading": round(heading, 2),
            "speed": speed,
            "speed_unit": "m/s",
            "route_segment": route_segment,
            "distance_to_next_decision_point": round(max(0.0, 48.0 - tick), 1),
            "distance_unit": "m",
        },
        "obstacles": [_pedestrian_obstacle(tick)] if obstacle_active else [],
        "decision": _decision_for_tick(tick),
    }

"""
# ---------------------------------------------------------------------------------------------
Function: simulation_complete_message
# ---------------------------------------------------------------------------------------------
Description:
This function generates a simulation complete message for the WebSocket interface. It includes the simulation ID,
coordinates metadata, timestamp, status, final tick, simulation time, completion reason, and summary information
about the route, including distance traveled, distance unit, decision count, intervention count, and collision
count. The message is structured as a dictionary and is intended to be sent to the client when a simulation session
is completed.

Args:
    simulation_id (str): The unique identifier for the simulation session.
    coordinates (CoordinatesMetadata): The metadata for the geographic coordinates, including version,
    system, units for x, y, and heading, the geographic coordinate reference system (CRS), and the origin coordinates (latitude and longitude).
    final_tick (int): The final tick number in the simulation.

Returns:
    dict[str, Any]: A dictionary representing the simulation complete message, including the simulation ID,
    coordinates metadata, timestamp, status, final tick, simulation time, completion reason, and summary
    information about the route, including distance traveled, distance unit, decision count, intervention count,
    and collision count.

"""
def simulation_complete_message(
    simulation_id: str,
    coordinates: CoordinatesMetadata,
    final_tick: int,
) -> dict[str, Any]:
    return {
        "message_type": "SIMULATION_COMPLETE",
        "simulation_id": simulation_id,
        "coordinates": _model_dump(coordinates),
        "timestamp": _timestamp(),
        "status": "COMPLETED",
        "final_tick": final_tick,
        "simulation_time": round(final_tick / TICK_RATE_HZ, 1),
        "simulation_time_unit": "s",
        "completion_reason": "M0_SAMPLE_COMPLETE",
        "summary": {
            "distance_travelled": route_summary()["distance"],
            "distance_unit": "m",
            "decision_count": final_tick + 1,
            "intervention_count": 20,
            "collision_count": 0,
        },
    }

"""
# ---------------------------------------------------------------------------------------------
Function: websocket_error_message
# ---------------------------------------------------------------------------------------------
Description:
This function generates an error message for the WebSocket interface. It includes the simulation ID,
coordinates metadata, timestamp, error code, error message, and optional error details. The message is
structured as a dictionary and is intended to be sent to the client when an error occurs during the
simulation session.

Args:
    simulation_id (str): The unique identifier for the simulation session.
    coordinates (CoordinatesMetadata): The metadata for the geographic coordinates, including version,
    system, units for x, y, and heading, the geographic coordinate reference system (CRS), and the origin coordinates (latitude and longitude).
    error_code (str): A code representing the type of error that occurred.
    error_message (str): A descriptive message providing details about the error.
    error_details (dict[str, Any] | None): Optional additional details about the error, provided as a dictionary. Default is None.

Returns:
    dict[str, Any]: A dictionary representing the error message, including the simulation ID,
    coordinates metadata, timestamp, error code, error message, and optional error details.

"""
def websocket_error_message(
    simulation_id: str,
    coordinates: CoordinatesMetadata,
    error_code: str,
    error_message: str,
    error_details: dict[str, Any] | None = None,
) -> dict[str, Any]:
    return {
        "message_type": "ERROR",
        "simulation_id": simulation_id,
        "coordinates": _model_dump(coordinates),
        "timestamp": _timestamp(),
        "error": {
            "error_code": error_code,
            "error_message": error_message,
            "error_details": error_details,
        },
    }

"""
# ---------------------------------------------------------------------------------------------
Function: _sample_pose
# ---------------------------------------------------------------------------------------------
Description:
This function calculates the position and heading of the ego vehicle along the predefined route 
based on the progress through the route. It takes a progress value between 0.0 and 1.0, where 0.0 
represents the start of the route and 1.0 represents the end of the route. The function returns a 
tuple containing the (x, y) position, heading in degrees, and the index of the current route segment.

Args:
    progress (float): A value between 0.0 and 1.0 representing the progress through the route.

Returns:
    tuple[tuple[float, float], float, int]: A tuple containing the (x, y) position of the ego vehicle, the heading in degrees, and the index of the current route segment.

"""
def _sample_pose(progress: float) -> tuple[tuple[float, float], float, int]:
    scaled = progress * (len(_ROUTE_POINTS) - 1)
    route_segment = min(int(scaled), len(_ROUTE_POINTS) - 2)
    segment_progress = scaled - route_segment
    start = _ROUTE_POINTS[route_segment]
    end = _ROUTE_POINTS[route_segment + 1]
    x = start[0] + (end[0] - start[0]) * segment_progress
    y = start[1] + (end[1] - start[1]) * segment_progress
    heading = degrees(atan2(end[1] - start[1], end[0] - start[0]))
    return (x, y), heading, route_segment

"""
# ---------------------------------------------------------------------------------------------
Function: _pedestrian_obstacle
# ---------------------------------------------------------------------------------------------
Description:
This function generates a dictionary representing a pedestrian obstacle in the simulation based on the current tick.
It calculates the position of the pedestrian based on the tick number and provides information about the obstacle's
type, position, velocity, prediction, trigger, and conflict metrics. The function returns a dictionary 
containing the obstacle information, which can be included in the tick message sent to the client.

Args:
    tick (int): The current tick number in the simulation.

Returns:
    dict[str, Any]: A dictionary representing the pedestrian obstacle, including its ID, type,
    position, velocity, prediction, trigger, and conflict metrics.

"""
def _pedestrian_obstacle(tick: int) -> dict[str, Any]:
    simulation_time = round(tick / TICK_RATE_HZ, 1)
    y = 27.0 + min(max(tick - 28, 0), 24) * 0.45
    return {
        "obstacle_id": "obstacle_pedestrian_01",
        "obstacle_type": "pedestrian",
        "position": {"x": 72.0, "y": round(y, 2)},
        "velocity": {"x": 0.0, "y": 1.2},
        "velocity_unit": "m/s",
        "prediction": [
            {
                "simulation_time": round(simulation_time + 1.0, 1),
                "simulation_time_unit": "s",
                "position": {"x": 72.0, "y": round(y + 1.2, 2)},
            }
        ],
        "trigger": {
            "trigger_type": "simulation_time",
            "simulation_time": 2.8,
            "simulation_time_unit": "s",
        },
        "conflict_metrics": {
            "distance": round(max(0.0, 18.0 - (tick - 28) * 0.8), 1),
            "distance_unit": "m",
            "time_to_conflict": round(max(0.0, 2.6 - (tick - 28) * 0.1), 1),
            "time_unit": "s",
            "lane_relationship": "crossing_route",
        },
    }

"""
# ---------------------------------------------------------------------------------------------
Function: _decision_for_tick
# ---------------------------------------------------------------------------------------------
Description:
This function generates a decision message based on the current tick number in the simulation. It determines
the appropriate action, reason code, summary, factors, eligible actions, rejected actions, conflict status, 
and model scores based on the tick value. The function returns a dictionary containing the decision information, 
which can be included in the tick message sent to the client.

Args:
    tick (int): The current tick number in the simulation.

Returns:
    dict[str, Any]: A dictionary representing the decision information, including the action taken,
    reason code, summary, factors, eligible actions, rejected actions, conflict status, and model scores.

"""
def _decision_for_tick(tick: int) -> dict[str, Any]:
    if 28 <= tick < 48:
        return {
            "action": "SLOW",
            "reason_code": "PEDESTRIAN_TTC_LOW",
            "summary": "Slowing because a pedestrian is predicted to enter the route.",
            "factors": [{"name": "time_to_conflict", "value": 1.8, "unit": "s"}],
            "eligible_actions": ["STOP", "SLOW"],
            "rejected_actions": [
                {"action": "MAINTAIN", "reason": "TTC_BELOW_THRESHOLD"},
                {"action": "SWERVE_LEFT", "reason": "LEFT_SPACE_OCCUPIED"},
            ],
            "conflict": False,
            "model_scores": None,
        }

    return {
        "action": "MAINTAIN",
        "reason_code": "CLEAR_ROUTE",
        "summary": "Maintaining speed because no active obstacle conflicts with the route.",
        "factors": [],
        "eligible_actions": ["MAINTAIN", "SLOW", "STOP"],
        "rejected_actions": [],
        "conflict": False,
        "model_scores": None,
    }

"""
# ---------------------------------------------------------------------------------------------
Function: _timestamp
# ---------------------------------------------------------------------------------------------
Description:
This function generates a timestamp in ISO 8601 format with UTC timezone information. It returns the
current date and time in the format "YYYY-MM-DDTHH:MM:SS.sssZ", where "Z" indicates that the time is in UTC. 
The function is used to provide consistent timestamps for messages sent over the WebSocket interface.

"""
def _timestamp() -> str:
    return datetime.now(UTC).isoformat().replace("+00:00", "Z")

"""
# ---------------------------------------------------------------------------------------------
Function: _model_dump
# ---------------------------------------------------------------------------------------------
Description:
This function serializes a Pydantic model into a dictionary. It checks if the model has a "model_dump" 
method (available in Pydantic v2) and uses it if available; otherwise, it falls back to using the "dict"
method (available in Pydantic v1). The function returns a dictionary representation of the model, which 
can be used for JSON serialization or other purposes. It is designed to provide compatibility with both 
Pydantic v1 and v2.

Args:
    model (BaseModel): A Pydantic model instance to be serialized.

Returns:
    dict[str, Any]: A dictionary representation of the Pydantic model.
    
"""
def _model_dump(model: CoordinatesMetadata) -> dict[str, Any]:
    dump_model = getattr(model, "model_dump", model.dict)
    return dump_model()
