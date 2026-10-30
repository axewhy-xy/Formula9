"""
FastAPI application entry point.

Author: Lau Xiang Yang
Date Created: 09/09/2026
Last Modified: 09/09/2026

Description:
    This module initializes the FastAPI application and configures its middleware and routes.

"""

import asyncio
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.encoders import jsonable_encoder
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from starlette.middleware.authentication import AuthenticationMiddleware

from src.auth.middleware import TokenAuthBackend, auth_error_response
from src.auth.service import close_auth_service, get_auth_service
from src.api.routes.simulation_ws import router as ws_router
from src.api.routes.training_v2 import (
    close_training_store, get_training_store, initialize_training_service, training_store_error_handler,
)
from src.api.router import api_router
from src.core.config import get_settings
from src.core.errors import AppError, app_error_handler
from src.core.training_telemetry import TrainingTraceMiddleware
from src.persistence.training_store import TrainingStoreError

# Initialize FastAPI application and configure middleware and routes
settings = get_settings()

@asynccontextmanager
async def lifespan(application: FastAPI):
    try:
        provider = application.dependency_overrides.get(get_training_store, get_training_store)
        store = await asyncio.to_thread(provider)
        await initialize_training_service(application, store)
        yield
    finally:
        for service in getattr(application.state, "training_services", {}).values():
            await service.close()
        application.state.training_services = {}
        close_training_store()
        close_auth_service()


# Initialize FastAPI application
app = FastAPI(title=settings.app_name, lifespan=lifespan)

# Sets request.user from the "Authorization: Bearer <token>" header. Added before CORS so CORS
# stays the outer layer and still answers browser preflight requests.
app.add_middleware(AuthenticationMiddleware, backend=TokenAuthBackend(get_auth_service),
                   on_error=auth_error_response)
app.add_middleware(TrainingTraceMiddleware)

# Configure CORS middleware to allow cross-origin requests
app.add_middleware(
    CORSMiddleware,
    allow_origins=settings.cors_origins,
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
    expose_headers=["X-Training-Trace-Id"],
)
app.add_exception_handler(AppError, app_error_handler)
app.add_exception_handler(TrainingStoreError, training_store_error_handler)

"""
# ---------------------------------------------------------------------------------------------
# Function: validation_error_handler()
# ---------------------------------------------------------------------------------------------
Description:
This function handles validation errors that occur during request processing. It returns a 
JSON response with a 422 status code and details about the validation error.

Args:
    _: Request - The incoming request object (not used in this handler).
    exc: RequestValidationError - The exception object containing details about the validation error.

Returns:
    JSONResponse - A JSON response with a 422 status code and error details.

"""
@app.exception_handler(RequestValidationError)
async def validation_error_handler(_: Request, exc: RequestValidationError) -> JSONResponse:
    return JSONResponse(
        status_code=422,
        content=jsonable_encoder(
            {"error": {"code": "validation_error", "details": exc.errors()}},
            custom_encoder={Exception: str},
        ),
    )

"""
# ---------------------------------------------------------------------------------------------
# Function: root()
# ---------------------------------------------------------------------------------------------
Description:
This function serves as the root endpoint of the FastAPI application. It returns a JSON response
indicating that the application is running.

Args:
    None

Returns:
    dict[str, str] - A dictionary containing a message indicating that the application is running.

"""
@app.get("/", include_in_schema=False)
async def root() -> dict[str, str]:
    return {"message": f"{settings.app_name} is running"}

# Include the API router with the specified prefix from settings
app.include_router(api_router, prefix=settings.api_prefix)
app.include_router(ws_router)
