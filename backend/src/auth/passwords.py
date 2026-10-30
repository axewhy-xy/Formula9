"""Salted PBKDF2 password records.

The format matches the native app's local users.json, so accounts created there can be imported
into MongoDB without anyone resetting their password.
"""

from __future__ import annotations

import hashlib
import hmac
import secrets

PASSWORD_FORMAT = "pbkdf2-sha256-v1"
ITERATIONS = 600_000
SALT_BYTES = 16
HASH_BYTES = 32


def hash_password(password: str) -> dict[str, str]:
    """Return the fields stored for a password; the password itself is never stored."""
    salt = secrets.token_bytes(SALT_BYTES)
    return {
        "password_format": PASSWORD_FORMAT,
        "salt": salt.hex(),
        "password_hash": _derive(password, salt).hex(),
    }


def verify_password(record: dict, password: str) -> bool:
    if not password or record.get("password_format") != PASSWORD_FORMAT:
        return False
    try:
        salt = bytes.fromhex(record["salt"])
        expected = bytes.fromhex(record["password_hash"])
    except (KeyError, TypeError, ValueError):
        return False
    if len(salt) != SALT_BYTES or len(expected) != HASH_BYTES:
        return False
    # Constant-time comparison, so response timing does not reveal how much of a guess matched
    return hmac.compare_digest(_derive(password, salt), expected)


def _derive(password: str, salt: bytes) -> bytes:
    return hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, ITERATIONS, HASH_BYTES)
