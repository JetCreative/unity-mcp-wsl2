"""Tool to enumerate Unity tests via MCP bridge."""
from typing import Annotated, Literal, Any

from fastmcp import Context
from pydantic import Field

from models import MCPResponse
from registry import mcp_for_unity_tool
from tools import async_send_with_unity_instance, get_unity_instance_from_context
from unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(description="Lists Unity tests, optionally filtered by mode")
async def list_tests(
    ctx: Context,
    mode: Annotated[
        Literal["EditMode", "PlayMode", "All"],
        Field(description="Unity test mode to list. Use 'All' to include both.")
    ] = "All",
) -> MCPResponse:
    unity_instance = get_unity_instance_from_context(ctx)

    params: dict[str, Any] = {}
    normalized_mode = (mode or "All").strip()
    if normalized_mode.lower() != "all":
        params["mode"] = normalized_mode

    response = await async_send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "list_tests",
        params,
    )

    if isinstance(response, dict):
        return MCPResponse(**response)
    return response
