"""

Configuration settings for the API.

Author: Lau Xiang Yang
Date Created: 09/09/2026
Last Modified: 09/09/2026

Description:
    This module defines the configuration settings for the API, including application name, environment, and CORS origins.

"""

from functools import lru_cache
from os import getenv
from pathlib import Path

from dotenv import load_dotenv
from pydantic import BaseModel, Field


"""
# ---------------------------------------------------------------------------------------------
# Class: Settings
# ---------------------------------------------------------------------------------------------
Description:
This class defines the configuration settings for the API. It includes attributes for the 
application name, environment, API prefix, and CORS origins. The settings can be customized 
through environment variables.

Attributes:
    app_name (str): The name of the application. Default is "Formula 9 API".
    environment (str): The environment in which the application is running. Default is "development".
    api_prefix (str): The prefix for the API routes. Default is "/api".
    cors_origins (list[str]): A list of allowed CORS origins. Default is ["http://localhost:5173"].

"""
class Settings(BaseModel):
    app_name: str = "Formula 9 API"
    environment: str = "development"
    api_prefix: str = "/api"
    cors_origins: list[str] = Field(default_factory=lambda: ["http://localhost:5173"])
    map_database_path: Path
    training_database_path: Path
    # LTA DataMall bus stops/services/routes, written by src.transit.import_lta
    transit_database_path: Path
    # Accounts are stored in MongoDB; without a URI the /auth endpoints answer 503
    mongodb_uri: str | None = None
    mongodb_database: str = "formula9"

"""
# ---------------------------------------------------------------------------------------------
# Function: get_settings()
# ---------------------------------------------------------------------------------------------
Description:
This function retrieves the configuration settings for the API. It uses caching to ensure that
the settings are built only once per process. The function reads environment variables to customize
the settings, including CORS origins.

Args:
    None

Returns:
    Settings: An instance of the Settings class containing the configuration settings for the API.

"""
@lru_cache
def get_settings() -> Settings:
    """Build settings once per process."""
    project_root = Path(__file__).resolve().parents[3]
    # Secrets such as MONGODB_URI live in the git-ignored backend/.env. Real environment
    # variables win over the file.
    load_dotenv(project_root / "backend" / ".env", override=False)
    origins = [origin.strip() for origin in getenv("CORS_ORIGINS", "http://localhost:5173").split(",")]
    configured_map = getenv("MAP_DATABASE_PATH")
    map_database_path = (
        Path(configured_map).expanduser().resolve()
        if configured_map
        else (project_root / "backend" / "fixtures" / "map" / "map.db").resolve()
    )
    configured_training = getenv("TRAINING_DATABASE_PATH")
    training_database_path = (
        Path(configured_training).expanduser().resolve()
        if configured_training
        else (project_root / "state" / "training.sqlite").resolve()
    )
    configured_transit = getenv("TRANSIT_DATABASE_PATH")
    transit_database_path = (
        Path(configured_transit).expanduser().resolve()
        if configured_transit
        else (project_root / "backend" / "fixtures" / "transit" / "transit.sqlite").resolve()
    )
    return Settings(
        environment=getenv("ENVIRONMENT", "development"),
        cors_origins=[origin for origin in origins if origin],
        map_database_path=map_database_path,
        training_database_path=training_database_path,
        transit_database_path=transit_database_path,
        mongodb_uri=getenv("MONGODB_URI") or None,
        mongodb_database=getenv("MONGODB_DATABASE", "formula9"),
    )
