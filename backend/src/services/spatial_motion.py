"""Bounded CPU geometry for the M2 road-constrained controller.

Operational boxes are conservatively enclosed in world AABBs. Sweeps include
every path segment and a full rotation envelope at authored corners. They may
hold earlier than an oriented-box solver, but never accept an unchecked leap.
"""

from __future__ import annotations

from bisect import bisect_right
from dataclasses import dataclass
import math

from contracts.v2.spatial import RoadSurface, rotate

Vec = tuple[float, float, float]


def add(a, b):
    return tuple(x + y for x, y in zip(a, b))


def scale(a, value):
    return tuple(x * value for x in a)


def cross(a, b):
    return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])


def frame_quaternion(forward, normal):
    """Columns are body forward, left, up; return canonical unit XYZW."""
    left = cross(normal, forward)
    m = [[forward[i], left[i], normal[i]] for i in range(3)]
    trace = sum(m[i][i] for i in range(3))
    if trace > 0:
        s = math.sqrt(trace + 1) * 2
        q = [(m[2][1]-m[1][2])/s, (m[0][2]-m[2][0])/s, (m[1][0]-m[0][1])/s, s/4]
    else:
        i = max(range(3), key=lambda n: m[n][n])
        j, k = (i+1) % 3, (i+2) % 3
        s = math.sqrt(1 + m[i][i] - m[j][j] - m[k][k]) * 2
        q = [0., 0., 0., (m[k][j]-m[j][k])/s]
        q[i], q[j], q[k] = s/4, (m[j][i]+m[i][j])/s, (m[k][i]+m[i][k])/s
    norm = math.sqrt(sum(x*x for x in q))
    return tuple(x/norm for x in q)


class SpatialUnknown(ValueError):
    """A required spatial check could not finish within its declared bound."""


@dataclass
class WorkBudget:
    limit: int
    used: int = 0

    def spend(self):
        if self.used >= self.limit:
            raise SpatialUnknown("SPATIAL_WORK_LIMIT")
        self.used += 1


@dataclass(frozen=True)
class AABB:
    low: Vec
    high: Vec

    def overlaps(self, other):
        return all(a <= d and c <= b for a, b, c, d in zip(self.low, self.high, other.low, other.high))

    def union(self, other):
        return AABB(tuple(map(min, self.low, other.low)), tuple(map(max, self.high, other.high)))


def body_bounds(position, orientation, dimensions, margin=0.):
    axes = [rotate(orientation, v) for v in ([1.,0.,0.], [0.,1.,0.], [0.,0.,1.])]
    extents = [sum(abs(axes[j][i])*dimensions[j]/2 for j in range(3)) + margin for i in range(3)]
    return AABB(tuple(x-e for x, e in zip(position, extents)), tuple(x+e for x, e in zip(position, extents)))


@dataclass(frozen=True)
class _Node:
    bounds: AABB
    key: str | None = None
    left: _Node | None = None
    right: _Node | None = None


class SpatialIndex:
    """Immutable median-split BVH; deterministic traversal and bounded queries."""

    MAX_ITEMS = 256

    def __init__(self, items: list[tuple[str, AABB]]):
        if len(items) > self.MAX_ITEMS or len({k for k, _ in items}) != len(items):
            raise ValueError("SPATIAL_INDEX_LIMIT_OR_DUPLICATE")
        self.root = self._build(sorted(items))

    @classmethod
    def _build(cls, items):
        if not items:
            return None
        bounds = items[0][1]
        for _, box in items[1:]:
            bounds = bounds.union(box)
        if len(items) == 1:
            return _Node(bounds, items[0][0])
        axis = max(range(3), key=lambda i: bounds.high[i]-bounds.low[i])
        items.sort(key=lambda item: (item[1].low[axis]+item[1].high[axis], item[0]))
        mid = len(items)//2
        return _Node(bounds, left=cls._build(items[:mid]), right=cls._build(items[mid:]))

    def query(self, box: AABB, budget: WorkBudget):
        stack, found = ([self.root] if self.root else []), []
        while stack:
            budget.spend()
            node = stack.pop()
            if not box.overlaps(node.bounds):
                continue
            if node.key is not None:
                found.append(node.key)
            else:
                stack.extend((node.right, node.left))
        return sorted(found)


