"""Immutable, verified scene bytes served independently of fleet telemetry."""

from __future__ import annotations

import hashlib
import itertools
import json
import math
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path

from pydantic import ValidationError

from contracts.v2.spatial import (ReadinessRequest, SceneCapabilities, SpatialReference,
                                  TrainingSceneRevision, safe_asset_path)
from src.core.errors import AppError
from .glb_validator import AssetValidationError, decode_json, validate_glb

PACKAGE_ROOT = Path(__file__).resolve().parents[2] / "fixtures" / "m2_scene"
MAX_MANIFEST_BYTES = 512 * 1024
MAX_PACKAGE_BYTES = 16 * 1024 * 1024


class SceneValidationError(AppError):
    error_code = "SCENE_NOT_READY"
    status_code = 409

    def __init__(self, code: str):
        super().__init__(code[:512])


@dataclass(frozen=True)
class VerifiedAsset:
    asset_id: str
    sha256: str
    data: bytes


def bounded_read(path: Path, bound: int) -> bytes:
    with path.open("rb") as source:
        data = source.read(bound + 1)
    if len(data) > bound:
        raise SceneValidationError("PACKAGE_BYTE_LIMIT")
    return data


def canonical_json(document: dict) -> bytes:
    return (json.dumps(document, indent=2, sort_keys=True, allow_nan=False) + "\n").encode("utf-8")


class TrainingScenePackage:
    """Retain verified bytes, avoiding file replacement/hash races after load.

    Manifest access returns a copy. A missing optional asset can fall back only
    to its declared mandatory asset with identical body bounds.
    """

    def __init__(self, root: Path):
        root = root.resolve()
        try:
            manifest_path = (root / "manifest.json").resolve()
            if not manifest_path.is_relative_to(root):
                raise SceneValidationError("UNSAFE_ASSET_PATH")
            raw = bounded_read(manifest_path, MAX_MANIFEST_BYTES)
            document = decode_json(raw)
            self._manifest = TrainingSceneRevision.model_validate(document)
            blueprint = dict(document, scene_revision="pending", spatial_revision="pending")
            fingerprint = hashlib.sha256(canonical_json(blueprint)).hexdigest()[:24]
            if self._manifest.scene_revision != f"scene-{fingerprint}" or self._manifest.spatial_revision != f"spatial-{fingerprint}":
                raise SceneValidationError("SCENE_CONTENT_REVISION_MISMATCH")
            # A revision identifies one served representation, even when the
            # source file has been reformatted or checked out with CRLF.
            self.manifest_bytes = canonical_json(document)
            if len(self.manifest_bytes) > MAX_MANIFEST_BYTES:
                raise SceneValidationError("PACKAGE_BYTE_LIMIT")
            self.manifest_sha256 = hashlib.sha256(self.manifest_bytes).hexdigest()
            self._assets: dict[str, VerifiedAsset] = {}
            self.unavailable_optional: dict[str, str] = {}
            if len(self.manifest_bytes) + sum(a.byte_length for a in self._manifest.assets) > MAX_PACKAGE_BYTES:
                raise SceneValidationError("PACKAGE_BYTE_LIMIT")
            for asset in self._manifest.assets:
                safe_asset_path(asset.path)
                path = (root / asset.path).resolve()
                if not path.is_relative_to(root):
                    raise SceneValidationError("UNSAFE_ASSET_PATH")
                try:
                    data = bounded_read(path, asset.byte_length)
                    if len(data) != asset.byte_length or hashlib.sha256(data).hexdigest() != asset.sha256:
                        raise SceneValidationError("ASSET_HASH_OR_SIZE_MISMATCH")
                    inventory = validate_glb(data)
                    if inventory.decoded_bytes != asset.decoded_bytes or any(not math.isfinite(a) or abs(a-b) > 1e-5 for a, b in zip(
                            inventory.bounds_min_m + inventory.bounds_max_m, tuple(asset.bounds_min_m + asset.bounds_max_m))):
                        raise SceneValidationError("DECODED_INVENTORY_MISMATCH")
                    self._assets[asset.asset_id] = VerifiedAsset(asset.asset_id, asset.sha256, data)
                except (OSError, AssetValidationError, SceneValidationError) as exc:
                    if asset.mandatory:
                        diagnostic = exc.detail if isinstance(exc, SceneValidationError) else str(exc)
                        raise SceneValidationError(f"MANDATORY_ASSET_INVALID:{asset.asset_id}:{diagnostic}") from exc
                    self.unavailable_optional[asset.asset_id] = asset.fallback_asset_id
            self._validate_dimensions()
        except SceneValidationError:
            raise
        except (OSError, ValueError, TypeError, RecursionError, ValidationError) as exc:
            raise SceneValidationError("INVALID_SCENE_PACKAGE") from exc

    @property
    def manifest(self) -> TrainingSceneRevision:
        return self._manifest.model_copy(deep=True)

    def _validate_dimensions(self):
        assets = {a.asset_id: a for a in self._manifest.assets}
        for a in assets.values():
            if a.fallback_asset_id:
                b = assets[a.fallback_asset_id]
                if a.bounds_min_m != b.bounds_min_m or a.bounds_max_m != b.bounds_max_m:
                    raise SceneValidationError("FALLBACK_DIMENSIONS_MISMATCH")
        for e in self._manifest.entities:
            a = assets[e.render_asset_id]
            # Every M2 body asset is baked to the body's declared dimensions.
            corners = itertools.product(*zip(a.bounds_min_m, a.bounds_max_m))
            points = [[sum(e.asset_to_body[r*4+c]*v[c] for c in range(3))+e.asset_to_body[r*4+3] for r in range(3)] for v in corners]
            low = [min(v[c] for v in points) for c in range(3)]
            high = [max(v[c] for v in points) for c in range(3)]
            if any(abs(low[c]+e.dimensions_m[c]/2) > 1e-4 or abs(high[c]-e.dimensions_m[c]/2) > 1e-4 for c in range(3)):
                raise SceneValidationError("RENDER_BODY_DIMENSIONS_MISMATCH")

    def reference(self) -> SpatialReference:
        m = self._manifest
        return SpatialReference(spatial_schema_version=m.spatial_schema_version,
                                scene_id=m.scene_id, scene_revision=m.scene_revision,
                                spatial_revision=m.spatial_revision, transform_revision=m.transform_revision,
                                manifest_sha256=self.manifest_sha256, required_capabilities=["world_3d"])

    def capabilities(self) -> SceneCapabilities:
        return SceneCapabilities(**self.reference().model_dump(), contract_version="2.1",
                                 asset_profile=self._manifest.asset_profile, world_3d=True,
                                 authoritative_3d_motion=True, local_manoeuvre=False, recorded_perception=False)

    def asset(self, asset_id: str) -> VerifiedAsset:
        if asset_id not in self._assets:
            raise SceneValidationError("ASSET_UNAVAILABLE")
        return self._assets[asset_id]

    def readiness(self, request: ReadinessRequest) -> dict:
        expected = self.reference().model_dump()
        if any(getattr(request, key) != value for key, value in expected.items()):
            raise SceneValidationError("SPATIAL_REVISION_MISMATCH")
        if not request.render_ready:
            raise SceneValidationError("CLIENT_RENDERER_NOT_READY")
        known = {a.asset_id: a for a in self._manifest.assets}
        if any(k not in known or known[k].sha256 != v or k not in self._assets for k, v in request.asset_hashes.items()):
            raise SceneValidationError("CLIENT_ASSET_HASH_MISMATCH")
        if any(a.mandatory and request.asset_hashes.get(a.asset_id) != a.sha256 for a in known.values()):
            raise SceneValidationError("MISSING_MANDATORY_GEOMETRY")
        fallbacks = {a.asset_id: a.fallback_asset_id for a in known.values() if not a.mandatory and a.asset_id not in request.asset_hashes}
        return {**expected, "ready": True, "fallback_assets": fallbacks,
                "evidence": "CLIENT_REPORTED_RENDER_READINESS", "authoritative_3d_motion": True}


