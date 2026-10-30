"""Account endpoints: sign up, log in, log out, and who am I."""

from __future__ import annotations

from datetime import datetime

from fastapi import APIRouter, Depends, Request, Response
from pydantic import BaseModel, Field

from src.auth.service import AccountsUnavailable, AuthService, InvalidCredentials, get_auth_service

router = APIRouter(prefix="/auth", tags=["auth"])


class Credentials(BaseModel):
    username: str = Field(max_length=64)
    password: str = Field(max_length=256)


class LoginSession(BaseModel):
    username: str
    # Sent back as "Authorization: Bearer <token>" on later requests
    token: str
    expires_at: datetime
    # Seconds until expires_at, for clients whose clock may differ from the server's
    expires_in: int


class CurrentUser(BaseModel):
    username: str


def require_auth_service() -> AuthService:
    service = get_auth_service()
    if service is None:
        raise AccountsUnavailable("Accounts are not configured on this server. Set MONGODB_URI in backend/.env.")
    return service


# Plain def (not async): FastAPI runs these in a worker thread, so slow password hashing
# does not hold up other requests.
@router.post("/signup", status_code=201)
def sign_up(body: Credentials, service: AuthService = Depends(require_auth_service)) -> LoginSession:
    return LoginSession(**service.sign_up(body.username, body.password))


@router.post("/login")
def log_in(body: Credentials, service: AuthService = Depends(require_auth_service)) -> LoginSession:
    return LoginSession(**service.log_in(body.username, body.password))


@router.post("/logout", status_code=204)
def log_out(request: Request, service: AuthService = Depends(require_auth_service)) -> Response:
    scheme, _, token = request.headers.get("authorization", "").partition(" ")
    if scheme.lower() == "bearer" and token.strip():
        service.log_out(token.strip())
    return Response(status_code=204)


@router.get("/me")
def current_user(request: Request) -> CurrentUser:
    if not request.user.is_authenticated:
        raise InvalidCredentials("Not logged in, or the session has expired.")
    return CurrentUser(username=request.user.username)
