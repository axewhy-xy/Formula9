"""Where accounts and log-in sessions are kept.

MongoAccountStore is the real store. MemoryAccountStore keeps the same data in dictionaries so
tests run without a network connection or an Atlas account.
"""

from __future__ import annotations

from threading import Lock
from typing import Protocol

from pymongo import MongoClient
from pymongo.errors import DuplicateKeyError, PyMongoError


class UsernameTaken(Exception):
    pass


class StoreUnavailable(Exception):
    """The database could not be reached or refused the request."""


class AccountStore(Protocol):
    def add_user(self, user: dict) -> None: ...
    def find_user(self, username_key: str) -> dict | None: ...
    def add_session(self, session: dict) -> None: ...
    def find_session(self, token_hash: str) -> dict | None: ...
    def remove_session(self, token_hash: str) -> None: ...
    def close(self) -> None: ...


class MongoAccountStore:
    """users: one document per account. sessions: one per log-in, deleted by MongoDB once expired."""

    def __init__(self, uri: str, database: str) -> None:
        try:
            # tz_aware returns expiry times in UTC, so they compare correctly with datetime.now(UTC)
            self._client = MongoClient(uri, serverSelectionTimeoutMS=5000, tz_aware=True)
        except PyMongoError as error:
            # mongodb+srv addresses are resolved here, so DNS and network failures surface now
            raise StoreUnavailable(str(error)) from error
        db = self._client[database]
        self._users = db["users"]
        self._sessions = db["sessions"]
        self._indexes_ready = False
        self._index_lock = Lock()

    def _ensure_indexes(self) -> None:
        # Created on first use rather than at start-up, so the API still starts while offline
        with self._index_lock:
            if self._indexes_ready:
                return
            self._users.create_index("username_key", unique=True)
            self._sessions.create_index("token_hash", unique=True)
            # expireAfterSeconds=0 deletes each session once its expires_at time has passed
            self._sessions.create_index("expires_at", expireAfterSeconds=0)
            self._indexes_ready = True

    def add_user(self, user: dict) -> None:
        try:
            self._ensure_indexes()
            self._users.insert_one(dict(user))
        except DuplicateKeyError as error:
            raise UsernameTaken(user["username"]) from error
        except PyMongoError as error:
            raise StoreUnavailable(str(error)) from error

    def find_user(self, username_key: str) -> dict | None:
        return self._find(self._users, {"username_key": username_key})

    def add_session(self, session: dict) -> None:
        try:
            self._ensure_indexes()
            self._sessions.insert_one(dict(session))
        except PyMongoError as error:
            raise StoreUnavailable(str(error)) from error

    def find_session(self, token_hash: str) -> dict | None:
        return self._find(self._sessions, {"token_hash": token_hash})

    def remove_session(self, token_hash: str) -> None:
        try:
            self._sessions.delete_one({"token_hash": token_hash})
        except PyMongoError as error:
            raise StoreUnavailable(str(error)) from error

    def close(self) -> None:
        self._client.close()

    def _find(self, collection, query: dict) -> dict | None:
        try:
            self._ensure_indexes()
            return collection.find_one(query, {"_id": False})
        except PyMongoError as error:
            raise StoreUnavailable(str(error)) from error


class MemoryAccountStore:
    def __init__(self) -> None:
        self._users: dict[str, dict] = {}
        self._sessions: dict[str, dict] = {}
        self._lock = Lock()

    def add_user(self, user: dict) -> None:
        with self._lock:
            if user["username_key"] in self._users:
                raise UsernameTaken(user["username"])
            self._users[user["username_key"]] = dict(user)

    def find_user(self, username_key: str) -> dict | None:
        with self._lock:
            user = self._users.get(username_key)
            return dict(user) if user else None

    def add_session(self, session: dict) -> None:
        with self._lock:
            self._sessions[session["token_hash"]] = dict(session)

    def find_session(self, token_hash: str) -> dict | None:
        with self._lock:
            session = self._sessions.get(token_hash)
            return dict(session) if session else None

    def remove_session(self, token_hash: str) -> None:
        with self._lock:
            self._sessions.pop(token_hash, None)

    def close(self) -> None:
        pass
