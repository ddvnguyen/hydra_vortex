import os
import subprocess
from pathlib import Path


def _get_version() -> str:
    root = Path(__file__).resolve().parent.parent.parent
    version_file = root / "VERSION"
    if version_file.is_file():
        return version_file.read_text().strip()
    try:
        import importlib.metadata
        return importlib.metadata.version("hydra")
    except (ImportError, Exception):
        pass
    return "0.0.0-dev"


def _get_revision() -> str:
    try:
        return subprocess.run(
            ["git", "rev-parse", "--short", "HEAD"],
            capture_output=True, text=True, timeout=5,
        ).stdout.strip()
    except Exception:
        return "unknown"


VERSION = _get_version()
REVISION = _get_revision()
