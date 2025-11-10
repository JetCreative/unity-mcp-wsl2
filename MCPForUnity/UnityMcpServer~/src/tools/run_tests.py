"""Tool for executing Unity Test Runner suites."""
from collections.abc import Iterable
from typing import Annotated, Literal, Any
import asyncio
import time

from fastmcp import Context
from pydantic import BaseModel, Field

from models import MCPResponse
from registry import mcp_for_unity_tool
from tools import get_unity_instance_from_context, async_send_with_unity_instance
from unity_connection import async_send_command_with_retry


class RunTestsSummary(BaseModel):
    total: int
    passed: int
    failed: int
    skipped: int
    durationSeconds: float
    resultState: str


class RunTestsTestResult(BaseModel):
    name: str
    fullName: str
    state: str
    durationSeconds: float
    message: str | None = None
    stackTrace: str | None = None
    output: str | None = None


class RunTestsResult(BaseModel):
    runId: str | None = None
    mode: str | None = None
    state: str | None = None
    summary: RunTestsSummary | None = None
    results: list[RunTestsTestResult] | None = None


class RunTestsResponse(MCPResponse):
    data: RunTestsResult | None = None


FINAL_STATES = {"completed", "failed", "canceled", "cancelled", "faulted"}
POLL_INTERVAL_SECONDS = 0.5
PROGRESS_LOG_INTERVAL = 5.0
DEFAULT_TIMEOUT_SECONDS = 600


class RunCompletionTimeout(Exception):
    def __init__(self, run_id: str, timeout_seconds: int, snapshot: dict | None = None):
        self.run_id = run_id
        self.timeout_seconds = timeout_seconds
        self.snapshot = snapshot or {}
        super().__init__(f"Test run '{run_id}' did not finish within {timeout_seconds} seconds.")


def coerce_int(value, default=None):
    """Best-effort conversion to a positive integer."""
    if value is None:
        return default
    try:
        if isinstance(value, bool):
            return default
        if isinstance(value, int):
            return value if value > 0 else default
        s = str(value).strip()
        if s.lower() in ("", "none", "null"):
            return default
        as_int = int(float(s))
        return as_int if as_int > 0 else default
    except Exception:
        return default


def coerce_bool(value, default=None):
    """Normalize disparate truthy/falsy input values."""
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return bool(value)
    text = str(value).strip().lower()
    if text in ("true", "1", "yes", "y", "on"):
        return True
    if text in ("false", "0", "no", "n", "off"):
        return False
    return default


def coerce_string_list(value: Any) -> list[str] | None:
    """Accept strings, iterables of strings, or scalars and return distinct values."""
    if value is None:
        return None

    def _stringify(item: Any) -> str | None:
        if item is None:
            return None
        text = str(item).strip()
        return text or None

    items: list[str] = []
    if isinstance(value, str):
        maybe = _stringify(value)
        if maybe:
            items.append(maybe)
    elif isinstance(value, Iterable):
        for entry in value:
            maybe = _stringify(entry)
            if maybe:
                items.append(maybe)
    else:
        maybe = _stringify(value)
        if maybe:
            items.append(maybe)

    if not items:
        return None

    seen = set()
    unique: list[str] = []
    for entry in items:
        if entry not in seen:
            seen.add(entry)
            unique.append(entry)
    return unique or None


def combine_string_lists(*values: Any) -> list[str] | None:
    combined: list[str] = []
    seen = set()
    for value in values:
        normalized = coerce_string_list(value)
        if not normalized:
            continue
        for entry in normalized:
            if entry in seen:
                continue
            seen.add(entry)
            combined.append(entry)
    return combined or None


def build_summary_message(run_id: str, result_data: dict | None) -> str:
    summary = (result_data or {}).get("summary") or {}
    result_state = summary.get("resultState") or "Unknown"
    total = summary.get("total")
    passed = summary.get("passed")
    failed = summary.get("failed")
    skipped = summary.get("skipped")
    if all(value is None for value in (total, passed, failed, skipped)):
        return f"Run '{run_id}' finished with state {result_state}."
    return (
        f"Run '{run_id}' finished with state {result_state}: "
        f"{passed or 0}/{total or 0} passed, {failed or 0} failed, {skipped or 0} skipped."
    )


