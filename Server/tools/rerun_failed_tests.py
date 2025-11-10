"""Tool for rerunning only the failed Unity tests from a prior run."""
from typing import Annotated, Any

from fastmcp import Context
from pydantic import Field

from registry import mcp_for_unity_tool
from tools import async_send_with_unity_instance, get_unity_instance_from_context
from unity_connection import async_send_command_with_retry

from .run_tests import (
    DefaultTimeoutSeconds,
    RunCompletionTimeout,
    RunTestsResponse,
    build_summary_message,
    coerce_bool,
    coerce_int,
    wait_for_run_completion,
)


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
    params["waitForCompletion"] = False

    timeout = coerce_int(timeout_seconds)
    wait_timeout = timeout or DefaultTimeoutSeconds
    if timeout is not None:
        params["timeoutSeconds"] = timeout

    response = await async_send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "rerun_failed_tests",
        params,
    )
    if not isinstance(response, dict):
        return response

    if not wait:
        return RunTestsResponse(**response)

    run_id_from_response = (response.get("data") or {}).get("runId") or run_id
    if not run_id_from_response:
        return RunTestsResponse(**response)

    try:
        final_data = await wait_for_run_completion(ctx, unity_instance, run_id_from_response, wait_timeout)
    except RunCompletionTimeout as exc:
        snapshot = exc.snapshot or {"runId": run_id_from_response, "state": "Unknown"}
        message = f"Failed-test rerun '{run_id_from_response}' is still running after {exc.timeout_seconds} seconds."
        return RunTestsResponse(success=False, message=message, error=message, data=snapshot)
    except Exception as exc:
        error_message = f"Failed to retrieve rerun results for '{run_id_from_response}': {exc}"
        return RunTestsResponse(success=False, message=error_message, error=str(exc), data={"runId": run_id_from_response})

    summary_message = build_summary_message(run_id_from_response, final_data)
    return RunTestsResponse(success=True, message=summary_message, data=final_data)
