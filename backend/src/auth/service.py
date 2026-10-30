"""Sign-up, log-in and log-out rules, independent of HTTP and of which database stores them."""

from __future__ import annotations

import hashlib
import re
import secrets
from datetime import UTC, datetime, timedelta
from threading import Lock
from typing import Callable

from src.auth.passwords import hash_password, verify_password
from src.auth.store import AccountStore, MongoAccountStore, StoreUnavailable, UsernameTaken
from src.core.config import get_settings
from src.core.errors import AppError

USERNAME_PATTERN = re.compile(r"[A-Za-z0-9_.-]{3,32}")
MIN_PASSWORD_LENGTH = 8
MAX_PASSWORD_LENGTH = 256
SESSION_LIFETIME = timedelta(hours=12)

# Checked when the username does not exist, so a wrong username takes as long as a wrong password
_DUMMY_RECORD = hash_password(secrets.token_urlsafe(16))


class InvalidAccountDetails(AppError):
    status_code = 400
    error_code = "invalid_account_details"


class UsernameTakenError(AppError):
    status_code = 409
    error_code = "username_taken"


class InvalidCredentials(AppError):
    status_code = 401
    error_code = "invalid_credentials"


class AccountsUnavailable(AppError):
    status_code = 503
    error_code = "accounts_unavailable"


class AuthService:
    def __init__(self, store: AccountStore, clock: Callable[[], datetime] = lambda: datetime.now(UTC)) -> None:
        self._store = store
        self._clock = clock

    def sign_up(self, username: str, password: str) -> dict:
        username = username.strip()
        if not USERNAME_PATTERN.fullmatch(username):
            raise InvalidAccountDetails("Usernames are 3 to 32 letters, numbers, dots, dashes or underscores.")
        if not MIN_PASSWORD_LENGTH <= len(password) <= MAX_PASSWORD_LENGTH:
            raise InvalidAccountDetails(f"Passwords are {MIN_PASSWORD_LENGTH} to {MAX_PASSWORD_LENGTH} characters.")
        user = {
            "username": username,
            # Lower-case copy keeps "Rishika" and "rishika" from becoming two accounts
            "username_key": username.lower(),
            **hash_password(password),
            "created_at": self._clock(),
        }
        try:
            self._store.add_user(user)
        except UsernameTaken as error:
            raise UsernameTakenError("That username is taken.") from error
        except StoreUnavailable as error:
            raise AccountsUnavailable("Accounts are unavailable right now. Try again shortly.") from error
        return self._start_session(username)

    def log_in(self, username: str, password: str) -> dict:
        try:
            user = self._store.find_user(username.strip().lower())
        except StoreUnavailable as error:
            raise AccountsUnavailable("Accounts are unavailable right now. Try again shortly.") from error
        # The same message for both cases, so nobody can probe which usernames exist
        if not verify_password(user or _DUMMY_RECORD, password) or user is None:
            raise InvalidCredentials("Incorrect username or password.")
        return self._start_session(user["username"])

    def log_out(self, token: str) -> None:
        try:
            self._store.remove_session(_token_hash(token))
        except StoreUnavailable as error:
            raise AccountsUnavailable("Accounts are unavailable right now. Try again shortly.") from error

    def user_for_token(self, token: str) -> str | None:
        """The username a token belongs to, or None when it is unknown or expired."""
        try:
            session = self._store.find_session(_token_hash(token))
        except StoreUnavailable as error:
            raise AccountsUnavailable("Accounts are unavailable right now. Try again shortly.") from error
        # MongoDB removes expired sessions about once a minute, so check the time here as well
        if session is None or session["expires_at"] <= self._clock():
            return None
        return session["username"]

    def _start_session(self, username: str) -> dict:
        token = secrets.token_urlsafe(32)
        now = self._clock()
        expires_at = now + SESSION_LIFETIME
        try:
            # Only a hash of the token is stored, so a copy of the database cannot be used to log in
            self._store.add_session({
                "token_hash": _token_hash(token),
                "username": username,
                "created_at": now,
                "expires_at": expires_at,
            })
        except StoreUnavailable as error:
            raise AccountsUnavailable("Accounts are unavailable right now. Try again shortly.") from error
        # expires_in lets clients time the expiry with their own clock, whatever its offset
        return {"username": username, "token": token, "expires_at": expires_at,
                "expires_in": int(SESSION_LIFETIME.total_seconds())}


def _token_hash(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


_service: AuthService | None = None
_service_lock = Lock()


def get_auth_service() -> AuthService | None:
    """The shared service, or None when MONGODB_URI is not set.

    Raises AccountsUnavailable (503) when the database cannot be reached yet.
    """
    global _service
    with _service_lock:
        if _service is None:
            settings = get_settings()
            if not settings.mongodb_uri:
                return None
            try:
                store = MongoAccountStore(settings.mongodb_uri, settings.mongodb_database)
            except StoreUnavailable as error:
                # Not cached, so the next request tries again once the network is back
                raise AccountsUnavailable("Accounts are unavailable right now. Try again shortly.") from error
            _service = AuthService(store)
        return _service


def set_auth_service(service: AuthService | None) -> None:
    """Replace the shared service; tests use this to run against MemoryAccountStore."""
    global _service
    with _service_lock:
        _service = service


def close_auth_service() -> None:
    global _service
    with _service_lock:
        if _service is not None:
            _service._store.close()
            _service = None