async def wait_for_run_completion(ctx, unity_instance: str | None, run_id: str, timeout_seconds: int) -> dict:
    """Poll Unity for run status until completion or timeout."""
    if not run_id:
        raise ValueError("run_id is required to wait for completion")

    start_time = time.monotonic()
    deadline = start_time + timeout_seconds if timeout_seconds else None
    last_snapshot: dict | None = None
    last_log = 0.0

    while True:
        try:
            status_resp = await async_send_with_unity_instance(
                async_send_command_with_retry,
                unity_instance,
                "get_test_run_status",
                {"runId": run_id},
            )
        except Exception as exc:
            now = time.monotonic()
            if now - last_log >= PROGRESS_LOG_INTERVAL:
                await ctx.info(f"Run '{run_id}' status unavailable ({exc}); retrying...")
                last_log = now
            status_resp = None

        if isinstance(status_resp, dict) and status_resp.get("success"):
            snapshot = status_resp.get("data") or {}
            last_snapshot = snapshot
            state = (snapshot.get("state") or "").lower()
            if state in FINAL_STATES:
                result_resp = await async_send_with_unity_instance(
                    async_send_command_with_retry,
                    unity_instance,
                    "get_test_run_result",
                    {"runId": run_id},
                )
                if isinstance(result_resp, dict) and result_resp.get("success"):
                    data = result_resp.get("data") or {}
                    data.setdefault("state", snapshot.get("state"))
                    return data

                # Fallback to snapshot information if detailed results are unavailable
                return {
                    "runId": snapshot.get("runId") or run_id,
                    "mode": snapshot.get("mode"),
                    "state": snapshot.get("state"),
                    "summary": snapshot.get("summary"),
                    "results": None,
                }

        now = time.monotonic()
        if deadline and now >= deadline:
            raise RunCompletionTimeout(run_id, timeout_seconds, last_snapshot)

        # If Unity reports an error (e.g., run trimmed) attempt to fetch final result anyway.
        if isinstance(status_resp, dict) and not status_resp.get("success"):
            error_text = (status_resp.get("error") or status_resp.get("message") or "").lower()
            if "not found" in error_text or "no active" in error_text:
                result_resp = await async_send_with_unity_instance(
                    async_send_command_with_retry,
                    unity_instance,
                    "get_test_run_result",
                    {"runId": run_id},
                )
                if isinstance(result_resp, dict) and result_resp.get("success"):
                    data = result_resp.get("data") or {}
                    return data

        if now - last_log >= PROGRESS_LOG_INTERVAL:
            state_str = (last_snapshot or {}).get("state") or "pending"
            elapsed = now - start_time
            await ctx.info(f"Run '{run_id}' still {state_str} after {elapsed:.1f}s...")
            last_log = now

        await asyncio.sleep(POLL_INTERVAL_SECONDS)


@mcp_for_unity_tool(
    description="Runs Unity tests for the specified mode"
)
async def run_tests(
    ctx: Context,
    mode: Annotated[Literal["EditMode", "PlayMode"], Field(
        description="Unity test mode to run")] = "EditMode",
    timeout_seconds: Annotated[int | str, Field(
        description="Optional timeout in seconds for the Unity test run (string, e.g. '30')")] | None = None,
    wait_for_completion: Annotated[bool | str, Field(
        description="Set false to return immediately after starting the run")] | None = True,
    test_names: Annotated[list[str] | str | None, Field(
        description="Optional fully-qualified test names to run")] = None,
    tests: Annotated[list[str] | str | None, Field(
        description="Alias for test_names")] = None,
    group_names: Annotated[list[str] | str | None, Field(
        description="Optional Unity Test Runner groups to include")] = None,
    category_names: Annotated[list[str] | str | None, Field(
        description="Optional category filters to include")] = None,
    categories: Annotated[list[str] | str | None, Field(
        description="Alias for category_names")] = None,
    assembly_names: Annotated[list[str] | str | None, Field(
        description="Optional assembly filters to include")] = None,
    assemblies: Annotated[list[str] | str | None, Field(
        description="Alias for assembly_names")] = None,
) -> RunTestsResponse:
    unity_instance = get_unity_instance_from_context(ctx)

    params: dict[str, Any] = {"mode": mode}
    ts = coerce_int(timeout_seconds)
    if ts is not None:
        params["timeoutSeconds"] = ts

    wait = coerce_bool(wait_for_completion, default=True)
    wait_timeout = ts or DEFAULT_TIMEOUT_SECONDS
    params["waitForCompletion"] = False

    filtered_tests = combine_string_lists(test_names, tests)
    if filtered_tests:
        params["testNames"] = filtered_tests

    filtered_groups = coerce_string_list(group_names)
    if filtered_groups:
        params["groupNames"] = filtered_groups

    filtered_categories = combine_string_lists(category_names, categories)
    if filtered_categories:
        params["categoryNames"] = filtered_categories

    filtered_assemblies = combine_string_lists(assembly_names, assemblies)
    if filtered_assemblies:
        params["assemblyNames"] = filtered_assemblies

    response = await async_send_with_unity_instance(async_send_command_with_retry, unity_instance, "run_tests", params)
    await ctx.info(f'Response {response}')
    if not isinstance(response, dict):
        return response

    if not wait:
        return RunTestsResponse(**response)

    run_id = (response.get("data") or {}).get("runId")
    if not run_id:
        return RunTestsResponse(**response)

    try:
        final_data = await wait_for_run_completion(ctx, unity_instance, run_id, wait_timeout)
    except RunCompletionTimeout as exc:
        snapshot = exc.snapshot or {"runId": run_id, "state": "Unknown"}
        message = f"Test run '{run_id}' is still running after {exc.timeout_seconds} seconds."
        return RunTestsResponse(success=False, message=message, error=message, data=snapshot)
    except Exception as exc:
        error_message = f"Failed to retrieve final results for run '{run_id}': {exc}"
        return RunTestsResponse(success=False, message=error_message, error=str(exc), data={"runId": run_id})

    summary_message = build_summary_message(run_id, final_data)
    return RunTestsResponse(success=True, message=summary_message, data=final_data)
