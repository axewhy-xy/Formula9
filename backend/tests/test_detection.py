"""
Unit tests for perception/detection.py.

Author: Choo Jian Wei
Date: 09/10/2026
Last Modified: 09/10/2026

Description:
    Tests the model-independent detection helpers: COCO class mapping, box
    overlap (IoU), duplicate merging and building cleaned Detections from
    raw model output. detection.py imports no torch, so these tests run in
    CI without the optional ML dependencies. Several boxes are real
    detections from KITTI drive 0001, frame 0.

"""

import numpy as np
import pytest

from backend.src.perception.detection import (
    Detection,
    box_iou,
    build_detections,
    merge_duplicates,
    to_obstacle_type,
)

# Real boxes from drive 0001 frame 0: one SUV reported as both "car" and
# "truck", and two neighbouring parked cars that slightly overlap.
SUV_AS_CAR = (313.0, 169.0, 409.0, 236.0)
SUV_AS_TRUCK = (314.0, 168.0, 409.0, 235.0)
PARKED_CAR_A = (504.0, 176.0, 533.0, 202.0)
PARKED_CAR_B = (532.0, 181.0, 549.0, 199.0)


def _detection(obstacle_type, score, box, coco_class="car"):
    return Detection(
        obstacle_type=obstacle_type, coco_class=coco_class, score=score, box=box
    )


"""
# -----------------------------------------------------------------------------------
# Function: test_to_obstacle_type_maps_relevant_classes
# -----------------------------------------------------------------------------------
Description:
    Every kept COCO class maps to its lowercase simulator type, which is
    what the frontend renderer checks.

"""
@pytest.mark.parametrize(
    ("coco_class", "expected"),
    [
        ("person", "pedestrian"),
        ("bicycle", "cyclist"),
        ("motorcycle", "cyclist"),
        ("car", "vehicle"),
        ("bus", "vehicle"),
        ("truck", "vehicle"),
        ("traffic light", "traffic_light"),
        ("stop sign", "stop_sign"),
    ],
)
def test_to_obstacle_type_maps_relevant_classes(coco_class, expected):
    assert to_obstacle_type(coco_class) == expected


"""
# -----------------------------------------------------------------------------------
# Function: test_to_obstacle_type_ignores_other_classes
# -----------------------------------------------------------------------------------
Description:
    Classes the simulator does not use return None instead of a guess or
    a KeyError. Names are case-sensitive, matching the model's output.

"""
@pytest.mark.parametrize("coco_class", ["dog", "bench", "potted plant", "Car", ""])
def test_to_obstacle_type_ignores_other_classes(coco_class):
    assert to_obstacle_type(coco_class) is None


"""
# -----------------------------------------------------------------------------------
# Function: test_box_iou_known_values
# -----------------------------------------------------------------------------------
Description:
    Checks identical, half-overlapping, separate and edge-touching boxes
    against hand-computed IoU values.

"""
@pytest.mark.parametrize(
    ("first", "second", "expected"),
    [
        ((0, 0, 10, 10), (0, 0, 10, 10), 1.0),           # identical
        ((0, 0, 10, 10), (5, 0, 15, 10), 50 / 150),      # overlap 50, union 150
        ((0, 0, 10, 10), (20, 20, 30, 30), 0.0),         # far apart
        ((0, 0, 10, 10), (10, 0, 20, 10), 0.0),          # touching edges only
        ((0, 0, 10, 10), (2, 2, 8, 8), 36 / 100),        # one inside the other
    ],
)
def test_box_iou_known_values(first, second, expected):
    assert box_iou(first, second) == pytest.approx(expected)


"""
# -----------------------------------------------------------------------------------
# Function: test_box_iou_is_symmetric
# -----------------------------------------------------------------------------------
Description:
    The overlap of A with B is the same as B with A.

"""
def test_box_iou_is_symmetric():
    assert box_iou(SUV_AS_CAR, SUV_AS_TRUCK) == pytest.approx(
        box_iou(SUV_AS_TRUCK, SUV_AS_CAR)
    )


"""
# -----------------------------------------------------------------------------------
# Function: test_box_iou_real_boxes
# -----------------------------------------------------------------------------------
Description:
    On real frame-0 boxes, the SUV's car/truck boxes overlap almost fully
    while two neighbouring parked cars barely overlap, so a 0.7 threshold
    separates duplicates from neighbours.

"""
def test_box_iou_real_boxes():
    assert box_iou(SUV_AS_CAR, SUV_AS_TRUCK) > 0.9
    assert box_iou(PARKED_CAR_A, PARKED_CAR_B) < 0.1


"""
# -----------------------------------------------------------------------------------
# Function: test_merge_duplicates_keeps_highest_score
# -----------------------------------------------------------------------------------
Description:
    Reproduces the frame-0 SUV reported as both "car" (0.74) and "truck"
    (0.60): only the higher-scoring one survives, whatever the input order.

"""
def test_merge_duplicates_keeps_highest_score():
    truck = _detection("vehicle", 0.60, SUV_AS_TRUCK, "truck")
    car = _detection("vehicle", 0.74, SUV_AS_CAR, "car")

    assert merge_duplicates([truck, car]) == [car]
    assert merge_duplicates([car, truck]) == [car]


