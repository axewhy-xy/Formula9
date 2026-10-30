"""
Model-independent object detection types and cleanup.

Author: Choo Jian Wei
Date Created: 09/10/2026
Last Modified: 09/10/2026

Description:
    Defines Detection (one object found in an image) and the Detector
    interface every model must provide, plus helpers that map COCO classes
    to simulator obstacle types and merge duplicate boxes.

    No torch is imported here, so this module and its tests run without
    the optional ML dependencies. Model code lives in perception/detectors/.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Iterable, Protocol


# COCO class name -> simulator obstacle_type. Lower case to match what the
# frontend renderer checks (e.g. obstacle_type === 'pedestrian').
OBSTACLE_TYPES = {
    "person": "pedestrian",
    "bicycle": "cyclist",
    "motorcycle": "cyclist",
    "car": "vehicle",
    "bus": "vehicle",
    "truck": "vehicle",
    "traffic light": "traffic_light",
    "stop sign": "stop_sign",
}

# -----------------------------------------------------------------------------------
# Class: Detection
# -----------------------------------------------------------------------------------
"""
Description:
    This class represents one object found in one camera image.

Attributes:
    obstacle_type: Simulator obstacle type, e.g. "vehicle" (see OBSTACLE_TYPES).
    coco_class: The model's original COCO class name, e.g. "truck".
    score: Model confidence, from 0 to 1.
    box: Pixel box (x1, y1, x2, y2), with (x1, y1) the top-left corner.
"""
@dataclass (frozen = True)
class Detection: 
    """One object found in one camera image."""
    obstacle_type: str
    coco_class: str
    score: float
    box: tuple[float, float, float,float]
"""
# -----------------------------------------------------------------------------------
# Function: to_obstacle_type
# -----------------------------------------------------------------------------------
Description:
    This function maps a COCO class name to a simulator obstacle type.

Args:
    coco_class (str): COCO class name from the model, e.g. "truck".

Returns:
    str | None: The obstacle type, e.g. "vehicle", or None if the
    simulator ignores that class.
"""
def to_obstacle_type(coco_class:str)->str|None:
    return OBSTACLE_TYPES.get(coco_class)
"""
# -----------------------------------------------------------------------------------
# Function: box_iou
# -----------------------------------------------------------------------------------
Description:
    This function computes the intersection-over-union of two pixel
    boxes: their shared area divided by their combined area. 1.0 means
    identical boxes and 0.0 means they do not touch.

Args:
    first (tuple): Box as (x1, y1, x2, y2).
    second (tuple): Box as (x1, y1, x2, y2).

Returns:
    float: The IoU, from 0.0 to 1.0.
"""
def box_iou(first:tuple[float, float, float, float], second:tuple[float, float, float, float])->float:
    overlap_width= min(first[2], second[2])-max(first[0], second[0])
    overlap_height= min(first[3], second[3])-max(first[1], second[1])
    if (overlap_width<=0) or (overlap_height<=0):
        return 0.0
    intersect = overlap_width*overlap_height
    first_area = (first[2]-first[0])* (first[3]-first[1])
    second_area = (second[2]-second[0])* (second[3]-second[1])
    union = first_area+ second_area - intersect
    return intersect/union
"""
# -----------------------------------------------------------------------------------
# Function: merge_duplicates
# -----------------------------------------------------------------------------------
Description:
    This function removes duplicate boxes of the same obstacle type, e.g.
    one vehicle reported as both "car" and "truck". Detections are visited
    from highest to lowest score; one is dropped if it overlaps an
    already-kept detection of the same type by at least iou_threshold.
    Different types never merge, so a pedestrian in front of a car is kept.

Args:
    detections (Iterable[Detection]): Detections from one image, in any order.
    iou_threshold (float): Minimum IoU for two same-type boxes to count as
        one object. Defaults to 0.7.

Returns:
    list[Detection]: The kept detections, highest score first.
"""
def merge_duplicates(detections: Iterable[Detection], iou_threshold: float = 0.7) -> list[Detection]:
    ordered = sorted (detections, key= lambda item: item.score,reverse=True)
    kept =[]
    for detection in ordered:
        is_duplicate = any(
            other.obstacle_type == detection.obstacle_type 
            and box_iou(other.box, detection.box)>= iou_threshold
            for other in kept)
        if not is_duplicate:
            kept.append(detection)
    return kept

"""
# -----------------------------------------------------------------------------------
# Function: build_detections
# -----------------------------------------------------------------------------------
Description:
    This function converts raw model output into cleaned Detections: it
    drops classes the simulator ignores and scores below min_score, then
    merges duplicate boxes.

Args:
    raw (Iterable[tuple]): (coco_class, score, box) tuples from a model.
    min_score (float): Minimum confidence to keep. Defaults to 0.5.
    iou_threshold (float): Passed to merge_duplicates. Defaults to 0.7.

Returns:
    list[Detection]: The cleaned detections, highest score first.
"""
def build_detections(raw: Iterable[tuple[str, float, tuple[float, float, float , float]]], min_score: float =0.5, iou_threshold: float = 0.7)-> list[Detection]:
    detections=[]
    for coco_class, score, box in raw:
        obstacle_type = to_obstacle_type(coco_class)
        if obstacle_type is None or score < min_score:
            continue
        detections.append (Detection(
            obstacle_type = obstacle_type,
            coco_class = coco_class,
            score = float(score),
            box = tuple(float(value) for value in box),
        )
        )
    return merge_duplicates(detections, iou_threshold)
# -----------------------------------------------------------------------------------
# Class: Detector
# -----------------------------------------------------------------------------------
"""
Description:
    The interface every object detection model provides. Any class with
    this method counts as a Detector (a Protocol needs no inheritance), so
    models in perception/detectors/ can be swapped freely.

Methods:
    detect: Finds objects in one image and returns them as cleaned
        Detections (see build_detections).
"""
class Detector (Protocol):
    def detect(self, image: Any)->list[Detection]:...