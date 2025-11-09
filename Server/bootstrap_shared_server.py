"""Bootstrap helpers to ensure the shared Unity MCP server code is on sys.path.

The repo historically carried multiple copies of the Python server (Windows,
WSL relay, and the embedded Unity package). This module centralizes the logic
for locating a single authoritative install and makes the current interpreter
reuse it instead of loading a stale fork.
"""
from __future__ import annotations

import os
import sys
from pathlib import Path
from typing import Iterable, Optional

_BOOTSTRAPPED = False
_RESOLVED_PATH: Optional[Path] = None


def _find_repo_root() -> Optional[Path]:
    current = Path(__file__).resolve()
    for parent in current.parents:
        if (parent / "Assets").is_dir():
            return parent
    return None


def _candidate_paths() -> Iterable[Path]:
    """Yield possible locations for the shared server sources."""
    override = os.environ.get("UNITY_MCP_SERVER_PATH")
    if override:
        for chunk in override.split(os.pathsep):
            chunk = chunk.strip()
            if chunk:
                yield Path(chunk).expanduser()

    # Windows install surfaced into WSL via env vars from interop
    win_profile = (
        os.environ.get("WIN_USERPROFILE")
        or os.environ.get("WSL_WIN_USERPROFILE")
        or os.environ.get("USERPROFILE")
    )
    if win_profile:
        yield Path(win_profile).expanduser() / "AppData/Local/UnityMCP/UnityMcpServer/src"

    username = os.environ.get("USERNAME") or os.environ.get("USER")
    if username:
        yield Path("/mnt/c/Users") / username / "AppData/Local/UnityMCP/UnityMcpServer/src"

    # Linux/mac dev installs (matches ServerInstaller fallbacks)
    home = Path.home()
    yield home / ".config/UnityMCP/UnityMcpServer/src"
    yield home / ".local/share/UnityMCP/UnityMcpServer/src"

    # Repo copies (package + WSL helper)
    repo_root = _find_repo_root()
    if repo_root:
        assets_root = repo_root / "Assets"
        yield assets_root / "MCP" / "UnityMcpServer~" / "src"
        yield assets_root / "unity-mcp-wsl2" / "MCPForUnity" / "UnityMcpServer~" / "src"
        yield assets_root / "unity-mcp-wsl2" / "Server"  # legacy fallback


def ensure_shared_server_on_path() -> Optional[Path]:
    """Insert the first available shared server location at the front of sys.path."""
    global _BOOTSTRAPPED, _RESOLVED_PATH
    if _BOOTSTRAPPED:
        return _RESOLVED_PATH

    for path in _candidate_paths():
        if not path:
            continue
        try:
            expanded = path.expanduser().resolve()
        except Exception:
            continue

        if not expanded.exists():
            continue

        # Heuristic: require either server.py or tools/ to exist
        if not ((expanded / "server.py").exists() or (expanded / "tools").is_dir()):
            continue

        sys.path.insert(0, str(expanded))
        os.environ.setdefault("UNITY_MCP_SERVER_PATH_RESOLVED", str(expanded))
        _RESOLVED_PATH = expanded
        break

    _BOOTSTRAPPED = True
    if _RESOLVED_PATH is None:
        # Emit a warning so devs know they are running the bundled WSL fork.
        sys.stderr.write(
            "[UnityMCP] Warning: Could not locate shared server sources; "
            "falling back to the bundled WSL copy.\n"
        )
    return _RESOLVED_PATH


__all__ = ["ensure_shared_server_on_path", "_RESOLVED_PATH"]
