"""Checksummed public authored scene data. No session mutation or arbitrary paths."""

from fastapi import APIRouter, Depends, Request
from fastapi.responses import Response

from contracts.v2.spatial import ReadinessRequest, SceneCapabilities, TrainingSceneRevision
from src.services.training_scene import SceneValidationError, TrainingScenePackage, get_training_scene

router = APIRouter(prefix="/v2/scenes", tags=["training-scenes-v2"])


def pinned_scene(scene_revision: str, package: TrainingScenePackage = Depends(get_training_scene)) -> TrainingScenePackage:
    if scene_revision != package.reference().scene_revision:
        raise SceneValidationError("SCENE_REVISION_NOT_AVAILABLE")
    return package


@router.get("/capabilities", response_model=SceneCapabilities)
def capabilities(package: TrainingScenePackage = Depends(get_training_scene)):
    return package.capabilities()


def immutable_response(request: Request, data: bytes, digest: str, media_type: str):
    headers = {"ETag": f'"{digest}"', "Cache-Control": "public, max-age=31536000, immutable",
               "X-Content-Type-Options": "nosniff"}
    if request.headers.get("if-none-match") == headers["ETag"]:
        return Response(status_code=304, headers=headers)
    return Response(data, media_type=media_type, headers=headers)


@router.get("/{scene_revision}/manifest", response_model=TrainingSceneRevision)
def manifest(request: Request, package: TrainingScenePackage = Depends(pinned_scene)):
    return immutable_response(request, package.manifest_bytes, package.manifest_sha256, "application/json")


@router.get("/{scene_revision}/assets/{asset_id}")
def asset(asset_id: str, request: Request, package: TrainingScenePackage = Depends(pinned_scene)):
    if len(asset_id) > 128:
        raise SceneValidationError("INVALID_ASSET_ID")
    asset = package.asset(asset_id)
    return immutable_response(request, asset.data, asset.sha256, "model/gltf-binary")


@router.post("/{scene_revision}/readiness")
async def readiness(request: Request, package: TrainingScenePackage = Depends(pinned_scene)):
    # Bound the body while streaming, before JSON/Pydantic allocation. No raw
    # untrusted body or stack trace is echoed in spatial diagnostics.
    body = bytearray()
    async for chunk in request.stream():
        if len(body) + len(chunk) > 16384:
            raise SceneValidationError("READINESS_BODY_LIMIT")
        body.extend(chunk)
    try:
        value = ReadinessRequest.model_validate_json(body)
    except ValueError as exc:
        raise SceneValidationError("INVALID_SPATIAL_HANDSHAKE") from exc
    return package.readiness(value)