@lru_cache(maxsize=1)
def get_training_scene() -> TrainingScenePackage:
    return TrainingScenePackage(PACKAGE_ROOT)


def validate_snapshot_bindings(snapshot, package: TrainingScenePackage) -> None:
    """Load-time gate for a 3D actor; never fetch assets in a tick."""
    if snapshot.spatial != package.reference():
        raise SceneValidationError("SPATIAL_REVISION_MISMATCH")
    manifest = package.manifest
    entities = {e.entity_id: e for e in manifest.entities}
    proxies = {p.proxy_id: p for p in manifest.proxies}
    surfaces = {s.surface_id: s for s in manifest.road_surfaces}
    assets = {a.asset_id: a for a in manifest.assets}
    for v in snapshot.vehicles:
        p = v.spatial
        if p is None or v.vehicle_id not in entities or entities[v.vehicle_id].kind != "SHUTTLE":
            raise SceneValidationError("MISSING_MANDATORY_SPATIAL_POSE")
        entity = entities[v.vehicle_id]
        if (p.collision_proxy_id != entity.proxy_id or p.collision_proxy_revision != proxies[entity.proxy_id].revision
                or p.render_asset_id != entity.render_asset_id or p.render_asset_sha256 != assets[entity.render_asset_id].sha256
                or p.asset_to_body != entity.asset_to_body or p.dimensions_m != entity.dimensions_m):
            raise SceneValidationError("SPATIAL_BINDING_MISMATCH")
        surface = surfaces.get(p.support_surface_id)
        if surface is None or surface.lane_id != p.lane_id or surface.deck_id != p.deck_id:
            raise SceneValidationError("INVALID_SUPPORT_BINDING")
