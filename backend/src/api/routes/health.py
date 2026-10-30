"""
Health check endpoint.

Author: Lau Xiang Yang
Date Created: 09/09/2026
Last Modified: 09/09/2026

Description:
    This module defines the health check endpoint for the API. It provides a simple way to verify
    that the API is running and responsive. The endpoint returns a JSON response indicating the
    health status of the application.

"""

from fastapi import APIRouter

# router for health check endpoints
router = APIRouter(tags=["health"])

"""
# ---------------------------------------------------------------------------------------------
# Function: health_check()
# ---------------------------------------------------------------------------------------------
Description:
This function serves as the health check endpoint for the API. It returns a JSON response indicating
the health status of the application. The endpoint can be used to verify that the API is running
and responsive.

Args:
    None

Returns:
    dict[str, str] - A dictionary containing the health status of the application.
    
"""
@router.get("/health")
async def health_check() -> dict[str, str]:
    return {"status": "ok"}
