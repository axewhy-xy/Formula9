"""Formula 9 backend package.

Date: 20/09/2026
Author: Lau Xiang Yang


Description:
The backend is normally launched with ``backend`` as the working directory,
while the renderer-independent ``contracts`` package lives at repository
root. Add that monorepo root to the module search path before any ``src``
submodule imports the shared contracts. This keeps the documented
``uvicorn src.app:app`` command working without a machine-specific
``PYTHONPATH`` setting.

"""

from pathlib import Path
import sys


_REPOSITORY_ROOT = str(Path(__file__).resolve().parents[2])
if _REPOSITORY_ROOT not in sys.path:
    sys.path.insert(0, _REPOSITORY_ROOT)