"""
# -----------------------------------------------------------------------------------
# Function: test_merge_duplicates_keeps_different_types
# -----------------------------------------------------------------------------------
Description:
    A pedestrian in front of a car has the same box but is a separate
    obstacle, so both are kept.

"""
def test_merge_duplicates_keeps_different_types():
    car = _detection("vehicle", 0.9, SUV_AS_CAR)
    person = _detection("pedestrian", 0.8, SUV_AS_CAR, "person")

    assert merge_duplicates([car, person]) == [car, person]


"""
# -----------------------------------------------------------------------------------
# Function: test_merge_duplicates_keeps_neighbouring_cars
# -----------------------------------------------------------------------------------
Description:
    Parked cars in a row overlap slightly but are different vehicles.

"""
def test_merge_duplicates_keeps_neighbouring_cars():
    first = _detection("vehicle", 0.99, PARKED_CAR_A)
    second = _detection("vehicle", 0.96, PARKED_CAR_B)

    assert merge_duplicates([first, second]) == [first, second]


"""
# -----------------------------------------------------------------------------------
# Function: test_merge_duplicates_sorts_by_score
# -----------------------------------------------------------------------------------
Description:
    The result is ordered highest score first, regardless of input order.

"""
def test_merge_duplicates_sorts_by_score():
    low = _detection("vehicle", 0.6, (0, 0, 10, 10))
    high = _detection("pedestrian", 0.9, (100, 0, 110, 10), "person")
    middle = _detection("cyclist", 0.75, (200, 0, 210, 10), "bicycle")

    assert [item.score for item in merge_duplicates([low, high, middle])] == [0.9, 0.75, 0.6]


"""
# -----------------------------------------------------------------------------------
# Function: test_merge_duplicates_threshold_is_inclusive
# -----------------------------------------------------------------------------------
Description:
    Two same-type boxes whose IoU equals the threshold exactly count as one
    object; just below the threshold they are kept apart.

"""
def test_merge_duplicates_threshold_is_inclusive():
    # Overlap 50, union 100: IoU is exactly 0.5.
    first = _detection("vehicle", 0.9, (0, 0, 10, 10))
    second = _detection("vehicle", 0.8, (0, 0, 10, 5))

    assert merge_duplicates([first, second], iou_threshold=0.5) == [first]
    assert merge_duplicates([first, second], iou_threshold=0.51) == [first, second]


"""
# -----------------------------------------------------------------------------------
# Function: test_merge_duplicates_empty
# -----------------------------------------------------------------------------------
Description:
    An image with no detections gives an empty list, not an error.

"""
def test_merge_duplicates_empty():
    assert merge_duplicates([]) == []


"""
# -----------------------------------------------------------------------------------
# Function: test_build_detections_filters_and_merges
# -----------------------------------------------------------------------------------
Description:
    Ignored classes and low scores are dropped, the SUV duplicate is
    merged, and the result is sorted by score.

"""
def test_build_detections_filters_and_merges():
    raw = [
        ("car", 0.74, SUV_AS_CAR),
        ("truck", 0.60, SUV_AS_TRUCK),
        ("dog", 0.99, (0, 0, 10, 10)),
        ("person", 0.30, (700, 160, 710, 190)),
        ("traffic light", 0.79, (660, 105, 665, 113)),
    ]

    detections = build_detections(raw, min_score=0.5)

    assert [(item.obstacle_type, item.coco_class) for item in detections] == [
        ("traffic_light", "traffic light"),
        ("vehicle", "car"),
    ]
    assert detections[1].box == SUV_AS_CAR


"""
# -----------------------------------------------------------------------------------
# Function: test_build_detections_min_score_is_inclusive
# -----------------------------------------------------------------------------------
Description:
    A score exactly equal to min_score is kept; just below it is dropped.

"""
def test_build_detections_min_score_is_inclusive():
    assert len(build_detections([("car", 0.5, (0, 0, 10, 10))], min_score=0.5)) == 1
    assert build_detections([("car", 0.49, (0, 0, 10, 10))], min_score=0.5) == []


"""
# -----------------------------------------------------------------------------------
# Function: test_build_detections_converts_model_numbers
# -----------------------------------------------------------------------------------
Description:
    Models return numpy/torch numbers; Detections hold plain Python floats
    so they print cleanly and can be saved as JSON.

"""
def test_build_detections_converts_model_numbers():
    raw = [("car", np.float32(0.9), np.array([1, 2, 30, 40], dtype=np.float32))]

    detection = build_detections(raw)[0]

    assert type(detection.score) is float
    assert detection.box == (1.0, 2.0, 30.0, 40.0)
    assert all(type(value) is float for value in detection.box)


"""
# -----------------------------------------------------------------------------------
# Function: test_build_detections_empty
# -----------------------------------------------------------------------------------
Description:
    No model output gives no detections.

"""
def test_build_detections_empty():
    assert build_detections([]) == []
