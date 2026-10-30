"""BE-M2-07 headless package, transform, corruption and HTTP readiness gates."""

import hashlib
import io
import json
import math
import shutil
import struct
from dataclasses import replace
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from pydantic import ValidationError

from contracts.v2.models import DispatcherCommand, parse_message
from contracts.v2.spatial import (BodyPose, ReadinessRequest, TrainingSceneRevision, orientation_to_render,
                                  render_asset_point, render_to_world, rotate, safe_asset_path, world_to_render)
from scripts.build_m2_scene import pack_glb
from src.app import app
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services.glb_validator import AssetValidationError, validate_glb
from src.services.training_scene import (PACKAGE_ROOT, SceneValidationError, TrainingScenePackage,
                                         get_training_scene, validate_snapshot_bindings)

FIXTURES = Path(__file__).resolve().parents[2] / "contracts/v2/fixtures"


def fixture(name):
    return json.loads((FIXTURES / f"{name}.json").read_bytes())


@pytest.fixture
def package_copy(tmp_path):
    target = tmp_path / "scene"
    shutil.copytree(PACKAGE_ROOT, target)
    return target


def rewrite_manifest(root, mutate, *, repin=False):
    value = json.loads((root / "manifest.json").read_bytes())
    mutate(value)
    if repin:
        blueprint = dict(value, scene_revision="pending", spatial_revision="pending")
        fingerprint = hashlib.sha256((json.dumps(blueprint, indent=2, sort_keys=True, allow_nan=False)+"\n").encode()).hexdigest()[:24]
        value.update(scene_revision=f"scene-{fingerprint}", spatial_revision=f"spatial-{fingerprint}")
    (root / "manifest.json").write_text(json.dumps(value), encoding="utf-8")


def changed_manifest(case):
    value = json.loads((PACKAGE_ROOT / "manifest.json").read_bytes())
    for change in case["changes"]:
        parent = value
        for key in change["path"][:-1]:
            parent = parent[key]
        key = change["path"][-1]
        if change.get("remove"):
            del parent[key]
        else:
            parent[key] = change["value"]
    return value


@pytest.mark.parametrize("case", fixture("spatial_manifest_cases")["invalid"], ids=lambda c: c["name"])
def test_shared_invalid_manifests_are_rejected(case):
    with pytest.raises(ValidationError):
        TrainingSceneRevision.model_validate(changed_manifest(case))


@pytest.mark.parametrize("case", fixture("spatial_manifest_cases")["valid"], ids=lambda c: c["name"])
def test_shared_valid_manifests_are_accepted(case):
    TrainingSceneRevision.model_validate(changed_manifest(case))


def unpack(data):
    size = struct.unpack_from("<I", data, 12)[0]
    return json.loads(data[20:20+size]), data[28+size:]


def test_authored_inventory_is_dimensioned_and_headless():
    package = TrainingScenePackage(PACKAGE_ROOT)
    manifest = package.manifest
    assert len(manifest.entities) == 13
    assert {e.kind for e in manifest.entities} == {"ROAD","BUILDING","STOP","DEPOT","CHARGER","SHUTTLE","OBSTRUCTION"}
    assert len([e for e in manifest.entities if e.kind == "SHUTTLE"]) == 5
    for a in manifest.assets:
        asset = package.asset(a.asset_id)
        assert hashlib.sha256(asset.data).hexdigest() == a.sha256
        assert validate_glb(asset.data).decoded_bytes == a.decoded_bytes
    assert package.capabilities().world_3d
    assert package.capabilities().authoritative_3d_motion
    assert fixture("scene_capabilities") == package.capabilities().model_dump()
    # Returned model mutation cannot change cached package or readiness identity.
    manifest.entities[0].position_m[0] += 50
    assert package.manifest.entities[0].position_m[0] != manifest.entities[0].position_m[0]


