"""Tool for rerunning only the failed Unity tests from a prior run."""
from typing import Annotated, Any

from fastmcp import Context
from pydantic import Field

from registry import mcp_for_unity_tool
from tools import async_send_with_unity_instance, get_unity_instance_from_context
from unity_connection import async_send_command_with_retry

from .run_tests import RunTestsResponse, coerce_bool, coerce_int


@mcp_for_unity_tool(description="Reruns failed Unity tests from a previous run")
async def rerun_failed_tests(
    ctx: Context,
    run_id: Annotated[str | None, Field(
        description="Optional test run identifier to pull failures from; defaults to the last completed run"
    )] = None,
    wait_for_completion: Annotated[bool | str | None, Field(
        description="Set false to return immediately after starting the rerun"
    )] = True,
    timeout_seconds: Annotated[int | str | None, Field(
        description="Optional timeout in seconds for the rerun (string, e.g. '30')"
    )] = None,
) -> RunTestsResponse:
    unity_instance = get_unity_instance_from_context(ctx)

    params: dict[str, Any] = {}
    if run_id:
        params["runId"] = run_id

    wait = coerce_bool(wait_for_completion, default=True)
    if wait is not None:
        params["waitForCompletion"] = wait

    timeout = coerce_int(timeout_seconds)
    if timeout is not None:
        params["timeoutSeconds"] = timeout

    response = await async_send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "rerun_failed_tests",
        params,
    )
    return RunTestsResponse(**response) if isinstance(response, dict) else response
