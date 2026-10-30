"""Version 2 training contracts. Version 1 playback remains in contracts.common."""

from .models import parse_message, transition_allowed

__all__ = ["parse_message", "transition_allowed"]