def test_same_golden_snapshot_resolves_semantic_ids_and_hashes():
    snapshot = parse_message(fixture("spatial_snapshot"))
    package = get_training_scene()
    validate_snapshot_bindings(snapshot, package)
    ramp = snapshot.vehicles[-1].spatial
    surface = package.manifest.road_surfaces[1]
    # Centre sits half the body height above support along the authored normal.
    support = [a-ramp.support_offset_m*b for a,b in zip(ramp.position_m,surface.normal)]
    assert support == pytest.approx([50.,0.,2.])
    assert rotate(ramp.orientation_xyzw,[0.,0.,1.]) == pytest.approx(surface.normal)


@pytest.mark.parametrize("case", fixture("spatial_cases")["transforms"], ids=lambda c:c["name"])
def test_cross_language_basis_and_pick_roundtrip(case):
    pose = BodyPose(position_m=case["position_m"], orientation_xyzw=case["orientation_xyzw"])
    actual = render_asset_point(pose, case["asset_to_body"], case["asset_vertex_m"])
    assert actual == pytest.approx(case["expected_render_m"], abs=1e-6)
    assert orientation_to_render(pose.orientation_xyzw) == pytest.approx(case["expected_render_orientation_xyzw"])
    assert render_to_world(world_to_render(pose.position_m)) == pytest.approx(case["expected_pick_world_m"])
    # B R v = (B R B^-1) B v, including slopes and q/-q.
    assert world_to_render(rotate(pose.orientation_xyzw,[2.,-.6,.8])) == pytest.approx(
        rotate(orientation_to_render(pose.orientation_xyzw),world_to_render([2.,-.6,.8])))


def test_hand_computed_cardinal_markers_anchor_generated_golden_values():
    cases = {c["name"]: c for c in fixture("spatial_cases")["transforms"]}
    for name, expected in {"east":[2.1,.8,-.6], "north":[11.5,4.1,5.7],
                           "west":[122.9,12.8,75.6], "south":[1.6,3.8,.1]}.items():
        assert cases[name]["expected_render_m"] == pytest.approx(expected)


@pytest.mark.parametrize("name,value", fixture("spatial_cases")["negative_snapshots"].items())
def test_cross_language_negative_snapshots(name,value):
    with pytest.raises(ValidationError):
        parse_message(value)


@pytest.mark.parametrize("field,value", [("position_m",[math.inf,0.,0.]), ("orientation_xyzw",[math.nan,0.,0.,1.]),
                                         ("dimensions_m",[4.,0.,2.])])
def test_nonfinite_or_degenerate_pose_rejected(field,value):
    raw = fixture("spatial_snapshot")
    raw["vehicles"][0]["spatial"][field] = value
    with pytest.raises(ValidationError):
        parse_message(raw)


@pytest.mark.parametrize("key,value", [("render_asset_sha256","0"*64),("support_surface_id","missing"),
                                       ("collision_proxy_revision","old"),("deck_id","wrong-deck")])
def test_package_binding_mismatch_blocks_load(key,value):
    snapshot = parse_message(fixture("spatial_snapshot"))
    setattr(snapshot.vehicles[0].spatial,key,value)
    with pytest.raises(SceneValidationError):
        validate_snapshot_bindings(snapshot,get_training_scene())


@pytest.mark.parametrize("value", ["../bad.glb","/absolute.glb","C:/bad.glb","assets\\bad.glb", "https://host/x.glb",
                                   "assets/%2e%2e/bad.glb","assets//bad.glb","./bad.glb","assets/x.gltf"])
def test_paths_cannot_escape_package(value):
    with pytest.raises(ValueError):
        safe_asset_path(value)