@dataclass(frozen=True)
class PathSegment:
    start: Vec
    end: Vec
    begin_m: float
    length_m: float
    normal: Vec
    orientation: tuple[float, float, float, float]
    surface_id: str
    lane_id: str
    deck_id: str
    width_m: float
    clearance_m: float

    def support(self, progress):
        return add(self.start, scale(tuple(b-a for a, b in zip(self.start, self.end)),
                                     (progress-self.begin_m)/self.length_m))


class RoadPath:
    MAX_SEGMENTS = 64
    MAX_SWEEP_PIECES = 32

    def __init__(self, surfaces: list[RoadSurface]):
        if not surfaces or len(surfaces) > 64:
            raise ValueError("INVALID_ROAD_PATH")
        segments, progress = [], 0.
        for i, surface in enumerate(surfaces):
            if i and (surface.surface_id not in surfaces[i-1].permitted_next_surface_ids
                      or math.dist(surfaces[i-1].path_samples_m[-1], surface.path_samples_m[0]) > 1e-6):
                raise ValueError("INVALID_SURFACE_TRANSITION")
            for a, b in zip(surface.path_samples_m, surface.path_samples_m[1:]):
                if len(segments) >= self.MAX_SEGMENTS:
                    raise ValueError("PATH_SEGMENT_LIMIT")
                length = math.dist(a, b)
                if (not math.isfinite(length) or length < 1e-6
                        or any(not math.isfinite(v) or abs(v) > 1_000_000 for v in (*a, *b))):
                    raise ValueError("INVALID_LOCAL_PATH_GEOMETRY")
                forward = tuple((y-x)/length for x, y in zip(a, b))
                segments.append(PathSegment(tuple(a), tuple(b), progress, length, tuple(surface.normal),
                                            frame_quaternion(forward, surface.normal), surface.surface_id,
                                            surface.lane_id, surface.deck_id, surface.width_m, surface.clearance_m))
                progress += length
        self.segments = tuple(segments)
        self.ends = tuple(s.begin_m+s.length_m for s in segments)
        self.length_m = progress

    def segment(self, progress):
        if not math.isfinite(progress) or not 0 <= progress <= self.length_m:
            raise SpatialUnknown("INVALID_ROUTE_PROGRESS")
        return self.segments[min(bisect_right(self.ends, progress), len(self.segments)-1)]

    def pose(self, progress, offset):
        segment = self.segment(progress)
        return add(segment.support(progress), scale(segment.normal, offset)), segment.orientation, segment

    def project(self, support):
        for segment in self.segments:
            delta = tuple(b-a for a, b in zip(segment.start, segment.end))
            t = sum((p-a)*d for p, a, d in zip(support, segment.start, delta))/segment.length_m**2
            if -1e-6 <= t <= 1+1e-6:
                progress = segment.begin_m + max(0., min(1., t))*segment.length_m
                if math.dist(segment.support(progress), support) <= 1e-5:
                    return progress
        raise ValueError("SHUTTLE_NOT_ON_AUTHORED_PATH")

    def sweep(self, start, end, dimensions, offset, margin, budget):
        self.segment(start)
        self.segment(end)
        if end < start:
            raise SpatialUnknown("REVERSE_NOT_AUTHORED")
        boxes = []
        for segment in self.segments:
            lo, hi = max(start, segment.begin_m), min(end, segment.begin_m+segment.length_m)
            if lo > hi:
                continue
            if len(boxes) >= self.MAX_SWEEP_PIECES:
                raise SpatialUnknown("SWEEP_PIECE_LIMIT")
            budget.spend()
            positions = [add(segment.support(p), scale(segment.normal, offset)) for p in (lo, hi)]
            box = body_bounds(positions[0], segment.orientation, dimensions, margin).union(
                body_bounds(positions[1], segment.orientation, dimensions, margin))
            # Enclose all intermediate orientations and support-offset movement
            # at a sharp join, including a step that ends exactly on the join.
            if segment.begin_m > 0 and start <= segment.begin_m <= end:
                radius = offset + math.sqrt(sum((d/2)**2 for d in dimensions)) + margin
                corner = AABB(tuple(x-radius for x in segment.start), tuple(x+radius for x in segment.start))
                box = box.union(corner)
            boxes.append(box)
        return tuple(boxes)


def envelope(boxes):
    result = boxes[0]
    for box in boxes[1:]:
        result = result.union(box)
    return result
