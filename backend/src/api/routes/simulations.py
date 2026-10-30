"""

Simulation endpoints.

Author: Lau Xiang Yang
Date Created: 09/09/2026
Last Modified: 09/09/2026

Description:
    This module defines the API endpoints for managing simulations. It provides functionality for creating, 
    retrieving, and deleting simulation sessions.

"""

from fastapi import APIRouter, Response, status

from src.schemas.simulation import CreateSimulationRequest, SimulationResponse
from src.services.session_manager import session_manager

# router for simulation endpoints
router = APIRouter(prefix="/simulations", tags=["simulations"])

"""
# ---------------------------------------------------------------------------------------------
# Function: create_simulation()
# ---------------------------------------------------------------------------------------------
Description:
This function handles the creation of a new simulation session. It accepts a payload containing
the necessary parameters for the simulation and returns a response with the details of the created
simulation session.

Args:
    payload: CreateSimulationRequest - The request payload containing the parameters for the simulation.

Returns:
    SimulationResponse - A response containing the details of the created simulation session.

"""
@router.post("", response_model=SimulationResponse, status_code=status.HTTP_201_CREATED)
async def create_simulation(payload: CreateSimulationRequest) -> SimulationResponse:
    return session_manager.create(payload)


"""
# ---------------------------------------------------------------------------------------------
# Function: get_simulation()
# ---------------------------------------------------------------------------------------------
Description:
This function retrieves the details of an existing simulation session based on the provided session ID.
It returns a response containing the details of the simulation session.

Args:
    session_id: str - The unique identifier of the simulation session to retrieve.

Returns:
    SimulationResponse - A response containing the details of the requested simulation session.

"""
@router.get("/{session_id}", response_model=SimulationResponse)
async def get_simulation(session_id: str) -> SimulationResponse:
    return session_manager.get(session_id)

"""
# ---------------------------------------------------------------------------------------------
# Function: delete_simulation()
# ---------------------------------------------------------------------------------------------
Description:
This function handles the deletion of an existing simulation session based on the provided session ID.
It returns a response with a 204 No Content status code upon successful deletion.

Args:
    session_id: str - The unique identifier of the simulation session to delete.

Returns:
    Response - A response with a 204 No Content status code indicating successful deletion.

"""
@router.delete("/{session_id}", status_code=status.HTTP_204_NO_CONTENT)
async def delete_simulation(session_id: str) -> Response:
    session_manager.delete(session_id)
    return Response(status_code=status.HTTP_204_NO_CONTENT)