@pytest.mark.parametrize("mutation", [
    lambda m: m.update(spatial_schema_version="3.0"),
    lambda m: m["entities"][0].update(render_asset_id="missing"),
    lambda m: m["assets"][0].update(path="../outside.glb"),
    lambda m: m["proxies"][0].update(dimensions_m=[1.,1.,1.]),
    lambda m: m["assets"][-2].update(fallback_asset_id="missing"),
    lambda m: m["road_surfaces"][0].update(permitted_next_surface_ids=["missing"]),
])
def test_invalid_manifest_prevents_readiness(package_copy,mutation):
    rewrite_manifest(package_copy,mutation)
    with pytest.raises(SceneValidationError):
        TrainingScenePackage(package_copy)


@pytest.mark.parametrize("mode", ["missing","corrupt","oversized"])
def test_mandatory_asset_failures_block_readiness(package_copy,mode):
    path = package_copy / "assets/shuttle.glb"
    if mode == "missing": path.unlink()
    elif mode == "corrupt": path.write_bytes(b"bad")
    else: path.write_bytes(b"x"*(2*1024*1024+1))
    with pytest.raises(SceneValidationError,match="MANDATORY_ASSET_INVALID"):
        TrainingScenePackage(package_copy)


def test_optional_detail_has_exact_mandatory_fallback(package_copy):
    (package_copy / "assets/shuttle-detail.glb").unlink()
    package = TrainingScenePackage(package_copy)
    req = fixture("scene_readiness")
    # This copy has identical manifest bytes/pins.
    response = package.readiness(ReadinessRequest.model_validate(req))
    assert response["ready"]
    assert response["fallback_assets"] == {"asset-shuttle-detail":"asset-shuttle"}


@pytest.mark.parametrize("mutate", [
    lambda d: d.update(extensionsUsed=["KHR_draco_mesh_compression"]),
    lambda d: d["buffers"][0].update(uri="https://host/bytes.bin"),
    lambda d: d["accessors"][0].update(count=65537),
    lambda d: d["accessors"][0].update(byteOffset=100000),
    lambda d: d["nodes"][0].update(scale=[100,100,100]),
    lambda d: d["nodes"][0].update(children=[0]),
    lambda d: d["nodes"][0].update(rotation=[0,0,0,2]),
    lambda d: d["meshes"][0]["primitives"][0].update(mode=1),
    lambda d: d["accessors"][0].update(min=[-999,-999,-999]),
])
def test_unsupported_or_malformed_glb_is_rejected(mutate):
    doc, binary = unpack(get_training_scene().asset("asset-shuttle").data)
    mutate(doc)
    with pytest.raises(AssetValidationError):
        validate_glb(pack_glb(doc,binary))


@pytest.mark.parametrize("mode", ["nan","index"])
def test_nonfinite_vertices_and_index_overrun_are_rejected(mode):
    doc, raw = unpack(get_training_scene().asset("asset-shuttle").data)
    binary = bytearray(raw)
    if mode == "nan": struct.pack_into("<f",binary,0,math.nan)
    else: struct.pack_into("<H",binary,doc["bufferViews"][3]["byteOffset"],65535)
    with pytest.raises(AssetValidationError):
        validate_glb(pack_glb(doc,bytes(binary)))


def test_nested_rigid_nodes_are_composed_and_counted():
    doc,binary = unpack(get_training_scene().asset("asset-shuttle").data)
    doc["nodes"] = [{"translation":[5,0,0],"children":[1]},{"mesh":0,"translation":[0,2,0]}]
    inventory = validate_glb(pack_glb(doc,binary))
    assert inventory.bounds_min_m == pytest.approx([2.6,.7,-1.])
    assert inventory.bounds_max_m == pytest.approx([7.4,3.3,1.])


