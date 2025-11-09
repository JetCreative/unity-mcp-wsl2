from .bootstrap_shared_server import ensure_shared_server_on_path

# Ensure the authoritative server sources are available before any other
# package-level imports execute. Expose the resolved path for diagnostics.
SERVER_SOURCE_PATH = ensure_shared_server_on_path()

__all__ = ["SERVER_SOURCE_PATH"]
