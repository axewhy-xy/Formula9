"""The live session stream: WS /api/v2/sessions/{session_id}/stream (contract 2.x, additive).

The backend pushes a session's committed snapshots and its traffic frames over one WebSocket instead of
being polled for them. Every message is one JSON text frame with a "type". Server to client:

- ``snapshot``: the session's newest committed FULL_SNAPSHOT, exactly as GET /snapshot gives it. The
  first message of every stream, then each newer one, at most one per ``snapshot_interval_ms``. A
  stream coalesces: it skips snapshots, never sends one older than one already sent, and never sends
  events. A client reads durable events from GET /events, as after a polled snapshot.
- ``traffic``: traffic frames after the last sent, oldest first, as GET /traffic's ``frames`` (at most
  a second's worth). Only while the client wants traffic and the session has some.
- ``heartbeat``: sent after HEARTBEAT_S with nothing else, so a quiet session is not a lost connection.
- ``error``: why the stream ends (``INVALID_REFERENCE`` deleted or unknown, ``ROLE_DENIED`` no login,
  ``SESSION_RECOVERING`` try again later), or that a client message was refused (``INVALID_PAYLOAD``,
  and the stream goes on). The server then closes with 4404, 4403, 1013 or 1011.

Client to server: ``traffic`` turns frames on or off, and may move the cursor (``after_tick``).

A client treats the stream as an accelerator: on any close it goes back to polling over HTTP, which
reports the session's state as before, and reconnects later.
"""

from typing import Annotated, Any, Literal, Union

from pydantic import BaseModel, ConfigDict, Field, FiniteFloat

from .models import FullSnapshot, Id

StreamErrorCode = Literal["INVALID_REFERENCE", "ROLE_DENIED", "SESSION_RECOVERING", "INVALID_PAYLOAD",
                          "STALE_REVISION", "QUEUE_FULL", "INTERNAL_ERROR"]


class StreamModel(BaseModel):
    model_config = ConfigDict(extra="allow", strict=True)


class StreamTrafficShuttle(StreamModel):
    """A shuttle's body in a traffic step. held_by (2.x, optional) says what the background traffic holds it
    up for when it does: a vehicle ("traffic") or the walkers on or waiting at a crossing on its way
    ("pedestrians"); a shuttle nothing in the traffic holds up has none."""
    vehicle_id: Id
    x_m: FiniteFloat
    y_m: FiniteFloat
    heading_deg: FiniteFloat
    speed_mps: FiniteFloat
    held_by: Literal["traffic", "pedestrians"] | None = None


class StreamSignalFault(StreamModel):
    junction_id: str
    mode: str
    until_s: FiniteFloat


class StreamTrafficFrame(StreamModel):
    """One traffic step. vehicles is base64 of vehicle_count records of 14 bytes, little-endian: uint16
    index in /api/traffic/{area}'s vehicles, int32 x cm, int32 y cm, int16 heading in tenths of a degree
    anticlockwise from east, uint16 speed cm/s. pedestrians (2.x, optional) is base64 of pedestrian_count
    records of 14 bytes, little-endian: uint16 index in /api/road-features/{area}/walkways's seeds, int32 x
    cm, int32 y cm, int16 heading in tenths of a degree anticlockwise from east, uint8 speed in units of
    2 cm/s, uint8 flags (bit 0 waiting at a kerb, bit 1 on a crossing's road part, bit 2 on a kerb ramp,
    bit 3 standing a while); a frame from a backend that walks none has neither. Presentation only; the
    snapshot is the authority."""
    tick: Annotated[int, Field(ge=0)]
    simulation_time_s: FiniteFloat
    vehicle_count: Annotated[int, Field(ge=0)]
    vehicles: str
    shuttles: list[StreamTrafficShuttle]
    signal_faults: list[StreamSignalFault] = Field(default_factory=list)
    pedestrian_count: Annotated[int, Field(ge=0)] = 0
    pedestrians: str = ""


class StreamSnapshot(StreamModel):
    type: Literal["snapshot"]
    session_id: Id
    snapshot: FullSnapshot


class StreamTraffic(StreamModel):
    type: Literal["traffic"]
    session_id: Id
    frames: Annotated[list[StreamTrafficFrame], Field(min_length=1, max_length=10)]


class StreamHeartbeat(StreamModel):
    type: Literal["heartbeat"]
    session_id: Id


class StreamErrorBody(StreamModel):
    code: StreamErrorCode
    message: Annotated[str, Field(max_length=512)]
    retryable: bool


class StreamError(StreamModel):
    type: Literal["error"]
    session_id: str
    error: StreamErrorBody


StreamMessage = Annotated[Union[StreamSnapshot, StreamTraffic, StreamHeartbeat, StreamError],
                          Field(discriminator="type")]


class StreamTrafficControl(BaseModel):
    """Client to server: whether to send traffic frames, and optionally the tick to send them after (-1
    for the newest kept); without after_tick the stream goes on from the last frame it sent."""
    model_config = ConfigDict(extra="forbid", strict=True)

    type: Literal["traffic"]
    enabled: bool
    after_tick: Annotated[int, Field(ge=-1)] | None = None


def stream_message_schema() -> dict[str, Any]:
    from pydantic import TypeAdapter
    return TypeAdapter(StreamMessage).json_schema()