@pytest.mark.parametrize("mode,diagnostic", [
    ("overflow", "NONFINITE_TRANSFORMED_GEOMETRY"),
    ("strided_indices", "STRIDED_INDICES_UNSUPPORTED"),
])
def test_invalid_transformed_or_index_geometry_blocks_package(package_copy, mode, diagnostic):
    doc, binary = unpack(get_training_scene().asset("asset-shuttle").data)
    if mode == "overflow":
        doc["nodes"] = [{"children":[1]}, {"translation":[1e308,0,0],"children":[2]},
                        {"translation":[1e308,0,0],"mesh":0}]
    else:
        view = doc["bufferViews"][doc["accessors"][3]["bufferView"]]
        indices = binary[view["byteOffset"]:view["byteOffset"]+view["byteLength"]]
        strided = b"".join(indices[i:i+2]+b"\0\0" for i in range(0,len(indices),2))
        view.update(byteOffset=len(binary),byteLength=len(strided),byteStride=4)
        binary += strided
        doc["buffers"][0]["byteLength"] = len(binary)
    data = pack_glb(doc,binary)
    with pytest.raises(AssetValidationError, match=diagnostic):
        validate_glb(data)
    (package_copy / "assets/shuttle.glb").write_bytes(data)
    def update_asset(m):
        asset = next(a for a in m["assets"] if a["asset_id"] == "asset-shuttle")
        asset.update(byte_length=len(data),sha256=hashlib.sha256(data).hexdigest())
    rewrite_manifest(package_copy,update_asset,repin=True)
    with pytest.raises(SceneValidationError, match=f"MANDATORY_ASSET_INVALID:asset-shuttle:{diagnostic}"):
        TrainingScenePackage(package_copy)


@pytest.mark.parametrize("bound", [math.nan, math.inf, -math.inf])
def test_nonfinite_import_inventory_cannot_pass_bounds_comparison(monkeypatch, bound):
    inventory = validate_glb(get_training_scene().asset("asset-road").data)
    monkeypatch.setattr("src.services.training_scene.validate_glb",
                        lambda _: replace(inventory,bounds_min_m=(bound,bound,bound)))
    with pytest.raises(SceneValidationError, match="DECODED_INVENTORY_MISMATCH"):
        TrainingScenePackage(PACKAGE_ROOT)


@pytest.mark.parametrize("name", ["surface one kilometre away", "normal differs from road slab",
                                  "surface above support face", "surface width exceeds slab",
                                  "surface strip overhangs slab"])
def test_inconsistent_support_geometry_blocks_package(package_copy, name):
    case = next(c for c in fixture("spatial_manifest_cases")["invalid"] if c["name"] == name)
    rewrite_manifest(package_copy,lambda m: m.update(changed_manifest(case)),repin=True)
    with pytest.raises(SceneValidationError, match="INVALID_SCENE_PACKAGE"):
        TrainingScenePackage(package_copy)


@pytest.mark.parametrize("formatting", ["compact", "crlf", "reordered"])
def test_reformatting_preserves_immutable_manifest_and_handshake(package_copy, formatting):
    original = get_training_scene()
    document = json.loads(original.manifest_bytes)
    if formatting == "compact":
        raw = json.dumps(document,separators=(",",":")).encode()
    elif formatting == "crlf":
        raw = original.manifest_bytes.replace(b"\n",b"\r\n")
    else:
        raw = json.dumps(dict(reversed(list(document.items()))),indent=4).encode()
    (package_copy / "manifest.json").write_bytes(raw)
    package = TrainingScenePackage(package_copy)
    assert package.reference() == original.reference()
    assert package.manifest_bytes == original.manifest_bytes
    app.dependency_overrides[get_training_scene] = lambda: package
    try:
        with TestClient(app) as client:
            base = f"/api/v2/scenes/{package.reference().scene_revision}"
            result = client.get(base+"/manifest")
            assert result.content == original.manifest_bytes
            assert result.headers["etag"] == f'"{original.manifest_sha256}"'
            assert client.get(base+"/manifest",headers={"If-None-Match":result.headers["etag"]}).status_code == 304
            assert client.post(base+"/readiness",json=fixture("scene_readiness")).json()["ready"]
    finally:
        app.dependency_overrides.pop(get_training_scene)


