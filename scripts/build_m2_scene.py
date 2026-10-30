"""Reproduce the original CC0 M2 blockout, schemas and cross-language fixtures.

Run from the repository root: python -m scripts.build_m2_scene
No DCC importer, network, random state, renderer or GPU is required.
"""

from __future__ import annotations

import copy
import hashlib
import json
import math
import struct
from pathlib import Path

from contracts.v2.models import parse_message
from contracts.v2.spatial import (BodyPose, ReadinessRequest, SceneCapabilities, SpatialReference,
                                  SpatialVehicle, TrainingSceneRevision, render_asset_point)
from backend.src.services.glb_validator import validate_glb
from backend.src.services.training_scene import TrainingScenePackage, validate_snapshot_bindings

ROOT = Path(__file__).resolve().parents[1]
PACKAGE = ROOT / "backend/fixtures/m2_scene"
CONTRACTS = ROOT / "contracts/v2"
ASSET_TO_BODY = [1., 0., 0., 0., 0., 0., -1., 0., 0., 1., 0., 0., 0., 0., 0., 1.]


def encoded(value):
    return (json.dumps(value, indent=2, sort_keys=True, allow_nan=False) + "\n").encode()


def pack_glb(doc, binary):
    raw = json.dumps(doc, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()
    raw += b" " * (-len(raw) % 4)
    binary += b"\0" * (-len(binary) % 4)
    return (struct.pack("<III", 0x46546C67, 2, 28+len(raw)+len(binary))
            + struct.pack("<II", len(raw), 0x4E4F534A)+raw
            + struct.pack("<II", len(binary), 0x004E4942)+binary)


def box_glb(dimensions, colour):
    # Six independently shaded faces; outward CCW triangles, explicit normals
    # and UVs. Input dimensions are body L/W/H, bytes remain glTF Y-up.
    l, w, h = dimensions
    positions, normals, uv, indices = [], [], [], []
    for normal, corners in [
        ([1, 0, 0], [[1,-1,-1], [1,1,-1], [1,1,1], [1,-1,1]]),
        ([-1,0,0], [[-1,-1,1], [-1,1,1], [-1,1,-1], [-1,-1,-1]]),
        ([0,1,0], [[-1,1,-1], [-1,1,1], [1,1,1], [1,1,-1]]),
        ([0,-1,0], [[-1,-1,1], [-1,-1,-1], [1,-1,-1], [1,-1,1]]),
        ([0,0,1], [[-1,-1,1], [1,-1,1], [1,1,1], [-1,1,1]]),
        ([0,0,-1], [[1,-1,-1], [-1,-1,-1], [-1,1,-1], [1,1,-1]]),
    ]:
        start = len(positions)
        positions += [[x*l/2, y*h/2, z*w/2] for x,y,z in corners]
        normals += [normal]*4
        uv += [[0,0], [1,0], [1,1], [0,1]]
        indices += [start, start+1, start+2, start, start+2, start+3]
    binary, views, accessors = b"", [], []
    for values, kind, component, fmt in ((positions, "VEC3", 5126, "f"), (normals, "VEC3", 5126, "f"),
                                         (uv, "VEC2", 5126, "f"), ([[i] for i in indices], "SCALAR", 5123, "H")):
        binary += b"\0" * (-len(binary) % 4)
        # Bounds are measured in the float32 representation actually written.
        chunk = b"".join(struct.pack("<"+fmt*len(row), *row) for row in values)
        values = [struct.unpack_from("<"+fmt*len(values[0]), chunk, i*struct.calcsize(fmt)*len(values[0])) for i in range(len(values))]
        views.append({"buffer": 0, "byteOffset": len(binary), "byteLength": len(chunk)})
        a = {"bufferView": len(views)-1, "componentType": component, "count": len(values), "type": kind}
        if len(accessors) == 0:
            a.update(min=[min(v[c] for v in values) for c in range(3)], max=[max(v[c] for v in values) for c in range(3)])
        accessors.append(a)
        binary += chunk
    doc = {"asset": {"version": "2.0", "generator": "formula9-scene-builder-1.0", "copyright": "CC0-1.0 Formula9 original blockout"},
           "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0}],
           "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1, "TEXCOORD_0": 2}, "indices": 3, "material": 0, "mode": 4}]}],
           "materials": [{"pbrMetallicRoughness": {"baseColorFactor": colour+[1.], "metallicFactor": 0., "roughnessFactor": 1.}}],
           "buffers": [{"byteLength": len(binary)}], "bufferViews": views, "accessors": accessors}
    return pack_glb(doc, binary)


def build():
    (PACKAGE / "assets").mkdir(parents=True, exist_ok=True)
    assets, entities, proxies = [], [], []
    slope = math.atan2(4, 20)
    ramp_q = [0., -math.sin(slope/2), 0., math.cos(slope/2)]
    sizes = {"road": [40.,6.,.2], "ramp": [math.hypot(20,4),6.,.2], "building": [12.,8.,10.],
             "stop": [3.,1.5,2.5], "depot": [16.,12.,.4], "charger": [1.,.8,1.8],
             "shuttle": [4.8,2.,2.6], "shuttle-detail": [4.8,2.,2.6], "obstruction": [2.,2.5,1.5]}
    colours = {"road": [.25,.28,.3], "ramp": [.35,.38,.4], "building": [.6,.62,.65],
               "stop": [.1,.6,.8], "depot": [.6,.6,.5], "charger": [.1,.7,.3],
               "shuttle": [.85,.5,.12], "shuttle-detail": [.9,.6,.2], "obstruction": [.8,.15,.1]}
    for name, dimensions in sizes.items():
        data = box_glb(dimensions, colours[name])
        inventory = validate_glb(data)
        path = f"assets/{name}.glb"
        (PACKAGE / path).write_bytes(data)
        assets.append({"asset_id": f"asset-{name}", "path": path, "sha256": hashlib.sha256(data).hexdigest(),
                       "byte_length": len(data), "decoded_bytes": inventory.decoded_bytes,
                       "bounds_min_m": list(inventory.bounds_min_m), "bounds_max_m": list(inventory.bounds_max_m),
                       "mandatory": name != "shuttle-detail", "fallback_asset_id": "asset-shuttle" if name == "shuttle-detail" else None,
                       "license": "CC0-1.0", "attribution": "Formula9 contributors; original procedural blockout, dedicated under CC0 1.0."})

    def entity(identifier, kind, asset, position, q=None):
        pose = {"position_m": position, "orientation_xyzw": q or [0.,0.,0.,1.]}
        entities.append({**pose, "entity_id": identifier, "kind": kind, "dimensions_m": sizes[asset],
                         "render_asset_id": f"asset-{asset}", "proxy_id": f"proxy-{identifier}", "asset_to_body": ASSET_TO_BODY})
        proxies.append({**pose, "proxy_id": f"proxy-{identifier}", "revision": "proxy-r1", "entity_id": identifier,
                        "classification": "ROAD_SUPPORT" if kind == "ROAD" else "NON_OPERATIONAL" if kind == "STOP" else "BLOCKING",
                        "dimensions_m": sizes[asset]})

    entity("road-flat", "ROAD", "road", [20.,0.,-.1])
    # Top face follows z=.2*(x-40); shift body centre along the road normal.
    entity("road-ramp", "ROAD", "ramp", [50.+.1*math.sin(slope),0.,2.-.1*math.cos(slope)], ramp_q)
    entity("building-01", "BUILDING", "building", [20.,14.,5.])
    entity("stop-terminal", "STOP", "stop", [10.,-4.5,1.25])
    entity("depot-main", "DEPOT", "depot", [-12.,0.,-.2])
    entity("charger-01", "CHARGER", "charger", [-15.,-3.,.9])
    entity("charger-02", "CHARGER", "charger", [-15.,3.,.9])
    entity("obstruction-01", "OBSTRUCTION", "obstruction", [30.,0.,.75])
    for i, x in enumerate([2.,9.,16.,23.,50.], 1):
        ramp = i == 5
        support = [x,0.,2. if ramp else 0.]
        normal = [-math.sin(slope),0.,math.cos(slope)] if ramp else [0.,0.,1.]
        entity(f"shuttle-{i:02}", "SHUTTLE", "shuttle", [a+1.3*b for a,b in zip(support, normal)], ramp_q if ramp else None)
    scene = {"contract_version": "2.1", "spatial_schema_version": "1.0", "asset_profile": "formula9-gltf2-r1",
             "scene_id": "m2-blockout", "scene_revision": "pending", "spatial_revision": "pending", "transform_revision": "canonical-enu-r1",
             "parent_scene_revision": None, "units": "metres", "world_basis": "RH_X_EAST_Y_NORTH_Z_UP",
             "geographic_origin": [1.3521,103.8198,0.], "local_origin_m": [125.,-75.,12.],
             "source_geography": "Authored local training blockout; geographic anchor illustrative, not surveyed Singapore roads.",
             "provenance": "AUTHORED_TRAINING_BLOCKOUT", "build_tool": "formula9-scene-builder-1.0", "importer_profile": "formula9-glb-validator-1.0",
             "assets": assets, "entities": entities, "proxies": proxies,
             "road_surfaces": [
                 {"surface_id": "surface-flat", "entity_id": "road-flat", "lane_id": "lane-main", "deck_id": "deck-ground", "directed_edge_id": "edge-flat",
                  "width_m": 6., "path_samples_m": [[0.,0.,0.],[40.,0.,0.]], "normal": [0.,0.,1.], "permitted_next_surface_ids": ["surface-ramp"], "clearance_m": 5.},
                 {"surface_id": "surface-ramp", "entity_id": "road-ramp", "lane_id": "lane-main", "deck_id": "deck-ramp", "directed_edge_id": "edge-ramp",
                  "width_m": 6., "path_samples_m": [[40.,0.,0.],[60.,0.,4.]], "normal": [-math.sin(slope),0.,math.cos(slope)], "permitted_next_surface_ids": [], "clearance_m": 5.}],
             "camera_bookmarks": [{"camera_id": "overview", "position_m": [30.,-70.,55.], "target_m": [25.,0.,0.]},
                                  {"camera_id": "incident-focus", "position_m": [25.,-12.,8.], "target_m": [30.,0.,.75]}]}
    # Hash the complete source blueprint with fixed placeholders. Rebuilding the
    # same inputs is byte-identical; any mesh/proxy/material edit changes pins.
    digest = hashlib.sha256(encoded(scene)).hexdigest()[:24]
    scene["scene_revision"], scene["spatial_revision"] = f"scene-{digest}", f"spatial-{digest}"
    TrainingSceneRevision.model_validate(scene)
    (PACKAGE / "manifest.json").write_bytes(encoded(scene))
    package = TrainingScenePackage(PACKAGE)
    schemas = {"training_scene": TrainingSceneRevision, "spatial_vehicle": SpatialVehicle,
               "spatial_reference": SpatialReference, "scene_capabilities": SceneCapabilities, "scene_readiness": ReadinessRequest}
    for name, model in schemas.items():
        schema = model.model_json_schema()
        schema["$id"] = f"https://formula9.local/contracts/v2/{name}.schema.json"
        (CONTRACTS / f"{name}.schema.json").write_bytes(encoded(schema))
    (CONTRACTS / "fixtures/scene_capabilities.json").write_bytes(encoded(package.capabilities().model_dump()))
    readiness = {**package.reference().model_dump(), "asset_profile": "formula9-gltf2-r1", "render_ready": True,
                 "asset_hashes": {a["asset_id"]: a["sha256"] for a in assets if a["mandatory"]}}
    (CONTRACTS / "fixtures/scene_readiness.json").write_bytes(encoded(readiness))

    snapshot = json.loads((CONTRACTS / "fixtures/snapshot.json").read_bytes())
    snapshot.update(contract_version="2.1", scene_revision=scene["scene_revision"], transform_revision=scene["transform_revision"], spatial=package.reference().model_dump())
    snapshot["scenario"].update(scene_revision=scene["scene_revision"], transform_revision=scene["transform_revision"], spatial=package.reference().model_dump())
    for route in snapshot["routes"]:
        route["scene_revision"] = scene["scene_revision"]
    shuttle_asset = next(a for a in assets if a["asset_id"] == "asset-shuttle")
    for v in snapshot["vehicles"]:
        e = next(e for e in entities if e["entity_id"] == v["vehicle_id"])
        ramp = v["vehicle_id"] == "shuttle-05"
        v["position"] = dict(zip(["x_m", "y_m", "z_m"], e["position_m"]))
        v["spatial"] = {k: e[k] for k in ("position_m", "orientation_xyzw", "dimensions_m", "asset_to_body", "render_asset_id")}
        v["spatial"].update(collision_proxy_id=e["proxy_id"], collision_proxy_revision="proxy-r1", support_offset_m=1.3,
                            support_surface_id="surface-ramp" if ramp else "surface-flat", lane_id="lane-main", deck_id="deck-ramp" if ramp else "deck-ground",
                            route_progress_m=10. if ramp else e["position_m"][0], movement="HELD", render_asset_sha256=shuttle_asset["sha256"])
    validate_snapshot_bindings(parse_message(snapshot), package)
    (CONTRACTS / "fixtures/spatial_snapshot.json").write_bytes(encoded(snapshot))
    invalid = {}
    for name in ["non_unit_quaternion", "missing_pose", "wrong_scene_revision", "unsupported_spatial_version", "body_scale"]:
        v = copy.deepcopy(snapshot)
        if name == "non_unit_quaternion": v["vehicles"][0]["spatial"]["orientation_xyzw"] = [0.,0.,0.,2.]
        if name == "missing_pose": del v["vehicles"][0]["spatial"]
        if name == "wrong_scene_revision": v["spatial"]["scene_revision"] = "wrong-scene"
        if name == "unsupported_spatial_version": v["spatial"]["spatial_schema_version"] = "99.0"
        if name == "body_scale": v["vehicles"][0]["spatial"]["asset_to_body"][0] = 100.
        invalid[name] = v

    transforms = []
    for name, q, position in [
        ("east", [0.,0.,0.,1.], [0.,0.,0.]),
        ("north", [0.,0.,math.sqrt(.5),math.sqrt(.5)], [12.,-8.,3.]),
        ("west", [0.,0.,1.,0.], [125.,-75.,12.]),
        ("south", [0.,0.,-math.sqrt(.5),math.sqrt(.5)], [1.,2.,3.]),
        ("ramp", ramp_q, [50.-1.3*math.sin(slope),0.,2.+1.3*math.cos(slope)]),
        ("negative-quaternion", [-v for v in ramp_q], [50.-1.3*math.sin(slope),0.,2.+1.3*math.cos(slope)]),
    ]:
        # Asymmetric nose/roof/left markers detect axis swaps and mirrored picks.
        vertex = [2.1,.8,-.6]
        matrix = ASSET_TO_BODY.copy()
        if name == "north": matrix[3],matrix[7],matrix[11] = .2,-.1,.3
        pose = BodyPose(position_m=position, orientation_xyzw=q)
        transforms.append({"name": name, **pose.model_dump(), "asset_to_body": matrix, "asset_vertex_m": vertex,
                           "expected_render_m": render_asset_point(pose, matrix, vertex),
                           "expected_render_orientation_xyzw": [q[0],q[2],-q[1],q[3]],
                           "expected_pick_world_m": [a+b for a,b in zip(position, [0.,0.,0.])]})
    fixtures = {"spatial_schema_version": "1.0", "matrix_layout": "ROW_MAJOR_COLUMN_VECTOR", "tolerance": 1e-6,
                "local_origin_m": [125.,-75.,12.], "geographic_origin": [1.3521,103.8198,0.],
                "transforms": transforms, "negative_snapshots": invalid}
    (CONTRACTS / "fixtures/spatial_cases.json").write_bytes(encoded(fixtures))
    print(json.dumps({"scene_revision": scene["scene_revision"], "manifest_sha256": package.manifest_sha256,
                      "assets": len(assets), "entities": len(entities), "bytes": sum(a["byte_length"] for a in assets)}))


if __name__ == "__main__":
    build()
