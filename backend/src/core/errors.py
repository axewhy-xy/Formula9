"""

Application errors and their HTTP representations.

Author: Lau Xiang Yang
Date Created: 09/09/2026
Last Modified: 09/09/2026

Description:
    This module defines custom application errors and their corresponding HTTP responses.

"""

from fastapi import Request
from fastapi.responses import JSONResponse

"""
# ---------------------------------------------------------------------------------------------
# Class: AppError
# ---------------------------------------------------------------------------------------------
Description:
This class represents a generic application error. It includes attributes for the HTTP status code,
error code, and error detail message. It serves as a base class for more specific application errors.

Attributes:
    status_code (int): The HTTP status code associated with the error. Default is 400.
    error_code (str): A string representing the error code. Default is "application_error".
    detail (str): A string containing details about the error.

Member Functions:
    __init__(self, detail: str) -> None:
        Initializes an instance of the AppError class with the provided error detail.

"""
class AppError(Exception):
    status_code = 400
    error_code = "application_error"

    def __init__(self, detail: str) -> None:
        self.detail = detail

"""
# ---------------------------------------------------------------------------------------------
# Class: SessionNotFoundError
# ---------------------------------------------------------------------------------------------
Description:
This class represents a specific application error indicating that a session was not found. It inherits
from the AppError class and overrides the status code and error code attributes.

Attributes:
    status_code (int): The HTTP status code associated with the error. Default is 404.
    error_code (str): A string representing the error code. Default is "session_not_found

"""
class SessionNotFoundError(AppError):
    status_code = 404
    error_code = "session_not_found"


class InvalidSimulationSourceError(AppError):
    """The requested dataset/sequence cannot be used to create a session."""

    status_code = 400
    error_code = "invalid_simulation_source"

"""
# ---------------------------------------------------------------------------------------------
# Function: app_error_handler()
# ---------------------------------------------------------------------------------------------
Description:
This function handles application errors by returning a JSON response with the appropriate HTTP status code
and error details. It is used as an exception handler for the FastAPI application.

Args:
    _: Request - The incoming request object (not used in this handler).
    exc: AppError - The exception object containing details about the application error.

Returns:
    JSONResponse - A JSON response with the appropriate HTTP status code and error details.
    
"""
async def app_error_handler(_: Request, exc: AppError) -> JSONResponse:
    return JSONResponse(
        status_code=exc.status_code,
        content={"error": {"code": exc.error_code, "message": exc.detail}},
    )