def test_canonical_manifest_expansion_remains_bounded(package_copy):
    rewrite_manifest(package_copy,lambda m: m.update(padding=["x"]*80000),repin=True)
    document = json.loads((package_copy / "manifest.json").read_bytes())
    raw = json.dumps(document,separators=(",",":")).encode()
    assert len(raw) < 512*1024
    (package_copy / "manifest.json").write_bytes(raw)
    with pytest.raises(SceneValidationError, match="PACKAGE_BYTE_LIMIT"):
        TrainingScenePackage(package_copy)


def test_http_manifest_assets_cache_and_readiness():
    with TestClient(app) as client:
        capabilities = client.get("/api/v2/scenes/capabilities").json()
        base = f"/api/v2/scenes/{capabilities['scene_revision']}"
        manifest = client.get(base+"/manifest")
        assert manifest.status_code == 200
        assert hashlib.sha256(manifest.content).hexdigest() == capabilities["manifest_sha256"]
        assert client.get(base+"/manifest",headers={"If-None-Match":manifest.headers["etag"]}).status_code == 304
        for a in manifest.json()["assets"]:
            result = client.get(base+"/assets/"+a["asset_id"])
            assert result.status_code == 200
            assert hashlib.sha256(result.content).hexdigest() == a["sha256"]
            assert len(result.content) == a["byte_length"]
        readiness = client.post(base+"/readiness",json=fixture("scene_readiness"))
        assert readiness.status_code == 200 and readiness.json()["ready"]
        assert readiness.json()["authoritative_3d_motion"]
        assert client.get("/api/v2/scenes/old-revision/manifest").status_code == 409
        assert client.get(base+"/assets/not-an-asset").status_code == 409


@pytest.mark.parametrize("mode", ["missing","hash","version","revision","renderer","oversized"])
def test_http_readiness_fail_closed(mode):
    req = fixture("scene_readiness")
    base = f"/api/v2/scenes/{req['scene_revision']}"
    if mode == "missing": del req["asset_hashes"]["asset-shuttle"]
    if mode == "hash": req["asset_hashes"]["asset-shuttle"] = "0"*64
    if mode == "version": req["spatial_schema_version"] = "99.0"
    if mode == "revision": req["spatial_revision"] = "wrong-spatial-revision"
    if mode == "renderer": req["render_ready"] = False
    if mode == "oversized": req["extra"] = "X"*20000
    with TestClient(app) as client:
        result = client.post(base+"/readiness",json=req)
    assert result.status_code == 409
    assert len(result.json()["error"]["message"]) <= 512
    assert "asset_hashes" not in result.text


def test_verified_bytes_survive_disk_replacement(package_copy):
    package = TrainingScenePackage(package_copy)
    original = package.asset("asset-shuttle").data
    (package_copy/"assets/shuttle.glb").write_bytes(b"replaced")
    assert package.asset("asset-shuttle").data == original


def test_standalone_scene_schema_matches_manifest():
    manifest = get_training_scene().manifest
    assert TrainingSceneRevision.model_validate_json(manifest.model_dump_json()) == manifest


def test_content_changes_require_a_new_scene_revision(package_copy):
    def move(m):
        m["entities"][2]["position_m"][0] += 1
        m["proxies"][2]["position_m"][0] += 1
    rewrite_manifest(package_copy,move)
    with pytest.raises(SceneValidationError,match="SCENE_CONTENT_REVISION_MISMATCH"):
        TrainingScenePackage(package_copy)


def test_package_admission_checks_total_declared_bytes_before_decoding(package_copy):
    def oversized(m):
        for a in m["assets"]:
            a["byte_length"] = 2*1024*1024
        # A legitimately rebuilt manifest still cannot exceed the package cap.
        blueprint = dict(m,scene_revision="pending",spatial_revision="pending")
        fingerprint = hashlib.sha256((json.dumps(blueprint,indent=2,sort_keys=True)+"\n").encode()).hexdigest()[:24]
        m["scene_revision"],m["spatial_revision"] = f"scene-{fingerprint}",f"spatial-{fingerprint}"
    rewrite_manifest(package_copy,oversized)
    with pytest.raises(SceneValidationError,match="PACKAGE_BYTE_LIMIT"):
        TrainingScenePackage(package_copy)


