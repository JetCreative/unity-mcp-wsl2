"""Tool for executing Unity Test Runner suites."""
from collections.abc import Iterable
from typing import Annotated, Literal, Any

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
    if wait is not None:
        params["waitForCompletion"] = wait

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
    return RunTestsResponse(**response) if isinstance(response, dict) else response
