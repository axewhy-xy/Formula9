"""
API Router

Author: Lau Xiang Yang
Date Created: 09/09/2026
Last Modified: 09/09/2026

Description:
    This module defines the main API router for the application. It aggregates all the individual
    route modules and includes them in a single router instance. This allows for a clean and
    organized structure for the API endpoints, making it easier to manage and maintain.
    
"""

from fastapi import APIRouter

from src.api.routes import auth, health, route_planning, scenes, simulations, training_scenes, training_v2, transit, buildings, road_features

# Create the main API router and include the individual route modules
api_router = APIRouter()
api_router.include_router(auth.router)
api_router.include_router(health.router)
api_router.include_router(route_planning.router)
api_router.include_router(scenes.router)
api_router.include_router(simulations.router)
api_router.include_router(training_v2.router)
api_router.include_router(training_scenes.router)
api_router.include_router(transit.router)
api_router.include_router(buildings.router)
api_router.include_router(road_features.router)