def test_checkpoint_retains_spatial_pins_on_recovery_and_cannot_swap_content(tmp_path):
    path = tmp_path / "spatial.sqlite"
    original = parse_message(fixture("spatial_snapshot"))
    with TrainingStore(path) as store:
        store.create_session(original)
        changed = original.model_copy(deep=True)
        changed.tick += 1
        changed.simulation_time_s += .05
        changed.spatial.manifest_sha256 = changed.scenario.spatial.manifest_sha256 = "0"*64
        with pytest.raises(TrainingStoreError,match="Publication does not follow"):
            store.commit_snapshot(changed)
        assert store.snapshot(original.session_id).spatial == original.spatial
    with TrainingStore(path) as recovered:
        snapshot = recovered.snapshot(original.session_id)
        assert snapshot.session.status == "PAUSED"
        assert snapshot.session_epoch == original.session_epoch + 1
        assert snapshot.spatial == original.spatial
        validate_snapshot_bindings(snapshot,get_training_scene())


def test_command_cannot_hot_swap_spatial_content(tmp_path):
    original = parse_message(fixture("spatial_snapshot"))
    command = DispatcherCommand(contract_version="2.1",command_id="pause-spatial",idempotency_key="pause-spatial",
                                session_id=original.session_id,command_type="PAUSE",expected_epoch=original.session_epoch,
                                expected_revision=original.session.aggregate_revision,target_type="SESSION",
                                target_id=original.session_id,payload={})
    with TrainingStore(tmp_path / "commands.sqlite") as store:
        store.create_session(original)
        store.accept("actor-01",command)
        changed = original.model_copy(deep=True)
        changed.session.status = "PAUSED"
        changed.session.aggregate_revision += 1
        changed.spatial.manifest_sha256 = changed.scenario.spatial.manifest_sha256 = "0"*64
        status = store.apply(command.command_id,changed)
        assert status.status == "REJECTED" and status.failure_code == "STALE_REVISION"
        assert store.snapshot(original.session_id).spatial == original.spatial


@pytest.mark.parametrize("image_format", ["PNG","JPEG"])
def test_embedded_texture_decode_and_dimension_limits(image_format):
    from PIL import Image
    doc,binary = unpack(get_training_scene().asset("asset-shuttle").data)
    stream = io.BytesIO()
    Image.new("RGB",(8,8),(255,128,0)).save(stream,format=image_format)
    encoded_image = stream.getvalue()
    binary += b"\0"*(-len(binary)%4)
    doc["bufferViews"].append({"buffer":0,"byteOffset":len(binary),"byteLength":len(encoded_image)})
    binary += encoded_image
    doc["buffers"][0]["byteLength"] = len(binary)
    doc["images"] = [{"bufferView":len(doc["bufferViews"])-1,"mimeType":"image/png" if image_format == "PNG" else "image/jpeg"}]
    doc["textures"] = [{"source":0}]
    doc["materials"][0]["pbrMetallicRoughness"]["baseColorTexture"] = {"index":0}
    inventory = validate_glb(pack_glb(doc,binary))
    assert inventory.decoded_bytes == 8*8*4+get_training_scene().manifest.assets[0].decoded_bytes
    stream = io.BytesIO()
    Image.new("RGB",(1025,1)).save(stream,format=image_format)
    too_large = stream.getvalue()
    start = doc["bufferViews"][-1]["byteOffset"]
    binary = binary[:start]+too_large
    doc["bufferViews"][-1]["byteLength"] = len(too_large)
    doc["buffers"][0]["byteLength"] = len(binary)
    with pytest.raises(AssetValidationError,match="TEXTURE_DIMENSION_LIMIT"):
        validate_glb(pack_glb(doc,binary))
