"""Static scene metadata and chunk endpoints."""

from fastapi import APIRouter, HTTPException, Query

from src.schemas.scene import SceneBounds, SceneChunkResponse, SceneMetadataResponse
from src.services.scene_repository import (
    SceneNotFoundError,
    SceneRepositoryError,
    get_scene_repository,
)


router = APIRouter(prefix="/scenes", tags=["scenes"])


def _translate_scene_error(error: SceneRepositoryError) -> HTTPException:
    if isinstance(error, SceneNotFoundError):
        return HTTPException(status_code=404, detail=str(error))
    return HTTPException(status_code=503, detail=str(error))


@router.get("/{scene_id}", response_model=SceneMetadataResponse)
def get_scene(scene_id: str) -> SceneMetadataResponse:
    try:
        return get_scene_repository().metadata(scene_id)
    except SceneRepositoryError as error:
        raise _translate_scene_error(error) from error


@router.get("/{scene_id}/chunks", response_model=SceneChunkResponse)
def get_scene_chunk(
    scene_id: str,
    min_x: float = Query(...),
    min_y: float = Query(...),
    max_x: float = Query(...),
    max_y: float = Query(...),
    lod: int = Query(3, ge=0, le=3),
    include_buildings: bool = Query(True),
    include_pois: bool = Query(True),
) -> SceneChunkResponse:
    bounds = SceneBounds(min_x=min_x, min_y=min_y, max_x=max_x, max_y=max_y)
    try:
        return get_scene_repository().chunk(
            scene_id,
            bounds,
            lod=lod,
            include_buildings=include_buildings,
            include_pois=include_pois,
        )
    except ValueError as error:
        raise HTTPException(status_code=422, detail=str(error)) from error
    except SceneRepositoryError as error:
        raise _translate_scene_error(error) from error
